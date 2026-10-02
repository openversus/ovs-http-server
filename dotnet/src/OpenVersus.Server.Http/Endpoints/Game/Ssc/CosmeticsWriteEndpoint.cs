using System.Text.Json.Nodes;
using MongoDB.Driver;
using OpenVersus.Server.Core.Cosmetics;
using OpenVersus.Server.Http.Shared.Endpoints;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// The cosmetics writes (<see cref="ICosmeticsService"/>) for the token's player: the SSC envelope around what
/// <see cref="Answer"/> returns, or {} (status 200) when the write fails, as the TS handlers answer from their catch. The
/// stores being unreachable is such a failure there too (mongoose gives up after its buffering timeout).
/// </summary>
public abstract class CosmeticsWriteEndpoint : JsonBodyEndpoint
{
    /// <summary>The answer's body, or null for {}.</summary>
    protected abstract Task<JsonNode?> Answer(ICosmeticsService cosmetics, string accountId, JsonObject body, CancellationToken ct);

    public override async Task HandleAsync(CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct) as JsonObject ?? [];
        JsonNode? answer;
        try
        {
            answer = await Answer(Resolve<ICosmeticsService>(), HttpContext.Session()?.AccountId ?? "", body, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or MongoException or RedisException or TimeoutException)
        {
            Logger.LogError("{Route}: {Error}", HttpContext.Request.Path, e.Message);
            answer = null;
        }

        await SendJsonAsync(answer is null ? new JsonObject() : new JsonObject { ["body"] = answer, ["metadata"] = null, ["return_code"] = 0 }, ct);
    }

    /// <summary>{ field: the body's value } (no key when the body has none).</summary>
    protected static JsonObject Echo(string field, JsonObject body, string from) =>
        body.TryGetPropertyValue(from, out var value) ? new JsonObject { [field] = value?.DeepClone() } : [];
}
