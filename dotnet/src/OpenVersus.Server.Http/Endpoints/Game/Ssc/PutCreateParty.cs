using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/create_party.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/create_party.
/// Ssc: binary.
/// </summary>
public sealed class PutCreateParty : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/create_party");
    }
}
