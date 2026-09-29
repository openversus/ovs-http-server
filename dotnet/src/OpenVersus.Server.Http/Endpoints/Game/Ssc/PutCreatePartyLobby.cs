using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/create_party_lobby.
/// Seen in: binary ssc name; captured 11x; TS server: PUT /ssc/invoke/create_party_lobby.
/// Ssc: server/capture.
/// </summary>
public sealed class PutCreatePartyLobby : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/create_party_lobby");
    }
}
