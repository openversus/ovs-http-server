using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Edge;

/// <summary>The edge's timing (the edge executable). The link's secret is Gateway:EdgeSecret, shared with the nodes.</summary>
public sealed class EdgeSettings
{
    [Description("A game that has not sent its first frame (its session token) within this time (ms) is closed.")]
    [Range(1000, 600000)]
    public int HandshakeTimeoutMs { get; set; } = 30000;

    [Description("How long a gateway node may take to accept a game's link (ms) before another is tried.")]
    [Range(100, 60000)]
    public int ConnectTimeoutMs { get; set; } = 3000;

    [Description("How often each link to a gateway node is pinged (websocket keep-alive, ms). With KeepAliveTimeoutMs, how fast a node that died without closing anything (a host gone, a network cut) is noticed and its games moved.")]
    [Range(200, 60000)]
    public int KeepAliveMs { get; set; } = 2000;

    [Description("A link whose node has not answered a keep-alive ping within this time (ms) is let go of, and its game moved to another node.")]
    [Range(200, 60000)]
    public int KeepAliveTimeoutMs { get; set; } = 3000;

    [Description("How long a game may be without a node (ms) before the edge gives up and closes it. Below the replay window (60 s, what a resume can replay) and Gateway:EdgeDetachGraceMs (how long a node keeps a game whose link ended), and well within the matchmaker's ~21 s of a queued player's heartbeat.")]
    [Range(1000, 55000)]
    public int GiveUpMs { get; set; } = 20000;

    [Description("While a game has no node, the edge pings it itself, at once and then every this many ms (the game goes back to its title screen when it has not been pinged for about 30 s).")]
    [Range(1000, 25000)]
    public int DetachedPingMs { get; set; } = 10000;

    [Description("How long a stopping edge waits for its games to leave (ms); 0: as long as they stay, so an edge's update drops no one (it stops taking new games at once, and a new edge takes those).")]
    [Range(0, int.MaxValue)]
    [RestartRequired]
    public int DrainTimeoutMs { get; set; }
}
