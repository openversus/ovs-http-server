using System.ComponentModel;
using OpenVersus.Server.Core.Ops;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenVersus.Server.Cli;

public sealed class QueuesCommand : AsyncCommand<ConnectionSettings>
{
    private readonly IAnsiConsole _console;

    public QueuesCommand(IAnsiConsole console)
    {
        _console = console;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, ConnectionSettings settings, CancellationToken cancellation)
    {
        using var client = ControlClient.For(settings);
        return OvsCtl.Report(_console, settings, await client.QueuesAsync(), queues =>
        {
            foreach (var queue in queues)
            {
                _console.MarkupLineInterpolated($"[bold]{queue.Queue}[/]: {queue.Players} player(s) in {queue.Tickets} ticket(s)");
                if (queue.Entries.Count == 0)
                {
                    continue;
                }

                var table = new Table().Border(TableBorder.Rounded).AddColumn("Waiting").AddColumn("Players").AddColumn("Skill").AddColumn("Party");
                foreach (var ticket in queue.Entries.OrderByDescending(t => t.WaitingSeconds))
                {
                    table.AddRow(
                        Markup.Escape(TimeSpan.FromSeconds(ticket.WaitingSeconds).ToString()),
                        Markup.Escape(string.Join(", ", ticket.Players.Select(p => $"{p.Name} ({p.Id})"))),
                        Markup.Escape(string.Join(", ", ticket.Players.Select(p => p.Skill?.ToString("0") ?? "?"))),
                        $"[grey]{Markup.Escape(ticket.PartyId)}[/]");
                }

                _console.Write(table);
            }
        });
    }
}

public sealed class OnlineSettings : ConnectionSettings
{
    [CommandOption("--players")]
    [Description("List the players too, not just how many.")]
    public bool Players { get; set; }
}

public sealed class OnlineCommand : AsyncCommand<OnlineSettings>
{
    private readonly IAnsiConsole _console;

    public OnlineCommand(IAnsiConsole console)
    {
        _console = console;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, OnlineSettings settings, CancellationToken cancellation)
    {
        using var client = ControlClient.For(settings);
        return OvsCtl.Report(_console, settings, await client.OnlineAsync(settings.Players), online =>
        {
            _console.MarkupLineInterpolated($"[bold]{online.Count}[/] player(s) connected");
            if (online.Players is { Count: > 0 } players)
            {
                var table = new Table().Border(TableBorder.Rounded).AddColumn("Name").AddColumn("Id").AddColumn("Status");
                foreach (var p in players)
                {
                    table.AddRow(Markup.Escape(p.Name), $"[grey]{Markup.Escape(p.Id)}[/]", Markup.Escape(p.Status ?? "-"));
                }

                _console.Write(table);
            }
        });
    }
}

public sealed class MatchesCommand : AsyncCommand<ConnectionSettings>
{
    private readonly IAnsiConsole _console;

    public MatchesCommand(IAnsiConsole console)
    {
        _console = console;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, ConnectionSettings settings, CancellationToken cancellation)
    {
        using var client = ControlClient.For(settings);
        return OvsCtl.Report(_console, settings, await client.MatchesAsync(), matches =>
        {
            _console.MarkupLineInterpolated($"[bold]{matches.Length}[/] match(es) in progress");
            if (matches.Length == 0)
            {
                return;
            }

            var table = new Table().Border(TableBorder.Rounded).AddColumn("Mode").AddColumn("Score").AddColumn("Teams").AddColumn("Set");
            foreach (var m in matches)
            {
                string teams = string.Join("  vs  ", m.Teams.Where(t => t.Key != "4").OrderBy(t => t.Key)
                    .Select(t => string.Join(" & ", t.Value.Select(p => $"{p.Name} ({p.Character.Replace("character_", "")})"))));
                table.AddRow(
                    Markup.Escape(m.Mode ?? "?"),
                    Markup.Escape(string.Join(" - ", m.Scores)) + (m.Conceded ? " [yellow](conceded)[/]" : ""),
                    Markup.Escape(teams),
                    $"[grey]{Markup.Escape(m.SetId)}[/]");
            }

            _console.Write(table);
        });
    }
}

public class PlayerSettings : ConnectionSettings
{
    [CommandArgument(0, "<WHO>")]
    [Description("The player: their id, their exact name (any case), or their Steam id.")]
    public string Who { get; set; } = "";
}

public sealed class RenameSettings : PlayerSettings
{
    [CommandArgument(1, "<NAME>")]
    [Description("The new name (at most 24 characters; must not be taken, in any case).")]
    public string Name { get; set; } = "";
}

internal static class PlayerRender
{
    public static void Show(IAnsiConsole console, PlayerView p)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[grey]Name[/]", Markup.Escape(p.Name));
        grid.AddRow("[grey]Id[/]", Markup.Escape(p.Id));
        grid.AddRow("[grey]Generated name[/]", Markup.Escape(p.HydraUsername ?? "-"));
        grid.AddRow("[grey]Steam id[/]", Markup.Escape(p.SteamId is { Length: > 0 } s ? s : "-"));
        grid.AddRow("[grey]Public id[/]", Markup.Escape(p.PublicId ?? "-"));
        grid.AddRow("[grey]Connected[/]", p.Online ? $"[green]yes[/] ({Markup.Escape(p.Status ?? "?")})" : "no");
        console.Write(grid);
    }
}

public sealed class PlayerShowCommand : AsyncCommand<PlayerSettings>
{
    private readonly IAnsiConsole _console;

    public PlayerShowCommand(IAnsiConsole console)
    {
        _console = console;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, PlayerSettings settings, CancellationToken cancellation)
    {
        using var client = ControlClient.For(settings);
        return OvsCtl.Report(_console, settings, await client.PlayerAsync(settings.Who), p => PlayerRender.Show(_console, p));
    }
}

public sealed class PlayerRenameCommand : AsyncCommand<RenameSettings>
{
    private readonly IAnsiConsole _console;

    public PlayerRenameCommand(IAnsiConsole console)
    {
        _console = console;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, RenameSettings settings, CancellationToken cancellation)
    {
        using var client = ControlClient.For(settings);
        return OvsCtl.Report(_console, settings, await client.RenameAsync(settings.Who, settings.Name), p => PlayerRender.Show(_console, p));
    }
}
