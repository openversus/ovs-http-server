using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Endpoints.Game.Profiles;

/// <summary>
/// PUT /profiles/{id}.
/// Seen in: binary 0x145066ea0.
/// </summary>
public sealed class PutProfilesById : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.PUT);
        Routes("/profiles/{id}");
    }
}
