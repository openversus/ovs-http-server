using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.AccelByte;

/// <summary>
/// Any method /lobby/*.
/// Seen in: TS server: ALL /lobby/*.
/// Server only.
/// </summary>
public sealed class AnyLobbyAll : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/lobby/{**rest}");
    }
}
