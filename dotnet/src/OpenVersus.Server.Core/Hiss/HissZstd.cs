using ZstdSharp;
using ZstdSharp.Unsafe;

namespace OpenVersus.Server.Core.Hiss;

/// <summary>
/// Compresses one hiss section as zstd, for the OpenVersus client's HydraZstd hook (ovs-client, dotnet/OpenVersus.Core:
/// Hooks/HydraZstdHook.cs, Net/ZstdInflate.cs), which decodes it where the game would inflate zlib.
/// <para>
/// The contract with that decoder: one zstd frame per section, starting with the magic 28 B5 2F FD (how the client
/// tells it from zlib); the frame header holds the content size, so the window is the section's size (the client
/// refuses a window over 16 MB; the largest section is 1.5 MB); and a checksum, so a damaged section fails instead of
/// giving the game wrong data. HissServiceTests checks each of these on the frames made here. Level 22, zstd's
/// highest: the answer is made once per CRC, so its cost (a few seconds) is never paid by a login.
/// </para>
/// </summary>
public static class HissZstd
{
    /// <summary>zstd's highest level.</summary>
    public const int Level = 22;

    /// <summary>The largest window the client accepts (its ZstdInflate.WindowLogMax, 24).</summary>
    public const long ClientWindowLimit = 16 << 20;

    /// <summary>One section as a zstd frame.</summary>
    public static byte[] Compress(byte[] section)
    {
        using var compressor = new Compressor(Level);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        compressor.SetParameter(ZSTD_cParameter.ZSTD_c_contentSizeFlag, 1);
        return compressor.Wrap(section).ToArray();
    }
}
