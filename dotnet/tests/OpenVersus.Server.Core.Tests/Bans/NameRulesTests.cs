using Microsoft.Extensions.Logging.Abstractions;
using OpenVersus.Server.Core.Bans;

namespace OpenVersus.Server.Core.Tests.Bans;

/// <summary>
/// The name lists: banned anywhere, force-change as a whole word, the allowed exceptions, literal terms, normalization,
/// and a list that fails to load. The terms are invented; only their shapes are the real lists' (leetspeak with $ and
/// +, a term inside an innocent word, YAML-special first characters, a blank line converted into a term).
/// </summary>
public sealed class NameRulesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-names-").FullName;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly BanSettings _settings;

    public NameRulesTests()
    {
        _settings = new BanSettings
        {
            BannedNamesFile = Path.Combine(_dir, "banned_names.yaml"),
            ForceChangeNamesFile = Path.Combine(_dir, "force_change_names.yaml"),
            AllowedNamesFile = Path.Combine(_dir, "allowed_names.yaml"),
        };
        Write(_settings.BannedNamesFile, "zorb", "qu$$", "gl+ch", "", "!zap", "@zap", "*zap", "#zap", "two words");
        Write(_settings.ForceChangeNamesFile, "blat", "x$y");
        Write(_settings.AllowedNamesFile, "zorbit");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private NameRules Rules() => new(new TestOptions<BanSettings>(_settings), _clock, NullLogger<NameRules>.Instance);

    // Written a minute before the clock: settled.
    private void Write(string? path, params string[] terms)
    {
        File.WriteAllText(path!, "# a comment\nterms:\n" + string.Concat(terms.Select(t => $"  - '{t}'\n")));
        File.SetLastWriteTimeUtc(path!, _clock.Now.UtcDateTime.AddMinutes(-1));
    }

    private static NameHit Banned(string term) => new(NameList.Banned, term);

    private static NameHit Force(string term) => new(NameList.ForceChange, term);

    [Theory]
    [InlineData("bigzorbking", "zorb")]
    [InlineData("ZORB", "zorb")]
    [InlineData("the_zorb_", "zorb")]
    // $ and + are letters, not a pattern's anchor and repeat.
    [InlineData("xqu$$x", "qu$$")]
    [InlineData("xgl+chx", "gl+ch")]
    [InlineData("hi !zap", "!zap")]
    [InlineData("@zap", "@zap")]
    [InlineData("*zap*", "*zap")]
    [InlineData("#zap", "#zap")]
    [InlineData("say two words", "two words")]
    // Fullwidth letters, a zero-width space, an accent (precomposed and combining), Cyrillic and Greek lookalikes.
    [InlineData("\uFF5A\uFF4F\uFF52\uFF42", "zorb")]
    [InlineData("zo\u200Brb", "zorb")]
    [InlineData("z\u00F3rb", "zorb")]
    [InlineData("zo\u0301rb", "zorb")]
    [InlineData("z\u043Erb", "zorb")]
    [InlineData("Z\u039FRB", "zorb")]
    [InlineData("qu\uFF04$", "qu$$")]
    public void BansATermAnywhere(string name, string term)
    {
        Assert.Equal(new NameCheck(true, Banned(term)), Rules().Check(name));
    }

    [Theory]
    [InlineData("quss")]
    [InlineData("qu")]
    [InlineData("glch")]
    [InlineData("gllllch")]
    [InlineData("zap")]
    [InlineData("twowords")]
    [InlineData("")]
    public void TermsAreLiteral(string name)
    {
        Assert.Equal(new NameCheck(true, null), Rules().Check(name));
    }

    [Fact]
    public void AnAllowedWordExceptsOnlyTheOccurrenceInsideIt()
    {
        var rules = Rules();
        Assert.Null(rules.Check("zorbitfan").Hit);
        Assert.Null(rules.Check("ZorbitZorbit").Hit);
        Assert.Equal(Banned("zorb"), rules.Check("zorbzorbit").Hit);
        Assert.Equal(Banned("zorb"), rules.Check("zorbit_zorb").Hit);
    }

    [Theory]
    [InlineData("the blat", true)]
    [InlineData("blat", true)]
    [InlineData("blat_x", true)]
    [InlineData("x.blat!", true)]
    [InlineData("BLAT", true)]
    [InlineData("blatter", false)]
    [InlineData("xblat", false)]
    [InlineData("1blat", false)]
    [InlineData("blat2", false)]
    public void ForcesAChangeForAWholeWord(string name, bool hit)
    {
        Assert.Equal(hit ? Force("blat") : null, Rules().Check(name).Hit);
    }

    [Fact]
    public void ABannedTermWinsOverAForceChangeTerm()
    {
        Assert.Equal(Banned("zorb"), Rules().Check("blat zorb").Hit);
    }

    [Fact]
    public void ABlankTermIsSkipped()
    {
        var (terms, blank) = NameRules.Parse("terms:\n  - ''\n  - '   '\n  - 'a'\n");
        Assert.Equal(["a"], terms);
        Assert.Equal(2, blank);
        Assert.Null(Rules().Check("anything at all").Hit);
    }

    [Theory]
    [InlineData("- 'a'\n")]
    [InlineData("other:\n  - 'a'\n")]
    [InlineData("terms: 'a'\n")]
    [InlineData("terms:\n  - ['a']\n")]
    [InlineData("terms:\n  - 'a'\n---\nterms:\n  - 'b'\n")]
    public void RefusesAFileThatIsNotATermsList(string yaml)
    {
        Assert.ThrowsAny<Exception>(() => NameRules.Parse(yaml));
    }

    [Fact]
    public void IsNotReadyUntilTheBannedAndForceListsLoad()
    {
        File.Delete(_settings.BannedNamesFile!);
        var rules = Rules();
        Assert.Equal(new NameCheck(false, Force("blat")), rules.Check("blat"));
        Write(_settings.BannedNamesFile, "zorb");
        Assert.Equal(new NameCheck(true, Banned("zorb")), rules.Check("zorb"));
    }

    [Fact]
    public void AFirstLoadThatFailsIsTriedAgain()
    {
        string path = _settings.BannedNamesFile!;
        File.WriteAllText(path, "terms: [");
        var stamp = _clock.Now.UtcDateTime.AddMinutes(-1);
        File.SetLastWriteTimeUtc(path, stamp);
        var rules = Rules();
        Assert.False(rules.Check("zorb").Ready);
        // Readable now, with the same write time (a mount that was not ready yet).
        File.WriteAllText(path, "terms:\n  - 'zorb'\n");
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal(new NameCheck(true, Banned("zorb")), rules.Check("zorb"));
    }

    [Fact]
    public void WorksWithoutAnAllowedList()
    {
        File.Delete(_settings.AllowedNamesFile!);
        Assert.Equal(new NameCheck(true, Banned("zorb")), Rules().Check("zorbit"));
    }

    [Fact]
    public void KeepsTheLastGoodListWhenAChangeCannotBeRead()
    {
        var rules = Rules();
        Assert.Equal(Banned("zorb"), rules.Check("zorb").Hit);
        File.WriteAllText(_settings.BannedNamesFile!, "terms:\n  - 'zorb\n  - [");
        File.SetLastWriteTimeUtc(_settings.BannedNamesFile!, _clock.Now.UtcDateTime.AddSeconds(-30));
        Assert.Equal(new NameCheck(true, Banned("zorb")), rules.Check("zorb"));
    }

    [Fact]
    public void ReadsAChangeOnlyOnceItHasSettled()
    {
        var rules = Rules();
        Assert.Equal(Banned("zorb"), rules.Check("zorb").Hit);
        File.WriteAllText(_settings.BannedNamesFile!, "terms:\n  - 'newterm'\n");
        File.SetLastWriteTimeUtc(_settings.BannedNamesFile!, _clock.Now.UtcDateTime);
        Assert.Equal(Banned("zorb"), rules.Check("zorb").Hit);
        Assert.Null(rules.Check("newterm").Hit);
        _clock.Now += NameRules.SettleTime;
        Assert.Null(rules.Check("zorb").Hit);
        Assert.Equal(Banned("newterm"), rules.Check("newterm").Hit);
    }
}
