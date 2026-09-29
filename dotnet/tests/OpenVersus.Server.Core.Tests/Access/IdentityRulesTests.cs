using MongoDB.Bson;
using OpenVersus.Server.Core.Access;

namespace OpenVersus.Server.Core.Tests.Access;

/// <summary>The TS server's identity tests (tests/identityAdoption.test.ts, identityHardware.test.ts), ported, plus the JavaScript details the port has to keep.</summary>
public sealed class IdentityRulesTests
{
    private const string Steam = "76561198000000001";
    private const string Install = "0123456789abcdef0123456789abcdef";

    private static DateTime Day(int n) => new(2026, 9, n, 0, 0, 0, DateTimeKind.Utc);

    private static BsonDocument IdLess(string id, int lastSeen = 1) => new()
    {
        { "id", id }, { "steamId", "" }, { "epicId", "" }, { "installId", "" }, { "provisional", false }, { "lastSeenAt", Day(lastSeen) },
    };

    private static BsonDocument Provisional(string id, int lastSeen = 1) => IdLess(id, lastSeen).Set("provisional", true);

    private static BsonDocument SteamOwner(string id) => IdLess(id).Set("steamId", Steam);

    private static string? Id(BsonDocument? doc) => doc?["id"].AsString;

    [Fact]
    public void AdoptionTakesTheIpsSingleIdLessAccount()
    {
        Assert.Equal("archive", Id(IdentityRules.ChooseAdoptionCandidate([IdLess("archive")])));
        Assert.Equal("archive", Id(IdentityRules.ChooseAdoptionCandidate([SteamOwner("steam"), IdLess("archive")])));
        Assert.Null(IdentityRules.ChooseAdoptionCandidate([SteamOwner("steam")]));
        Assert.Null(IdentityRules.ChooseAdoptionCandidate([IdLess("installed").Set("installId", Install)]));
    }

    [Fact]
    public void AdoptionRefusesAHouseholdWithTwoIdLessAccounts()
    {
        Assert.Null(IdentityRules.ChooseAdoptionCandidate([IdLess("a"), IdLess("b")]));
    }

    [Fact]
    public void AdoptionPrefersARealLegacyAccountThenTheNewestProvisionalOne()
    {
        Assert.Equal("legacy", Id(IdentityRules.ChooseAdoptionCandidate([Provisional("p"), IdLess("legacy")])));
        Assert.Equal("new", Id(IdentityRules.ChooseAdoptionCandidate([Provisional("old", 1), Provisional("new", 5)])));
        Assert.Null(IdentityRules.ChooseAdoptionCandidate([]));
    }

    [Fact]
    public void IpRecoveryNeverCountsProvisionalAccounts()
    {
        Assert.Equal("real", Id(IdentityRules.ChooseUnambiguousLegacyIpCandidate([Provisional("p1"), Provisional("p2"), IdLess("real")])));
        Assert.Null(IdentityRules.ChooseUnambiguousLegacyIpCandidate([Provisional("p1"), Provisional("p2")]));
        Assert.Equal("owner", Id(IdentityRules.ChooseUnambiguousLegacyIpCandidate([Provisional("p"), SteamOwner("owner")])));
        Assert.Null(IdentityRules.ChooseUnambiguousLegacyIpCandidate([SteamOwner("a"), SteamOwner("b").Set("steamId", "76561198000000002")]));
    }

    [Fact]
    public void TheIpRuleReleasesOnlyOtherDurableInactiveAccounts()
    {
        var me = ObjectId.GenerateNewId();
        var filter = IdentityRules.StaleIpLinkFilter("203.0.113.9", me, new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal("203.0.113.9", filter["ip"].AsString);
        Assert.Equal(new BsonDocument("$ne", me), filter["_id"]);
        Assert.Equal(new DateTime(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc), filter["lastSeenAt"]["$lt"].ToUniversalTime());
        Assert.Equal(new BsonDocument("$ne", true), filter["provisional"]);
        Assert.Equal(IdentityRules.DurableIdClauses(), filter["$or"]);
        Assert.Equal(7, IdentityRules.StaleIpLinkDays);
    }

    [Fact]
    public void AdoptionQueriesOnlyAccountsWithoutADurableId()
    {
        var legacy = IdentityRules.IdLessAccountFilter("203.0.113.9", provisional: false);
        Assert.Equal(new BsonDocument("$ne", true), legacy["provisional"]);
        Assert.Equal(IdentityRules.DurableIdClauses(), legacy["$nor"]);
        Assert.True(IdentityRules.IdLessAccountFilter("203.0.113.9", provisional: true)["provisional"].AsBoolean);
    }

    [Theory]
    [InlineData(IdentityKind.Steam, Steam, Steam)]
    [InlineData(IdentityKind.Steam, "  " + Steam + "\t", Steam)]
    [InlineData(IdentityKind.Steam, "7656119800000000", "7656119800000000")]
    [InlineData(IdentityKind.Steam, "12345", "")]
    [InlineData(IdentityKind.Steam, "Unknown", "")]
    [InlineData(IdentityKind.Steam, "N/A", "")]
    [InlineData(IdentityKind.Epic, "AAAABBBBCCCCDDDDEEEEFFFF00001111", "aaaabbbbccccddddeeeeffff00001111")]
    [InlineData(IdentityKind.Install, Install + "0", "")]
    [InlineData(IdentityKind.Hardware, Install, "")]
    public void NormalizesAsTheTsServerDoes(IdentityKind kind, string value, string expected)
    {
        Assert.Equal(expected, IdentityRules.Normalize(kind, value));
    }

    [Fact]
    public void KeepsJavaScriptsDigitsAndWhiteSpace()
    {
        // Arabic-Indic digits are digits to .NET's \d, not to JavaScript's.
        Assert.Equal("", IdentityRules.Normalize(IdentityKind.Steam, new string('١', 17)));
        // JavaScript's trim removes a byte order mark; .NET's Trim does not.
        Assert.Equal(Steam, IdentityRules.Normalize(IdentityKind.Steam, "﻿" + Steam));
        // .NET's Trim removes U+0085; JavaScript's trim does not.
        Assert.Equal("", IdentityRules.Normalize(IdentityKind.Steam, Steam + "\u0085"));
    }

    [Fact]
    public void KeepsOnlyStrongVersionTwoHardware()
    {
        string hw = new('a', 64);
        Assert.Equal(new HardwareSignal(hw, "2", "strong"), IdentityRules.NormalizeHardware(hw.ToUpperInvariant(), " 2 ", "STRONG"));
        Assert.Equal(HardwareSignal.None, IdentityRules.NormalizeHardware(hw, "1", "strong"));
        Assert.Equal(HardwareSignal.None, IdentityRules.NormalizeHardware(hw, "2", "weak"));
        Assert.Equal(HardwareSignal.None, IdentityRules.NormalizeHardware("abc", "2", "strong"));
    }
}
