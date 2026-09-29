using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/game_install.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/game_install.
/// Ssc: binary.
/// </summary>
public sealed class PutGameInstall : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/game_install");
    }
}
