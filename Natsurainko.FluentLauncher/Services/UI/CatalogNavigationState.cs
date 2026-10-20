using System.Collections.Generic;

namespace Natsurainko.FluentLauncher.Services.UI;

// Navigation metadata only: resource responses remain in the bounded catalog cache.
internal sealed record CatalogSearchState(
    int Source, string Query, string Category, string Version, int Page, int? LastPage,
    double ScrollOffset, bool SlugMode, string? EntryQuery);

internal sealed class CatalogNavigationState
{
    private readonly Dictionary<string, CatalogSearchState> _latest = [];
    private readonly Dictionary<(string Route, int Source), CatalogSearchState> _providers = [];

    public void Save(string route, CatalogSearchState state)
    {
        _latest[route] = state;
        _providers[(route, state.Source)] = state;
    }

    public CatalogSearchState? Find(string route) => _latest.GetValueOrDefault(route);
    public CatalogSearchState? Find(string route, int source) => _providers.GetValueOrDefault((route, source));
}
