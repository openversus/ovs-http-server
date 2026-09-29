using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/bot_queue.
/// Seen in: binary ssc name; captured 1x.
/// Ssc: binary.
/// </summary>
public sealed class PutBotQueue : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/bot_queue");
    }
}
