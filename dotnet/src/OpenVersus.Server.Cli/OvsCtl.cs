using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenVersus.Server.Core.Hosting;
using OpenVersus.Server.Core.Settings;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenVersus.Server.Cli;

/// <summary>
/// ovsctl: status and settings of a running OpenVersus service, through its control API. Exit codes: 0 done,
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
            config.SetApplicationName("ovsctl");
            config.ConfigureConsole(console);
            config.AddCommand<HealthCommand>("health")
                .WithDescription("Every instance of every service: ready or not, version, uptime, last heard from. With a service or instance: its checks. --probe: this service only, for a container's health check.")
                .WithExample("health").WithExample("health", "matchmaking").WithExample("health", "--probe");
            config.AddCommand<StatusCommand>("status").WithDescription("The service's status: instance, version, uptime, whether settings are shared.");
            config.AddCommand<QueuesCommand>("queues").WithDescription("Who is waiting in each matchmaking queue, and for how long. Asks the matchmaking service by default.");
            config.AddCommand<OnlineCommand>("online").WithDescription("How many players are connected (--players: who, and through which gateway node and edge). Asks the access service by default.");
            config.AddCommand<MatchesCommand>("matches").WithDescription("Matches in progress, as the website's /matches shows them. Asks the match flow service by default.");
            config.AddCommand<LobbyCommand>("lobby").WithDescription("A custom lobby by its join code (any case) or its id, or every custom lobby (--all): leader, mode, and who is on which team, with each member's LobbyPlayerIndex. Asks the lobbies service unless --service or OVS_SERVICE says otherwise.")
                .WithExample("lobby", "GQRBM").WithExample("lobby", "--all");
            config.AddBranch("player", player =>
            {
                player.SetDescription("Player records. Asks the access service by default.");
                player.AddCommand<PlayerOnlineCommand>("online").WithDescription("Who is connected, with each one's name, username, id, Steam id, IP, gateway node and edge.");
                player.AddCommand<PlayerShowCommand>("show").WithDescription("A player's record, and their connection: the gateway node and the edge it goes through.");
                player.AddCommand<PlayerRenameCommand>("rename").WithDescription("Rename a player (an administrator's rename: no censoring).");
                player.AddCommand<PlayerDisconnectCommand>("disconnect").WithDescription("Close a player's game connection, as a heartbeat timeout would (the game logs out).");
                player.AddCommand<PlayerBanCommand>("ban").WithDescription("Ban the person behind a player for good: their IP, Steam, Epic, hardware and install ids and the player id; recorded in Mongo and the auto-ban file, and their connection is closed.");
                player.AddCommand<PlayerUnbanCommand>("unban").WithDescription("Lift a player's bans: their ban records are marked lifted (kept), and each identifier no other ban holds is let through; one still held is listed with what holds it (a ban file's entry is lifted with `bans lift`).");
            });
            config.AddBranch("steam", steam =>
            {
                steam.SetDescription("The Steam identity service: the connection to Steam and the auth sessions it holds.");
                steam.AddCommand<SteamStatusCommand>("status").WithDescription("Whether the service is connected to Steam, the sessions it holds by state, the verdict counts, and each held session.");
            });
            config.AddBranch("bans", bans =>
            {
                bans.SetDescription("Single ban values: the ban files' entries, as imported into Mongo (a person's bans are `player ban` and `player unban`). Asks the access service by default.");
                bans.AddCommand<BansLiftCommand>("lift").WithDescription("Lift one value (ip, cidr, steam, epic, hardware, install or id), or with --all every value that is one of a player's identifiers. The entry is kept, marked lifted: the import does not bring it back, and its line can stay in the file.")
                    .WithExample("bans", "lift", "steam", "76561198000000000", "--reason", "a false positive").WithExample("bans", "lift", "--all", "SomePlayer");
            });
            config.AddBranch("settings", settings =>
            {
                settings.SetDescription("Read and change settings while the service runs. get, set and unset ask the first running service that has the key, unless one is named.");
                settings.AddCommand<ListCommand>("list").WithDescription("Every setting, its value and its overrides.");
                settings.AddCommand<GetCommand>("get").WithDescription("One setting, with its description.");
                settings.AddCommand<SetCommand>("set").WithDescription("Override a setting (for every replica by default).");
                settings.AddCommand<UnsetCommand>("unset").WithDescription("Remove an override, so the value falls back.");
            });
        });
        return app.RunAsync(args);
    }

    /// <summary>Prints the reply's value (as JSON with --json, else with <paramref name="render"/>) or its error.</summary>
    /// <summary>
    /// The service a setting's command asks: the one chosen (--service, OVS_SERVICE, --socket, --port); else the
    /// command's default if it has the key, else the first running service that has it (a setting belongs to the services
    /// that read it: Gateway:* the ws and edge services, Realtime:* access and http, ...), named on the console unless
    /// --json. Null client: none has it, with why.
    /// </summary>
    public static async Task<(ControlClient? Client, string? Error)> ServiceWithAsync(IAnsiConsole console, KeySettings settings,
        Func<string, ControlClient>? clientFor = null)
    {
        if (settings.ServiceNamed)
        {
            return (ControlClient.For(settings), null);
        }

        bool reached = false;
        foreach (string service in KnownServices.All.Select(s => s.Name).Where(n => n != settings.Service).Prepend(settings.Service))
        {
            var client = clientFor?.Invoke(service) ?? ControlClient.For(settings, service);
            var probe = await client.GetAsync(settings.Key);
            if (probe.Value is not null || (!probe.Unreachable && probe.Error?.StartsWith("unknown setting", StringComparison.Ordinal) == false))
            {
                if (service != settings.Service && !settings.Json)
                {
                    console.MarkupLineInterpolated($"[grey]Asking the {service} service: it has {settings.Key}.[/]");
                }

                return (client, null);
            }

            reached |= !probe.Unreachable;
            client.Dispose();
        }

        return (null, reached ? $"no running service has a setting '{settings.Key}'" : "no service's control API could be reached");
    }

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

