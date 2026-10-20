using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Natsurainko.FluentLauncher.Services.Network;

internal enum CatalogVersionSource { CurseForge, Modrinth, Mojang }

/// <summary>Provider-specific Minecraft versions, with Mojang as a fallback.</summary>
internal sealed class CatalogVersionService(
    HttpClient httpClient,
    string curseForgeApiKey,
    CatalogPageCache cache,
    ILogger<CatalogVersionService> logger)
{
    internal const string CurseForgeUrl = "https://api.curseforge.com/v1/minecraft/version";
    internal const string ModrinthUrl = "https://api.modrinth.com/v2/tag/game_version";
    internal const string MojangUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public Task<string[]> GetVersionsAsync(int resourceSource, CancellationToken token, bool refresh = false)
        => refresh ? cache.RefreshAsync(() => GetVersionsCoreAsync(resourceSource, token)) : GetVersionsCoreAsync(resourceSource, token);

    private async Task<string[]> GetVersionsCoreAsync(int resourceSource, CancellationToken token)
    {
        var source = resourceSource == 0 ? CatalogVersionSource.CurseForge : CatalogVersionSource.Modrinth;
        try { return await LoadAsync(source, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load Minecraft versions from {Source}; trying Mojang", source);
        }

        return await LoadAsync(CatalogVersionSource.Mojang, token);
    }

    private async Task<string[]> LoadAsync(CatalogVersionSource source, CancellationToken token)
    {
        string url = source switch
        {
            CatalogVersionSource.CurseForge => CurseForgeUrl,
            CatalogVersionSource.Modrinth => ModrinthUrl,
            _ => MojangUrl
        };
        string key = "catalog-versions-v1:" + url;
        long generation = cache.Generation;
        byte[]? cached = await cache.ReadAsync(key, token);
        if (cached != null)
        {
            try { return ParseVersions(cached, source); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Ignoring invalid cached Minecraft version list for {Source}", source);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (source == CatalogVersionSource.CurseForge)
            request.Headers.Add("x-api-key", curseForgeApiKey);
        using var response = await httpClient.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        string[] versions = ParseVersions(bytes, source);
        await cache.WriteAsync(key, bytes, generation, token);
        logger.LogInformation("Loaded {Count} Minecraft versions from {Source}", versions.Length, source);
        return versions;
    }

    internal static string[] ParseVersions(byte[] bytes, CatalogVersionSource source)
    {
        using var document = JsonDocument.Parse(bytes);
        JsonElement items = document.RootElement;
        if (source != CatalogVersionSource.Modrinth)
        {
            string property = source == CatalogVersionSource.CurseForge ? "data" : "versions";
            if (items.ValueKind != JsonValueKind.Object || !items.TryGetProperty(property, out items))
                throw new InvalidDataException("Missing Minecraft version list.");
        }
        if (items.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Expected a Minecraft version array.");

        string nameProperty = source switch
        {
            CatalogVersionSource.CurseForge => "versionString",
            CatalogVersionSource.Modrinth => "version",
            _ => "id"
        };
        string dateProperty = source switch
        {
            CatalogVersionSource.CurseForge => "dateModified",
            CatalogVersionSource.Modrinth => "date",
            _ => "releaseTime"
        };
        var versions = new List<(string Name, DateTimeOffset Date)>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(nameProperty, out var name)
                || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
                continue;
            DateTimeOffset date = DateTimeOffset.MinValue;
            if (item.TryGetProperty(dateProperty, out var dateValue) && dateValue.ValueKind == JsonValueKind.String)
                DateTimeOffset.TryParse(dateValue.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out date);
            versions.Add((name.GetString()!, date));
        }
        string[] result = versions.OrderByDescending(v => v.Date).Select(v => v.Name)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (result.Length == 0) throw new InvalidDataException("Minecraft version list is empty.");
        return result;
    }
}
