using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/reset_custom_lobby_to_defaults.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/reset_custom_lobby_to_defaults.
/// Ssc: server/capture.
/// </summary>
public sealed class PutResetCustomLobbyToDefaults : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/reset_custom_lobby_to_defaults");
    }
}
