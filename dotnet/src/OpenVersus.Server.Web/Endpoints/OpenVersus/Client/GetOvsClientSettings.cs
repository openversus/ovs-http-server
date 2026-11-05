using FastEndpoints;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.Web.Endpoints.OpenVersus.Client;

/// <summary>
/// GET /ovs/client-settings: what the OpenVersus client reads once at startup, <c>{"betaSpeedPercent": 110}</c> (the
/// Beta Speed mutator's speed, BetaSpeed:Percent; the client reports the one it runs as X-OVS-BetaSpeed). Nothing secret
/// and nothing per player: no token. C# only, no TS route.
/// </summary>
public sealed class GetOvsClientSettings : EndpointWithoutRequest
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET);
        Routes("/ovs/client-settings");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = BetaSpeed.ClientSettings(Resolve<IOptionsMonitor<BetaSpeedSettings>>().CurrentValue);
        await Send.StringAsync(Core.Compat.Js.Stringify(settings), contentType: "application/json; charset=utf-8", cancellation: ct);
    }
}
