using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Social.Endpoints.OpenVersus.Client;

/// <summary>
/// DELETE /ovs/friends/{friendId}.
/// Seen in: TS server: DELETE /ovs/friends/{friendId}.
/// Server only.
/// </summary>
public sealed class DeleteOvsFriendsByFriendId : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.DELETE);
        Routes("/ovs/friends/{friendId}");
    }
}
