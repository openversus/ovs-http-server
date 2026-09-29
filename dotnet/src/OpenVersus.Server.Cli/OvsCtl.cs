using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenVersus.Server.Cli;

/// <summary>
/// ovs-ctl: status and settings of a running OpenVersus service, through its control API. Exit codes: 0 done,
/// 1 refused or not found, 2 the service could not be reached.
/// </summary>
public static class OvsCtl
{
    public const int Done = 0;
    public const int Refused = 1;
    public const int Unreachable = 2;

    public static Task<int> RunAsync(string[] args, IAnsiConsole console)
    {
        var services = new ServiceCollection();
        services.AddSingleton(console);
        var app = new CommandApp(new TypeRegistrar(services));
        app.Configure(config =>
        {
            config.SetApplicationName("ovs-ctl");
            config.ConfigureConsole(console);
            config.AddCommand<StatusCommand>("status").WithDescription("The service's status: instance, version, uptime, whether settings are shared.");
            config.AddCommand<QueuesCommand>("queues").WithDescription("Who is waiting in each matchmaking queue, and for how long.");
            config.AddCommand<OnlineCommand>("online").WithDescription("How many players are connected (--players: who).");
            config.AddCommand<MatchesCommand>("matches").WithDescription("Matches in progress, as the website's /matches shows them.");
            config.AddBranch("player", player =>
            {
                player.SetDescription("Player records.");
                player.AddCommand<PlayerShowCommand>("show").WithDescription("A player's record.");
                player.AddCommand<PlayerRenameCommand>("rename").WithDescription("Rename a player (an administrator's rename: no censoring).");
            });
            config.AddBranch("settings", settings =>
            {
                settings.SetDescription("Read and change settings while the service runs.");
                settings.AddCommand<ListCommand>("list").WithDescription("Every setting, its value and its overrides.");
                settings.AddCommand<GetCommand>("get").WithDescription("One setting, with its description.");
                settings.AddCommand<SetCommand>("set").WithDescription("Override a setting (for every replica by default).");
                settings.AddCommand<UnsetCommand>("unset").WithDescription("Remove an override, so the value falls back.");
            });
        });
        return app.RunAsync(args);
    }

    /// <summary>Prints the reply's value (as JSON with --json, else with <paramref name="render"/>) or its error.</summary>
    internal static int Report<T>(IAnsiConsole console, ConnectionSettings settings, ControlReply<T> reply, Action<T> render)
    {
        if (reply.Error is not null)
        {
            console.MarkupLineInterpolated($"[red]{reply.Error}[/]");
            return reply.Unreachable ? Unreachable : Refused;
        }

        if (settings.Json)
        {
            // Straight to the output, not through the console's line wrapping, or a long value breaks the JSON.
            console.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(reply.Value, JsonSerializerOptions.Web));
        }
        else
        {
            render(reply.Value!);
        }

        return Done;
    }

    internal static Table SettingsTable(IEnumerable<SettingView> settings)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Setting").AddColumn("Value").AddColumn("Cluster").AddColumn("Instance").AddColumn("Type");
        foreach (var s in settings)
        {
            string key = Markup.Escape(s.Key) + (s.RestartRequired ? " [grey](restart)[/]" : "") + (s.Secret ? " [grey](secret)[/]" : "");
            table.AddRow(new Markup(key), Cell(s.Value), Cell(s.Cluster), Cell(s.Instance), new Markup($"[grey]{Markup.Escape(s.Type)}[/]"));
        }

        return table;
    }

    private static Markup Cell(string? value) => value is null ? new Markup("[grey]-[/]") : new Markup(Markup.Escape(value));
}

public sealed class StatusCommand : AsyncCommand<ConnectionSettings>
{
    private readonly IAnsiConsole _console;

