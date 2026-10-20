using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using FluentLauncher.Infra.UI.Notification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Natsurainko.FluentLauncher;
using Natsurainko.FluentLauncher.Services.Network;
using Natsurainko.FluentLauncher.Services.UI;
using Natsurainko.FluentLauncher.Services.UI.Notification;
using Natsurainko.FluentLauncher.ViewModels.Downloads;

internal static class VersionTests
{
    const string Modrinth = """
        [{"version":"old","date":"2025-01-01T00:00:00Z","version_type":"release"},
         {"version":"snapshot","date":"2026-03-01T00:00:00Z","version_type":"snapshot"},
         {"version":"new","date":"2026-02-01T00:00:00Z","version_type":"release"},
         {"version":"new","date":"2026-02-01T00:00:00Z"},{"version":""},{}]
        """;
    const string CurseForge = """
        {"data":[{"versionString":"cf-new","dateModified":"2026-02-01T00:00:00Z"},
                 {"versionString":"cf-old","dateModified":"2025-01-01T00:00:00Z"}]}
        """;
    const string Mojang = """
        {"versions":[{"id":"future-release","releaseTime":"2026-01-01T00:00:00Z","type":"release"},
                     {"id":"future-snapshot","releaseTime":"2026-02-01T00:00:00Z","type":"snapshot"},
                     {"id":"old-alpha","releaseTime":"2010-01-01T00:00:00Z","type":"old_alpha"}]}
        """;

