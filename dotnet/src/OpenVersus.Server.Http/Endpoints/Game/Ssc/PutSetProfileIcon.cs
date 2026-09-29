using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/set_profile_icon.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/set_profile_icon.
/// Ssc: server/capture.
/// </summary>
public sealed class PutSetProfileIcon : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/set_profile_icon");
    }
}
