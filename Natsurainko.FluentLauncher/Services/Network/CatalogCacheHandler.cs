using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Natsurainko.FluentLauncher.Services.Network;

/// <summary>Caches only catalog search JSON; downloads, images and authentication are never cached.</summary>
internal sealed class CatalogCacheHandler(CatalogPageCache cache, ILogger<CatalogCacheHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri? uri = request.RequestUri;
        bool modrinth = uri?.Host == "api.modrinth.com" && uri.AbsolutePath == "/v2/search";
        bool curseforge = uri?.Host == "api.curseforge.com" && uri.AbsolutePath == "/v1/mods/search";
        if (request.Method != HttpMethod.Get || !(modrinth || curseforge))
            return await base.SendAsync(request, cancellationToken);

        string key = uri!.AbsoluteUri;
        long generation = cache.Generation;
        byte[]? cached = await cache.ReadAsync(key, cancellationToken);
        if (cached != null && IsSearchResponse(cached, modrinth))
        {
            logger.LogInformation("Catalog cache hit: {Uri}", uri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = CreateContent(cached)
            };
        }

        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (IsSearchResponse(bytes, modrinth))
                    await cache.WriteAsync(key, bytes, generation, cancellationToken);
            }
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static ByteArrayContent CreateContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static bool IsSearchResponse(byte[] bytes, bool modrinth)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty(modrinth ? "hits" : "data", out var items)
                && items.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }
}
