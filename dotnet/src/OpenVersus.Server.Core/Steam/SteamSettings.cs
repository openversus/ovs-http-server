using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace OpenVersus.Server.Core.Steam;

/// <summary>
/// The online Steam ticket check and Steam presence (docs/IDENTIFY.md "Asking Steam"): the Steam identity service holds
/// an auth session with Steam for each registered client's ticket. Read by the web service (identify waits for the
/// verdict), the login (a refused Steam id is not an identity for a while), the presence readers and the service itself.
/// </summary>
public sealed class SteamSettings
{
    [Description("Ask Steam about each verified ticket (the Steam identity service must run): identify waits for the verdict, a refused ticket's Steam id is dropped, and Steam's presence outranks the websocket's. Off: the offline signature check alone, as before.")]
    public bool Enabled { get; set; }

    [Description("How long /api/identify waits for Steam's verdict before answering from the offline check alone (the client gives up at 5000).")]
    [Range(0, 4500)]
    public int IdentifyWaitMs { get; set; } = 2500;

    [Description("How long the service waits for Steam's verdict on an opened session before counting it unavailable (the offline verdict stands).")]
    [Range(1000, 120000)]
    public int VerdictTimeoutMs { get; set; } = 20000;

    [Description("For how long after Steam refused a ticket its Steam id is a claim, not an identity, at the login (install id, hardware and IP decide).")]
    [Range(0, 1440)]
    public int RefusalHoldMinutes { get; set; } = 10;

    [Description("For how long after Steam reported a game closed (or refused it) that outranks the websocket's online_players, which the reaper clears later; past it the websocket decides again (a relaunch without Steam, say).")]
    [Range(0, 60)]
    public int PresenceOverrideMinutes { get; set; } = 5;

    [Description("The first wait before reconnecting to Steam after a drop; doubled each failure up to a minute.")]
    [Range(500, 60000)]
    public int ReconnectBackoffMs { get; set; } = 5000;
}
