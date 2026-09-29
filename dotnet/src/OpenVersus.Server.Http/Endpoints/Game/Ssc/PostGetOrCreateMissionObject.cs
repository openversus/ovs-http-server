using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// POST /ssc/invoke/get_or_create_mission_object.
/// Seen in: binary ssc name; captured 11x; TS server: POST /ssc/invoke/get_or_create_mission_object.
/// Ssc: server/capture.
/// </summary>
public sealed class PostGetOrCreateMissionObject : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ssc/invoke/get_or_create_mission_object");
    }
}
