using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OpenVersus.Server.Core.Bans;

// The name lists (BanSettings.BannedNamesFile, ForceChangeNamesFile, AllowedNamesFile): YAML, a top-level `terms:`
// sequence, every term literal text (the lists spell letters with $, +, @ and !; nothing in them is a pattern). The TS
// server read them as text files and joined them into one unescaped regex between \b: a $ meant "end of the name", so
// most leetspeak entries never matched, and JavaScript's \b counts _ as a letter, so "x_term" passed.
//   banned   a name containing a term ANYWHERE: the person is banned.
//   force    a name containing a term as a whole word (neither neighbour a letter or digit): the name must change.
//   allowed  words that contain a term but are fine: an occurrence lying entirely inside one of them is not a hit.
// Names and terms are compared after Normalize: lookalike letters folded (fullwidth, accented, Cyrillic and Greek ones
// drawn like Latin letters), zero-width characters and combining marks dropped, lower case. A blank term is skipped (it
// would be inside every name).
// A list that fails to load keeps its last good terms, and a file changed less than SettleTime ago is not read yet
// (an editor may still be writing it: a cut-off YAML list is still valid YAML, only shorter). Until the banned and
// force lists have loaded once, a check is not Ready: a name change must then be refused, never let through.

/// <summary>The list a name hit and the term it contains, as the ban record keeps them.</summary>
public sealed record NameHit(NameList List, string Term)
{
    /// <summary>The list's name in a ban record (its file's name without the extension).</summary>
    public string ListName => List == NameList.Banned ? "banned_names" : "force_change_names";
}

public enum NameList
{
    Banned,
    ForceChange,
}

/// <summary>What a name check found: <see cref="Ready"/> false when the banned or force list has never loaded.</summary>
public sealed record NameCheck(bool Ready, NameHit? Hit);

public interface INameRules
{
    /// <summary>The banned term <paramref name="name"/> contains anywhere, else the force-change term it contains as a word.</summary>
    NameCheck Check(string name);
}

internal sealed class NameRules(IOptionsMonitor<BanSettings> settings, TimeProvider time, ILogger<NameRules> log) : INameRules
{
    /// <summary>How long a changed list file must stay unchanged before it is read.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    private readonly TermFile _banned = new("banned", required: true);
    private readonly TermFile _force = new("force-change", required: true);
    private readonly TermFile _allowed = new("allowed", required: false);

    public NameCheck Check(string name)
    {
        var current = settings.CurrentValue;
        var now = time.GetUtcNow().UtcDateTime;
        var banned = _banned.Get(current.BannedNamesFile, now, log);
        var force = _force.Get(current.ForceChangeNamesFile, now, log);
        var allowed = _allowed.Get(current.AllowedNamesFile, now, log);

        string text = Normalize(name);
        var exceptions = Occurrences(text, allowed.Terms);
        NameHit? hit = Find(text, banned.Terms, exceptions, wholeWord: false) is { } b ? new NameHit(NameList.Banned, b)
            : Find(text, force.Terms, exceptions, wholeWord: true) is { } f ? new NameHit(NameList.ForceChange, f)
            : null;
        return new NameCheck(banned.Loaded && force.Loaded, hit);
    }

