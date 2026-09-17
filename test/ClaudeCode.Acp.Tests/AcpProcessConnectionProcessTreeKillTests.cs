using ClaudeCode.Acp;
using System;
using System.Diagnostics;
using System.Reflection;
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
