using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Access.Endpoints.Game.Sessions;

/// <summary>
/// Any method /sessions/auth/password.
/// Seen in: binary 0x140f9baf0.
/// Social layer; method from unknown.
/// </summary>
public sealed class AnySessionsAuthPassword : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/sessions/auth/password");
    }
}
