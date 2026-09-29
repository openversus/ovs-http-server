using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// POST /ssc/invoke/attempt_daily_refresh.
/// Seen in: binary ssc name; captured 11x; TS server: POST /ssc/invoke/attempt_daily_refresh.
/// Ssc: server/capture.
/// </summary>
public sealed class PostAttemptDailyRefresh : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ssc/invoke/attempt_daily_refresh");
    }
}
