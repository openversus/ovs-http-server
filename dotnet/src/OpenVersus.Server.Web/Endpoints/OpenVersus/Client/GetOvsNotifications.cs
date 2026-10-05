using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Client;

/// <summary>
/// GET /ovs/notifications.
/// Seen in: captured 2299x; TS server: GET /ovs/notifications.
/// Capture only; server only.
/// </summary>
public sealed class GetOvsNotifications : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ovs/notifications");
    }
}
