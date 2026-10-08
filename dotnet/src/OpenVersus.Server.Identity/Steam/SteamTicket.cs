using System.Buffers.Binary;

namespace OpenVersus.Server.Identity.Steam;

/// <summary>
/// A Steam session ticket (ISteamUser::GetAuthSessionTicket) or the bare app ownership ticket inside one, laid out as
/// SteamKit's steam3_appticket.hsl has it (little-endian throughout):
/// <list type="bullet">
/// <item>a full ticket: u32 20, the GC token u64, the SteamID u64, the token's time u32; u32 24, 8 unknown bytes, the
/// session's external IP u32, 4 filler bytes, the connection time u32, the connection count u32; u32 the length of the
/// rest;</item>
/// <item>the ownership ticket: u32 its length (itself included), version u32, SteamID u64, app id u32, external and
/// internal IP u32, flags u32, generated and expires u32 (Unix seconds), u16 licenses (u32 each), u16 DLCs (app id u32,
/// u16 licenses, u32 each), u16 reserved;</item>
/// <item>128 bytes: Steam's RSA-SHA1 signature over the ownership ticket's bytes.</item>
/// </list>
/// Only the ownership ticket is signed: the session header (time, external IP) and the GC token are not, and Steam
/// reuses the ownership ticket for about three weeks, so a ticket stands for its SteamID, not for this moment.
/// </summary>
public sealed record SteamTicket(
    string SteamId,
    uint AppId,
    uint Version,
    uint OwnershipFlags,
    IReadOnlyList<uint> Licenses,
    IReadOnlyList<SteamTicketDlc> Dlc,
    DateTimeOffset OwnershipGenerated,
    DateTimeOffset OwnershipExpires,
    string OwnershipExternalIp,
    string OwnershipInternalIp,
    SteamTicketSession? Session)
{
    /// <summary>The ownership ticket's bytes, which the signature covers.</summary>
    public required ReadOnlyMemory<byte> SignedBytes { get; init; }

    /// <summary>The 128-byte signature, or empty for an unsigned ticket.</summary>
    public required ReadOnlyMemory<byte> Signature { get; init; }

    public bool HasSignature => Signature.Length > 0;

    /// <summary>The ticket, or null when the bytes are not laid out as one (every length and count must fit).</summary>
    public static SteamTicket? Parse(ReadOnlyMemory<byte> ticket)
    {
        try
        {
            return ParseOrThrow(ticket);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static SteamTicket? ParseOrThrow(ReadOnlyMemory<byte> ticket)
    {
        var bytes = ticket.Span;
        int pos = 0;
        SteamTicketSession? session = null;
        if (bytes.Length >= 4 && U32(bytes, 0) == 20)
        {
            pos = 4;
            ulong gcToken = U64(bytes, pos);
            pos += 8;
            pos += 8; // the SteamID, read from the ownership ticket
            var tokenGenerated = Seconds(U32(bytes, pos));
            pos += 4;
            if (U32(bytes, pos) != 24)
            {
                return null;
            }

            pos += 4;
            pos += 8; // unknown
            string externalIp = Ip(U32(bytes, pos));
            pos += 4;
            pos += 4; // filler
            uint connectionTime = U32(bytes, pos);
            pos += 4;
            uint connectionCount = U32(bytes, pos);
            pos += 4;
            uint rest = U32(bytes, pos);
            pos += 4;
            if (rest != (uint)(bytes.Length - pos))
            {
                return null;
            }

            session = new SteamTicketSession(gcToken, tokenGenerated, externalIp, connectionTime, connectionCount);
        }

        int ownershipOffset = pos;
        uint ownershipLength = U32(bytes, pos);
        pos += 4;
        long ownershipEnd = (long)ownershipOffset + ownershipLength;
        if (ownershipEnd != bytes.Length && ownershipEnd + 128 != bytes.Length)
        {
            return null;
        }

        uint version = U32(bytes, pos);
        pos += 4;
        ulong steamId = U64(bytes, pos);
        pos += 8;
        uint appId = U32(bytes, pos);
        pos += 4;
        string ownershipExternalIp = Ip(U32(bytes, pos));
        pos += 4;
        string ownershipInternalIp = Ip(U32(bytes, pos));
        pos += 4;
        uint flags = U32(bytes, pos);
        pos += 4;
        var generated = Seconds(U32(bytes, pos));
        pos += 4;
        var expires = Seconds(U32(bytes, pos));
        pos += 4;

        var licenses = new List<uint>();
        int licenseCount = U16(bytes, pos);
        pos += 2;
        for (int i = 0; i < licenseCount; i++)
        {
            licenses.Add(U32(bytes, pos));
            pos += 4;
        }

        var dlc = new List<SteamTicketDlc>();
        int dlcCount = U16(bytes, pos);
        pos += 2;
        for (int i = 0; i < dlcCount; i++)
        {
            uint dlcAppId = U32(bytes, pos);
            pos += 4;
            var dlcLicenses = new List<uint>();
            int dlcLicenseCount = U16(bytes, pos);
            pos += 2;
            for (int j = 0; j < dlcLicenseCount; j++)
            {
                dlcLicenses.Add(U32(bytes, pos));
                pos += 4;
            }

            dlc.Add(new SteamTicketDlc(dlcAppId, dlcLicenses));
        }

        pos += 2; // reserved
        // The fields must fill the ownership ticket exactly: a count that reaches into the signature is not a ticket.
        if (pos != ownershipEnd)
        {
            return null;
        }

        var signature = ownershipEnd + 128 == bytes.Length ? ticket.Slice((int)ownershipEnd, 128) : ReadOnlyMemory<byte>.Empty;
        return new SteamTicket(steamId.ToString(System.Globalization.CultureInfo.InvariantCulture), appId, version, flags, licenses, dlc,
            generated, expires, ownershipExternalIp, ownershipInternalIp, session)
        {
            SignedBytes = ticket.Slice(ownershipOffset, (int)ownershipLength),
            Signature = signature,
        };
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(at, 2));

    private static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at, 4));

    private static ulong U64(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(at, 8));

    private static DateTimeOffset Seconds(uint unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    // The u32 is read little-endian and then printed as a big-endian address, as steam-appticket (intToString) does.
    private static string Ip(uint value) => $"{(value >> 24) & 255}.{(value >> 16) & 255}.{(value >> 8) & 255}.{value & 255}";
}

/// <summary>A DLC the ticket's owner holds, with its licenses.</summary>
public sealed record SteamTicketDlc(uint AppId, IReadOnlyList<uint> Licenses);

/// <summary>A full session ticket's unsigned header: the GC token and the client's Steam session at the time.</summary>
public sealed record SteamTicketSession(ulong GcToken, DateTimeOffset TokenGenerated, string ExternalIp, uint ConnectionTime, uint ConnectionCount);
