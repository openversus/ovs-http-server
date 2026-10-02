using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Lobbies.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/casual_queue.
/// Seen in: binary ssc name.
/// Ssc: binary.
/// </summary>
public sealed class AnyCasualQueue : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/casual_queue");
    }
}
