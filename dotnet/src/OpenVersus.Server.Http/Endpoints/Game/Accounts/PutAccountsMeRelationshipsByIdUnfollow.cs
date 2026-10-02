using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// PUT /accounts/me/relationships/{id}/unfollow.
/// Seen in: binary 0x144ff4a20.
/// </summary>
public sealed class PutAccountsMeRelationshipsByIdUnfollow : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/accounts/me/relationships/{id}/unfollow");
    }
}
