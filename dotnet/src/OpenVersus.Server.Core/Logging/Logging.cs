using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Settings;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace OpenVersus.Server.Core.Logging;

/// <summary>Logging settings.</summary>
public sealed class LogSettings
{
    [Description("The lowest level written to the log: Verbose, Debug, Information, Warning, Error or Fatal.")]
    public LogEventLevel Level { get; set; } = LogEventLevel.Information;
}

/// <summary>
/// Serilog under Microsoft.Extensions.Logging: code logs through <c>ILogger</c>, Serilog writes it. Output goes to the
/// console, which is what a container's log collects; a deployment that also wants files adds a sink through
/// configuration (<c>Serilog:WriteTo:0:Name=File</c> with its <c>Args:path</c>, which the Compose files do into a shared
/// log volume). The level is the <c>Log:Level</c> setting, so it can be changed while the service runs; per-source
/// levels come from the <c>Serilog</c> section of appsettings.
/// </summary>
public static class OpenVersusLogging
{
    private const string OutputTemplate = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    internal static WebApplicationBuilder AddOpenVersusLogging(this WebApplicationBuilder builder)
    {
        builder.AddSetting<LogSettings>("Log");
        var levels = new LoggingLevelSwitch(builder.Configuration.GetSection("Log").Get<LogSettings>()?.Level ?? LogEventLevel.Information);
        builder.Services.AddSingleton(levels);
        builder.Services.AddSerilog((services, config) => config
            .ReadFrom.Configuration(builder.Configuration)
            .MinimumLevel.ControlledBy(levels)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: OutputTemplate));
        return builder;
    }

    /// <summary>Follows the <c>Log:Level</c> setting from then on.</summary>
    internal static void FollowLevelSetting(IServiceProvider services)
    {
        var levels = services.GetRequiredService<LoggingLevelSwitch>();
        services.GetRequiredService<IOptionsMonitor<LogSettings>>().OnChange(s => levels.MinimumLevel = s.Level);
    }
}
