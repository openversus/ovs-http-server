using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenVersus.Server.Core.Compat;
using OpenVersus.Server.Core.Matches;
using OpenVersus.Server.Core.Settings;

namespace OpenVersus.Server.Core.Tests.Matches;

/// <summary>
/// The Beta Speed mutator's speed (BetaSpeed:Percent): what it accepts, the rollback tick rate it gives, what the client
/// reads, and how the client's report is judged. Needs no stores.
/// </summary>
public sealed class BetaSpeedTests
{
    private static IConfiguration Config(string? percent) => new ConfigurationBuilder()
        .AddInMemoryCollection(percent is null ? [] : [new KeyValuePair<string, string?>("BetaSpeed:Percent", percent)])
        .Build();

    // As a service binds it at startup (AddSetting: data annotations, ValidateOnStart).
    private static BetaSpeedSettings Bound(string? percent)
    {
        var services = new ServiceCollection();
        services.AddOptions<BetaSpeedSettings>().Bind(Config(percent).GetSection("BetaSpeed")).ValidateDataAnnotations();
        return services.BuildServiceProvider().GetRequiredService<IOptions<BetaSpeedSettings>>().Value;
    }

    [Theory]
    [InlineData(null, 110)]
    [InlineData("50", 50)]
    [InlineData("120", 120)]
    [InlineData("200", 200)]
    public void ASpeedFrom50To200IsTaken(string? percent, int expected) => Assert.Equal(expected, Bound(percent).Percent);

    [Theory]
    [InlineData("49")]
    [InlineData("201")]
    [InlineData("0")]
    [InlineData("-110")]
    public void AnyOtherIsRefused(string percent)
    {
        Assert.Throws<OptionsValidationException>(() => Bound(percent));

        // And as a change while running (the control API).
        var catalog = new SettingsCatalog();
        catalog.Register(typeof(BetaSpeedSettings), "BetaSpeed");
        Assert.NotNull(catalog.Validate("BetaSpeed:Percent", percent, Config(null)));
        Assert.Null(catalog.Validate("BetaSpeed:Percent", "120", Config(null)));
    }

    [Theory]
    [InlineData(110, 66)]
    [InlineData(120, 72)]
    [InlineData(100, 60)]
    [InlineData(50, 30)]
    [InlineData(200, 120)]
    // 60 x 1.01 = 60.6: rounded.
    [InlineData(101, 61)]
    public void TheTickRateIsSixtyFramesAtItsSpeed(int percent, int tickRate) => Assert.Equal(tickRate, BetaSpeed.TickRate(percent));

    [Fact]
    public void TheClientReadsItsSpeed()
    {
        Assert.Equal("""{"betaSpeedPercent":110}""", Js.Stringify(BetaSpeed.ClientSettings(new BetaSpeedSettings())));
        Assert.Equal("""{"betaSpeedPercent":120}""", Js.Stringify(BetaSpeed.ClientSettings(new BetaSpeedSettings { Percent = 120 })));
    }

    [Theory]
    [InlineData("110", 110, true)]
    [InlineData("120", 110, false)]
    [InlineData(null, 110, false)]
    [InlineData("", 110, false)]
    [InlineData("1.1", 110, false)]
    [InlineData("-110", 110, false)]
    [InlineData("fast", 110, false)]
    public void TheClientsReportMustBeTheSpeed(string? header, int percent, bool reports) => Assert.Equal(reports, BetaSpeed.Reports(header, percent));
}
