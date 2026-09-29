using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/autoparty_join.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/autoparty_join.
/// Ssc: server/capture.
/// </summary>
public sealed class PutAutopartyJoin : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/autoparty_join");
    }
}
