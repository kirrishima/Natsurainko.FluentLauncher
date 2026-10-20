using System.Net;
using System.Text.Json;
using System.Web;
using FluentLauncher.Infra.UI.Navigation;
using FluentLauncher.Infra.UI.Notification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Natsurainko.FluentLauncher;
using Natsurainko.FluentLauncher.Services.Network;
using Natsurainko.FluentLauncher.Services.UI;
using Natsurainko.FluentLauncher.Services.UI.Notification;
using Natsurainko.FluentLauncher.ViewModels.Downloads;

internal static class NavigationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "fluent-navigation-tests-" + Guid.NewGuid().ToString("N"));
        var cache = new CatalogPageCache(folder, () => new(true, 30, 5), ex => throw ex);
        var server = new CatalogServer();
        using var http = new HttpClient(new CatalogCacheHandler(cache, NullLogger<CatalogCacheHandler>.Instance) { InnerHandler = server });
        App.Set(cache);
        App.Set(new CatalogNavigationState());
        App.Set(new CatalogVersionService(http, "test", cache, NullLogger<CatalogVersionService>.Instance));
        App.Set<ILogger<ResourceDefaultViewModel>>(NullLogger<ResourceDefaultViewModel>.Instance);
        App.Set<INotificationService>(new Notices());
        try
        {
            var search = new SearchProviderService();
            var vm = new PaginationTests.TestCatalog(http, search) { ResourceSource = 1 };
            vm.Start("initial-query", "Mods/Default");
            await Wait(() => !vm.Loading);
            search.Submit!("edited-query");
            await Wait(() => !vm.Loading);
            vm.SelectedCategory = "optimization";
            await Wait(() => !vm.Loading);
            vm.FilteredVersion = "manual-version";
            await Wait(() => !vm.Loading);
            await vm.NextPageCommand.ExecuteAsync(null);
            vm.ScrollOffset = 350;
            int networkCalls = server.ModrinthSearches;
            Leave(vm);

            // The navigation framework creates a new VM and replays the original route parameter.
            var restored = new PaginationTests.TestCatalog(http, search);
            restored.Start("initial-query", "Mods/Default");
            await Wait(() => !restored.Loading);
            check(restored.ResourceSource == 1 && restored.SearchQuery == "edited-query" && search.Input == "edited-query",
                "Back restores the edited search and search box instead of replaying the initial query");
            check(restored.SelectedCategory == "optimization" && restored.FilteredVersion == "manual-version" && restored.CurrentPage == 2,
                "Back restores provider filters and page number in a newly created view model");
            check(restored.ScrollOffset == 350 && restored.SearchResult?.Count == 25 && !restored.HasNextPage,
                "Back restores scroll position and the final page state");
            check(server.ModrinthSearches == networkCalls, "restored results are loaded from the bounded disk cache without a network search");

            restored.ResourceSource = 0;
            await Wait(() => !restored.Loading);
            restored.ResourceSource = 1;
            await Wait(() => !restored.Loading);
            check(restored.CurrentPage == 2 && restored.SelectedCategory == "optimization" && restored.ScrollOffset == 350,
                "switching providers retains each provider's page, category and scroll position");
            check(server.ModrinthSearches == networkCalls, "switching back to the provider reuses the cached page");
            Leave(restored);

            var otherCatalog = new PaginationTests.TestCatalog(http, new SearchProviderService());
            otherCatalog.Start(null, "Shaders/Default");
            await Wait(() => !otherCatalog.Loading);
            check(otherCatalog.ResourceSource == 0 && otherCatalog.SearchQuery == "" && otherCatalog.CurrentPage == 1,
                "different catalog routes do not share search state");
            Leave(otherCatalog);

            var newSearch = new PaginationTests.TestCatalog(http, search);
            newSearch.Start("brand-new-query", "Mods/Default");
            await Wait(() => !newSearch.Loading);
            check(newSearch.SearchQuery == "brand-new-query" && newSearch.CurrentPage == 1 && newSearch.ScrollOffset == 0,
                "an explicit new navigation query starts on page one");
            Leave(newSearch);

            var perfVm = new PaginationTests.TestCatalog(http, new SearchProviderService()) { ResourceSource = 1 };
            int uiThreadId = 0;
            var uiThread = new Thread(() =>
            {
                uiThreadId = Environment.CurrentManagedThreadId;
                perfVm.Start("thread-check", "Performance/Default");
            }) { IsBackground = true };
            uiThread.Start();
            check(uiThread.Join(TimeSpan.FromSeconds(5)), "catalog activation returns without waiting for response processing");
            await Wait(() => !perfVm.Loading && server.LastSearchThread != 0);
            check(server.LastSearchThread != uiThreadId, "HTTP cache access and resource parsing start off the UI thread");
            Leave(perfVm);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    static void Leave(ResourceDefaultViewModel vm)
    {
        ((INavigationAware)vm).OnNavigatedFrom();
        vm.IsActive = false;
    }
    static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    sealed class Notices : INotificationService { public void Show<T>(DefaultNotification notification) { } }
    sealed class CatalogServer : HttpMessageHandler
    {
        public int ModrinthSearches;
        public int LastSearchThread;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri uri = request.RequestUri!;
            string body;
            if (uri.AbsolutePath == "/v2/tag/game_version")
                body = "[{\"version\":\"test-version\",\"date\":\"2026-01-01T00:00:00Z\"}]";
            else if (uri.AbsolutePath == "/v1/minecraft/version")
                body = "{\"data\":[{\"versionString\":\"test-version\",\"dateModified\":\"2026-01-01T00:00:00Z\"}]}";
            else if (uri.Host == "api.curseforge.com") body = "{\"data\":[]}";
            else
            {
                Interlocked.Increment(ref ModrinthSearches);
                LastSearchThread = Environment.CurrentManagedThreadId;
                var query = HttpUtility.ParseQueryString(uri.Query);
                int offset = int.Parse(query["offset"] ?? "0");
                var hits = Enumerable.Range(offset, Math.Max(0, Math.Min(50, 75 - offset))).Select(i => new
                {
                    project_id = $"id{i}", slug = $"mod{i}", project_type = "mod", title = $"Mod {i}",
                    description = "Test", downloads = 1, date_modified = "2026-01-01T00:00:00Z", author = "test",
                    versions = new[] { "test-version" }, display_categories = Array.Empty<string>(), gallery = Array.Empty<string>(), icon_url = ""
                });
                body = JsonSerializer.Serialize(new { hits });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
