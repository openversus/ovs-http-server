using System.Net;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Settings;
using OpenVersus.Server.Proxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

// The migration's reverse proxy (PROXY_PORT): the game talks to this, and each request goes to the C# http service if
// its route is ported (Proxy:PortedRoutes), else to the TS server. Stateless: any number of replicas.
var builder = OpenVersusHost.CreateBuilder(KnownServices.Proxy, args);
builder.AddSetting<ProxySettings>("Proxy");

var initial = builder.Configuration.GetSection("Proxy").Get<ProxySettings>() ?? new ProxySettings();
var (routes, clusters) = ProxyRoutes.Build(initial);
builder.Services.AddReverseProxy()
    .LoadFromMemory(routes, clusters)
    .AddTransforms(context =>
    {
        // Both backends see the Host the game sent: the TS server compares it with WB_DOMAIN.
        context.AddOriginalHost(true);
        // The client's address for both backends (see ClientAddress in the http service): a proxy in front of this one
        // has already said it in X-Real-IP; else it is this connection's. X-Forwarded-For is appended to as usual.
        // No X-Forwarded-Host: the TS server would read the host name as the client's address.
        context.AddXForwardedFor(action: ForwardedTransformActions.Append);
        context.AddXForwardedProto(action: ForwardedTransformActions.Set);
        context.AddXForwardedHost(action: ForwardedTransformActions.Off);
        context.AddXForwardedPrefix(action: ForwardedTransformActions.Off);
        context.AddRequestTransform(transform =>
        {
            var request = transform.HttpContext.Request;
            if (!request.Headers.ContainsKey("X-Real-IP") && transform.HttpContext.Connection.RemoteIpAddress is { } remote)
            {
                transform.ProxyRequest.Headers.TryAddWithoutValidation("X-Real-IP", (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString());
            }

            return ValueTask.CompletedTask;
        });
    });
builder.Services.AddHostedService<ProxyRoutesSync>();

var app = builder.Build();
app.UseOpenVersus();
app.MapReverseProxy();
app.Logger.LogWarning("MIGRATION BRIDGE: this proxy ties the C# services to the TS server until every route is ported; see dotnet/docs/MIGRATION-BRIDGES.md (1)");
app.Logger.LogInformation("Routes to C# ({CSharp}): {Routes}; everything else to {Ts}", initial.CSharpUrl, initial.PortedRoutes, initial.TsUrl);
app.Run();

/// <summary>The entry point, visible to the tests' WebApplicationFactory.</summary>
public partial class Program;
