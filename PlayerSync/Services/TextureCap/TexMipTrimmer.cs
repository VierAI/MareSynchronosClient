namespace MareSynchronos.Services.TextureCap;

/// <summary>
/// Fork (texture size cap): makes a smaller .tex by dropping the largest mip levels. The remaining mip data is copied
/// byte for byte and only the 80-byte header is rewritten, so the format (BC1/3/4/5/7, RGBA, ...) never changes and
/// nothing is recompressed. Textures without enough mip levels are left alone.
/// </summary>
public static class TexMipTrimmer
{
    public const int HeaderSize = 80;
    private const uint TextureTypeMask = 0x3C00000;
    private const uint TextureType2D = 0x800000;

    public readonly record struct TexInfo(uint Attribute, uint Format, int Width, int Height, int Depth, int MipCount, int ArraySize);

    public static bool TryReadHeader(string path, out TexInfo info)
    {
        info = default;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var b = new byte[16];
            if (fs.Read(b, 0, 16) < 16) return false;
            info = Parse(b);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static TexInfo Parse(byte[] b) => new(
        BitConverter.ToUInt32(b, 0), BitConverter.ToUInt32(b, 4),
        BitConverter.ToUInt16(b, 8), BitConverter.ToUInt16(b, 10), BitConverter.ToUInt16(b, 12),
        b[14] & 0x7F, b[15]);

    /// <summary>Bytes for one mip level, or -1 for a format we don't handle.</summary>
    public static long MipSize(uint format, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        long blocks = (long)((width + 3) / 4) * ((height + 3) / 4);
        return format switch
        {
            0x3420 or 0x6120 => blocks * 8,                    // BC1, BC4
            0x3430 or 0x3431 or 0x6230 or 0x6432 => blocks * 16, // BC2, BC3, BC5, BC7
            0x1130 or 0x1131 => (long)width * height,          // L8, A8
            0x1440 or 0x1441 => (long)width * height * 2,      // B4G4R4A4, B5G5R5A1
            0x1450 or 0x1451 or 0x2150 or 0x2250 => (long)width * height * 4, // B8G8R8A8, B8G8R8X8, R32F, R16G16F
            0x2260 or 0x2460 => (long)width * height * 8,      // R32G32F, R16G16B16A16F
            0x2470 => (long)width * height * 16,               // R32G32B32A32F
            _ => -1,
        };
    }

    /// <summary>
    /// Writes a copy of <paramref name="source"/> whose largest side is at most <paramref name="cap"/> to <paramref name="destination"/>.
    /// Returns false (with a reason) when the texture is already small enough or can't be trimmed.
    /// </summary>
    public static bool TryTrim(string source, int cap, string destination, out TexInfo before, out int newWidth, out int newHeight, out string reason)
    {
        before = default;
        newWidth = newHeight = 0;
        reason = string.Empty;
        byte[] data;
        try
        {
            data = File.ReadAllBytes(source);
        }
        catch (Exception ex)
        {
            reason = "read failed: " + ex.Message;
            return false;
        }

        if (data.Length < HeaderSize) { reason = "too small"; return false; }
        before = Parse(data);
        var info = before;
        if ((info.Attribute & TextureTypeMask) != TextureType2D || info.Depth > 1 || info.ArraySize > 1) { reason = "not a plain 2D texture"; return false; }
        if (Math.Max(info.Width, info.Height) <= cap) { reason = "already within cap"; return false; }
        if (MipSize(info.Format, info.Width, info.Height) < 0) { reason = $"unsupported format 0x{info.Format:X}"; return false; }
        var mipCount = Math.Min(info.MipCount, 13);

        var skip = 0;
        while (Math.Max(Math.Max(1, info.Width >> skip), Math.Max(1, info.Height >> skip)) > cap) skip++;
        if (skip >= mipCount) { reason = "not enough mip levels"; return false; }

        // Check the stored mip offsets match the expected layout before trusting them.
        var offsets = new uint[13];
        for (var i = 0; i < 13; i++) offsets[i] = BitConverter.ToUInt32(data, 28 + i * 4);
        long expected = offsets[0];
        if (expected < HeaderSize) { reason = "bad first mip offset"; return false; }
        var sizes = new long[mipCount];
        for (var i = 0; i < mipCount; i++)
        {
            if (offsets[i] != expected) { reason = "unexpected mip layout"; return false; }
            sizes[i] = MipSize(info.Format, info.Width >> i, info.Height >> i);
            expected += sizes[i];
        }
        if (expected > data.Length) { reason = "file shorter than its mips"; return false; }

        var newMipCount = mipCount - skip;
        newWidth = Math.Max(1, info.Width >> skip);
        newHeight = Math.Max(1, info.Height >> skip);
        long payload = 0;
        for (var i = skip; i < mipCount; i++) payload += sizes[i];

        var output = new byte[HeaderSize + payload];
        Buffer.BlockCopy(data, 0, output, 0, HeaderSize);
        BitConverter.TryWriteBytes(output.AsSpan(8), (ushort)newWidth);
        BitConverter.TryWriteBytes(output.AsSpan(10), (ushort)newHeight);
        output[14] = (byte)((data[14] & 0x80) | newMipCount);
        for (var j = 0; j < 3; j++)
        {
            var lod = (long)BitConverter.ToUInt32(data, 16 + j * 4) - skip;
            lod = Math.Clamp(lod, 0, newMipCount - 1);
            BitConverter.TryWriteBytes(output.AsSpan(16 + j * 4), (uint)lod);
        }
        long offset = HeaderSize;
        for (var i = 0; i < 13; i++)
        {
            uint value = 0;
            if (i < newMipCount)
            {
                value = (uint)offset;
                offset += sizes[skip + i];
            }
            BitConverter.TryWriteBytes(output.AsSpan(28 + i * 4), value);
        }
        Buffer.BlockCopy(data, (int)offsets[skip], output, HeaderSize, (int)payload);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + ".tmp";
            File.WriteAllBytes(temp, output);
            File.Move(temp, destination, true);
        }
        catch (Exception ex)
        {
            reason = "write failed: " + ex.Message;
            return false;
        }

        return true;
    }
}
