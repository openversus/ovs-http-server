using System.Security.Cryptography;
using OpenVersus.Server.Core.Steam;
using OpenVersus.Server.TestSupport;

namespace OpenVersus.Server.Core.Tests.Steam;

/// <summary>
/// Tickets built here and signed with a key of this test's own, laid out as SteamKit's steam3_appticket.hsl; Steam's
/// real key only sees a real ticket, which lives outside the repository (OVS_TEST_STEAM_TICKET, a personal identifier).
/// </summary>
public sealed class SteamTicketTests
{
    private const uint AppId = SteamTickets.AppId;
    private static readonly DateTimeOffset s_now = SteamTickets.Now;
    private static readonly DateTimeOffset s_generated = SteamTickets.Generated;
    private static readonly DateTimeOffset s_expires = SteamTickets.Expires;

    // One key for the class: the theory data is built before any instance exists.
    private static readonly RSA s_key = SteamTickets.NewKey();
    private static readonly SteamTicketVerifier s_verifier = new(SteamTickets.PublicOf(s_key));

    [Fact]
    public void ParsesAFullSessionTicket()
    {
        byte[] bytes = Full(Signed(Ownership(licenses: [12345, 67890], dlc: [(1818760u, new uint[] { 555 })])));

        var ticket = SteamTicket.Parse(bytes);

        Assert.NotNull(ticket);
        Assert.Equal("76561198000000091", ticket.SteamId);
        Assert.Equal(AppId, ticket.AppId);
        Assert.Equal(2u, ticket.Version);
        Assert.Equal(0u, ticket.OwnershipFlags);
        Assert.Equal([12345u, 67890u], ticket.Licenses);
        Assert.Equal(1818760u, Assert.Single(ticket.Dlc).AppId);
        Assert.Equal([555u], ticket.Dlc[0].Licenses);
        Assert.Equal(s_generated, ticket.OwnershipGenerated);
        Assert.Equal(s_expires, ticket.OwnershipExpires);
        Assert.Equal("203.0.113.7", ticket.OwnershipExternalIp);
        Assert.Equal("192.168.1.20", ticket.OwnershipInternalIp);
        Assert.NotNull(ticket.Session);
        Assert.Equal(0x1122334455667788ul, ticket.Session.GcToken);
        Assert.Equal(s_now.AddMinutes(-5), ticket.Session.TokenGenerated);
        Assert.Equal("203.0.113.7", ticket.Session.ExternalIp);
        Assert.Equal(90000u, ticket.Session.ConnectionTime);
        Assert.Equal(3u, ticket.Session.ConnectionCount);
        Assert.True(ticket.HasSignature);
        Assert.Equal(128, ticket.Signature.Length);
    }

    [Fact]
    public void ParsesABareOwnershipTicket()
    {
        var ticket = SteamTicket.Parse(Signed(Ownership()));

        Assert.NotNull(ticket);
        Assert.Null(ticket.Session);
        Assert.Equal("76561198000000091", ticket.SteamId);
        Assert.True(ticket.HasSignature);
    }

