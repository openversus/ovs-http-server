using FastEndpoints;
using OpenVersus.Server.Core.Matches;

namespace OpenVersus.Server.MatchFlow.Endpoints.OpenVersus.Rollback;

/// <summary>
/// POST /mvsi_register: the older registry, for MVSI rollback servers (<see cref="IRollbackCallbacks.RegisterLegacyAsync"/>).
/// Seen in: TS server: POST /mvsi_register; ovs-rollback-server HTTPHelper (an MVSI server).
/// Server only.
/// </summary>
public sealed class PostMvsiRegister : RollbackEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.POST);
        Routes("/mvsi_register");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var callbacks = Resolve<IRollbackCallbacks>();
        await SendRegistrationAsync(callbacks, await callbacks.RegisterLegacyAsync(await ReadBodyAsync(ct)), ct);
    }
}
