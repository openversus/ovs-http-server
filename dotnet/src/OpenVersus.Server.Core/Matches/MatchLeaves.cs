using System.Text.Json.Nodes;
using OpenVersus.Server.Core.Compat;
using StackExchange.Redis;

namespace OpenVersus.Server.Core.Matches;

/// <summary>
/// A player leaving a match, said by the game over HTTP (PUT /matches/{id}/leave, the lobbies service): a record on the
/// match flow's match:results stream (field "leave": {matchId, playerId}), which the match flow settles as it settles
/// the game's websocket close (<see cref="IMatchStatusEvents.LeftAsync"/>). Any number of signals for one leave (this,
/// the close, the node's PlayerDisconnect, the end-of-match report) settle it once: each effect is keyed per match.
/// </summary>
public static class MatchLeaves
{
    public const string Field = "leave";

    public static Task AppendAsync(IDatabase redis, string matchId, string playerId) =>
        redis.StreamAddAsync(MatchResults.Stream, Field, Js.Stringify(new JsonObject { ["matchId"] = matchId, ["playerId"] = playerId }),
            maxLength: 10_000, useApproximateMaxLength: true);
}
