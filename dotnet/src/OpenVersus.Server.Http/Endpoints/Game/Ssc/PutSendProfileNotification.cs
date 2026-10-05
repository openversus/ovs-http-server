using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/send_profile_notification.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/send_profile_notification.
/// Ssc: binary.
/// </summary>
public sealed class PutSendProfileNotification : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/send_profile_notification");
    }
}
