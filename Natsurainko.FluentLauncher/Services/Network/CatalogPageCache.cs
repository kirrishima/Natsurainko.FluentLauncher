using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Natsurainko.FluentLauncher.Services.Network;

internal readonly record struct CatalogCacheOptions(bool Enabled, int LifetimeMinutes, int MaxSizeMb);

/// <summary>Bounded disk cache for search response bodies. File timestamps preserve TTL and LRU across restarts.</summary>
internal sealed class CatalogPageCache(
    string directory,
    Func<CatalogCacheOptions> getOptions,
    Action<Exception> reportError)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AsyncLocal<bool> _bypass = new();
    private long _generation;

    public bool BypassRead => _bypass.Value;
    public long Generation => Interlocked.Read(ref _generation);

    public async Task<T> RefreshAsync<T>(Func<Task<T>> action)
    {
        bool previous = _bypass.Value;
        _bypass.Value = true;
        try { return await action(); }
        finally { _bypass.Value = previous; }
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var options = getOptions();
            Trim(options);
            if (!options.Enabled || BypassRead) return null;
            string path = GetPath(key);
            if (!File.Exists(path)) return null;
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reportError(ex);
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(string key, byte[] bytes, long generation, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        string? temporary = null;
        try
        {
            var options = getOptions();
            Trim(options);
            long budget = Budget(options);
            // Do not repopulate after Clear was clicked while a request was in flight.
            if (!options.Enabled || generation != Generation || bytes.LongLength > budget) return;
            Directory.CreateDirectory(directory);
            string path = GetPath(key);
            if (File.Exists(path)) File.Delete(path);
            Evict(budget - bytes.LongLength);
            temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, token);
            File.Move(temporary, path, true);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reportError(ex);
        }
        finally
        {
            try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { reportError(ex); }
            _gate.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Interlocked.Increment(ref _generation);
            if (Directory.Exists(directory))
                foreach (var file in new DirectoryInfo(directory).GetFiles()) file.Delete();
        }
        finally { _gate.Release(); }
    }

    public async Task TrimAsync()
    {
        await _gate.WaitAsync();
        try { Trim(getOptions()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { reportError(ex); }
        finally { _gate.Release(); }
    }

    private string GetPath(string key) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");

    private static long Budget(CatalogCacheOptions options) => Math.Clamp(options.MaxSizeMb, 1, 50) * 1024L * 1024L;

    private void Trim(CatalogCacheOptions options)
    {
        if (!Directory.Exists(directory)) return;
        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Clamp(options.LifetimeMinutes, 5, 120));
        foreach (var file in new DirectoryInfo(directory).GetFiles())
            if (!options.Enabled || file.Extension == ".tmp" || file.LastWriteTimeUtc <= cutoff)
                file.Delete();
        Evict(options.Enabled ? Budget(options) : 0);
    }

    private void Evict(long budget)
    {
        var files = new DirectoryInfo(directory).GetFiles("*.json")
            .OrderBy(f => f.LastAccessTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
        long size = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (size <= budget) break;
            size -= file.Length;
            file.Delete();
        }
    }
}
