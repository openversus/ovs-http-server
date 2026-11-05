using FastEndpoints;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.MatchFlow.Endpoints.Game.Ssc;

/// <summary>
/// An SSC route known only by its name in the game's binary: never captured, never seen in a server log, and never
/// handled by the TS server, which answered it with its catch-all. Answers what the game has always got
/// (<see cref="TsCatchAllEndpoint"/>), with any method.
/// </summary>
public abstract class NeverSeenSscEndpoint : TsCatchAllEndpoint
{
    /// <summary>The route's name: /ssc/invoke/{Route}.</summary>
    protected abstract string Route { get; }

    public override void Configure()
    {
        Verbs(FastEndpoints.Http.GET, FastEndpoints.Http.PUT, FastEndpoints.Http.POST, FastEndpoints.Http.DELETE);
        Routes($"/ssc/invoke/{Route}");
    }
}
