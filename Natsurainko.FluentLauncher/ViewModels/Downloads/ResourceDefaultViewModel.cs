using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentLauncher.Infra.UI.Navigation;
using FluentLauncher.Infra.UI.Notification;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Natsurainko.FluentLauncher.Services.Network;
using Natsurainko.FluentLauncher.Services.UI.Notification;
using Natsurainko.FluentLauncher.Services.Settings;
using Natsurainko.FluentLauncher.Services.UI;
using Natsurainko.FluentLauncher.Utils;
using Nrk.FluentCore.Resources;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using static Natsurainko.FluentLauncher.Services.UI.SearchProviderService;

namespace Natsurainko.FluentLauncher.ViewModels.Downloads;

internal abstract partial class ResourceDefaultViewModel(
    CurseForgeClient curseForgeClient,
    ModrinthClient modrinthClient,
    SettingsService settingsService,
    INavigationService navigationService,
    SearchProviderService searchProviderService) : PageVM, INavigationAware
{
    private string? _pageKey;
    private CancellationTokenSource? _cancellationTokenSource;
    private CancellationTokenSource? _versionListCancellationTokenSource;
    private bool _updatingVersionList;
    private BindedSearchProvider? _bindedSearchProvider;
    private bool depressCategoryChangedInvokeSearch = true;
    private const int PageSize = 50;
    private int _searchGeneration;
    private int? _lastPage;
    private bool _slugMode;

    public event Action? PageChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageLabel))]
    public partial int CurrentPage { get; set; } = 1;

    [ObservableProperty]
    public partial bool HasNextPage { get; set; }

    public string CurrentPageLabel => string.Format(CultureInfo.CurrentCulture, LocalizedStrings.Catalog__Page, CurrentPage);

    partial void OnCurrentPageChanged(int value) => UpdatePageCommands();
    partial void OnHasNextPageChanged(bool value) => UpdatePageCommands();
    partial void OnLoadingChanged(bool value) => UpdatePageCommands();

    void UpdatePageCommands()
    {
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    bool CanPreviousPage() => !Loading && CurrentPage > 1;
    bool CanNextPage() => !Loading && HasNextPage;
    bool CanRefresh() => !Loading;

    [RelayCommand(CanExecute = nameof(CanPreviousPage))]
    Task PreviousPage() => LoadPageAsync(CurrentPage - 1);

    [RelayCommand(CanExecute = nameof(CanNextPage))]
    Task NextPage() => LoadPageAsync(CurrentPage + 1);

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    Task Refresh()
    {
        _lastPage = null;
        return Task.WhenAll(LoadPageAsync(CurrentPage, refresh: true), UpdateVersionsAsync(refresh: true));
    }

    [ObservableProperty]
    public partial ObservableCollection<object>? SearchResult { get; set; }

    [ObservableProperty]
    public partial int ResourceSource { get; set; } = 0;

    [ObservableProperty]
    public partial string[] Categories { get; set; } = null!;

    [ObservableProperty]
    public partial string SelectedCategory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilteredVersion { get; set; } = LocalizedStrings.ResourceCategories__All;

    [ObservableProperty]
    public partial string[] MinecraftVersions { get; set; } = [LocalizedStrings.ResourceCategories__All];

    [ObservableProperty]
    public partial bool Loading { get; set; }

    [ObservableProperty]
    public partial bool LoadFailed { get; set; }

    [ObservableProperty]
    public partial bool Searched { get; set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    protected abstract CurseForgeResourceType CurseForgeResourceType { get; }

    protected abstract ModrinthResourceType ModrinthResourceType { get; }

    protected abstract string[] ModrinthCategories { get; }

    protected abstract Dictionary<string, int> CurseForgeCategories { get; }

    partial void OnResourceSourceChanged(int value)
    {
        depressCategoryChangedInvokeSearch = true;

        UpdateFiltersSource();
        SearchReceiveHandle(SearchQuery);

        depressCategoryChangedInvokeSearch = false;
        if (IsActive) _ = UpdateVersionsAsync();
    }

    partial void OnSelectedCategoryChanged(string value)
    {
        if (!depressCategoryChangedInvokeSearch)
            SearchReceiveHandle(SearchQuery);
    }

    partial void OnFilteredVersionChanged(string value)
    {
        if (!_updatingVersionList) SearchReceiveHandle(SearchQuery);
    }

    void INavigationAware.SetNavigationKey(string key) => _pageKey = key;

    void INavigationAware.OnNavigatedTo(object? parameter)
    {
        UpdateFiltersSource();

        if (parameter is string query)
            SearchQuery = query;

        _bindedSearchProvider = searchProviderService.BindProvider(this);
        _bindedSearchProvider.BindQuerySubmition(query => SearchReceiveHandle(query));
        _bindedSearchProvider.BindSuggestionsSource(SuggestionProvider);
    }

    void INavigationAware.OnNavigatedFrom() 
    {
        _bindedSearchProvider?.Dispose();

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();

        _versionListCancellationTokenSource?.Cancel();
        _versionListCancellationTokenSource?.Dispose();

        ++_searchGeneration;
        Loading = false;

        GC.Collect();
    }

    protected override void OnLoaded()
    {
        SearchReceiveHandle(SearchQuery);
        depressCategoryChangedInvokeSearch = false;
        _ = UpdateVersionsAsync();
    }

    async Task UpdateVersionsAsync(bool refresh = false)
    {
        _versionListCancellationTokenSource?.Cancel();
        _versionListCancellationTokenSource?.Dispose();
        _versionListCancellationTokenSource = new();
        var token = _versionListCancellationTokenSource.Token;
        int source = ResourceSource;
        try
        {
            var versions = await App.GetService<CatalogVersionService>().GetVersionsAsync(source, token, refresh);
            if (token.IsCancellationRequested || source != ResourceSource || !IsActive) return;
            string selectedVersion = FilteredVersion;
            _updatingVersionList = true;
            try
            {
                MinecraftVersions = [LocalizedStrings.ResourceCategories__All, .. versions];
                FilteredVersion = selectedVersion;
            }
            finally { _updatingVersionList = false; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || source != ResourceSource || !IsActive) return;
            App.GetService<ILogger<ResourceDefaultViewModel>>().LogWarning(ex, "Could not update the Minecraft version filter");
            App.GetService<INotificationService>().Show<InfoBar>(new DefaultNotification
            {
                Title = LocalizedStrings.Catalog__VersionsUnavailable,
                Message = LocalizedStrings.Catalog__ManualVersion,
                Type = NotificationType.Warning,
                Delay = 8
            });
        }
    }

    [RelayCommand]
    void ResourceItemInvoke(object mod) => navigationService.NavigateTo(_pageKey!.Replace("Default", "Resource"), mod);

    [RelayCommand]
    void ClearSearchQuery()
    {
        _bindedSearchProvider?.ClearInput();
        SearchReceiveHandle(string.Empty);
    }

    async void SearchReceiveHandle(string query, bool slugMode = false)
    {
        if (!IsActive) return;

        SearchQuery = query;
        _slugMode = slugMode;
        _lastPage = null;
        CurrentPage = 1;
        HasNextPage = false;
        SearchResult = [];
        Searched = !string.IsNullOrEmpty(query);
        await LoadPageAsync(1);
    }

    async Task LoadPageAsync(int page, bool refresh = false)
    {
        if (!IsActive) return;

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = new();
        var token = _cancellationTokenSource.Token;
        int generation = ++_searchGeneration;
        int source = ResourceSource;
        string query = SearchQuery;
        string category = SelectedCategory;
        string? version = string.IsNullOrWhiteSpace(FilteredVersion)
            || FilteredVersion == LocalizedStrings.ResourceCategories__All ? null : FilteredVersion;
        bool slugMode = _slugMode;
        Loading = true;
        LoadFailed = false;

        async Task<object[]> FetchAsync()
        {
            return source switch
            {
                0 => (await curseForgeClient.SearchResourcesAsync(query, CurseForgeResourceType,
                    categoryId: CurseForgeCategories[category], version: version, slugMode: slugMode,
                    cancellationToken: token, pageSize: PageSize, offset: (page - 1) * PageSize)).ToArray(),
                1 => (await modrinthClient.SearchResourcesAsync(query, ModrinthResourceType,
                    categories: category == "all" ? null : category, version: version,
                    cancellationToken: token, pageSize: PageSize, offset: (page - 1) * PageSize)).ToArray(),
                _ => []
            };
        }

        try
        {
            object[] result = refresh
                ? await App.GetService<CatalogPageCache>().RefreshAsync(FetchAsync)
                : await FetchAsync();
            if (token.IsCancellationRequested || generation != _searchGeneration || !IsActive) return;

            if (result.Length == 0 && page > CurrentPage)
            {
                _lastPage = CurrentPage;
                HasNextPage = false;
                ShowCatalogNotice(LocalizedStrings.Catalog__LastPage);
                return;
            }

            // A refreshed page may have disappeared since the catalog was last visited.
            if (result.Length == 0 && page > 1)
            {
                ShowCatalogNotice(LocalizedStrings.Catalog__LastPage);
                await LoadPageAsync(1, refresh);
                return;
            }

            if (result.Length < PageSize) _lastPage = page;
            SearchResult = new(result);
            CurrentPage = page;
            HasNextPage = result.Length == PageSize && (!_lastPage.HasValue || page < _lastPage.Value)
                && (source != 0 || page * PageSize < 10000);
            App.GetService<ILogger<ResourceDefaultViewModel>>().LogInformation(
                "Catalog {Source} page {Page}: {Count} results", source == 0 ? "CurseForge" : "Modrinth", page, result.Length);
            PageChanged?.Invoke();

            if (slugMode && source == 0 && result.Length == 1)
                ResourceItemInvoke(result[0]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation != _searchGeneration || token.IsCancellationRequested || !IsActive) return;
            LoadFailed = true;
            App.GetService<ILogger<ResourceDefaultViewModel>>().LogError(ex,
                "Catalog {Source} page {Page} failed (category {Category}, version {Version})", source, page, category, version);
            App.GetService<INotificationService>().Show<InfoBar>(new DefaultNotification
            {
                Title = LocalizedStrings.Catalog__SearchFailed,
                Message = LocalizedStrings.Catalog__Retry,
                Type = NotificationType.Error,
                Delay = 8
            });
        }
        finally
        {
            if (generation == _searchGeneration) Loading = false;
        }
    }

    static void ShowCatalogNotice(string title) => App.GetService<INotificationService>().Show<InfoBar>(
        new DefaultNotification { Title = title, Delay = 5 });

    void UpdateFiltersSource()
    {
        Categories = ResourceSource == 0
            ? [.. CurseForgeCategories.Keys]
            : ModrinthCategories;

        if (!Categories.Contains(SelectedCategory))
            SelectedCategory = Categories[0];
    }

    private IEnumerable<Suggestion> SuggestionProvider(string query)
    {
        if (!settingsService.EnableLocalizedResourceSuggestions)
            return [];

        if (CurseForgeResourceType != CurseForgeResourceType.McMod || string.IsNullOrEmpty(query))
            return [];

        return SuggestionHelper.GetSearchModSuggestions(query, ResourceSource, slug => SearchReceiveHandle(slug, true));
    }
}

