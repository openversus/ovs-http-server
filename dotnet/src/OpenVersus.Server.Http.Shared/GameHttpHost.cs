using System.Reflection;
using FastEndpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Access;
using OpenVersus.Server.Core.Clients;
using OpenVersus.Server.Core.Hiss;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Preferences;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Http.Shared.Hosting;
using OpenVersus.Server.Http.Shared.Stubs;

namespace OpenVersus.Server.Http.Shared;

/// <summary>
/// The request pipeline every service that answers the game's HTTP runs, so that a route answers the same whichever
/// service owns it (docs/routes.json, owner): the Hydra method override and bodies, the session token, the client gate,
/// the gameplay preferences recorder, then the service's own endpoints.
/// </summary>
public static class GameHttpHost
{
    /// <summary>
    /// What the pipeline needs, and FastEndpoints with the endpoints of <paramref name="endpoints"/> only: a service
    /// answers its own routes and no other's, whatever else its process has loaded.
    /// </summary>
    public static WebApplicationBuilder AddGameHttp(this WebApplicationBuilder builder, Assembly endpoints)
    {
        builder.AddSetting<StubSettings>("Stubs");
        // The Crc in the hiss and in every TS catch-all answer (Hiss:ContentRevision).
        builder.AddSetting<HissSettings>("Hiss");
        // The session token check reads these.
        builder.AddSetting<AccessSettings>("Access");
        builder.AddSetting<RealtimeSettings>("Realtime");
        builder.AddClientUpdateGate();
        builder.AddGameplayPreferences();
        builder.Services.AddFastEndpoints(o =>
        {
            o.DisableAutoDiscovery = true;
            o.Assemblies = [endpoints];
        });
        return builder;
    }

    /// <summary>
    /// The pipeline, after whatever the service puts first (the HTTP service's /batch). With <paramref name="fallback"/>,
    /// a path no endpoint answers gets the stub fallback after the token check, as in the TS server; only the service the
    /// router sends unknown paths to has one.
    /// </summary>
    public static WebApplication UseGameHttp(this WebApplication app, bool fallback = false)
    {
        // Before routing: the Hydra SDK sends some GETs as PUT with the real method in a header.
        app.UseHydraMethodOverride();
        // The game's Hydra binary bodies become JSON for the endpoints, and their JSON answers go back as Hydra.
        app.UseHydraBodies();
        app.UseRouting();
        // Names the endpoint that answered (ported or stub), for captures and the route map tests.
        app.Use((context, next) =>
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<EndpointDefinition>() is { } endpoint)
            {
                context.Response.Headers[Stub.EndpointHeader] = endpoint.EndpointType.Name;
            }

            return next(context);
        });
        // Game routes answer only a valid session token, as the TS server's hydraTokenMiddleware.
        app.UseHydraToken();
        // Gameplay transitions only from a current, registered client, as the TS server's requireCurrentClientForGameplay.
        app.UseClientGameplayGate();
        // The player's input settings the game sends with party-lobby requests (Core/Preferences).
        app.UseGameplayPreferencesRecorder();
        app.UseFastEndpoints(c =>
        {
            // ASP.NET's authorization is not used: game endpoints (a namespace with .Endpoints.Game) require the session
            // token through RequiresHydraToken (see HydraToken), unless they carry NoHydraToken; the other kinds
            // (OpenVersus client, website, rollback server) do their own checks, as in the TS server, except those
            // marked HydraTokenRequired (behind the TS token middleware too).
            c.Endpoints.Configurator = ep =>
            {
                ep.AllowAnonymous();
                // FastEndpoints does not carry class attributes into the endpoint's metadata, so the exemption is read here.
                var exemption = ep.EndpointType.GetCustomAttributes(typeof(NoHydraTokenAttribute), false).OfType<NoHydraTokenAttribute>().SingleOrDefault();
                bool required = ep.EndpointType.IsDefined(typeof(HydraTokenRequiredAttribute), false);
                if ((IsGameEndpoint(ep.EndpointType) || required) && (exemption is null || exemption.RouteValue is not null))
                {
                    ep.Options(b => b.WithMetadata(exemption is null ? [RequiresHydraToken.Instance] : [RequiresHydraToken.Instance, exemption]));
                }
            };
        });
        app.UseOpenVersus();
        if (fallback)
        {
            // The TS server checks the token before it knows whether a path exists. Every path, files included: the
            // TS catch-all answered /favicon.ico too, where ASP.NET's default fallback leaves anything with a dot.
            app.MapFallback("{**path}", Stub.FallbackAsync).WithMetadata(RequiresHydraToken.Instance);
        }

        return app;
    }

    /// <summary>A game route's endpoint: its namespace has an Endpoints.Game segment, in whichever service it lives.</summary>
    public static bool IsGameEndpoint(Type endpoint) =>
        endpoint.Namespace is { } ns && (ns.Contains(".Endpoints.Game.", StringComparison.Ordinal) || ns.EndsWith(".Endpoints.Game", StringComparison.Ordinal));
}
