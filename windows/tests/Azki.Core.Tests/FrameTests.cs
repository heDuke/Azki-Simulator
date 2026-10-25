using System.Buffers.Binary;
using Azki.Core.Frame;
using Azki.Core.Session;

namespace Azki.Core.Tests;

internal static class SyntheticFrame
{
    public static byte[] Build(uint sequence, int width, int height, Func<int, int, ushort> pixel, int? stride = null,
        uint version = 1, uint format = 1, string magic = "Q66FRAME")
    {
        int rowBytes = stride ?? width * 2;
        var data = new byte[FrameHeader.ByteCount + rowBytes * height];
        System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(data, 0);
        var words = new uint[] { version, sequence, (uint)width, (uint)height, (uint)rowBytes, format };
        for (int index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8 + index * 4), words[index]);
        }
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(FrameHeader.ByteCount + y * rowBytes + x * 2), pixel(x, y));
            }
        }
        return data;
    }
}

public sealed class FrameTests
{
    private static readonly byte[] Red = [0, 0, 0xF8, 0xFF];
    private static readonly byte[] Blue = [0xF8, 0, 0, 0xFF];
    private static readonly string[] Geometries = ["192×490", "212×520", "336×480", "432×514"];

    [Theory]
    [InlineData((ushort)0xF800, 0x00, 0x00, 0xF8)] // red
    [InlineData((ushort)0x07E0, 0x00, 0xFC, 0x00)] // green
    [InlineData((ushort)0x001F, 0xF8, 0x00, 0x00)] // blue
    [InlineData((ushort)0xFFFF, 0xF8, 0xFC, 0xF8)] // white, low bits left clear like vImage + mask
    [InlineData((ushort)0x0000, 0x00, 0x00, 0x00)]
    public void Rgb565PixelsBecomeLeftAlignedBgra(ushort pixel, byte b, byte g, byte r)
    {
        var source = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(source, pixel);
        var output = new byte[4];
        Rgb565.ToBgra(source, output);
        Assert.Equal(new[] { b, g, r, (byte)0xFF }, output);
    }

