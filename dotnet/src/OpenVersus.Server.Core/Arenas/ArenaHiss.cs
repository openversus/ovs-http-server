using System.ComponentModel;
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

    /// <summary>Arenas:Enabled (its default when the service has no such setting).</summary>
    public static bool IsEnabled(IServiceProvider services) =>
        services.GetService<IOptionsMonitor<ArenaSettings>>()?.CurrentValue.Enabled ?? new ArenaSettings().Enabled;

    /// <summary>The Arena button's feature toggle, on exactly while <paramref name="enabled"/>.</summary>
    public static void ApplyTo(JsonNode answer, bool enabled)
    {
        if (answer["body"]?["Data"]?["feature-toggles"]?["_hydra_compressed"] is JsonObject toggles)
        {
            toggles[FeatureToggle] = enabled;
        }
    }
}
