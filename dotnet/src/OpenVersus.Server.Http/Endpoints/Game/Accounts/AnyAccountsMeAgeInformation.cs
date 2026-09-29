using FastEndpoints;
using OpenVersus.Server.Http.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Accounts;

/// <summary>
/// Any method /accounts/me/age_information.
/// Seen in: binary 0x140fa1bd0.
/// Social layer; method from unknown.
/// </summary>
public sealed class AnyAccountsMeAgeInformation : StubEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/accounts/me/age_information");
    }
}