    public StatusCommand(IAnsiConsole console)
    {
        _console = console;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, ConnectionSettings settings, CancellationToken cancellation)
    {
        using var client = ControlClient.For(settings);
        return OvsCtl.Report(_console, settings, await client.StatusAsync(), status =>
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("[grey]Service[/]", Markup.Escape(status.Service));
            grid.AddRow("[grey]Instance[/]", Markup.Escape(status.Instance));
            grid.AddRow("[grey]Version[/]", Markup.Escape(status.Version ?? "?"));
            grid.AddRow("[grey]Up[/]", Markup.Escape($"{TimeSpan.FromSeconds(status.UptimeSeconds)} (since {status.Started:u})"));
            grid.AddRow("[grey]Shared settings[/]", status.SharedSettings ? "[green]yes (Redis)[/]" : "[yellow]no: this instance only[/]");
            grid.AddRow("[grey]Via[/]", Markup.Escape(client.Where));
            _console.Write(grid);
        });
    }
}

public sealed class ListSettings : ConnectionSettings
{
    [CommandArgument(0, "[FILTER]")]
    [Description("Only settings whose key contains this.")]
    public string? Filter { get; set; }
}

public sealed class ListCommand : AsyncCommand<ListSettings>
{
    private readonly IAnsiConsole _console;

    public ListCommand(IAnsiConsole console)
    {
        _console = console;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, ListSettings settings, CancellationToken cancellation)
    {
        using var client = ControlClient.For(settings);
        var reply = await client.ListAsync();
        if (reply.Value is not null && settings.Filter is not null)
        {
            reply = reply with { Value = reply.Value.Where(s => s.Key.Contains(settings.Filter, StringComparison.OrdinalIgnoreCase)).ToArray() };
        }

        return OvsCtl.Report(_console, settings, reply, all => _console.Write(OvsCtl.SettingsTable(all)));
    }
}

public class KeySettings : ConnectionSettings
{
    [CommandArgument(0, "<KEY>")]
    [Description("The setting's key, e.g. Log:Level.")]
    public string Key { get; set; } = "";
}

public sealed class GetCommand : AsyncCommand<KeySettings>
{
    private readonly IAnsiConsole _console;

    public GetCommand(IAnsiConsole console)
    {
        _console = console;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, KeySettings settings, CancellationToken cancellation)
    {
        using var client = ControlClient.For(settings);
        return OvsCtl.Report(_console, settings, await client.GetAsync(settings.Key), setting =>
        {
            _console.Write(OvsCtl.SettingsTable([setting]));
            if (setting.Description != "")
            {
                _console.MarkupLineInterpolated($"[grey]{setting.Description}[/]");
            }
        });
    }
}

public class ScopedSettings : KeySettings
{
    [CommandOption("--scope <SCOPE>")]
    [Description("cluster (every replica, through Redis) or instance (only the one reached).")]
    [DefaultValue(SettingScope.Cluster)]
    public SettingScope Scope { get; set; } = SettingScope.Cluster;
}

public sealed class SetSettings : ScopedSettings
{
    [CommandArgument(1, "<VALUE>")]
    [Description("The new value.")]
    public string Value { get; set; } = "";
}

public sealed class SetCommand : AsyncCommand<SetSettings>
{
    private readonly IAnsiConsole _console;

    public SetCommand(IAnsiConsole console)
    {
        _console = console;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, SetSettings settings, CancellationToken cancellation)
    {
        using var client = ControlClient.For(settings);
        return OvsCtl.Report(_console, settings, await client.SetAsync(settings.Key, settings.Value, settings.Scope), setting =>
            _console.Write(OvsCtl.SettingsTable([setting])));
    }
}

public sealed class UnsetCommand : AsyncCommand<ScopedSettings>
{
    private readonly IAnsiConsole _console;

    public UnsetCommand(IAnsiConsole console)
    {
        _console = console;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, ScopedSettings settings, CancellationToken cancellation)
    {
        using var client = ControlClient.For(settings);
        return OvsCtl.Report(_console, settings, await client.UnsetAsync(settings.Key, settings.Scope), setting =>
            _console.Write(OvsCtl.SettingsTable([setting])));
    }
}
