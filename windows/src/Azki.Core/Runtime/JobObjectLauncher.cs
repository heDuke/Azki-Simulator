using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Azki.Core.Runtime;

/// <summary>
/// Starts engine processes under supervision. C# port of the process-group half of
/// <c>StandaloneProcessSandbox.swift</c> / <c>StandaloneProcessReaper.swift</c>.
/// </summary>
/// <remarks>
/// Windows: every process gets its own Job Object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
/// Everything it starts (the runner supervisor's nested job, the runner, QEMU) stays in the job, so
/// <see cref="SupervisedProcess.Dispose"/>, an app crash or the app being killed ends the whole tree:
/// the kernel closes the handle and kills the job. The process is assigned right after it starts;
/// <c>serve</c> starts children only when it receives <c>start</c>, so nothing escapes that window.
/// Other OSes (tests and the Linux harness): the process tree is killed on dispose.
/// </remarks>
public static class JobObjectLauncher
{
    /// <summary>Starts <paramref name="startInfo"/>; output and error lines go to <paramref name="output"/>.</summary>
    public static SupervisedProcess Start(ProcessStartInfo startInfo, Action<string>? output = null,
        IDisposable? attachment = null)
    {
        startInfo.UseShellExecute = false;
        // A hidden console: the runtime's own CTRL_BREAK process-group stops need one.
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        JobHandle? job = OperatingSystem.IsWindows() ? JobHandle.Create() : null;
        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            process.OutputDataReceived += (_, line) => { if (line.Data is not null) { Capture(stdout, line.Data); output?.Invoke(line.Data); } };
            process.ErrorDataReceived += (_, line) => { if (line.Data is not null) { output?.Invoke(line.Data); } };
            if (!process.Start())
            {
                throw new SimulatorException(SimulatorErrorKind.Engine, "无法启动引擎进程：" + startInfo.FileName);
            }
            if (job is not null && OperatingSystem.IsWindows())
            {
                job.Assign(process);
            }
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return new SupervisedProcess(process, job, stdout, attachment);
        }
        catch
        {
            job?.Dispose();
            if (process is not null)
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } } catch (InvalidOperationException) { }
                process.Dispose();
            }
            throw;
        }
    }

    private static void Capture(StringBuilder buffer, string line)
    {
        lock (buffer)
        {
            if (buffer.Length < 4 * 1024 * 1024)
            {
                buffer.Append(line).Append('\n');
            }
        }
    }
}

/// <summary>A started engine process and (on Windows) its kill-on-close Job Object.</summary>
public sealed class SupervisedProcess : IDisposable
{
    private readonly Process _process;
    private readonly JobHandle? _job;
    private readonly StringBuilder _stdout;
    private readonly IDisposable? _attachment;
    private bool _disposed;

    internal SupervisedProcess(Process process, JobHandle? job, StringBuilder stdout, IDisposable? attachment)
    {
        _process = process;
        _job = job;
        _stdout = stdout;
        _attachment = attachment;
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    /// <summary>Everything written to standard output so far (capped at 4 MiB).</summary>
    public string StandardOutput
    {
        get { lock (_stdout) { return _stdout.ToString(); } }
    }

    /// <summary>Live processes in the job (Windows), or null where jobs do not exist.</summary>
    public int? ActiveProcessCount => _job is not null && OperatingSystem.IsWindows() ? _job.ActiveProcesses() : null;

    public async Task<int> WaitForExitAsync(CancellationToken cancellation = default)
    {
        await _process.WaitForExitAsync(cancellation).ConfigureAwait(false);
        return _process.ExitCode;
    }

    /// <summary>Kills the process and everything it started.</summary>
    public void Kill()
    {
        if (_job is not null && OperatingSystem.IsWindows())
        {
            _job.Terminate();
        }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Kill();
        try
        {
            _process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
        _job?.Dispose();
        _process.Dispose();
        _attachment?.Dispose();
    }
}

/// <summary>Owns one Job Object handle; closing it kills every process still in the job.</summary>
internal sealed partial class JobHandle : IDisposable
{
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private nint _handle;

    private JobHandle(nint handle) => _handle = handle;

    [SupportedOSPlatform("windows")]
    public static JobHandle Create()
    {
        nint handle = CreateJobObjectW(0, 0);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateJobObject failed");
        }
        var limits = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            int error = Marshal.GetLastPInvokeError();
            CloseHandle(handle);
            throw new Win32Exception(error, "SetInformationJobObject failed");
        }
        return new JobHandle(handle);
    }

    [SupportedOSPlatform("windows")]
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "AssignProcessToJobObject failed");
        }
    }

    [SupportedOSPlatform("windows")]
    public void Terminate()
    {
        if (_handle != 0)
        {
            TerminateJobObject(_handle, 1);
        }
    }

    [SupportedOSPlatform("windows")]
    public int ActiveProcesses()
    {
        var info = new BasicAccountingInformation();
        return _handle != 0 && QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, ref info,
            (uint)Marshal.SizeOf<BasicAccountingInformation>(), 0) ? (int)info.ActiveProcesses : 0;
    }

    public void Dispose()
    {
        if (_handle != 0 && OperatingSystem.IsWindows())
        {
            CloseHandle(_handle);
        }
        _handle = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateJobObjectW(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(nint job, int infoClass, ref BasicAccountingInformation info, uint length, nint returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