    // The services run with invariant globalization (Directory.Build.props: no ICU), where string.Normalize folds nothing,
    // so the folding is a table: Latin letters with accents (U+00C0-U+024F, to their NFKD base letter) and the Cyrillic and
    // Greek letters drawn like Latin ones. Fullwidth ASCII is folded by arithmetic. Not folded: mathematical alphanumerics,
    // circled and other enclosed letters.
    private static readonly (char To, string From)[] s_lookalikes =
    [
        ('a', "ÀÁÂÃÄÅàáâãäåĀāĂăĄąǍǎǞǟǠǡǺǻȀȁȂȃȦȧаАαΑ"),
        ('b', "ƀɃвВβΒ"),
        ('c', "ÇçĆćĈĉĊċČčȻȼсСϲϹ"),
        ('d', "ÐðĎďĐđԁԀ"),
        ('e', "ÈÉÊËèéêëĒēĔĕĖėĘęĚěȄȅȆȇȨȩɆɇеёЕЁεΕ"),
        ('f', "Ƒƒ"),
        ('g', "ĜĝĞğĠġĢģǤǥǦǧǴǵɡ"),
        ('h', "ĤĥĦħȞȟнНΗ"),
        ('i', "ÌÍÎÏìíîïĨĩĪīĬĭĮįİıƗǏǐȈȉȊȋіїІЇιΙ"),
        ('j', "ĴĵǰɈɉјЈ"),
        ('k', "ĶķĸǨǩкКκΚ"),
        ('l', "ĹĺĻļĽľŁłƚȽӏӀ"),
        ('m', "мМΜ"),
        ('n', "ÑñŃńŅņŇňŊŋǸǹηΝ"),
        ('o', "ÒÓÔÕÖØòóôõöøŌōŎŏŐőƠơǑǒǪǫǬǭȌȍȎȏȪȫȬȭȮȯȰȱоОοΟ"),
        ('p', "рРρΡ"),
        ('q', "ԛԚ"),
        ('r', "ŔŕŖŗŘřȐȑȒȓɌɍ"),
        ('s', "ŚśŜŝŞşŠšſȘșѕЅ"),
        ('t', "ŢţŤťŦŧȚțтТτΤ"),
        ('u', "ÙÚÛÜùúûüŨũŪūŬŭŮůŰűŲųƯưǓǔǕǖǗǘǙǚǛǜȔȕȖȗɄυ"),
        ('v', "ν"),
        ('w', "ŴŵԝԜω"),
        ('x', "хХχΧ"),
        ('y', "ÝýÿŶŷŸȲȳуУΥ"),
        ('z', "ŹźŻżŽžƵƶȤȥΖ"),
    ];

    private static readonly Dictionary<char, string> s_fold = BuildFold();

    private static Dictionary<char, string> BuildFold()
    {
        var fold = new Dictionary<char, string>
        {
            ['Æ'] = "ae", ['æ'] = "ae", ['Þ'] = "th", ['þ'] = "th", ['ß'] = "ss", ['Ĳ'] = "ij", ['ĳ'] = "ij", ['Œ'] = "oe", ['œ'] = "oe",
            ['Ǆ'] = "dz", ['ǅ'] = "dz", ['ǆ'] = "dz", ['Ǳ'] = "dz", ['ǲ'] = "dz", ['ǳ'] = "dz",
            ['Ǉ'] = "lj", ['ǈ'] = "lj", ['ǉ'] = "lj", ['Ǌ'] = "nj", ['ǋ'] = "nj", ['ǌ'] = "nj",
        };
        foreach (var (to, from) in s_lookalikes)
        {
            foreach (char c in from)
            {
                fold.Add(c, to.ToString());
            }
        }

        return fold;
    }

