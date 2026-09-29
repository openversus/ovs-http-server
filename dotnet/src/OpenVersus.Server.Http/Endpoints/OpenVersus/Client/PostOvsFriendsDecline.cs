using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Client;

/// <summary>
/// POST /ovs/friends/decline.
/// Seen in: TS server: POST /ovs/friends/decline.
/// Server only.
/// </summary>
public sealed class PostOvsFriendsDecline : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs/friends/decline");
    }
}
