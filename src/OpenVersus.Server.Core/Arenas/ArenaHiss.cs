using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OpenVersus.Server.Core.Arenas;

// Arenas (docs/ARENAS.md): everything a player does in the mode starts from an Arena lobby, which the mode select's Arena
// button opens (UMvsPlaylistScreen.ArenaLobbyButton, beside the Custom Game button). The game shows that button while the
// hiss feature toggle "Arena" is on. The event queue evtq_arena (its own feature toggle and bAlwaysAvailable) stays off:
// it lists Arena among a party lobby's queues and sends the party to matchmaking with no Arena lobby, which the mode
// never did.

/// <summary>Arenas settings.</summary>
public sealed class ArenaSettings
{
    [Description("Shows the Arena button in the game's mode select (the hiss feature toggle Arena), which opens an Arena lobby. Off: no button.")]
    public bool Enabled { get; set; } = true;

    [Description("Seconds an Arena lobby waits in matchmaking before the game starts it with bots (the hiss float ArenaMatchmakingBotTimeout, while Arenas is on; off, the hiss keeps its 500): the game waits this many seconds, give or take 10, at random. A change moves the Crc, so games fetch the hiss again. Below 11 the wait could be 0, which the game takes as no wait at all.")]
    [Range(11, 1800)]
    public int MatchmakingBotTimeoutSeconds { get; set; } = 240;
}

/// <summary>What Arenas changes in the hiss.</summary>
public static class ArenaHiss
{
    /// <summary>The feature toggle that shows the mode select's Arena button.</summary>
    public const string FeatureToggle = "Arena";

    /// <summary>
    /// Added to the Crc while Arenas is on, so the two catalogs never share a Crc. 1v1 Testing Grounds adds 100,000: each
    /// of the four combinations has its own.
    /// </summary>
    public const double CrcOffset = 200_000;

    /// <summary>
    /// Added to the Crc, while Arenas is on, once per second of Arenas:MatchmakingBotTimeoutSeconds, so each value has its
    /// own Crc: above the on/off offsets' sum (300,000), so no two combinations meet; 1800 s on the default CRC stays below
    /// 2^31.
    /// </summary>
    public const double CrcPerBotTimeoutSecond = 400_000;

    /// <summary>The hiss float the game arms its Arena matchmaking timer from.</summary>
    public const string BotTimeoutFloat = "ArenaMatchmakingBotTimeout";

    /// <summary>Arenas:Enabled (its default when the service has no such setting).</summary>
    public static bool IsEnabled(IServiceProvider services) =>
        services.GetService<IOptionsMonitor<ArenaSettings>>()?.CurrentValue.Enabled ?? new ArenaSettings().Enabled;

    /// <summary>Arenas:MatchmakingBotTimeoutSeconds (its default when the service has no such setting).</summary>
    public static int BotTimeoutSeconds(IServiceProvider services) =>
        services.GetService<IOptionsMonitor<ArenaSettings>>()?.CurrentValue.MatchmakingBotTimeoutSeconds ?? new ArenaSettings().MatchmakingBotTimeoutSeconds;

    /// <summary>
    /// The Arena button's feature toggle, on exactly while <paramref name="enabled"/>, and while on the matchmaking bot
    /// timeout, <paramref name="botTimeoutSeconds"/> (off, the hiss is the TS one: nothing arms that timer without the button).
    /// </summary>
    public static void ApplyTo(JsonNode answer, bool enabled, int botTimeoutSeconds)
    {
        if (answer["body"]?["Data"]?["feature-toggles"]?["_hydra_compressed"] is JsonObject toggles)
        {
            toggles[FeatureToggle] = enabled;
        }

        if (enabled && answer["body"]?["Data"]?["float-settings"]?["_hydra_compressed"] is JsonObject floats)
        {
            floats[BotTimeoutFloat] = botTimeoutSeconds;
        }
    }
}
