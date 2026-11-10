using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Hosting;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Client;

/// <summary>
/// POST /ovs/netstats {matchId, delay, ping, depth, final?}: the OpenVersus client's live figures for the match it is
/// playing (5 s into it, then every 10 s; a last one marked final when it ends), shown on the live matches page
/// (<see cref="LiveNetStats"/>). The player is the identify token's (x-hydra-access-token, as /ovs/quit takes it), and
/// must be a player of that match. Answers {ok}: true also for a match the page does not list (a custom lobby's),
/// whose report is taken and left at that; false with the reason for one that is refused, which is logged; a client
/// nobody can place is answered {ok: false}.
/// Server only (2026.11.10).
/// </summary>
public sealed class PostOvsNetStats : EndpointWithoutRequest
{
    /// <summary>The largest body read: a report is under a hundred bytes.</summary>
    public const int MaxBodyBytes = 1024;

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs/netstats");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string? refused = "unknown player";
        try
        {
            var claims = IdentifyTokens.Verify(HttpContext.Request.Headers[HydraToken.Header].ToString(), Resolve<IOptionsMonitor<AccessSettings>>().CurrentValue.IdentifySecret, DateTimeOffset.UtcNow);
            if (claims is not null && TryResolve<IConnectionMultiplexer>() is { } redis
                && await Resolve<IAccountResolver>().ResolveAsync(AccountLookups.From(HttpContext, claims)) is { } player)
            {
                refused = HttpContext.Request.ContentLength is > MaxBodyBytes ? "too large"
                    : (await LiveNetStats.ReportAsync(redis.GetDatabase(), player.Id, await BodyAsync(ct), DateTimeOffset.UtcNow)).Refused;
                if (refused is not null)
                {
                    Logger.LogInformation("Live figures from {Player} not kept: {Reason}", player.Id, refused);
                }
            }
        }
        catch (Exception e) when (e is RedisException or TimeoutException or InvalidOperationException)
        {
            Logger.LogError(e, "Error in /ovs/netstats");
            refused = "error";
        }

        var answer = new JsonObject { ["ok"] = refused is null };
        if (refused is not null)
        {
            answer["reason"] = refused;
        }

        await Send.StringAsync(answer.ToJsonString(), contentType: "application/json; charset=utf-8", cancellation: ct);
    }

    // The body as a JSON object, read up to MaxBodyBytes; null for anything else.
    private async Task<JsonObject?> BodyAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[MaxBodyBytes + 1];
        int read = 0;
        while (read < buffer.Length && await HttpContext.Request.Body.ReadAsync(buffer.AsMemory(read), ct) is > 0 and var n)
        {
            read += n;
        }

        if (read > MaxBodyBytes)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(buffer.AsSpan(0, read)) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
