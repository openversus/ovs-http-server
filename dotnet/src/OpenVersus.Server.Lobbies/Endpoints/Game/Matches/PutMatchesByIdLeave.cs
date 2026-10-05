using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Matches;

/// <summary>
/// PUT /matches/{id}/leave.
/// Seen in: binary 0x144fd9f20; TS server: PUT /matches/{id}/leave.
/// </summary>
public sealed class PutMatchesByIdLeave : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/matches/{id}/leave");
    }
}
