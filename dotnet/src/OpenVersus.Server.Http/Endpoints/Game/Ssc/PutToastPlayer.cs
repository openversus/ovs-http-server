using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/toast_player.
/// Seen in: binary ssc name; captured 4x; TS server: PUT /ssc/invoke/toast_player.
/// Ssc: server/capture.
/// </summary>
public sealed class PutToastPlayer : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/toast_player");
    }
}
