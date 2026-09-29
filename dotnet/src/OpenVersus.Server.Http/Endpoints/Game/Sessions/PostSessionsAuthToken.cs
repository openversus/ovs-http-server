using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Sessions;

/// <summary>
/// POST /sessions/auth/token.
/// Seen in: binary 0x140f9be40; captured 11x; TS server: POST /sessions/auth/token.
/// Social layer; method from capture.
/// </summary>
public sealed class PostSessionsAuthToken : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/sessions/auth/token");
    }
}
