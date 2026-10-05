using FastEndpoints;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /ovs_register: the match's registry for its rollback server, or for a P2P node to learn its role, signed; then
/// its players are told to connect, unless its host node will say when (<see cref="IRollbackCallbacks.RegisterAsync"/>).
/// Seen in: TS server: POST /ovs_register; ovs-rollback-server HTTPHelper (FetchMatchConfigAsync, FetchSignedMatchConfigAsync).
/// Server only.
/// </summary>
public sealed class PostOvsRegister : RollbackEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/ovs_register");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var callbacks = Resolve<IRollbackCallbacks>();
        await SendRegistrationAsync(callbacks, await callbacks.RegisterAsync(await ReadBodyAsync(ct)), ct);
    }
}
