using System.Text.Json.Nodes;
using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Identity;
using OpenVersus.Server.Core.Realtime;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Web.Site;
using StackExchange.Redis;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Client;

/// <summary>
/// GET /ovs/notifications: the OpenVersus client polls it every 2 s for what is queued for its player
/// (PlayerMessages.NotifyClientAsync: party invites, toasts, the web login code) and gets them all, as a JSON array ([]
/// when there are none, or when nothing identifies the player). TS server: GET /ovs/notifications (server.ts). The player
/// is found as everywhere else (IAccountResolver) but for the headers: the client's token (x-hydra-access-token: the one
/// /api/identify signed, checked here as the TS server's decodeToken checks it), its Steam, Epic or install id, then the
/// IP. The TS server also took x-steam-id, x-epic-id and x-install-id alone, which let anyone read any player's queue.
/// The same token
/// keeps the IP's identity record alive while the client runs (IpIdentityRefresh), and a session that logged in while the
/// record was missing is marked registered again.
/// </summary>
public sealed class GetOvsNotifications : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ovs/notifications");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var delivered = new JsonArray();
        try
        {
            string ip = WebAccounts.Ip(HttpContext);
            if (ip.Length > 0 && Resolve<IServiceProvider>().GetService<IConnectionMultiplexer>() is { } multiplexer)
            {
                var redis = multiplexer.GetDatabase();
                var claims = ClientClaims();
                bool refreshed = await IpIdentityRefresh.RefreshAsync(redis, claims, ip, DateTimeOffset.UtcNow);
                // Never by the x-steam-id, x-epic-id or x-install-id headers: anyone can send them, and what this answers is
                // the player's queue (the web login code among it). The verified token and the IP only.
                var lookup = AccountLookups.From(HttpContext, claims) with { SteamHeader = null, EpicHeader = null, InstallHeader = null };
                if (await Resolve<IAccountResolver>().ResolveAsync(lookup) is { } player)
                {
                    if (refreshed && player.Connection.FirstOrDefault(e => e.Name == "identityRegistered").Value != "1")
                    {
                        string version = claims!["clientVersion"]?.ToString() ?? "";
                        await redis.HashSetAsync($"connections:{player.Id}", [new HashEntry("clientVersion", version), new HashEntry("identityRegistered", "1")]);
                        Logger.LogInformation("Unlocked the live session of {Player}: its client is registered (version {Version})", player.Id, version);
                    }

                    string key = PlayerMessages.NotificationPrefix + player.Id;
                    for (var item = await redis.ListLeftPopAsync(key); item.HasValue; item = await redis.ListLeftPopAsync(key))
                    {
                        delivered.Add(Js.Parse(item.ToString()));
                    }

                    if (delivered.Count > 0)
                    {
                        Logger.LogInformation("Delivered {Count} client notification(s) to {Player} via={Source} (IP {Ip})", delivered.Count, player.Id, player.Source, ip);
                    }
                }
            }
        }
        catch (Exception e) when (e is RedisException or TimeoutException or System.Text.Json.JsonException)
        {
            Logger.LogError(e, "Error in /ovs/notifications");
        }

        await Send.StringAsync(Js.Stringify(delivered), contentType: "application/json; charset=utf-8", cancellation: ct);
    }

    // The client's token, when it sent one this server signed and it has not expired; else none.
    private JsonObject? ClientClaims()
    {
        string token = HttpContext.Request.Headers[HydraToken.Header].ToString();
        if (token.Length == 0 || Resolve<IOptionsMonitor<AccessSettings>>().CurrentValue.JwtSecret is not { Length: > 0 } secret)
        {
            return null;
        }

        try
        {
            return AccessTokens.Verify(token, secret, DateTimeOffset.UtcNow);
        }
        catch (AccessTokenException)
        {
            return null;
        }
    }
}