public sealed class HealthSettings : ConnectionSettings
{
    [CommandArgument(0, "[target]")]
    [Description("A service (its instances and their checks) or an instance (all it reported; a unique part of its id is enough).")]
    public string? Target { get; set; }

    [CommandOption("--probe")]
    [Description("Only the service this connects to: one line, exit 0 ready, 1 not ready, 2 unreachable. What a container's health check runs.")]
    public bool Probe { get; set; }
}

/// <summary>
/// The cluster's health from the instance registry (any service's control API reads it from Redis), or with --probe
/// the readiness of the one service reached. Exit 0 when everything shown is ready (a stopped instance aside), 1 when
/// something is not, 2 when no service could be reached.
/// </summary>
public sealed class HealthCommand : AsyncCommand<HealthSettings>
{
    private readonly IAnsiConsole _console;

    public HealthCommand(IAnsiConsole console)
    {
        _console = console;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, HealthSettings settings, CancellationToken cancellation)
    {
        using var client = ControlClient.For(settings);
        if (settings.Probe)
        {
            return await ProbeAsync(client, settings);
        }

        var cluster = await client.ClusterAsync();
        if (cluster.Error is not null && !cluster.Unreachable)
        {
            // No registry to read (no Redis): the one service reached is all there is to show.
            _console.MarkupLineInterpolated($"[yellow]No cluster view: {cluster.Error}.[/]");
            return await ProbeAsync(client, settings, everyCheck: true);
        }

        int done = OvsCtl.Report(_console, settings, cluster, view =>
        {
            if (settings.Target is null)
            {
                Overview(view);
            }
            else
            {
                Detail(view, settings.Target);
            }
        });
        if (done != OvsCtl.Done)
        {
            return done;
        }

        var shown = settings.Target is null ? cluster.Value!.Instances : Matching(cluster.Value!, settings.Target);
        if (settings.Target is not null && shown.Count == 0)
        {
            return OvsCtl.Refused;
        }

        return shown.All(i => i.State is "Ready" or "Stopped") ? OvsCtl.Done : OvsCtl.Refused;
    }

