using System.Net;
using System.Text.Json;
using System.Web;
using FluentLauncher.Infra.UI.Navigation;
using FluentLauncher.Infra.UI.Notification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Natsurainko.FluentLauncher;
using Natsurainko.FluentLauncher.Services.Network;
using Natsurainko.FluentLauncher.Services.Settings;
using Natsurainko.FluentLauncher.Services.UI;
using Natsurainko.FluentLauncher.Services.UI.Notification;
using Natsurainko.FluentLauncher.ViewModels.Downloads;
using Nrk.FluentCore.Resources;

internal static class PaginationTests
{
    public static async Task Run(Action<bool, string> check, CatalogPageCache cache)
    {
        var notices = new Notices();
        App.Set<INotificationService>(notices);
        App.Set<ILogger<ResourceDefaultViewModel>>(NullLogger<ResourceDefaultViewModel>.Instance);
        App.Set(cache);
        App.Set(new CatalogNavigationState());
        var server = new CatalogServer();
        using var http = new HttpClient(server);
        App.Set(new CatalogVersionService(http, "test", cache, NullLogger<CatalogVersionService>.Instance));
        var search = new SearchProviderService();
        var navigation = new Navigation();
        var vm = new TestCatalog(http, search, navigation) { ResourceSource = 1 };
        vm.Start();
        await Wait(() => !vm.Loading);
        check(vm.CurrentPage == 1 && vm.SearchResult?.Count == 50 && vm.HasNextPage, "full first page enables Next");
        check(!vm.ShowEmptyResults, "populated catalog does not show the empty-results overlay");
        object resource = vm.SearchResult![0];
        vm.ResourceItemInvokeCommand.Execute(resource);
        check(navigation.Count == 1, "ready resource card opens its detail page");
        await vm.NextPageCommand.ExecuteAsync(null);
        check(vm.CurrentPage == 2 && vm.SearchResult?.Count == 50 && vm.HasNextPage, "second page uses offset and enables probing for more");
        object firstOnLastPage = vm.SearchResult![0];
        await vm.NextPageCommand.ExecuteAsync(null);
        check(vm.CurrentPage == 2 && ReferenceEquals(firstOnLastPage, vm.SearchResult![0]) && !vm.HasNextPage,
            "empty next page keeps the last populated page and disables Next");
        check(notices.Items.Count == 1 && notices.Items[0].Title == LocalizedStrings.Catalog__LastPage
            && notices.Items[0].Delay == 5, "empty next page shows the timed last-page InfoBar");
        await vm.PreviousPageCommand.ExecuteAsync(null);
        await vm.NextPageCommand.ExecuteAsync(null);
        check(vm.CurrentPage == 2 && !vm.HasNextPage, "known end is retained when navigating backwards and forwards");

        search.Submit!("partial");
        await Wait(() => !vm.Loading);
        check(vm.CurrentPage == 1 && vm.HasNextPage, "new query resets page and end marker");
        await vm.NextPageCommand.ExecuteAsync(null);
        check(vm.SearchResult?.Count == 25 && !vm.HasNextPage, "short page ends pagination immediately");
        vm.SelectedCategory = "optimization";
        await Wait(() => !vm.Loading);
        check(vm.CurrentPage == 1, "category change resets the page");
        search.Submit!("empty");
        await Wait(() => !vm.Loading);
        check(vm.CurrentPage == 1 && vm.SearchResult?.Count == 0 && !vm.HasNextPage && notices.Items.Count == 1,
            "empty first page does not show a misleading last-page notice");
        check(vm.ShowEmptyResults, "completed empty search shows the empty-results overlay");

        search.Submit!("old");
        await Wait(() => server.Pending.ContainsKey("old"));
        check(!vm.ShowEmptyResults, "starting a search hides the empty-results overlay before results arrive");
        check(vm.ResourceItemInvokeCommand.CanExecute(resource), "loading does not switch cards into their disabled visual state");
        vm.ResourceItemInvokeCommand.Execute(resource);
        check(navigation.Count == 1, "busy cards ignore activation without opening stale results");
        search.Submit!("new");
        await Wait(() => server.Pending.ContainsKey("new"));
        check(vm.Loading && !vm.NextPageCommand.CanExecute(null) && !vm.RefreshCommand.CanExecute(null), "page controls are disabled during loading");
        server.Pending["old"].SetResult(true);
        await Task.Delay(30);
        check(vm.Loading && vm.SearchQuery == "new", "cancelled request cannot clear newer request loading state");
        server.Pending["new"].SetResult(true);
        await Wait(() => !vm.Loading);
        check(vm.SearchQuery == "new" && vm.SearchResult?.Count == 3, "latest request owns the results");
        search.Submit!("away");
        await Wait(() => server.Pending.ContainsKey("away"));
        ((INavigationAware)vm).OnNavigatedFrom();
        vm.IsActive = false;
        server.Pending["away"].SetResult(true);
        await Task.Delay(30);
        check(!vm.Loading && vm.SearchResult?.Count == 0, "leaving the page prevents late results from being applied");
    }

    static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    internal sealed partial class TestCatalog(HttpClient http, SearchProviderService search, INavigationService? navigation = null) : ResourceDefaultViewModel(
        new CurseForgeClient("test", http), new ModrinthClient(http), new SettingsService(), navigation ?? new Navigation(), search)
    {
        protected override CurseForgeResourceType CurseForgeResourceType => CurseForgeResourceType.McMod;
        protected override ModrinthResourceType ModrinthResourceType => ModrinthResourceType.McMod;
        protected override string[] ModrinthCategories => ["all", "optimization"];
        protected override Dictionary<string, int> CurseForgeCategories => new() { ["All"] = 0 };
        public void Start(string? query = null, string route = "Test/Default", bool history = false)
        {
            App.GetService<CatalogNavigationState>().BeginCatalogNavigation(history);
            ((INavigationAware)this).SetNavigationKey(route);
            ((INavigationAware)this).OnNavigatedTo(query);
            IsActive = true;
            OnLoaded();
        }
    }
    sealed class Navigation : INavigationService
    {
        public int Count { get; private set; }
        public void NavigateTo(string key, object parameter) => Count++;
    }
    sealed class Notices : INotificationService
    {
        public List<DefaultNotification> Items { get; } = [];
        public void Show<T>(DefaultNotification notification) => Items.Add(notification);
    }
    sealed class CatalogServer : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<bool>> Pending { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/v2/tag/game_version")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[{\"version\":\"test-version\",\"date\":\"2026-01-01T00:00:00Z\"}]")
                };
            var parameters = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            string query = parameters["query"] ?? "";
            if (query is "old" or "new" or "away")
            {
                var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Pending[query] = pending;
                await pending.Task; // Deliberately ignore cancellation to exercise late-response protection.
            }
            int total = query switch { "partial" => 75, "empty" => 0, "new" => 3, _ => 100 };
            int offset = int.Parse(parameters["offset"] ?? "0");
            int count = Math.Max(0, Math.Min(50, total - offset));
            var hits = Enumerable.Range(offset, count).Select(i => new
            {
                project_id = $"id{i}", slug = $"mod{i}", project_type = "mod", title = $"Mod {i}",
                description = "Test", downloads = 1, date_modified = "2026-01-01T00:00:00Z", author = "test",
                versions = new[] { "1.21.1" }, display_categories = Array.Empty<string>(), gallery = Array.Empty<string>(), icon_url = ""
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { hits })) };
        }
    }
}
