using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OpenVersus.Server.Core.Bans;

/// <summary>
/// One ban as the services record it, the same in Mongo (player_bans) and in the auto-ban file: who, every identifier
/// banned, what they did (the name they had and the one they tried), what matched, and how it was enforced.
/// </summary>
public sealed record BanRecord(
    string BanId,
    DateTimeOffset At,
    string Reason,
    string Source,
    BanIdentifiers Who,
    string NameAtBan = "",
    string AttemptedName = "",
    string PlayerCreatedAt = "",
    string MatchedList = "",
    string MatchedTerm = "",
    string RequestIp = "",
    string UserAgent = "",
    bool Online = false,
    bool Disconnected = false);

// The auto-ban file: a YAML sequence of records, appended to (one record per write, under an exclusive lock: several
// services ban), never rewritten. Comments and an empty file are fine; a record's identifiers are what is banned.
//   - ban_id: '...'
//     at: '2026-10-07T21:14:03.512Z'
//     reason: '...'
//     source: 'namechange'          (namechange, login, sweep, manual)
//     player: {id, name_at_ban, attempted_name, created_at}
//     matched: {list, term}
//     identifiers: {ip, steam_id, epic_id, hardware_id, install_id}
//     request: {ip, user_agent}
//     online: true
//     disconnected: true
internal static class AutoBans
{
    /// <summary>The identifiers of every record (with the player id), in file order. Throws when the file is not a list of records.</summary>
    public static List<BanIdentifiers> Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is YamlScalarNode { Value: null or "" })
        {
            return [];
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlSequenceNode records)
        {
            throw new FormatException("expected one YAML document holding a list of ban records");
        }

        var all = new List<BanIdentifiers>(records.Children.Count);
        foreach (var node in records)
        {
            if (node is not YamlMappingNode record)
            {
                throw new FormatException($"line {node.Start.Line}: a ban record must be a mapping");
            }

            var ids = Child(record, "identifiers") as YamlMappingNode;
            all.Add(new BanIdentifiers(
                Text(ids, "ip"), Text(ids, "steam_id"), Text(ids, "epic_id"), Text(ids, "hardware_id"), Text(ids, "install_id"),
                Text(Child(record, "player") as YamlMappingNode, "id")));
        }

        return all;
    }

    /// <summary>The record as one item of the file's list, ending in a newline.</summary>
    public static string ToYaml(BanRecord r)
    {
        static YamlScalarNode S(string value) => new(value) { Style = ScalarStyle.SingleQuoted };
        static YamlScalarNode B(bool value) => new(value ? "true" : "false") { Style = ScalarStyle.Plain };
        var record = new YamlMappingNode
        {
            { "ban_id", S(r.BanId) },
            { "at", S(r.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")) },
            { "reason", S(r.Reason) },
            { "source", S(r.Source) },
            { "player", new YamlMappingNode { { "id", S(r.Who.PlayerId) }, { "name_at_ban", S(r.NameAtBan) }, { "attempted_name", S(r.AttemptedName) }, { "created_at", S(r.PlayerCreatedAt) } } },
            { "matched", new YamlMappingNode { { "list", S(r.MatchedList) }, { "term", S(r.MatchedTerm) } } },
            {
                "identifiers", new YamlMappingNode
                {
                    { "ip", S(r.Who.Ip) }, { "steam_id", S(r.Who.SteamId) }, { "epic_id", S(r.Who.EpicId) },
                    { "hardware_id", S(r.Who.HardwareId) }, { "install_id", S(r.Who.InstallId) },
                }
            },
            { "request", new YamlMappingNode { { "ip", S(r.RequestIp) }, { "user_agent", S(r.UserAgent) } } },
            { "online", B(r.Online) },
            { "disconnected", B(r.Disconnected) },
        };

        var writer = new StringWriter { NewLine = "\n" };
        new YamlStream(new YamlDocument(new YamlSequenceNode(record))).Save(writer, assignAnchors: false);
        string text = writer.ToString();
        // Save ends the document with "...": an appended item must not.
        int end = text.LastIndexOf("...", StringComparison.Ordinal);
        return (end >= 0 && text[end..].Trim() == "..." ? text[..end] : text).TrimEnd() + "\n";
    }

    /// <summary>Appends the record to the file (created when missing) under an exclusive lock, retried while another service holds it.</summary>
    public static void Append(string path, BanRecord record)
    {
        byte[] item = System.Text.Encoding.UTF8.GetBytes(ToYaml(record));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                bool newline = false;
                if (file.Length > 0)
                {
                    file.Seek(-1, SeekOrigin.End);
                    newline = file.ReadByte() != '\n';
                }

                file.Seek(0, SeekOrigin.End);
                if (newline)
                {
                    file.WriteByte((byte)'\n');
                }

                file.Write(item);
                file.Flush(flushToDisk: true);
                return;
            }
            catch (IOException) when (attempt < 40 && File.Exists(path))
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>Logs an error at startup when the auto-ban file cannot be written, instead of at the first ban.</summary>
    public static void CheckWritable(string? path, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            log.LogWarning("No auto-ban file (Bans:AutoBansFile): bans this service makes are kept in Mongo and Redis only");
            return;
        }

        try
        {
            using var _ = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogError(e, "The auto-ban file {Path} cannot be written: bans this service makes are kept in Mongo and Redis only", Path.GetFullPath(path));
        }
    }

    private static YamlNode? Child(YamlMappingNode? map, string key) =>
        map is not null && map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    private static string Text(YamlMappingNode? map, string key) => Child(map, key) is YamlScalarNode { Value: { } value } ? value : "";

    /// <summary>The auto-ban file's identifiers by kind, reread when the file changes (after it has settled).</summary>
    internal sealed class Reader
    {
        private readonly Lock _lock = new();
        private string? _path;
        private DateTime _stamp = DateTime.MinValue;
        private Dictionary<BanKind, HashSet<string>> _byKind = [];
        private bool _loaded;

        public Reader Get(string? configured, DateTime now, ILogger log)
        {
            lock (_lock)
            {
                string? path = string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);
                if (path != _path)
                {
                    (_path, _stamp, _byKind, _loaded) = (path, DateTime.MinValue, [], false);
                }

                if (path is null || !File.Exists(path))
                {
                    return this;
                }

                var stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == _stamp || (_loaded && (now - stamp).Duration() < NameRules.SettleTime))
                {
                    return this;
                }

                try
                {
                    var byKind = new Dictionary<BanKind, HashSet<string>>();
                    var records = Parse(File.ReadAllText(path));
                    foreach (var (kind, value) in records.SelectMany(r => r.Known()))
                    {
                        (byKind.TryGetValue(kind, out var set) ? set : byKind[kind] = []).Add(BanService.Canonical(kind, value));
                    }

                    (_byKind, _stamp, _loaded) = (byKind, stamp, true);
                    log.LogInformation("Auto-ban file {Path}: {Count} records", path, records.Count);
                }
                catch (Exception e) when (e is FormatException or YamlException or IOException or UnauthorizedAccessException)
                {
                    // The last good records stay; bad content is not read again until the file changes, but with nothing
                    // loaded yet (a mount not readable yet) every check tries again.
                    if (_loaded)
                    {
                        _stamp = stamp;
                    }

                    log.LogError(e, "The auto-ban file {Path} could not be read: keeping its last good records", path);
                }

                return this;
            }
        }

        public bool Contains(BanKind kind, string value)
        {
            lock (_lock)
            {
                return _byKind.TryGetValue(kind, out var set) && set.Contains(value);
            }
        }
    }
}
