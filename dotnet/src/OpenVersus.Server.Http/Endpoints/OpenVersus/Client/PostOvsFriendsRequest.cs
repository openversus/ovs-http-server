using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Client;

/// <summary>
/// POST /ovs/friends/request.
/// Seen in: TS server: POST /ovs/friends/request.
/// Server only.
/// </summary>
public sealed class PostOvsFriendsRequest : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs/friends/request");
    }
}
