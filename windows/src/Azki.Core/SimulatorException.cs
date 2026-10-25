namespace Azki.Core;

/// <summary>Error families of <c>SimulatorError</c> (SimulatorFrame.swift).</summary>
public enum SimulatorErrorKind
{
    InvalidSession,
    UnsafeFile,
    InvalidFrame,
    FrameBeingWritten,
    Disconnected,
    Transport,
    Remote,
    Engine,
}

public sealed class SimulatorException : Exception
{
    public SimulatorException(SimulatorErrorKind kind, string message, string? code = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        Code = code;
    }

    public SimulatorErrorKind Kind { get; }

    /// <summary>The bridge / runtime error code (for example <c>unsafe_session</c>), when known.</summary>
    public string? Code { get; }
}
