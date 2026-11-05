using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Social.Endpoints.Game.Relationships;

/// <summary>
/// Any method /relationships/followers.
/// Seen in: binary fragment.
/// Binary fragment; no builder found yet.
/// </summary>
public sealed class AnyRelationshipsFollowers : TsCatchAllEndpoint
{
    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes("/relationships/followers");
    }
}