    public static async Task Run(Action<bool, string> check)
    {
        string[] parsed = CatalogVersionService.ParseVersions(Encoding.UTF8.GetBytes(Modrinth), CatalogVersionSource.Modrinth);
        check(parsed.SequenceEqual(new[] { "snapshot", "new", "old" }), "version parsing sorts chronologically, deduplicates and retains snapshots");
        parsed = CatalogVersionService.ParseVersions(Encoding.UTF8.GetBytes(Mojang), CatalogVersionSource.Mojang);
        check(parsed.SequenceEqual(new[] { "future-snapshot", "future-release", "old-alpha" }), "Mojang fallback includes all version types");

        string folder = Path.Combine(Path.GetTempPath(), "fluent-version-tests-" + Guid.NewGuid().ToString("N"));
        bool enabled = true;
        var cache = new CatalogPageCache(folder, () => new(enabled, 30, 5), ex => throw new Exception("Unexpected cache failure", ex));
        var server = new VersionServer();
        using var http = new HttpClient(server);
        var service = new CatalogVersionService(http, "test-key", cache, NullLogger<CatalogVersionService>.Instance);
        try
        {
            check((await service.GetVersionsAsync(1, default))[0] == "snapshot", "Modrinth uses its version tags");
            var restartedService = new CatalogVersionService(http, "test-key", cache, NullLogger<CatalogVersionService>.Instance);
            await restartedService.GetVersionsAsync(1, default);
            check(server.Requests.Count == 1, "version metadata is served from the shared disk cache");
            await service.GetVersionsAsync(1, default, refresh: true);
            check(server.Requests.Count == 2, "explicit refresh bypasses version metadata cache");
            check((await service.GetVersionsAsync(0, default))[0] == "cf-new" && server.Keys.Last() == "test-key", "CurseForge version request uses its existing API key");
            check(new DirectoryInfo(folder).GetFiles().Sum(f => f.Length) <= 5 * 1024 * 1024, "version metadata shares the catalog size budget");
            await cache.ClearAsync();
            server.FailProvider = true;
            check((await service.GetVersionsAsync(1, default))[0] == "future-snapshot", "unavailable provider falls back to Mojang");
            check(server.Keys.Last() == null, "Mojang fallback never receives the CurseForge API key");
            int requests = server.Requests.Count;
            await service.GetVersionsAsync(1, default);
            check(server.Requests.Count == requests + 1, "fallback manifest is cached while failed provider responses are not");
            await cache.ClearAsync();
            server.FailProvider = false;
            server.EmptyProvider = true;
            check((await service.GetVersionsAsync(1, default))[0] == "future-snapshot", "empty provider list also triggers Mojang fallback");
            enabled = false;
            await cache.TrimAsync();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            requests = server.Requests.Count;
            try { await service.GetVersionsAsync(1, cancelled.Token); throw new Exception("Cancellation was ignored"); }
            catch (OperationCanceledException) { }
            check(server.Requests.Count == requests, "cancelled version request does not start a fallback");

            server.EmptyProvider = false;
            server.DelayModrinth = true;
            App.Set(cache);
            App.Set(service);
            App.Set<ILogger<ResourceDefaultViewModel>>(NullLogger<ResourceDefaultViewModel>.Instance);
            var notices = new Notices();
            App.Set<INotificationService>(notices);
            var vm = new PaginationTests.TestCatalog(http, new SearchProviderService()) { ResourceSource = 1 };
            vm.Start();
            await Wait(() => server.ModrinthStarted);
            vm.FilteredVersion = "manual-version";
            // Simulate an editable ComboBox clearing its text when ItemsSource is replaced.
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.MinecraftVersions)) vm.FilteredVersion = ""; };
            vm.ResourceSource = 0;
            await Wait(() => vm.MinecraftVersions.Contains("cf-new"));
            check(vm.FilteredVersion == "manual-version" && vm.MinecraftVersions[0] == LocalizedStrings.ResourceCategories__All,
                "updating the dropdown preserves manual input and the All option");
            server.PendingModrinth.SetResult(true);
            await Task.Delay(30);
            check(vm.MinecraftVersions.Contains("cf-new") && !vm.MinecraftVersions.Contains("snapshot"), "late version response cannot overwrite a newly selected provider");
            server.FailAll = true;
            vm.ResourceSource = 1;
            await Wait(() => notices.Items.Count > 0);
            check(vm.FilteredVersion == "manual-version" && notices.Items.Last().Title == LocalizedStrings.Catalog__VersionsUnavailable,
                "total version-source failure preserves manual input and shows a warning");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    public static async Task RunLive(Action<bool, string> check)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string text = await File.ReadAllTextAsync(Path.Combine(root, "Natsurainko.FluentLauncher/Utils/Extensions/DependencyInjectionExtensions.cs"));
        string key = Regex.Match(text, "CurseForgeClientApiKey = \"([^\"]+)\"").Groups[1].Value;
        string folder = Path.Combine(Path.GetTempPath(), "fluent-live-versions-" + Guid.NewGuid().ToString("N"));
        var cache = new CatalogPageCache(folder, () => new(false, 30, 5), _ => { });
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FluentLauncher-version-check/1.0");
        var service = new CatalogVersionService(http, key, cache, NullLogger<CatalogVersionService>.Instance);
        try
        {
            foreach (int source in new[] { 0, 1 })
            {
                var versions = await service.GetVersionsAsync(source, default);
                check(versions.Length > 50 && versions.Distinct().Count() == versions.Length,
                    $"live {(source == 0 ? "CurseForge" : "Modrinth")} versions: {versions.Length}, newest {versions[0]}");
            }
            byte[] manifest = await http.GetByteArrayAsync(CatalogVersionService.MojangUrl);
            var fallback = CatalogVersionService.ParseVersions(manifest, CatalogVersionSource.Mojang);
            check(fallback.Length > 100, $"live Mojang fallback: {fallback.Length} versions, newest {fallback[0]}");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    sealed class Notices : INotificationService
    {
        public List<DefaultNotification> Items { get; } = [];
        public void Show<T>(DefaultNotification notification) => Items.Add(notification);
    }
    sealed class VersionServer : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public List<string?> Keys { get; } = [];
        public bool FailProvider, FailAll, EmptyProvider, DelayModrinth, ModrinthStarted;
        public TaskCompletionSource<bool> PendingModrinth { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/search"))
                return Response(uri.Host == "api.curseforge.com" ? "{\"data\":[]}" : "{\"hits\":[]}");
            Requests.Add(uri);
            Keys.Add(request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null);
            if (FailAll || (FailProvider && uri.Host != "piston-meta.mojang.com"))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (uri.Host == "api.modrinth.com" && DelayModrinth)
            {
                ModrinthStarted = true;
                await PendingModrinth.Task;
            }
            return Response(uri.Host switch
            {
                "api.modrinth.com" => EmptyProvider ? "[]" : Modrinth,
                "api.curseforge.com" => CurseForge,
                _ => Mojang
            });
        }
        static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
