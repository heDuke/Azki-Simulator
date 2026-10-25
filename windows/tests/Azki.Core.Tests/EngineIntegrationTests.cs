using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Azki.Core.Runtime;
using Azki.Core.Session;
using Azki.Core.Storage;

namespace Azki.Core.Tests;

/// <summary>
/// Runs against a real engine bundle when <c>AZKI_ENGINE_BUNDLE</c> points at one (Windows CI sets it
/// after assembling the bundle). No firmware: a legacy session over placeholder files is only
/// fingerprinted by the runtime, never executed (the same check as <c>azki-engine selftest</c>).
/// </summary>
public sealed class EngineIntegrationTests
{
    private static EngineBundle RequireBundle()
    {
        var root = Environment.GetEnvironmentVariable(EngineBundle.EnvironmentVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(root), "AZKI_ENGINE_BUNDLE is not set");
        var bundle = new EngineBundle(Path.GetFullPath(root!));
        Assert.True(bundle.IsComplete, "Incomplete engine bundle at " + bundle.Root);
        return bundle;
    }

    [Fact]
    public async Task ServesAControlEndpointAndStopsWithoutLeftovers()
    {
        var bundle = RequireBundle();
        var cancellation = TestContext.Current.CancellationToken;
        using var work = new TemporaryDirectory();
        var host = new RuntimeHost(bundle, new DataLocations(Path.Combine(work.Path, "data")));
        var source = Path.Combine(work.Path, "source");
        foreach (var name in new[] { "build/qemu-system-arm", "firmware/vela_ap.bin", "firmware/vela_resource.bin" })
        {
            var path = Path.Combine(source, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(1024));
        }
        var sessionDirectory = Path.Combine(work.Path, "session");
        await host.RunJsonAsync(TimeSpan.FromMinutes(2), cancellation, "prepare", sessionDirectory, "--source", source);

        var session = await SimulatorSession.LaunchAsync(host, new PreparedSession(work.Path, sessionDirectory, null, null),
            cancellation: cancellation);
        int servePid;
        await using (session)
        {
            var status = await session.StatusAsync(cancellation);
            Assert.Equal("stopped", status.State);
            Assert.Equal(FrameReadStatus.Unavailable, session.Frames.Read().Status);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(ControlEndpoint.IsEndpointFile(session.Connection.ControlPath));
                var endpoint = ControlEndpoint.Load(session.Connection.ControlPath);
                Assert.NotNull(endpoint.Token);
                Assert.True(session.ActiveProcessCount >= 1);
                // A client with a wrong token is closed without a reply.
                using var rogue = new TcpClient();
                await rogue.ConnectAsync("127.0.0.1", endpoint.Port, cancellation);
                await rogue.GetStream().WriteAsync(Encoding.ASCII.GetBytes(new string('0', 64) + "\n{\"version\":1,\"command\":\"status\"}\n"), cancellation);
                var buffer = new byte[256];
                int read;
                try
                {
                    read = await rogue.GetStream().ReadAsync(buffer, cancellation);
                }
                catch (IOException)
                {
                    read = 0;
                }
                Assert.Equal(0, read);
                // The bridge still serves the real client afterwards.
                Assert.Equal("stopped", (await session.StatusAsync(cancellation)).State);
            }
            servePid = session.ServeProcessId;
        }
        Assert.False(IsAlive(servePid), "serve survived the session");
        Assert.True(File.Exists(Path.Combine(work.Path, "serve.log")));
    }

    [Fact]
    public async Task DisposingTheSupervisorKillsGrandchildren()
    {
        var bundle = RequireBundle();
        var cancellation = TestContext.Current.CancellationToken;
        const string script = "import subprocess, sys, time\n"
            + "child = subprocess.Popen([sys.executable, '-I', '-S', '-c', 'import time; time.sleep(120)'])\n"
            + "print(child.pid, flush=True)\ntime.sleep(120)\n";
        var info = new ProcessStartInfo(bundle.Python);
        foreach (var argument in new[] { "-I", "-S", "-B", "-c", script })
        {
            info.ArgumentList.Add(argument);
        }
        var lines = new System.Collections.Concurrent.BlockingCollection<string>();
        int grandchild;
        using (var process = JobObjectLauncher.Start(info, lines.Add))
        {
            Assert.True(lines.TryTake(out var line, 30_000, cancellation), "child did not report its pid");
            grandchild = int.Parse(line!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(IsAlive(grandchild));
            if (OperatingSystem.IsWindows())
            {
                // python + its child; the hidden console's conhost.exe may be in the job too (seen on arm64).
                Assert.InRange(process.ActiveProcessCount ?? 0, 2, 3);
            }
        }
        await Task.Delay(500, cancellation);
        Assert.False(IsAlive(grandchild), "grandchild survived its supervisor");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
