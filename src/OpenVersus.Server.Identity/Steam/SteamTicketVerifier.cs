using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OpenVersus.Server.Identity.Steam;

/// <summary>What a ticket check found.</summary>
public abstract record SteamTicketCheck
{
    /// <summary>Steam signed this ticket for <see cref="SteamTicket.SteamId"/>, for the app, and it has not expired.</summary>
    /// <param name="Hash">SHA-256 of the whole ticket, lowercase hex: what is stored instead of the ticket.</param>
    public sealed record Verified(SteamTicket Ticket, string Hash) : SteamTicketCheck;

    /// <summary>Not accepted: <see cref="Reason"/> is one of the <see cref="SteamTicketVerifier"/> reasons.</summary>
    public sealed record Refused(string Reason) : SteamTicketCheck;
}

public interface ISteamTicketVerifier
{
    /// <summary>Checks a ticket a client sent as text (hex, or base64 / base64url) against Steam's signature, for <paramref name="appId"/>, at <paramref name="now"/>.</summary>
    SteamTicketCheck Check(string? ticketText, uint appId, DateTimeOffset now);

    /// <summary>Checks a ticket's bytes.</summary>
    SteamTicketCheck Check(ReadOnlyMemory<byte> ticket, uint appId, DateTimeOffset now);
}

/// <summary>
/// Verifies Steam session tickets offline: the ownership ticket inside carries Steam's RSA-SHA1 (PKCS#1 v1.5)
/// signature, checked with Steam's system public key. A ticket without a signature is refused (steam-appticket counts
/// one as valid; here it never is): an unsigned ticket is a claim, and the whole point is that a Steam id is not.
/// </summary>
public sealed partial class SteamTicketVerifier(RSA publicKey) : ISteamTicketVerifier, IDisposable
{
    // One RSA for the service; its instance members are not guaranteed thread-safe, and registrations arrive together.
    private readonly Lock _verify = new();

    public const string Malformed = "malformed";
    public const string Unsigned = "unsigned";
    public const string BadSignature = "bad signature";
    public const string Expired = "expired";
    public const string WrongApp = "wrong app";

    // Steam's system public key (the one @doctormckay/steam-crypto ships as system.pem): 1024 bits, exponent 0x11.
    public const string SteamSystemKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIGdMA0GCSqGSIb3DQEBAQUAA4GLADCBhwKBgQDf7BrWLBBmLBc1OhSwfFkRf53T
        2Ct64+AVzRkeRuh7h3SiGEYxqQMUeYKO6UWiSRKpI2hzic9pobFhRr3Bvr/WARvY
        gdTckPv+T1JzZsuVcNfFjrocejN1oWI0Rrtgt4Bo+hOneoo3S57G9F1fOpn5nsQ6
        6WOiu4gZKODnFMBCiQIBEQ==
        -----END PUBLIC KEY-----
        """;

    /// <summary>A verifier with Steam's system key.</summary>
    public static SteamTicketVerifier ForSteam()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(SteamSystemKeyPem);
        return new SteamTicketVerifier(rsa);
    }

    [GeneratedRegex("^[0-9a-fA-F]+$")]
    private static partial Regex Hex();

    public SteamTicketCheck Check(string? ticketText, uint appId, DateTimeOffset now)
    {
        // As the TS server read a ticket (handlers/access.ts steamIdFromSignedTicket): hex when it looks like hex, and
        // base64 (URL alphabet accepted, padding optional); the first encoding that yields a ticket decides.
        if (string.IsNullOrEmpty(ticketText) || ticketText.Length < 32 || ticketText.Length > 32768)
        {
            return new SteamTicketCheck.Refused(Malformed);
        }

        SteamTicketCheck? result = null;
        if (ticketText.Length % 2 == 0 && Hex().IsMatch(ticketText))
        {
            result = Check(Convert.FromHexString(ticketText), appId, now);
            if (result is SteamTicketCheck.Verified || result is SteamTicketCheck.Refused { Reason: not Malformed })
            {
                return result;
            }
        }

        try
        {
            string normalized = ticketText.Replace('-', '+').Replace('_', '/');
            byte[] bytes = Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
            var fromBase64 = Check(bytes, appId, now);
            if (result is null || fromBase64 is not SteamTicketCheck.Refused { Reason: Malformed })
            {
                return fromBase64;
            }
        }
        catch (FormatException)
        {
            // Not base64 either.
        }

        return result ?? new SteamTicketCheck.Refused(Malformed);
    }

    public SteamTicketCheck Check(ReadOnlyMemory<byte> ticket, uint appId, DateTimeOffset now)
    {
        if (SteamTicket.Parse(ticket) is not { } parsed)
        {
            return new SteamTicketCheck.Refused(Malformed);
        }

        if (!parsed.HasSignature)
        {
            return new SteamTicketCheck.Refused(Unsigned);
        }

        bool signed;
        lock (_verify)
        {
            signed = publicKey.VerifyData(parsed.SignedBytes.Span, parsed.Signature.Span, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        }

        if (!signed)
        {
            return new SteamTicketCheck.Refused(BadSignature);
        }

        if (parsed.OwnershipExpires < now)
        {
            return new SteamTicketCheck.Refused(Expired);
        }

        if (appId != 0 && parsed.AppId != appId)
        {
            return new SteamTicketCheck.Refused(WrongApp);
        }

        return new SteamTicketCheck.Verified(parsed, Convert.ToHexStringLower(SHA256.HashData(ticket.Span)));
    }

    public void Dispose() => publicKey.Dispose();
}

public static class SteamTicketHosting
{
    /// <summary>The Steam ticket verifier with Steam's own key (a test registers one with a key of its own).</summary>
    public static WebApplicationBuilder AddSteamTickets(this WebApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<ISteamTicketVerifier>(_ => SteamTicketVerifier.ForSteam());
        return builder;
    }
}
