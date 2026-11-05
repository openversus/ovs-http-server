using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Client;

/// <summary>
/// POST /ovs/quit: the OpenVersus client says the game asked to exit (Alt+F4, the window's close button, the menu's
/// quit), just before it does. The player is the identify token's (x-hydra-access-token, as /ovs/notifications takes
/// it). <see cref="IQuitTracker"/> decides whether it was a walk-out of a matchmade match and answers {counted, reason,
/// count, lockedUntil}; a client nobody can place is answered {counted: false}.
/// Server only (2026.11.05).
/// </summary>
public sealed class PostOvsQuit : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs/quit");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var answer = new JsonObject { ["counted"] = false, ["reason"] = "unknown player" };
        try
        {
            var claims = IdentifyTokens.Verify(HttpContext.Request.Headers[HydraToken.Header].ToString(), Resolve<IOptionsMonitor<AccessSettings>>().CurrentValue.IdentifySecret, DateTimeOffset.UtcNow);
            if (claims is not null && TryResolve<IConnectionMultiplexer>() is not null && TryResolve<IQuitTracker>() is { } quits
                && await Resolve<IAccountResolver>().ResolveAsync(AccountLookups.From(HttpContext, claims)) is { } player)
            {
                var verdict = await quits.ReportAsync(player.Id, DateTimeOffset.UtcNow);
                answer = new JsonObject
                {
                    ["counted"] = verdict.Counted,
                    ["reason"] = verdict.Reason,
                    ["count"] = verdict.Count,
                    ["lockedUntil"] = verdict.LockedUntil is { } until ? until.ToUnixTimeMilliseconds() : null,
                };
            }
            else
            {
                Logger.LogInformation("A quit was reported from {Ip} by a client nobody can place; ignored", WebAccounts.Ip(HttpContext));
            }
        }
        catch (Exception e) when (e is RedisException or TimeoutException or InvalidOperationException)
        {
            Logger.LogError(e, "Error in /ovs/quit");
        }

        await Send.StringAsync(Js.Stringify(answer), contentType: "application/json; charset=utf-8", cancellation: ct);
    }
}
