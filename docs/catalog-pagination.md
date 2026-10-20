# Catalog pagination and cache

CurseForge and Modrinth catalogs load 50 resources per page. Search, category, version and provider changes reset the catalog to page 1. Previous/Next controls are disabled while loading. Refresh requests the current page from the provider and replaces its cached response.

A page with fewer than 50 results ends pagination. If a full final page is followed by an empty response, the populated page stays visible, Next is disabled and an informational InfoBar appears for five seconds. CurseForge's 10,000-result API window is respected. Search errors are logged and shown in an error InfoBar without discarding an already displayed page. Cancelled or superseded searches cannot update the current results.

## Cache settings

Settings → Downloads → Catalog cache follows Download Behavior.

- Enabled by default, with a 30-minute lifetime and a shared 5 MiB limit.
- Lifetime can be set from 5 to 120 minutes; size from 1 to 50 MiB.
- Only successful search JSON responses from `/v1/mods/search` on CurseForge and `/v2/search` on Modrinth are cached. Authentication, images and resource downloads are excluded.
- The full request URL is hashed into the cache key, including provider, query, filters, ordering, page size and offset.
- Entries are stored in the application's `LocalCache/catalog-pages-v1` directory. Modification time controls expiration; access time controls LRU eviction. Expired entries are removed before LRU eviction. Refresh does not extend the lifetime of other pages.
- Decreasing the size/lifetime trims existing entries. Disabling caching deletes stored pages. Clear cache also prevents requests already in flight from repopulating the cache.
- Cache I/O failures are logged; network searches continue without the cache. HTTP errors and malformed search envelopes are not cached.

## Validation

Use Visual Studio 2026 / .NET SDK 10 for the current C# 14 source generators; the application still targets .NET 9. Open the solution with Debug, x64 and the Package launch profile. Close an already running launcher before testing another channel: the existing single-instance activation key is shared between channels.

Run the deterministic regression checks from the repository root:

```powershell
dotnet run --project tests/Catalog.Tests/Catalog.Tests.csproj
```

The executable tests compile the production cache, HTTP handler and catalog view model. Minimal headless UI adapters replace Windows boundaries, while actual FluentCore clients construct and parse requests. They cover disk persistence, TTL, LRU, limits, clearing, refresh, response validation, pagination boundaries, timed notification configuration, filter resets and overlapping/cancelled searches.

For UI verification, check both providers, navigate forwards/backwards, change filters, refresh, and inspect the cache section in a narrow and a wide window. Confirm a short final page disables Next; a full final page followed by an empty response must retain its contents and show the timed InfoBar. Cache hits are logged as `Catalog cache hit`; page counts are logged separately.

## Publishing the branch

The main repository references local feature commits in the FluentCore and Localization submodules. Before sharing the branch, push both submodule branches to their respective forks, update their URLs in `.gitmodules` to those forks, commit that URL change, then push the main repository branch. Do not run `git submodule update --remote`, which would replace the pinned feature revisions.
