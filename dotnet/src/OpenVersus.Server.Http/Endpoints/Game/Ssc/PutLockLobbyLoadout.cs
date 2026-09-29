using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Ssc;

/// <summary>
/// PUT /ssc/invoke/lock_lobby_loadout.
/// Seen in: binary ssc name; captured 18x; TS server: PUT /ssc/invoke/lock_lobby_loadout.
/// Ssc: server/capture.
/// </summary>
public sealed class PutLockLobbyLoadout : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/ssc/invoke/lock_lobby_loadout");
    }
}
