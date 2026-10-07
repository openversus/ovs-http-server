using System.Text.Json.Nodes;

namespace OpenVersus.Server.Core.Matches;

/// <summary>
/// What a bot plays with unless told otherwise: the TS server's data/botDefaults.ts (BOT_DEFAULT_PERKS, BOT_DEFAULT_CHARACTER,
/// BOT_DEFAULT_SKIN, BOT_DIFFICULTY), which its websocket also falls back to when it builds a bot's config (here,
/// GameplayConfigs). Custom lobbies and the Casual queue's bot matches use it.
/// </summary>
public static class BotDefaults
{
    public static readonly IReadOnlyList<string> Perks = ["perk_gen_boxer", "perk_team_speed_force_assist", "perk_purest_of_motivations", "perk_gen_well_rounded"];

    /// <summary>The fighter and skin of a bot whose bot_config:{bot} names none.</summary>
    public const string Character = "character_jason", Skin = "skin_jason_000";

    /// <summary>BotSettingSlug -> the difficulty the gameplay config's BotDifficultyMin and Max carry.</summary>
    public static readonly IReadOnlyDictionary<string, int> Difficulty = new Dictionary<string, int> { ["VeryEasy"] = 0, ["Easy"] = 1, ["Medium"] = 2, ["Hard"] = 3 };

    public static JsonArray PerksArray() => new([.. Perks.Select(p => (JsonNode)p)]);

    /// <summary>
    /// What makes a match unranked for the TS websocket and match end: isCustomGame is the only marker they have. With it
    /// the config is unranked (bIsRanked false, ModeString "1v1"/"2v2" instead of "ranked-1v1"), no ranked set (best of 3)
    /// is opened, so the match ends back in the menus (RematchDeclinedNotification) instead of waiting for set check-ins,
    /// and none of the TS rating paths rate it (MIGRATION-BRIDGES.md 6). Used for the Casual queue's matches, whose
    /// rematch is C#'s (Rematches); the TS websocket declined every rematch of such a match.
    /// </summary>
    public static JsonObject UnrankedNotificationFields() => new() { ["isCustomGame"] = true };

    /// <summary>
    /// isCustomGame also tells the game bIsCustomGame (a custom lobby's match); a Casual match is not one: merged over the
    /// websocket's config, this sets it back.
    /// </summary>
    public static JsonObject UnrankedConfigOverride() => new() { ["bIsCustomGame"] = false };
}

/// <summary>
/// The fighters a Casual bot is picked from, each with the skins it may wear (Matchmaking/bot-fighters.json, made by
/// tools/casual/gen_bot_fighters.mjs from the TS server's enabled skins).
/// </summary>
internal static class BotFighters
{
    private static readonly Lazy<IReadOnlyList<(string Character, string[] Skins)>> s_fighters = new(() =>
    {
        using var stream = typeof(BotFighters).Assembly.GetManifestResourceStream("OpenVersus.Server.Core.Matchmaking.bot-fighters.json")
            ?? throw new InvalidOperationException("bot-fighters.json is not embedded");
        return ((JsonObject)JsonNode.Parse(stream)!).Select(f => (f.Key, f.Value!.AsArray().Select(s => (string)s!).ToArray())).ToList();
    });

    public static IReadOnlyList<(string Character, string[] Skins)> All => s_fighters.Value;

    /// <summary><paramref name="count"/> different fighters, each in a random skin of theirs.</summary>
    public static IReadOnlyList<(string Character, string Skin)> Pick(int count, Random random)
    {
        var fighters = All.OrderBy(_ => random.Next()).Take(count).ToList();
        return [.. fighters.Select(f => (f.Character, f.Skins[random.Next(f.Skins.Length)]))];
    }
}
