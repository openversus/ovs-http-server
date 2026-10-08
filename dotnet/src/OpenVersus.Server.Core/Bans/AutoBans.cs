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

// The auto-ban file: the trail of every ban and lift the services made, a YAML sequence appended to (one item per write,
// under an exclusive lock: several services ban) and never rewritten or read back: Mongo holds the bans. Comments are fine.
//   - action: 'ban'
//     ban_id: '...'
//     at: '2026-10-07T21:14:03.512Z'
//     reason: '...'
//     source: 'namechange'          (namechange, login, sweep, manual)
//     player: {id, name_at_ban, attempted_name, created_at}
//     matched: {list, term}
//     identifiers: {ip, steam_id, epic_id, hardware_id, install_id}
//     request: {ip, user_agent}
//     online: true
//     disconnected: true
//   - action: 'lift'
//     at, reason, source, player: {id, name}, lifted_bans: [ban ids], lifted_values: [a ban file's entries],
//     no_longer_banned: [...], still_banned: [...]
internal static class AutoBans
{
    /// <summary>The record as one item of the file's list, ending in a newline.</summary>
    public static string ToYaml(BanRecord r)
    {
        static YamlScalarNode S(string value) => new(value) { Style = ScalarStyle.SingleQuoted };
        static YamlScalarNode B(bool value) => new(value ? "true" : "false") { Style = ScalarStyle.Plain };
        var record = new YamlMappingNode
        {
            { "action", S("ban") },
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

        return Item(record);
    }

    /// <summary>A lift as one item of the file's list, ending in a newline.</summary>
    public static string LiftToYaml(DateTimeOffset at, string reason, string source, string playerId, string name,
        IEnumerable<string> liftedBans, IEnumerable<string> liftedValues, IEnumerable<string> noLongerBanned, IEnumerable<string> stillBanned)
    {
        static YamlScalarNode S(string value) => new(value) { Style = ScalarStyle.SingleQuoted };
        static YamlSequenceNode L(IEnumerable<string> values) => new(values.Select(v => (YamlNode)S(v))) { Style = YamlDotNet.Core.Events.SequenceStyle.Block };
        return Item(new YamlMappingNode
        {
            { "action", S("lift") },
            { "at", S(at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")) },
            { "reason", S(reason) },
            { "source", S(source) },
            { "player", new YamlMappingNode { { "id", S(playerId) }, { "name", S(name) } } },
            { "lifted_bans", L(liftedBans) },
            { "lifted_values", L(liftedValues) },
            { "no_longer_banned", L(noLongerBanned) },
            { "still_banned", L(stillBanned) },
        });
    }

    private static string Item(YamlMappingNode item)
    {
        var writer = new StringWriter { NewLine = "\n" };
        new YamlStream(new YamlDocument(new YamlSequenceNode(item))).Save(writer, assignAnchors: false);
        string text = writer.ToString();
        // Save ends the document with "...": an appended item must not.
        int end = text.LastIndexOf("...", StringComparison.Ordinal);
        return (end >= 0 && text[end..].Trim() == "..." ? text[..end] : text).TrimEnd() + "\n";
    }

    /// <summary>Appends an item to the file (created when missing) under an exclusive lock, retried while another service holds it.</summary>
    public static void Append(string path, string yaml)
    {
        byte[] item = System.Text.Encoding.UTF8.GetBytes(yaml);
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
}