    /// <summary>Lookalike letters folded, format characters (zero-width) and combining marks dropped, lower case.</summary>
    internal static string Normalize(string text)
    {
        var kept = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is >= '\uFF01' and <= '\uFF5E')
            {
                kept.Append(char.ToLowerInvariant((char)(c - 0xFEE0)));
            }
            else if (s_fold.TryGetValue(c, out string? folded))
            {
                kept.Append(folded);
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.Format or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                kept.Append(char.ToLowerInvariant(c));
            }
        }

        return kept.ToString();
    }

    /// <summary>A list file's terms, normalized, without blanks and duplicates. Throws when it is not a `terms:` list.</summary>
    internal static (string[] Terms, int Blank) Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new FormatException("expected one YAML document holding a `terms:` list");
        }

        if (!root.Children.TryGetValue(new YamlScalarNode("terms"), out var node) || node is not YamlSequenceNode list)
        {
            throw new FormatException("no `terms:` list");
        }

        var terms = new List<string>(list.Children.Count);
        int blank = 0;
        foreach (var item in list)
        {
            if (item is not YamlScalarNode scalar)
            {
                throw new FormatException($"line {item.Start.Line}: a term must be a string");
            }

            string term = Normalize(scalar.Value ?? "");
            if (term.Trim().Length == 0)
            {
                blank++;
                continue;
            }

            terms.Add(term);
        }

        return ([.. terms.Distinct(StringComparer.Ordinal)], blank);
    }

    private static List<(int Start, int End)> Occurrences(string text, string[] words)
    {
        var spans = new List<(int, int)>();
        foreach (string word in words)
        {
            for (int at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal))
            {
                spans.Add((at, at + word.Length));
            }
        }

        return spans;
    }

    private static string? Find(string text, string[] terms, List<(int Start, int End)> exceptions, bool wholeWord)
    {
        foreach (string term in terms)
        {
            for (int at = text.IndexOf(term, StringComparison.Ordinal); at >= 0; at = text.IndexOf(term, at + 1, StringComparison.Ordinal))
            {
                int end = at + term.Length;
                if (wholeWord && (!Separator(text, at - 1) || !Separator(text, end)))
                {
                    continue;
                }

                if (!exceptions.Exists(e => e.Start <= at && end <= e.End))
                {
                    return term;
                }
            }
        }

        return null;
    }

    private static bool Separator(string text, int index) => index < 0 || index >= text.Length || !char.IsLetterOrDigit(text[index]);

    private sealed class TermFile(string name, bool required)
    {
        private readonly Lock _lock = new();
        private string? _path;
        private DateTime _stamp = DateTime.MinValue;
        private string[] _terms = [];
        private bool _loaded;
        private string? _reported;

        public (string[] Terms, bool Loaded) Get(string? configured, DateTime now, ILogger log)
        {
            lock (_lock)
            {
                string? path = string.IsNullOrWhiteSpace(configured) ? null : Path.GetFullPath(configured);
                if (path != _path)
                {
                    (_path, _stamp, _terms, _loaded, _reported) = (path, DateTime.MinValue, [], false, null);
                }

                if (path is null)
                {
                    Report(log, "not configured", null);
                    return (_terms, _loaded);
                }

                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    Report(log, "missing", null);
                    return (_terms, _loaded);
                }

                // A first load reads at once; a change waits until the file has been still for SettleTime (either way
                // round: a mount's clock may be ahead).
                if (info.LastWriteTimeUtc == _stamp || (_loaded && (now - info.LastWriteTimeUtc).Duration() < SettleTime))
                {
                    return (_terms, _loaded);
                }

                try
                {
                    var (terms, blank) = Parse(File.ReadAllText(path));
                    (_terms, _loaded, _stamp, _reported) = (terms, true, info.LastWriteTimeUtc, null);
                    log.LogInformation("Name list {List} {Path}: {Count} terms{Blank}", name, path, terms.Length,
                        blank > 0 ? $" ({blank} blank skipped)" : "");
                }
                catch (Exception e) when (e is FormatException or YamlException or IOException or UnauthorizedAccessException)
                {
                    // Bad content is not read again until the file changes; with nothing loaded yet (a mount not readable
                    // yet), every check tries again.
                    if (_loaded)
                    {
                        _stamp = info.LastWriteTimeUtc;
                    }

                    Report(log, "unreadable", e);
                }

                return (_terms, _loaded);
            }
        }

        private void Report(ILogger log, string problem, Exception? e)
        {
            if (_reported == problem)
            {
                return;
            }

            _reported = problem;
            var level = required ? LogLevel.Error : LogLevel.Information;
            string keeps = _loaded ? $"keeping the last good {_terms.Length} terms" : required ? "no name is checked against it until it loads" : "no exceptions";
            log.Log(level, e, "Name list {List} {Path} is {Problem}: {Keeps}", name, _path ?? "(no path)", problem, keeps);
        }
    }
}
