using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/update_player_preferences.
/// Seen in: binary ssc name; TS server: PUT /ssc/invoke/update_player_preferences.
/// Ssc: server/capture.
/// </summary>
public sealed class PutUpdatePlayerPreferences : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/update_player_preferences");
    }
}
