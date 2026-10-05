using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Social.Endpoints.Game.Ssc;

/// <summary>
/// Any method /ssc/invoke/follow_account.
/// Seen in: binary ssc name.
/// Ssc: binary.
/// </summary>
public sealed class AnyFollowAccount : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/ssc/invoke/follow_account");
    }
}
