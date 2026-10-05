using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Matches;

/// <summary>
/// The lobby object the party routes answer with and the websocket sends (the TS server builds the same object in eight
/// places: ssc.ts, shared.routes.ts, websocket.ts): its keys in the TS server's order.
/// </summary>
public static class LobbyDocuments
{
    public const string Cluster = "ec2-us-east-1-dokken";

    /// <summary>One player of a lobby, as the lobby's teams list them and its per-player maps key them.</summary>
    public sealed record Member(string Id, long JoinedAt, JsonNode GameplayPreferences, string Character, string Skin);

    /// <summary>
    /// The lobby: <paramref name="members"/> on team 0 in order, the other four teams empty. <paramref name="matchId"/>
    /// is the last key when given (the party routes); the match document's server_data has none.
    /// </summary>
    public static JsonObject Lobby(IReadOnlyList<Member> members, string leaderId, string gameVersion, string modeString, string? matchId)
    {
        JsonObject players = [], gameplay = [], autoParty = [], platforms = [], loadouts = [];
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            players[m.Id] = TeamPlayer(m.Id, m.JoinedAt, i);
            gameplay[m.Id] = m.GameplayPreferences.DeepClone();
            autoParty[m.Id] = false;
            platforms[m.Id] = "PC";
            loadouts[m.Id] = Loadout(m.Character, m.Skin);
        }

        var teams = new JsonArray(new JsonObject { ["TeamIndex"] = 0, ["Players"] = players, ["Length"] = members.Count });
        for (int t = 1; t <= 4; t++)
        {
            teams.Add(new JsonObject { ["TeamIndex"] = t, ["Players"] = new JsonObject(), ["Length"] = 0 });
        }

        var lobby = new JsonObject
        {
            ["Teams"] = teams,
            ["LeaderID"] = leaderId,
            ["LobbyType"] = 0,
            ["ReadyPlayers"] = new JsonObject(),
            ["PlayerGameplayPreferences"] = gameplay,
            ["PlayerAutoPartyPreferences"] = autoParty,
            ["GameVersion"] = gameVersion,
            ["HissCrc"] = 1167552915,
            ["Platforms"] = platforms,
            ["AllMultiplayParams"] = MultiplayParams(),
            ["LockedLoadouts"] = loadouts,
            ["ModeString"] = modeString,
            ["IsLobbyJoinable"] = true,
        };
        if (matchId is not null)
        {
            lobby["MatchID"] = matchId;
        }

        return lobby;
    }

    /// <summary>The SSC envelope around a lobby: {body: {lobby, Cluster}, metadata: null, return_code: 0}.</summary>
    public static JsonObject Answer(JsonObject lobby) => Ssc(new JsonObject { ["lobby"] = lobby, ["Cluster"] = Cluster });

    /// <summary>{body, metadata: null, return_code}.</summary>
    public static JsonObject Ssc(JsonObject body, int returnCode = 0) => new() { ["body"] = body, ["metadata"] = null, ["return_code"] = returnCode };

    public static JsonObject AllMultiplay() => MultiplayParams();

    public static JsonObject TeamPlayer(string id, long joinedAt, int index) => new()
    {
        ["Account"] = new JsonObject { ["id"] = id },
        ["JoinedAt"] = Date(joinedAt),
        ["BotSettingSlug"] = "",
        ["LobbyPlayerIndex"] = index,
        ["CrossplayPreference"] = 1,
    };

    public static JsonObject Loadout(string character, string skin) => new() { ["Character"] = character, ["Skin"] = skin };

    public static JsonObject Date(long seconds) => new() { ["_hydra_unix_date"] = seconds };

    private static JsonObject MultiplayParams() => new()
    {
        ["1"] = Multiplay("ec2-us-east-1-dokken", "1252499", ""),
        ["2"] = Multiplay("ec2-us-east-1-dokken", "1252922", "19c465a7-f21f-11ea-a5e3-0954f48c5682"),
        ["3"] = Multiplay("", "1252925", ""),
        ["4"] = Multiplay("ec2-us-east-1-dokken", "1252928", "19c465a7-f21f-11ea-a5e3-0954f48c5682"),
    };

    private static JsonObject Multiplay(string cluster, string profile, string region) =>
        new() { ["MultiplayClusterSlug"] = cluster, ["MultiplayProfileId"] = profile, ["MultiplayRegionId"] = region };
}
