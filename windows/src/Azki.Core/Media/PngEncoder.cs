using System.Buffers.Binary;
using System.IO.Compression;
using Azki.Core.Frame;

namespace Azki.Core.Media;

/// <summary>Minimal, dependency-free PNG (8-bit RGB) writer for decoded frames; used by screenshots and tests.</summary>
public static class PngEncoder
{
    public static byte[] Encode(SimulatorFrame frame)
    {
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + frame.Width * 3];
            for (int y = 0; y < frame.Height; y++)
            {
                var source = frame.Bgra.AsSpan(y * frame.Width * 4, frame.Width * 4);
                for (int x = 0; x < frame.Width; x++)
                {
                    row[1 + x * 3] = source[x * 4 + 2];
                    row[2 + x * 3] = source[x * 4 + 1];
                    row[3 + x * 3] = source[x * 4];
                }
                zlib.Write(row);
            }
        }
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), frame.Height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type RGB
        Chunk(output, "IHDR"u8, header);
        Chunk(output, "IDAT"u8, raw.ToArray());
        Chunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        output.Write(type);
        output.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        output.Write(word);
    }

    private static readonly uint[] CrcTable = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }
}
