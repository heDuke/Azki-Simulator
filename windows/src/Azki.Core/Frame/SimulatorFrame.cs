using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Azki.Core.Frame;

/// <summary>
/// The finite display contract of the imported Xiaomi Band profiles
/// (<c>SimulatorDisplayGeometry</c> in SimulatorFrame.swift, bridge.py <c>DISPLAY_GEOMETRIES</c>).
/// </summary>
public readonly record struct DisplayGeometry(int Width, int Height)
{
    public static readonly DisplayGeometry[] All = [new(192, 490), new(212, 520), new(336, 480), new(432, 514)];

    public static DisplayGeometry? Find(int width, int height)
    {
        foreach (var geometry in All)
        {
            if (geometry.Width == width && geometry.Height == height)
            {
                return geometry;
            }
        }
        return null;
    }

    public override string ToString() => $"{Width}×{Height}";
}

/// <summary>
/// Wire header <c>&lt;8s6I</c>: magic <c>Q66FRAME</c>, version, sequence, width, height, stride, format,
/// followed by <c>height</c> rows of little-endian RGB565. An odd sequence means the producer is writing.
/// </summary>
public readonly record struct FrameHeader(uint Sequence, int Width, int Height, int Stride)
{
    public const int ByteCount = 32;
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "Q66FRAME"u8;

    public int FrameBytes => ByteCount + Stride * Height;

    private static uint Word(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    public static FrameHeader Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < ByteCount || !data[..8].SequenceEqual(Magic))
        {
            throw new SimulatorException(SimulatorErrorKind.InvalidFrame, "画面无效：文件头不匹配");
        }
        if (Word(data, 8) != 1 || Word(data, 28) != 1)
        {
            throw new SimulatorException(SimulatorErrorKind.InvalidFrame, "画面无效：版本或像素格式不受支持");
        }
        uint sequence = Word(data, 12);
        if (sequence == 0 || (sequence & 1) != 0)
        {
            throw new SimulatorException(SimulatorErrorKind.FrameBeingWritten, "画面正在更新");
        }
        uint width = Word(data, 16), height = Word(data, 20), stride = Word(data, 24);
        if (width is < 1 or > 2048 || height is < 1 or > 2048 || stride < width * 2 || stride > 8192 || stride % 2 != 0
            || (long)stride * height > MaximumBytes - ByteCount)
        {
            throw new SimulatorException(SimulatorErrorKind.InvalidFrame, "画面无效：尺寸、行宽或长度超限");
        }
        return new FrameHeader(sequence, (int)width, (int)height, (int)stride);
    }
}

/// <summary>A decoded frame in BGRA8 (premultiplied-equal, alpha 255), the layout of <c>WriteableBitmap</c>.</summary>
public sealed class SimulatorFrame
{
    private SimulatorFrame(uint sequence, int width, int height, byte[] bgra)
    {
        Sequence = sequence;
        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public uint Sequence { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Width × Height × 4 bytes, rows tightly packed, B G R A.</summary>
    public byte[] Bgra { get; }

    /// <summary>Decodes a complete frame file; <paramref name="expected"/> pins the session's geometry.</summary>
    public static SimulatorFrame Decode(ReadOnlySpan<byte> data, DisplayGeometry? expected = null)
    {
        var header = FrameHeader.Decode(data);
        if (data.Length != header.FrameBytes)
        {
            throw new SimulatorException(SimulatorErrorKind.InvalidFrame, "画面无效：像素长度不匹配");
        }
        if (expected is { } geometry
            && (header.Width != geometry.Width || header.Height != geometry.Height || header.Stride != geometry.Width * 2))
        {
            throw new SimulatorException(SimulatorErrorKind.InvalidFrame, "画面无效：画面尺寸或行宽与已验证会话不一致");
        }
        var output = new byte[header.Width * header.Height * 4];
        var pixels = data[FrameHeader.ByteCount..];
        for (int row = 0; row < header.Height; row++)
        {
            Rgb565.ToBgra(pixels.Slice(row * header.Stride, header.Width * 2),
                output.AsSpan(row * header.Width * 4, header.Width * 4));
        }
        return new SimulatorFrame(header.Sequence, header.Width, header.Height, output);
    }
}

/// <summary>
/// Little-endian RGB565 → BGRA8. Channels are left-aligned (r5 &lt;&lt; 3, g6 &lt;&lt; 2) exactly like the
/// macOS path (vImage expansion with the low bits masked off), so both show identical pixels.
/// </summary>
public static class Rgb565
{
    public static void ToBgra(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length % 2 != 0 || destination.Length < source.Length * 2)
        {
            throw new ArgumentException("RGB565 source must be whole pixels and the destination 4 bytes per pixel");
        }
        var input = MemoryMarshal.Cast<byte, ushort>(source);
        var output = MemoryMarshal.Cast<byte, uint>(destination[..(source.Length * 2)]);
        if (!BitConverter.IsLittleEndian)
        {
            ScalarBigEndian(source, output);
            return;
        }
        int index = 0;
        if (Vector.IsHardwareAccelerated && input.Length >= Vector<ushort>.Count)
        {
            var blue = new Vector<uint>(0x1f);
            var green = new Vector<uint>(0x3f);
            var alpha = new Vector<uint>(0xff000000);
            for (; index <= input.Length - Vector<ushort>.Count; index += Vector<ushort>.Count)
            {
                Vector.Widen(new Vector<ushort>(input[index..]), out var low, out var high);
                Convert(low, blue, green, alpha).CopyTo(output[index..]);
                Convert(high, blue, green, alpha).CopyTo(output[(index + Vector<uint>.Count)..]);
            }
        }
        for (; index < input.Length; index++)
        {
            output[index] = Pixel(input[index]);
        }
    }

    private static Vector<uint> Convert(Vector<uint> p, Vector<uint> blue, Vector<uint> green, Vector<uint> alpha) =>
        ((p & blue) << 3) | (((p >> 5) & green) << 10) | ((p >> 11) << 19) | alpha;

    /// <summary>One pixel as a little-endian BGRA word.</summary>
    public static uint Pixel(ushort p) =>
        ((uint)(p & 0x1f) << 3) | ((uint)((p >> 5) & 0x3f) << 10) | ((uint)(p >> 11) << 19) | 0xff000000;

    private static void ScalarBigEndian(ReadOnlySpan<byte> source, Span<uint> output)
    {
        for (int index = 0; index < output.Length; index++)
        {
            uint bgra = Pixel(BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(index * 2, 2)));
            output[index] = BinaryPrimitives.ReverseEndianness(bgra);
        }
    }
}
