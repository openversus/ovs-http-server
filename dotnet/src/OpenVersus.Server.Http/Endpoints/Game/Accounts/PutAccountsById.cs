using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// PUT /accounts/{id}.
/// Seen in: binary 0x144fddd00.
/// Also 0x144fddeb0.
/// </summary>
public sealed class PutAccountsById : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/accounts/{id}");
    }
}
