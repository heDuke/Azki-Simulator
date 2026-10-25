using Azki.Core.Frame;

namespace Azki.Core.Session;

public enum FrameReadStatus
{
    /// <summary>A complete, newer frame was decoded.</summary>
    New,
    /// <summary>The published frame has the same header (sequence) as the last one returned.</summary>
    Unchanged,
    /// <summary>No frame yet (missing file, zero sequence or a write in progress); poll again.</summary>
    Unavailable,
}

public readonly record struct FrameReadResult(FrameReadStatus Status, SimulatorFrame? Frame = null);

/// <summary>
/// Reads <c>frame.raw</c>, which <c>minative_bridge.py</c> publishes up to 60 times a second by
/// writing a temporary file and <c>os.replace</c>-ing it over the old one.
/// </summary>
/// <remarks>
/// Windows: the file is opened with <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/> and
/// closed right after the read, so the writer's replace never fails because of this reader (a replace
/// that keeps failing stops the frame worker and with it the simulator). New frames are detected by the
/// header sequence number (even = stable), never by the file's modification time.
/// </remarks>
public sealed class FrameReader
{
    private readonly string _path;
    private readonly DisplayGeometry? _expected;
    private readonly byte[] _header = new byte[FrameHeader.ByteCount];
    private byte[] _buffer = [];
    private uint _lastSequence;

    public FrameReader(string sessionDirectory, DisplayGeometry? expected)
    {
        _path = Path.Combine(sessionDirectory, "frame.raw");
        _expected = expected;
    }

    public string FramePath => _path;

    /// <summary>Sequence of the last frame returned as <see cref="FrameReadStatus.New"/> (0 before the first).</summary>
    public uint LastSequence => _lastSequence;

    public void Reset() => _lastSequence = 0;

    public FrameReadResult Read()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(_path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 0,
                Options = FileOptions.None,
            });
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException
                                      or UnauthorizedAccessException or IOException)
        {
            // Missing before the first frame; on Windows a replace can be in flight (delete pending).
            return new FrameReadResult(FrameReadStatus.Unavailable);
        }
        using (stream)
        {
            long length = stream.Length;
            if (length < FrameHeader.ByteCount || length > FrameHeader.MaximumBytes)
            {
                return new FrameReadResult(FrameReadStatus.Unavailable);
            }
            if (stream.ReadAtLeast(_header, FrameHeader.ByteCount, throwOnEndOfStream: false) != FrameHeader.ByteCount)
            {
                return new FrameReadResult(FrameReadStatus.Unavailable);
            }
            FrameHeader header;
            try
            {
                header = FrameHeader.Decode(_header);
            }
            catch (SimulatorException error) when (error.Kind == SimulatorErrorKind.FrameBeingWritten)
            {
                return new FrameReadResult(FrameReadStatus.Unavailable);
            }
            if (header.Sequence == _lastSequence)
            {
                return new FrameReadResult(FrameReadStatus.Unchanged);
            }
            if (length != header.FrameBytes)
            {
                throw new SimulatorException(SimulatorErrorKind.InvalidFrame, "画面无效：像素长度不匹配");
            }
            if (_buffer.Length != length)
            {
                _buffer = new byte[length];
            }
            _header.CopyTo(_buffer, 0);
            int pixels = (int)length - FrameHeader.ByteCount;
            if (stream.ReadAtLeast(_buffer.AsSpan(FrameHeader.ByteCount), pixels, throwOnEndOfStream: false) != pixels)
            {
                return new FrameReadResult(FrameReadStatus.Unavailable);
            }
            var frame = SimulatorFrame.Decode(_buffer, _expected);
            _lastSequence = frame.Sequence;
            return new FrameReadResult(FrameReadStatus.New, frame);
        }
    }
}
