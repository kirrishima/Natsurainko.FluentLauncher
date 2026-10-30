using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Natsurainko.FluentLauncher.Services.Network;
using Nrk.FluentCore.Resources;

string folder = Path.Combine(Path.GetTempPath(), "fluent-catalog-tests-" + Guid.NewGuid().ToString("N"));
var options = new CatalogCacheOptions(true, 30, 1);
var errors = new List<Exception>();
var cache = NewCache();
CatalogPageCache NewCache() => new(folder, () => options, errors.Add);
string FileFor(string key) => Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
async Task Put(string key, byte[] value) => await cache.WriteAsync(key, value, cache.Generation, default);
try
{
    Check(await cache.ReadAsync("missing", default) == null, "empty cache is a miss");
    await Put("page1", [1, 2, 3]);
    cache = NewCache();
    Check((await cache.ReadAsync("page1", default))!.SequenceEqual(new byte[] { 1, 2, 3 }), "cache survives restart");
    File.SetLastWriteTimeUtc(FileFor("page1"), DateTime.UtcNow.AddMinutes(-31));
    Check(await cache.ReadAsync("page1", default) == null, "TTL expires independently of last access");

    await Put("a", new byte[350_000]);
    await Put("b", new byte[350_000]);
    File.SetLastAccessTimeUtc(FileFor("a"), DateTime.UtcNow.AddMinutes(-2));
    File.SetLastAccessTimeUtc(FileFor("b"), DateTime.UtcNow.AddMinutes(-1));
    await Put("c", new byte[350_000]);
    Check(!File.Exists(FileFor("a")) && File.Exists(FileFor("b")) && File.Exists(FileFor("c")), "LRU evicts oldest page");
    Check(new DirectoryInfo(folder).GetFiles().Sum(f => f.Length) <= 1024 * 1024, "combined disk budget is enforced");
    await Put("oversize", new byte[1_048_577]);
    Check(!File.Exists(FileFor("oversize")), "oversized response is not cached");

    long beforeClear = cache.Generation;
    await cache.ClearAsync();
    await cache.WriteAsync("inflight", [1], beforeClear, default);
    Check(!Directory.EnumerateFiles(folder).Any(), "clear prevents in-flight request from repopulating cache");
    options = options with { MaxSizeMb = 2 };
    await Put("large", new byte[1_200_000]);
    options = options with { MaxSizeMb = 1 };
    await cache.TrimAsync();
    Check(!File.Exists(FileFor("large")), "reducing size evicts existing pages immediately");

    await Put("disabled", [1]);
    options = options with { Enabled = false };
    await cache.TrimAsync();
    await Put("disabled", [2]);
    Check(await cache.ReadAsync("disabled", default) == null && !Directory.EnumerateFiles(folder).Any(), "disabled cache is cleared and bypassed");
    options = options with { Enabled = true };

    var transport = new FakeTransport();
    using var http = new HttpClient(new CatalogCacheHandler(cache, NullLogger<CatalogCacheHandler>.Instance) { InnerHandler = transport });
    const string search = "https://api.modrinth.com/v2/search?limit=50&offset=0";
    await http.GetStringAsync(search);
    await http.GetStringAsync(search);
    Check(transport.Requests.Count == 1, "second search is served from disk");
    await http.GetStringAsync(search.Replace("offset=0", "offset=50"));
    Check(transport.Requests.Count == 2, "pages have separate cache keys");
    await cache.RefreshAsync(() => http.GetStringAsync(search));
    Check(transport.Requests.Count == 3 && !cache.BypassRead, "refresh bypasses cache and restores normal caching");
    await http.GetStringAsync("https://api.modrinth.com/v2/project/example");
    await http.GetStringAsync("https://api.modrinth.com/v2/project/example");
    Check(transport.Requests.Count == 5, "non-search requests are never cached");
    transport.Status = HttpStatusCode.TooManyRequests;
    using var error1 = await http.GetAsync(search + "&query=error");
    using var error2 = await http.GetAsync(search + "&query=error");
    Check(transport.Requests.Count == 7, "HTTP errors are never cached");
    transport.Status = HttpStatusCode.OK;
    transport.Body = "{\"error\":\"unavailable\"}";
    await http.GetStringAsync(search + "&query=invalid");
    await http.GetStringAsync(search + "&query=invalid");
    Check(transport.Requests.Count == 9, "invalid search bodies are never cached");
    File.WriteAllText(FileFor(search), "broken json");
    transport.Body = "{\"hits\":[]}";
    await http.GetStringAsync(search);
    Check(transport.Requests.Count == 10, "corrupt cached JSON falls back to network");

    // Exercise actual core clients with a fake HTTP server, including reserved characters.
    var modrinth = new ModrinthClient(http);
    await modrinth.SearchResourcesAsync("a&b #c", ModrinthResourceType.McMod, categories: "optimization", offset: 50);
    var query = HttpUtility.ParseQueryString(transport.Requests.Last().Query);
    Check(query["query"] == "a&b #c" && query["limit"] == "50" && query["offset"] == "50", "Modrinth page and query parameters are encoded correctly");
    Check(query["facets"] == "[[\"project_type:mod\"],[\"categories:optimization\"]]", "Modrinth categories use valid facets");
    transport.Body = "{\"data\":[]}";
    var curseforge = new CurseForgeClient("test-key", http);
    await curseforge.SearchResourcesAsync("test", CurseForgeResourceType.McMod, offset: 50);
    query = HttpUtility.ParseQueryString(transport.Requests.Last().Query);
    Check(query["pageSize"] == "50" && query["index"] == "50" && query["categoryId"] == null, "CurseForge paging preserves the all-categories fix");
    Check(errors.Count == 0, "cache produced no unexpected I/O errors");
    await PaginationTests.Run(Check, cache);
    await VersionTests.Run(Check);
    await NavigationTests.Run(Check);
    LocalizationTests.Run(Check);
    if (args.Contains("--live-versions")) await VersionTests.RunLive(Check);
}
finally
{
    if (Directory.Exists(folder)) Directory.Delete(folder, true);
}

sealed class FakeTransport : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Body { get; set; } = "{\"hits\":[]}";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body), RequestMessage = request });
    }
}
