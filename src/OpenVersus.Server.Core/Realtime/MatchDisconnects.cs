using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenVersus.Server.Core.Matches;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Realtime;

// What a player's disconnect does to their match: the match flow's reader of the realtime gateway's disconnects
// (realtime:connections, consumer group "matchflow"; ConnectionEvents.cs). Each disconnected event is the TS websocket's
// close for the match (websocket.ts 511-616): a pregame dodge, a mid-game leave of a set game, a leave between a set's
// games (IMatchStatusEvents.GameClosedAsync). For a P2P match it is the only such signal: its node sends no
// PlayerDisconnect. A close made for a gateway node that died (reaped, GatewayReaper) is the server's failure, a crash
// for the match. The lobbies, the queue and the session are the lobbies service's (LobbyDisconnects).
//
// Nothing is done for a player who has connected or logged in again since (ConnectionEvents.BackAsync): the game they
// left is a rollback server's to call a crash or a dodge, where there is one; TS ignored the close of a connection a newer
// one had replaced.
//
// Redis, read     realtime:connections (XREADGROUP, group "matchflow", made at the stream's end); realtime:conn:{player};
//                 connections:{player} (jwt); the rest is MatchStatusEvents'

/// <summary>The match flow's reader of the realtime gateway's disconnects (see the header of MatchDisconnects.cs).</summary>
internal sealed class MatchDisconnects(IServiceProvider services, IMatchStatusEvents events, TimeProvider time, ILogger<MatchDisconnects> logger)
    : ConnectionEventsReader(services, time, logger)
{
    public const string GroupName = "matchflow";

    protected override string Group => GroupName;

    protected override async Task OnEventAsync(IDatabase redis, ConnectionEvent connectionEvent)
    {
        if (connectionEvent.Type != "disconnected")
        {
            return;
        }

        if ((await ConnectionEvents.BackAsync(redis, connectionEvent.PlayerId, connectionEvent.ConnectionId, connectionEvent.TokenHash)).Back is { } back)
        {
            Log.LogInformation("Disconnect of {Player} ({Connection}) not handled for their match: {Why}", connectionEvent.PlayerId, connectionEvent.ConnectionId, back);
            return;
        }

        await events.GameClosedAsync(connectionEvent.PlayerId, nodeGone: connectionEvent.Reaped);
    }
}

public static class MatchDisconnectsHosting
{
    /// <summary>The match flow's reader of the realtime gateway's disconnects; needs AddMatchStatusEvents.</summary>
    public static WebApplicationBuilder AddMatchDisconnects(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddHostedService<MatchDisconnects>();
        return builder;
    }
}
