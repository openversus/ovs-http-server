using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Social.Endpoints.OpenVersus.Client;

/// <summary>
/// POST /ovs/friends/accept.
/// Seen in: TS server: POST /ovs/friends/accept.
/// Server only.
/// </summary>
public sealed class PostOvsFriendsAccept : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs/friends/accept");
    }
}
