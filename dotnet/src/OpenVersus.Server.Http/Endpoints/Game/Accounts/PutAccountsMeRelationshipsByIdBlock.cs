using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// PUT /accounts/me/relationships/{id}/block.
/// Seen in: binary 0x144fe81e0; TS server: PUT /accounts/me/relationships/{blockid}/block.
/// </summary>
public sealed class PutAccountsMeRelationshipsByIdBlock : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/accounts/me/relationships/{id}/block");
    }
}
