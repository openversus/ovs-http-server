using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Social.Endpoints.OpenVersus.Client;

/// <summary>
/// POST /ovs/friends/block.
/// Seen in: TS server: POST /ovs/friends/block.
/// Server only.
/// </summary>
public sealed class PostOvsFriendsBlock : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs/friends/block");
    }
}
