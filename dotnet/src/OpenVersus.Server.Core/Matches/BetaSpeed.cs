using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace OpenVersus.Server.Core.Matches;

// The Beta Speed mutator's speed (ovs_beta_speed: every Testing Grounds match, and a custom game that picks it), set here
// alone. Every player's client and the rollback server must run a match at the same speed: clients that differ desync,
// and a tick rate that differs from the clients' gets them pulled back to 60 frames a second. So the client reads the
// speed at startup (GET /ovs/client-settings) and reports the one it runs on every request (X-OVS-BetaSpeed); a Testing
// Grounds request from a client that runs another (it started before a change) is refused with a message to restart
// (TestingGrounds.WrongBetaSpeed), and the rollback server is given the matching tick rate (RollbackCallbacks: the
// registry's tick_rate). Known gap: custom games are not checked, so a custom lobby with Beta Speed whose players run
// different speeds desyncs.

/// <summary>Beta Speed settings.</summary>
public sealed class BetaSpeedSettings
{
    [Description("The speed of a match with the Beta Speed mutator (ovs_beta_speed), in percent of normal: the clients read it at startup (GET /ovs/client-settings) and the rollback server runs the match at 60 x Percent / 100 frames a second, rounded (whole at a multiple of 5). A client started before a change is refused from Testing Grounds until it restarts. 50 to 200.")]
    [Range(50, 200)]
    public int Percent { get; set; } = 110;
}

public static class BetaSpeed
{
    /// <summary>The client's report of the Beta Speed it runs, in percent.</summary>
    public const string Header = "X-OVS-BetaSpeed";

    /// <summary>The settings in force (defaults when the service has none).</summary>
    public static BetaSpeedSettings Of(IServiceProvider services) =>
        services.GetService<IOptionsMonitor<BetaSpeedSettings>>()?.CurrentValue ?? new BetaSpeedSettings();

    /// <summary>A Beta Speed match's frame rate: 60 x <paramref name="percent"/> / 100, rounded. Every frame is still 1/60 s of game time.</summary>
    public static int TickRate(int percent) => (60 * percent + 50) / 100;

    /// <summary>Whether <paramref name="header"/> (the client's X-OVS-BetaSpeed) reports <paramref name="percent"/>.</summary>
    public static bool Reports(string? header, int percent) =>
        int.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out int theirs) && theirs == percent;

    /// <summary>GET /ovs/client-settings: what the client reads at startup.</summary>
    public static JsonObject ClientSettings(BetaSpeedSettings settings) => new() { ["betaSpeedPercent"] = settings.Percent };
}
