using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/cancel_party_invite.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/cancel_party_invite.
/// Ssc: binary.
/// </summary>
public sealed class PutCancelPartyInvite : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/cancel_party_invite");
    }
}
