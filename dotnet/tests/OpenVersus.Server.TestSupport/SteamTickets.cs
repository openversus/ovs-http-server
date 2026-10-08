using System.Security.Cryptography;

namespace OpenVersus.Server.TestSupport;

/// <summary>
/// Steam session tickets built for tests and signed with a key of the test's own, laid out as SteamKit's
/// steam3_appticket.hsl (what <c>Core/Steam/SteamTicket</c> parses). Steam's real key only sees a real ticket, which
/// lives outside the repository.
/// </summary>
public static class SteamTickets
{
    public const ulong SteamId = 76561198000000091;
    public const uint AppId = 1818750;
    public static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Generated = Now.AddDays(-3);
    public static readonly DateTimeOffset Expires = Now.AddDays(18);

    /// <summary>A signing key; <see cref="PublicOf"/> gives the verifier's half.</summary>
    public static RSA NewKey() => RSA.Create(1024);

    public static RSA PublicOf(RSA key)
    {
        var publicKey = RSA.Create();
        publicKey.ImportRSAPublicKey(key.ExportRSAPublicKey(), out _);
        return publicKey;
    }

    /// <summary>A full session ticket (header, signed ownership ticket) for <paramref name="key"/>'s owner.</summary>
    public static byte[] Session(RSA key, ulong steamId = SteamId, uint appId = AppId, uint[]? licenses = null, DateTimeOffset? expires = null) =>
        Full(Signed(Ownership(steamId, appId, licenses, expires: expires), key), steamId);

    public static byte[] Ownership(ulong steamId = SteamId, uint appId = AppId, uint[]? licenses = null, (uint AppId, uint[] Licenses)[]? dlc = null, DateTimeOffset? expires = null)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(0u); // length, patched below
        w.Write(2u); // version
        w.Write(steamId);
        w.Write(appId);
        w.Write(0xCB007107u); // 203.0.113.7
        w.Write(0xC0A80114u); // 192.168.1.20
        w.Write(0u); // flags
        w.Write((uint)Generated.ToUnixTimeSeconds());
        w.Write((uint)(expires ?? Expires).ToUnixTimeSeconds());
        licenses ??= [];
        w.Write((ushort)licenses.Length);
        foreach (uint license in licenses)
        {
            w.Write(license);
        }

        dlc ??= [];
        w.Write((ushort)dlc.Length);
        foreach (var (dlcApp, dlcLicenses) in dlc)
        {
            w.Write(dlcApp);
            w.Write((ushort)dlcLicenses.Length);
            foreach (uint license in dlcLicenses)
            {
                w.Write(license);
            }
        }

        w.Write((ushort)0); // reserved
        w.Flush();
        byte[] bytes = stream.ToArray();
        BitConverter.GetBytes((uint)bytes.Length).CopyTo(bytes, 0);
        return bytes;
    }

    public static byte[] Signed(byte[] ownership, RSA key) => [.. ownership, .. key.SignData(ownership, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1)];

    /// <summary>The 52-byte session header, the section length and <paramref name="ownershipSection"/> (signed or not).</summary>
    public static byte[] Full(byte[] ownershipSection, ulong steamId = SteamId)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(20u);
        w.Write(0x1122334455667788ul); // GC token
        w.Write(steamId);
        w.Write((uint)Now.AddMinutes(-5).ToUnixTimeSeconds());
        w.Write(24u);
        w.Write(0ul); // unknown
        w.Write(0xCB007107u); // external IP
        w.Write(0u); // filler
        w.Write(90000u); // connection time
        w.Write(3u); // connection count
        w.Write((uint)ownershipSection.Length);
        w.Write(ownershipSection);
        w.Flush();
        return stream.ToArray();
    }
}