    [Fact]
    public void VerifiesATicketTheKeySigned()
    {
        byte[] bytes = Full(Signed(Ownership()));

        var check = s_verifier.Check(bytes, AppId, s_now);

        var verified = Assert.IsType<SteamTicketCheck.Verified>(check);
        Assert.Equal("76561198000000091", verified.Ticket.SteamId);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), verified.Hash);
    }

    [Fact]
    public void AcceptsAnyAppWhenNoneIsRequired()
    {
        Assert.IsType<SteamTicketCheck.Verified>(s_verifier.Check(Full(Signed(Ownership(appId: 480))), 0, s_now));
    }

    [Fact]
    public void RefusesAnUnsignedTicket()
    {
        // steam-appticket counts a ticket with no signature as valid; here it is a claim.
        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.Unsigned), s_verifier.Check(Full(Ownership()), AppId, s_now));
        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.Unsigned), s_verifier.Check(Ownership(), AppId, s_now));
    }

    [Fact]
    public void RefusesATicketWhoseSignedBytesChanged()
    {
        byte[] bytes = Full(Signed(Ownership()));
        bytes[56 + 4 + 4 + 2] ^= 1; // a byte of the SteamID (the ownership ticket starts after the 52-byte header and the section length)

        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.BadSignature), s_verifier.Check(bytes, AppId, s_now));
    }

    [Fact]
    public void RefusesATicketWhoseSignatureChanged()
    {
        byte[] bytes = Full(Signed(Ownership()));
        bytes[^1] ^= 1;

        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.BadSignature), s_verifier.Check(bytes, AppId, s_now));
    }

    [Fact]
    public void RefusesATicketSignedByAnotherKey()
    {
        using var other = RSA.Create(1024);
        byte[] ownership = Ownership();
        byte[] bytes = Full([.. ownership, .. other.SignData(ownership, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1)]);

        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.BadSignature), s_verifier.Check(bytes, AppId, s_now));
    }

    [Fact]
    public void RefusesAnExpiredTicket()
    {
        byte[] bytes = Full(Signed(Ownership()));

        Assert.IsType<SteamTicketCheck.Verified>(s_verifier.Check(bytes, AppId, s_expires));
        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.Expired), s_verifier.Check(bytes, AppId, s_expires.AddSeconds(1)));
    }

    [Fact]
    public void RefusesAnotherAppsTicket()
    {
        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.WrongApp), s_verifier.Check(Full(Signed(Ownership(appId: 480))), AppId, s_now));
    }

    public static TheoryData<string, byte[]> Malformed()
    {
        byte[] good = Full(Signed(Ownership(licenses: [1, 2])));
        byte[] wrongSection = (byte[])good.Clone();
        wrongSection[52]++; // the section length
        byte[] wrongOwnershipLength = (byte[])good.Clone();
        wrongOwnershipLength[56] += 4;
        byte[] countOverrun = (byte[])good.Clone();
        countOverrun[56 + 40] = 3; // three licenses declared, two present: the fields would reach into the signature
        byte[] badHeader = (byte[])good.Clone();
        badHeader[24] = 25; // the session header's length
        return new TheoryData<string, byte[]>
        {
            { "empty", [] },
            { "three bytes", [20, 0, 0] },
            { "truncated", good[..100] },
            { "one byte more", [.. good, 0] },
            { "random", Enumerable.Range(0, 240).Select(i => (byte)(i * 7)).ToArray() },
            { "wrong section length", wrongSection },
            { "wrong ownership length", wrongOwnershipLength },
            { "license count overrun", countOverrun },
            { "bad session header", badHeader },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void RefusesWhatIsNotATicket(string name, byte[] bytes)
    {
        Assert.Null(SteamTicket.Parse(bytes));
        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.Malformed), s_verifier.Check(bytes, AppId, s_now));
        Assert.NotEmpty(name);
    }

    [Fact]
    public void ReadsHexAndBase64Text()
    {
        byte[] bytes = Full(Signed(Ownership()));

        Assert.IsType<SteamTicketCheck.Verified>(s_verifier.Check(Convert.ToHexString(bytes), AppId, s_now));
        Assert.IsType<SteamTicketCheck.Verified>(s_verifier.Check(Convert.ToHexStringLower(bytes), AppId, s_now));
        Assert.IsType<SteamTicketCheck.Verified>(s_verifier.Check(Convert.ToBase64String(bytes), AppId, s_now));
        Assert.IsType<SteamTicketCheck.Verified>(s_verifier.Check(Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'), AppId, s_now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0802109bdc82df01")]
    [InlineData("not a ticket at all, nor base64 !!! ???")]
    public void RefusesTextThatIsNoTicket(string? text)
    {
        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.Malformed), s_verifier.Check(text, AppId, s_now));
    }

    [Fact]
    public void RefusalReasonsSurviveTheTextDecoding()
    {
        byte[] bytes = Full(Signed(Ownership(appId: 480)));

        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.WrongApp), s_verifier.Check(Convert.ToHexString(bytes), AppId, s_now));
        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.Unsigned), s_verifier.Check(Convert.ToBase64String(Full(Ownership())), AppId, s_now));
    }

    [Fact]
    public void CarriesSteamsSystemKey()
    {
        using var verifier = SteamTicketVerifier.ForSteam();
        using var rsa = RSA.Create();
        rsa.ImportFromPem(SteamTicketVerifier.SteamSystemKeyPem);
        var parameters = rsa.ExportParameters(false);

        Assert.Equal(1024, rsa.KeySize);
        Assert.Equal([0x11], parameters.Exponent);
        // A ticket of this test's key is not Steam's.
        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.BadSignature), verifier.Check(Full(Signed(Ownership())), AppId, s_now));
    }

    [SkippableFact]
    public void VerifiesARealTicketWithSteamsKey()
    {
        // A real session ticket (hex) and the SteamID64 it names, from the environment only: never in the repository.
        string? hex = Environment.GetEnvironmentVariable("OVS_TEST_STEAM_TICKET");
        string? steamId = Environment.GetEnvironmentVariable("OVS_TEST_STEAM_ID");
        Skip.If(string.IsNullOrEmpty(hex) || string.IsNullOrEmpty(steamId), "OVS_TEST_STEAM_TICKET and OVS_TEST_STEAM_ID are not set");

        using var verifier = SteamTicketVerifier.ForSteam();
        var check = verifier.Check(hex, AppId, DateTimeOffset.UtcNow);

        var verified = Assert.IsType<SteamTicketCheck.Verified>(check);
        Assert.Equal(steamId, verified.Ticket.SteamId);
        Assert.NotNull(verified.Ticket.Session);

        byte[] flipped = Convert.FromHexString(hex);
        flipped[^130] ^= 1; // inside the signed ownership ticket
        Assert.Equal(new SteamTicketCheck.Refused(SteamTicketVerifier.BadSignature), verifier.Check(flipped, AppId, DateTimeOffset.UtcNow));
    }

    private static byte[] Ownership(uint appId = AppId, uint[]? licenses = null, (uint AppId, uint[] Licenses)[]? dlc = null, DateTimeOffset? expires = null) =>
        SteamTickets.Ownership(appId: appId, licenses: licenses, dlc: dlc, expires: expires);

    private static byte[] Signed(byte[] ownership) => SteamTickets.Signed(ownership, s_key);

    private static byte[] Full(byte[] ownershipSection) => SteamTickets.Full(ownershipSection);
}
