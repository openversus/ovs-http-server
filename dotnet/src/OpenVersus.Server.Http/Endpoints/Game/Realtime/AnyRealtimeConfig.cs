using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Realtime;

/// <summary>
/// Any method /realtime/config.
/// Seen in: binary 0x140f9abc0.
/// Social layer; method from unknown.
/// </summary>
public sealed class AnyRealtimeConfig : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/realtime/config");
    }
}
