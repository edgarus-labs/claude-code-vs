using ClaudeCode.Acp;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ClaudeCode.Acp.Tests;

/// <summary>
/// AcpProcessConnection.DisposeAsync used to call the bare Process.Kill(), which only terminates the
/// direct child - any grandchild the adapter process spawned (a common shape: a launcher script that
/// execs a real interpreter/runtime as a child of itself) was left running, orphaned, after disposal.
/// These tests spawn a real two-level process tree (cmd.exe /c ping, where cmd.exe is the direct
/// child and ping.exe is the grandchild it waits on) to prove the whole tree is terminated.
/// </summary>
public sealed class AcpProcessConnectionProcessTreeKillTests
{
    [Fact]
    public async Task DisposeAsync_ProcessSpawnedChildren_AreAlsoTerminated_NotJustTheDirectProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // cmd.exe /c spawns "ping" as its own child (a grandchild relative to this test process)
        // rather than exec-replacing itself, and blocks waiting on it - exactly the shape a naive
        // direct-process-only kill fails to clean up. Passed as separate argv tokens (not one
        // whitespace-containing string) so ProcessArgumentEscaping does not quote the whole command.
        var connection = new AcpProcessConnection("cmd.exe", new[] { "/c", "ping", "-n", "60", "127.0.0.1" });
        try
        {
            FieldInfo processField = typeof(AcpProcessConnection).GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var process = (Process)processField.GetValue(connection)!;

            int? grandchildPid = await PollForGrandchildPidAsync(process.Id, TimeSpan.FromSeconds(10));
            Assert.True(grandchildPid.HasValue, "expected the spawned cmd.exe to have started a ping.exe child within the timeout.");

            await connection.DisposeAsync();

            Assert.True(
                await WaitForProcessExitAsync(grandchildPid!.Value, TimeSpan.FromSeconds(10)),
                "the grandchild process spawned by the ACP adapter's own child must be terminated too, not left orphaned.");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_LauncherExitedBeforeDisposal_TerminatesItsSurvivingChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string childPidFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pid");
        int? childPid = null;
        var connection = new AcpProcessConnection("powershell.exe", new[]
        {
            "-NoProfile", "-NonInteractive", "-Command",
            "$child = Start-Process -FilePath \"$env:SystemRoot\\System32\\ping.exe\" -ArgumentList '-n 60 127.0.0.1' -WindowStyle Hidden -PassThru; " +
            "[IO.File]::WriteAllText('" + childPidFile.Replace("'", "''") + "', [string]$child.Id)",
        });
        try
        {
            var process = (Process)typeof(AcpProcessConnection)
                .GetField("_process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(connection)!;
            Assert.True(await WaitForProcessExitAsync(process.Id, TimeSpan.FromSeconds(10)),
                "The launcher must already have exited before connection disposal.");
            childPid = int.Parse(await File.ReadAllTextAsync(childPidFile), CultureInfo.InvariantCulture);
            using (var child = Process.GetProcessById(childPid.Value))
            {
                Assert.False(child.HasExited);
            }

            await connection.DisposeAsync(TimeSpan.FromMilliseconds(200));

            Assert.True(await WaitForProcessExitAsync(childPid.Value, TimeSpan.FromSeconds(10)),
                "A launcher exiting first must not orphan its still-running child.");
        }
        finally
        {
            await connection.DisposeAsync();
            if (childPid.HasValue)
            {
                try
                {
                    using var child = Process.GetProcessById(childPid.Value);
                    if (!child.HasExited)
                    {
                        child.Kill();
                    }
                }
                catch (ArgumentException)
                {
                    // A passing test has already terminated the child.
                }
            }

            File.Delete(childPidFile);
        }
    }

    [Fact]
    public async Task ContainedLaunch_PreservesWorkingDirectoryEnvironmentAndRedirectedStreams()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "acp launch " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        WindowsJobProcess? process = null;
        Exception? failure = null;
        try
        {
            const string script = "[Console]::Out.WriteLine([IO.Directory]::GetCurrentDirectory()); " +
                "[Console]::Out.WriteLine($env:ACP_LAUNCH_VALUE); " +
                "[Console]::Error.WriteLine([Console]::In.ReadLine()); Start-Sleep -Seconds 60";
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                WorkingDirectory = directory,
            };
            startInfo.Environment["ACP_LAUNCH_VALUE"] = "spaces & percent% equals=value";
            process = WindowsJobProcess.Start(startInfo);
            using var output = new StreamReader(process.StandardOutput);
            using var input = new StreamWriter(process.StandardInput) { AutoFlush = true };
            await input.WriteLineAsync("redirected input");

            Assert.Equal(directory, await output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("spaces & percent% equals=value", await output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("redirected input", await process.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            using (process)
            {
                if (process is not null)
                {
                    // Closing the job requests termination; wait for the cwd handle to close
                    // even when an assertion or redirected read failed.
                    process.Terminate();
                    await process.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }

            Directory.Delete(directory);
        }
        catch (Exception cleanupFailure) when (failure is not null)
        {
            throw new AggregateException("Contained launch and its cleanup both failed.", failure, cleanupFailure);
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static async Task<int?> PollForGrandchildPidAsync(int parentId, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            int? pid = QueryChildPid(parentId);
            if (pid is not null)
            {
                return pid;
            }

            await Task.Delay(100);
        }

        return null;
    }

    private static int? QueryChildPid(int parentId)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            $"(Get-CimInstance Win32_Process -Filter 'ParentProcessId={parentId}' | Where-Object Name -eq 'PING.EXE').ProcessId");

        using var proc = Process.Start(startInfo);
        if (proc is null)
        {
            return null;
        }

        string output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(5000);
        return int.TryParse(output, out int pid) ? pid : (int?)null;
    }

    private static async Task<bool> WaitForProcessExitAsync(int pid, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true; // no longer exists.
            }

            await Task.Delay(100);
        }

        return false;
    }
}
