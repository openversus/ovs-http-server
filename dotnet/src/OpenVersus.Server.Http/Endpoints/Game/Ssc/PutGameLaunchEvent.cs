using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/game_launch_event.
/// Seen in: binary ssc name; captured 11x; TS server: PUT /ssc/invoke/game_launch_event.
/// Ssc: binary.
/// </summary>
public sealed class PutGameLaunchEvent : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/game_launch_event");
    }
}
