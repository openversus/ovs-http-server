using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.CustomLobbies;

/// <summary>
/// The game modes a custom lobby can be set to, and what each gives the lobby: the TS server's GAME_MODES_CONFIG and
/// MAP_ROTATIONS (modules/customLobby/gameModes.data.ts, maps.data.ts), as game-modes.json holds what it reads of them
/// (tools/customlobby/gen_game_modes.mjs). A mode the data does not have, or one with no teams, cannot be set: the TS
/// server threw reading it (<see cref="UnknownGameModeException"/>), and the routes answer as they did.
/// </summary>
public static class GameModes
{
    private static readonly Lazy<JsonObject> s_modes = new(() =>
    {
        using var stream = typeof(GameModes).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.CustomLobbies.game-modes.json")
            ?? throw new InvalidOperationException("game-modes.json is not embedded");
        return JsonNode.Parse(stream) as JsonObject ?? throw new InvalidOperationException("game-modes.json is not an object");
    });

    public const string Default = "gm_classic_2v2";

    /// <summary>The mode a team style sets (update_team_style_for_custom_game): Duos, Solos and FFA have one each, the rest the default.</summary>
    public static string ForTeamStyle(string? style) => style switch
    {
        "Duos" => "gm_classic_2v2",
        "Solos" => "gm_classic_1v1",
        "FFA" => "gm_classic_ffa",
        _ => Default,
    };

    public static bool Has(string slug) => s_modes.Value[slug] is JsonObject;

    /// <summary>
    /// The lobby settings a mode starts with (getCustomLobbyDefaultSettings), keys in the TS order: GameModeSlug,
    /// match_config, Maps (the rotation's, all selected), WorldBuffs (the mode's required ones), PlayerBuffs, Handicaps.
    /// A value the mode's data lacks is left out, as JSON.stringify left out undefined.
    /// </summary>
    public static JsonObject DefaultSettings(string slug)
    {
        var mode = Mode(slug);
        if (mode["Teams"] is not JsonArray { Count: > 0 } teams || teams[0] is not JsonObject)
        {
            throw new UnknownGameModeException(slug, "has no teams");
        }

        var config = new JsonObject { ["TeamStyle"] = mode["TeamStyle"]?.DeepClone() };
        if (mode["TeamStyle"] is null)
        {
            config.Remove("TeamStyle");
        }

        config["QueueType"] = "Unselected";
        config["Context"] = "Custom";
        config["ModeDifficulty"] = "Unselected";
        config["GameModeAlias"] = "Versus";
        Copy(mode, "NumRingouts", config, "NumRingoutsForWin");
        Copy(mode, "MatchDuration", config, "MatchDuration");
        Copy(mode, "bMapHazards", config, "AllowHazards");
        config["AllowDuplicateCharacters"] = true;
        config["AreRewardsSkipped"] = true;
        config["num_set_wins_required"] = 1;
        config["EnableShields"] = 1;

        return new JsonObject
        {
            ["GameModeSlug"] = slug,
            ["match_config"] = config,
            ["Maps"] = Maps(slug),
            ["WorldBuffs"] = WorldBuffs(slug),
            ["PlayerBuffs"] = new JsonObject(),
            ["Handicaps"] = new JsonObject(),
        };
    }

    /// <summary>The mode's maps, each selected (getGameModeMaps); empty when its rotation has none.</summary>
    public static JsonArray Maps(string slug) =>
        Mode(slug)["Maps"] is JsonArray maps
            ? new JsonArray([.. maps.Select(m => (JsonNode)new JsonObject { ["Map"] = m?.DeepClone(), ["IsSelected"] = true })])
            : [];

    /// <summary>The world buffs the mode requires (getWorldBuffs).</summary>
    public static JsonArray WorldBuffs(string slug) => Mode(slug)["RequiredWorldBuffs"] is JsonArray buffs ? (JsonArray)buffs.DeepClone() : [];

    /// <summary>
    /// The buffs each team slot gets (computeBuffMatrix): {team index: {teamBuffs: the team's, players: {slot: the
    /// slot's own, when it has any}}}. The scripts give each player of a team, in the order they iterate the team, its slot's.
    /// </summary>
    public static JsonObject BuffMatrix(string slug)
    {
        var matrix = new JsonObject();
        var teams = Mode(slug)["Teams"] as JsonArray ?? [];
        for (int t = 0; t < teams.Count; t++)
        {
            var players = new JsonObject();
            var slots = teams[t]?["Players"] as JsonArray ?? [];
            for (int p = 0; p < slots.Count; p++)
            {
                if (slots[p] is JsonArray { Count: > 0 } buffs)
                {
                    players[p.ToString(System.Globalization.CultureInfo.InvariantCulture)] = buffs.DeepClone();
                }
            }

            matrix[t.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["teamBuffs"] = teams[t]?["RequiredTeamPlayerBuffs"] is JsonArray teamBuffs ? teamBuffs.DeepClone() : new JsonArray(),
                ["players"] = players,
            };
        }

        return matrix;
    }

    private static JsonObject Mode(string slug) => s_modes.Value[slug] as JsonObject ?? throw new UnknownGameModeException(slug, "is not a game mode");

    private static void Copy(JsonObject from, string key, JsonObject to, string asKey)
    {
        if (from[key] is { } value)
        {
            to[asKey] = value.DeepClone();
        }
    }
}

/// <summary>A game mode the custom lobby cannot use (unknown, or without teams).</summary>
public sealed class UnknownGameModeException(string slug, string why) : Exception($"game mode {slug} {why}");