    private async Task<int> ProbeAsync(ControlClient client, ConnectionSettings settings, bool everyCheck = false)
    {
        var reply = await client.HealthAsync();
        int done = OvsCtl.Report(_console, settings, reply, health =>
        {
            var failing = health.Checks.Where(c => c.Value.Status != "Healthy").ToList();
            _console.MarkupLine(health.Ready ? $"[green]{Markup.Escape(settings.Service)}: ready[/]" : $"[red]{Markup.Escape(settings.Service)}: not ready[/]");
            foreach (var (name, check) in everyCheck ? health.Checks.ToList() : failing)
            {
                _console.MarkupLineInterpolated($"  {name}: {check.Status}{(check.Description is null ? "" : $" ({check.Description})")}");
            }
        });
        return done == OvsCtl.Done && !reply.Value!.Ready ? OvsCtl.Refused : done;
    }

    private void Overview(ClusterView view)
    {
        int ready = view.Instances.Count(i => i.State == "Ready");
        int live = view.Instances.Count(i => i.State != "Stopped");
        _console.MarkupLine(view.Instances.Count == 0
            ? "[yellow]No instance has registered (none running against this Redis, or all gone for more than 10 minutes).[/]"
            : $"{(ready == live ? "[green]" : "[red]")}{ready} of {live} running instances ready[/] in {view.Instances.Select(i => i.Service).Distinct().Count()} services");
        if (view.Instances.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded)
                .AddColumn("Service").AddColumn("Instance").AddColumn("State").AddColumn("Version").AddColumn("Up").AddColumn("Last heartbeat");
            foreach (var group in view.Instances.GroupBy(i => i.Service))
            {
                // Replicas of one service on different builds: worth seeing at a glance.
                bool skew = group.Where(i => i.Version is not null).Select(i => i.Version).Distinct().Count() > 1;
                foreach (var i in group)
                {
                    table.AddRow(new Markup(Markup.Escape(i.Service)), new Markup(Markup.Escape(i.Instance)), State(i),
                        new Markup(skew ? $"[yellow]{Markup.Escape(ShortVersion(i.Version))}[/]" : Markup.Escape(ShortVersion(i.Version))),
                        new Markup(i.Started == default || i.State is "Stopped" or "Missing" ? "[grey]-[/]" : Markup.Escape(Ago(view.At - i.Started))),
                        new Markup(Markup.Escape(Ago(view.At - i.Seen) + " ago")));
                }
            }

            _console.Write(table);
        }