    [Fact]
    public void VectorPathMatchesScalarForEveryValue()
    {
        var source = new byte[65536 * 2 + 6]; // every RGB565 value plus an unaligned tail
        for (int value = 0; value < 65536 + 3; value++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(value * 2), (ushort)(value * 40503));
        }
        var output = new byte[source.Length * 2];
        Rgb565.ToBgra(source, output);
        for (int index = 0; index < source.Length / 2; index++)
        {
            ushort pixel = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(index * 2));
            Assert.Equal(Rgb565.Pixel(pixel), BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(index * 4)));
        }
    }

    [Fact]
    public void DecodesAFullFrameWithStridePadding()
    {
        var data = SyntheticFrame.Build(2, 3, 2, (x, y) => (ushort)(x == 0 ? 0xF800 : y == 1 ? 0x001F : 0), stride: 8);
        var frame = SimulatorFrame.Decode(data);
        Assert.Equal((2u, 3, 2), (frame.Sequence, frame.Width, frame.Height));
        Assert.Equal(3 * 2 * 4, frame.Bgra.Length);
        Assert.Equal(Red, frame.Bgra.AsSpan(0, 4).ToArray());           // (0,0) red
        Assert.Equal(Blue, frame.Bgra.AsSpan(4 * 4, 4).ToArray()); // (1,1) blue
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]
    public void ZeroOrOddSequenceIsBeingWritten(uint sequence)
    {
        var data = SyntheticFrame.Build(sequence, 2, 2, (_, _) => 0);
        var error = Assert.Throws<SimulatorException>(() => FrameHeader.Decode(data));
        Assert.Equal(SimulatorErrorKind.FrameBeingWritten, error.Kind);
    }

    [Fact]
    public void RejectsBadMagicVersionFormatAndGeometry()
    {
        Assert.Throws<SimulatorException>(() => FrameHeader.Decode(SyntheticFrame.Build(2, 2, 2, (_, _) => 0, magic: "Q66FRAMX")));
        Assert.Throws<SimulatorException>(() => FrameHeader.Decode(SyntheticFrame.Build(2, 2, 2, (_, _) => 0, version: 2)));
        Assert.Throws<SimulatorException>(() => FrameHeader.Decode(SyntheticFrame.Build(2, 2, 2, (_, _) => 0, format: 2)));
        var narrow = SyntheticFrame.Build(2, 4, 2, (_, _) => 0);
        BinaryPrimitives.WriteUInt32LittleEndian(narrow.AsSpan(24), 6); // stride < width * 2
        Assert.Throws<SimulatorException>(() => FrameHeader.Decode(narrow));
        var frame = SyntheticFrame.Build(2, 336, 480, (_, _) => 0);
        Assert.Throws<SimulatorException>(() => SimulatorFrame.Decode(frame, new DisplayGeometry(212, 520)));
        Assert.Throws<SimulatorException>(() => SimulatorFrame.Decode(frame.AsSpan(0, frame.Length - 2)));
        Assert.Equal(336, SimulatorFrame.Decode(frame, new DisplayGeometry(336, 480)).Width);
    }

    [Fact]
    public void ReaderDetectsNewFramesBySequenceNotTime()
    {
        using var directory = new TemporaryDirectory();
        var reader = new FrameReader(directory.Path, new DisplayGeometry(336, 480));
        Assert.Equal(FrameReadStatus.Unavailable, reader.Read().Status);

        Publish(directory.Path, SyntheticFrame.Build(2, 336, 480, (_, _) => 0xF800));
        var first = reader.Read();
        Assert.Equal(FrameReadStatus.New, first.Status);
        Assert.Equal(2u, first.Frame!.Sequence);

        // Same header republished with a newer mtime: unchanged.
        Publish(directory.Path, SyntheticFrame.Build(2, 336, 480, (_, _) => 0xF800));
        File.SetLastWriteTimeUtc(Path.Combine(directory.Path, "frame.raw"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(FrameReadStatus.Unchanged, reader.Read().Status);

        // A write in progress (odd) is not an error.
        Publish(directory.Path, SyntheticFrame.Build(3, 336, 480, (_, _) => 0));
        Assert.Equal(FrameReadStatus.Unavailable, reader.Read().Status);

        // A newer frame with an older mtime is still new.
        Publish(directory.Path, SyntheticFrame.Build(4, 336, 480, (_, _) => 0x001F));
        File.SetLastWriteTimeUtc(Path.Combine(directory.Path, "frame.raw"), DateTime.UtcNow.AddMinutes(-10));
        var second = reader.Read();
        Assert.Equal(FrameReadStatus.New, second.Status);
        Assert.Equal(0xF8, second.Frame!.Bgra[0]);
    }

    [Fact]
    public void ReaderNeverBlocksTheWritersReplace()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "frame.raw");
        Publish(directory.Path, SyntheticFrame.Build(2, 192, 490, (_, _) => 1));
        // A reader that holds the file open with the reader's share mode (as during a read) ...
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            // ... must not make the writer's os.replace (MoveFileEx REPLACE_EXISTING) fail.
            Publish(directory.Path, SyntheticFrame.Build(4, 192, 490, (_, _) => 2));
        }
        var reader = new FrameReader(directory.Path, new DisplayGeometry(192, 490));
        Assert.Equal(4u, reader.Read().Frame!.Sequence);
    }

    [Fact]
    public void SupportedGeometriesMatchTheBridge()
    {
        Assert.Equal(Geometries, DisplayGeometry.All.Select(g => g.ToString()));
        Assert.Null(DisplayGeometry.Find(100, 100));
    }

    private static void Publish(string directory, byte[] data)
    {
        var temporary = Path.Combine(directory, ".frame-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, Path.Combine(directory, "frame.raw"), overwrite: true);
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "azki-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class PngEncoderTests
{
    [Fact]
    public void WritesAValidPngSkeleton()
    {
        var frame = SimulatorFrame.Decode(SyntheticFrame.Build(2, 192, 490, (x, _) => (ushort)(x < 96 ? 0xF800 : 0x001F)));
        var png = Azki.Core.Media.PngEncoder.Encode(frame);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png.AsSpan(0, 8).ToArray());
        Assert.Equal(192, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(490, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        // IEND with its well-known CRC closes the file.
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 }, png.AsSpan(png.Length - 12).ToArray());
    }
}
