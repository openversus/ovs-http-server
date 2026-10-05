using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Social.Endpoints.Game.Accounts;

/// <summary>
/// GET /accounts/{id}/relationships/followers.
/// Seen in: binary 0x144fed800.
/// </summary>
public sealed class GetAccountsByIdRelationshipsFollowers : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/accounts/{id}/relationships/followers");
    }
}