        var none = view.Services.Except(view.Instances.Select(i => i.Service)).ToList();
        if (none.Count > 0)
        {
            _console.MarkupLineInterpolated($"[grey]No instances: {string.Join(", ", none)}[/]");
        }
    }

    private void Detail(ClusterView view, string target)
    {
        var shown = Matching(view, target);
        if (shown.Count == 0)
        {
            _console.MarkupLineInterpolated($"[red]No service or instance '{target}' in the registry.[/]");
            return;
        }

        foreach (var i in shown)
        {
            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow(new Markup("[grey]Service[/]"), new Markup(Markup.Escape(i.Service)));
            grid.AddRow(new Markup("[grey]Instance[/]"), new Markup(Markup.Escape(i.Instance)));
            grid.AddRow(new Markup("[grey]State[/]"), State(i));
            grid.AddRow(new Markup("[grey]Version[/]"), new Markup(Markup.Escape(i.Version ?? "?")));
            grid.AddRow(new Markup("[grey]Started[/]"), new Markup(i.Started == default ? "[grey]-[/]" : Markup.Escape($"{i.Started:u} ({Ago(view.At - i.Started)} ago)")));
            grid.AddRow(new Markup("[grey]Last heartbeat[/]"), new Markup(Markup.Escape($"{i.Seen:u} ({Ago(view.At - i.Seen)} ago)")));
            foreach (var (name, check) in i.Checks.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                string colour = check.Status == "Healthy" ? "green" : "red";
                grid.AddRow(new Markup($"[grey]Check {Markup.Escape(name)}[/]"),
                    new Markup($"[{colour}]{Markup.Escape(check.Status)}[/]{(check.Description is null ? "" : " " + Markup.Escape(check.Description))}"));
            }

            if (i.Checks.Count == 0 && i.State is "Ready" or "NotReady")
            {
                grid.AddRow(new Markup("[grey]Checks[/]"), new Markup("[grey]none: this service needs no store[/]"));
            }

            _console.Write(grid);
            _console.WriteLine();
        }
    }

    // A service's instances, or the instances whose id contains the target (an id, or a unique part of one).
    private static IReadOnlyList<InstanceReport> Matching(ClusterView view, string target)
    {
        var service = view.Instances.Where(i => string.Equals(i.Service, target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (service.Count > 0)
        {
            return service;
        }

        var exact = view.Instances.Where(i => i.Instance == target).ToList();
        return exact.Count > 0 ? exact : view.Instances.Where(i => i.Instance.Contains(target, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static Markup State(InstanceReport i) => i.State switch
    {
        "Ready" => new Markup("[green]ready[/]"),
        "NotReady" => new Markup($"[red]not ready[/]{Markup.Escape(Failing(i))}"),
        "Stopped" => new Markup("[grey]stopped[/]"),
        "Missing" => new Markup("[red]missing[/] [grey](no heartbeat)[/]"),
        _ => new Markup(Markup.Escape(i.State)),
    };

    private static string Failing(InstanceReport i)
    {
        var names = i.Checks.Where(c => c.Value.Status != "Healthy").Select(c => c.Key).ToList();
        return names.Count == 0 ? "" : $" ({string.Join(", ", names)})";
    }

    // "1.0.0+<40-hex commit>[-dirty]" -> "1.0.0+<7>[-dirty]".
    private static string ShortVersion(string? version)
    {
        if (version is null)
        {
            return "?";
        }

        int plus = version.IndexOf('+');
        if (plus < 0 || version.Length - plus - 1 < 7)
        {
            return version;
        }

        string commit = version[(plus + 1)..];
        string suffix = commit.EndsWith("-dirty", StringComparison.Ordinal) ? "-dirty" : "";
        return $"{version[..plus]}+{commit[..7]}{suffix}";
    }

    private static string Ago(TimeSpan span) => span.TotalSeconds < 0 ? "0s"
        : span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds}s"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m{span.Seconds:00}s"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours}h{span.Minutes:00}m"
        : $"{(int)span.TotalDays}d{span.Hours:00}h";
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
        var (found, error) = await OvsCtl.ServiceWithAsync(_console, settings);
        if (found is not { } client)
        {
            _console.MarkupLineInterpolated($"[red]{error}[/]");
            return OvsCtl.Refused;
        }

        using var _ = client;
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
        var (found, error) = await OvsCtl.ServiceWithAsync(_console, settings);
        if (found is not { } client)
        {
            _console.MarkupLineInterpolated($"[red]{error}[/]");
            return OvsCtl.Refused;
        }

        using var _ = client;
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
        var (found, error) = await OvsCtl.ServiceWithAsync(_console, settings);
        if (found is not { } client)
        {
            _console.MarkupLineInterpolated($"[red]{error}[/]");
            return OvsCtl.Refused;
        }

        using var _ = client;
        return OvsCtl.Report(_console, settings, await client.UnsetAsync(settings.Key, settings.Scope), setting =>
            _console.Write(OvsCtl.SettingsTable([setting])));
    }
}
