using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Social.Endpoints.Game.Accounts;

/// <summary>
/// PUT /accounts/me/relationships/{id}/unblock.
/// Seen in: binary 0x144ff4890; TS server: PUT /accounts/me/relationships/{blockid}/unblock.
/// </summary>
public sealed class PutAccountsMeRelationshipsByIdUnblock : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/accounts/me/relationships/{id}/unblock");
    }
}
