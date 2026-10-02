using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.OpenVersus.Client;

/// <summary>
/// PUT /ovs/friends/send-request/{targetId}.
/// Seen in: TS server: PUT /ovs/friends/send-request/{targetId}.
/// Server only.
/// </summary>
public sealed class PutOvsFriendsSendRequestByTargetId : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ovs/friends/send-request/{targetId}");
    }
}
