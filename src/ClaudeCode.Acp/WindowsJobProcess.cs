using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeCode.Acp;

// Owns the Windows process tree, not just the launcher. No adapter code executes before job
// assignment succeeds. Nested jobs preserve restrictions imposed by Visual Studio's own host;
// incompatible parent-job restrictions fail launch rather than allowing an uncontained fallback.
internal sealed class WindowsJobProcess : IDisposable
{
    private readonly SafeFileHandle _job;

    private WindowsJobProcess(Process process, SafeFileHandle job, Stream input, Stream output, StreamReader error)
    {
        Process = process;
        _job = job;
        StandardInput = input;
        StandardOutput = output;
        StandardError = error;
    }

    internal Process Process { get; }

    internal Stream StandardInput { get; }

    internal Stream StandardOutput { get; }

    internal StreamReader StandardError { get; }

    internal static WindowsJobProcess Start(ProcessStartInfo startInfo, Action? beforeProcessCreate = null)
    {
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        SafeFileHandle? childInput = null;
        SafeFileHandle? parentInput = null;
        SafeFileHandle? parentOutput = null;
        SafeFileHandle? childOutput = null;
        SafeFileHandle? parentError = null;
        SafeFileHandle? childError = null;
        Stream? input = null;
        Stream? output = null;
        StreamReader? error = null;
        Process? process = null;
        var nativeProcess = new ProcessInformation();
        var broker = new ProcessInformation();
        IntPtr attributeList = IntPtr.Zero;
        IntPtr handleList = IntPtr.Zero;
        bool attributesInitialized = false;
        bool started = false;
        try
        {
            if (job.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the ACP process job.");
            }

            var limits = new JobExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure ACP process containment.");
            }

            // Inheritable handles never exist in the VS host: an unrelated Process.Start there
            // could otherwise inherit our pipes despite the adapter's explicit handle list.
            string brokerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var brokerStartup = new StartupInformationEx
            {
                StartupInfo = new StartupInformation { Size = Marshal.SizeOf<StartupInformation>() },
            };
            if (!CreateProcessW(brokerPath, new char[1], IntPtr.Zero, IntPtr.Zero, false,
                0x08000004, null, null, ref brokerStartup, out broker)) // NO_WINDOW | SUSPENDED
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the suspended ACP launch broker.");
            }

            // Preserve any inherited host-job restrictions. Incompatible nesting fails closed
            // while the broker is suspended; neither it nor the adapter has executed any code.
            if (!AssignProcessToJobObject(job, broker.Process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to contain the ACP launch broker.");
            }

            CreateRedirectedPipe(out childInput, out parentInput);
            CreateRedirectedPipe(out parentOutput, out childOutput);
            CreateRedirectedPipe(out parentError, out childError);
            IntPtr inheritedInput = DuplicateIntoBroker(childInput, broker.Process);
            IntPtr inheritedOutput = DuplicateIntoBroker(childOutput, broker.Process);
            IntPtr inheritedError = DuplicateIntoBroker(childError, broker.Process);

            IntPtr attributeSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attributeSize);
            attributeList = Marshal.AllocHGlobal(attributeSize);
            if (!InitializeProcThreadAttributeList(attributeList, 2, 0, ref attributeSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            attributesInitialized = true;
            handleList = Marshal.AllocHGlobal(IntPtr.Size * 4);
            Marshal.WriteIntPtr(handleList, 0, inheritedInput);
            Marshal.WriteIntPtr(handleList, IntPtr.Size, inheritedOutput);
            Marshal.WriteIntPtr(handleList, IntPtr.Size * 2, inheritedError);
            IntPtr parentAttribute = IntPtr.Add(handleList, IntPtr.Size * 3);
            Marshal.WriteIntPtr(parentAttribute, broker.Process);
            if (!UpdateProcThreadAttribute(attributeList, 0, new IntPtr(0x20002), handleList,
                new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) // PROC_THREAD_ATTRIBUTE_HANDLE_LIST
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!UpdateProcThreadAttribute(attributeList, 0, new IntPtr(0x20000), parentAttribute,
                new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero)) // PROC_THREAD_ATTRIBUTE_PARENT_PROCESS
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startup = new StartupInformationEx
            {
                StartupInfo = new StartupInformation
                {
                    Size = Marshal.SizeOf<StartupInformationEx>(),
                    Flags = 0x100, // STARTF_USESTDHANDLES
                    StandardInput = inheritedInput,
                    StandardOutput = inheritedOutput,
                    StandardError = inheritedError,
                },
                AttributeList = attributeList,
            };
            string command = ProcessArgumentEscaping.ToArgumentsString(new[] { startInfo.FileName });
            if (!string.IsNullOrEmpty(startInfo.Arguments))
            {
                command += " " + startInfo.Arguments;
            }

            var environment = new StringBuilder();
            var variables = new SortedDictionary<string, string?>(startInfo.Environment, StringComparer.OrdinalIgnoreCase);
            foreach (var variable in variables)
            {
                environment.Append(variable.Key).Append('=').Append(variable.Value).Append('\0');
            }

            environment.Append('\0');
            var commandLine = new char[command.Length + 1];
            command.CopyTo(0, commandLine, 0, command.Length);
            beforeProcessCreate?.Invoke();
            // SUSPENDED | NO_WINDOW | UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT.
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                0x08080404, environment.ToString(), string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                ref startup, out nativeProcess))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to start the ACP adapter.");
            }

            if (!IsProcessInJob(nativeProcess.Process, job, out bool contained))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to verify the ACP adapter's process job.");
            }

            if (!contained)
            {
                throw new InvalidOperationException("The ACP adapter did not inherit its required process job.");
            }

            // The broker never runs. Its private pipe handles are no longer needed after the
            // adapter inherits them; terminate and drain it before allowing adapter execution.
            if (!TerminateProcess(broker.Process, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to terminate the suspended ACP launch broker.");
            }

            if (WaitForSingleObject(broker.Process, 2000) != 0)
            {
                throw new TimeoutException("The suspended ACP launch broker did not terminate.");
            }

            process = Process.GetProcessById(nativeProcess.ProcessId);
            process.EnableRaisingEvents = true;
            input = new FileStream(parentInput, FileAccess.Write, bufferSize: 1);
            output = new FileStream(parentOutput, FileAccess.Read);
            error = new StreamReader(new FileStream(parentError, FileAccess.Read), Console.OutputEncoding, detectEncodingFromByteOrderMarks: true);
            if (ResumeThread(nativeProcess.Thread) == uint.MaxValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resume the contained ACP adapter.");
            }

            var result = new WindowsJobProcess(process, job, input, output, error);
            started = true;
            return result;
        }
        finally
        {
            try
            {
                if (!started)
                {
                    // The original handle remains valid even if job assignment or Process setup fails.
                    if (nativeProcess.Process != IntPtr.Zero)
                    {
                        TerminateProcess(nativeProcess.Process, 1);
                    }

                    using var errorToDispose = error;
                    using var outputToDispose = output;
                    using var inputToDispose = input;
                    job.Dispose();
                }
            }
            finally
            {
                if (!started)
                {
                    parentInput?.Dispose();
                    parentOutput?.Dispose();
                    parentError?.Dispose();
                    process?.Dispose();
                }

                childInput?.Dispose();
                childOutput?.Dispose();
                childError?.Dispose();
                if (nativeProcess.Thread != IntPtr.Zero)
                {
                    CloseHandle(nativeProcess.Thread);
                }

                if (nativeProcess.Process != IntPtr.Zero)
                {
                    CloseHandle(nativeProcess.Process);
                }

                if (broker.Thread != IntPtr.Zero)
                {
                    CloseHandle(broker.Thread);
                }

                if (broker.Process != IntPtr.Zero)
                {
                    // Also covers failure before job assignment; no broker thread is ever resumed.
                    TerminateProcess(broker.Process, 1);
                    CloseHandle(broker.Process);
                }

                if (attributesInitialized)
                {
                    DeleteProcThreadAttributeList(attributeList);
                }

                Marshal.FreeHGlobal(attributeList);
                Marshal.FreeHGlobal(handleList);
            }
        }
    }

    internal void Terminate() => _job.Dispose();

    public void Dispose()
    {
        using var process = Process;
        using var error = StandardError;
        using var output = StandardOutput;
        using var input = StandardInput;
        Terminate();
    }

    private static void CreateRedirectedPipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
        if (!CreatePipe(out read, out write, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static IntPtr DuplicateIntoBroker(SafeFileHandle handle, IntPtr broker)
    {
        if (!DuplicateHandle(GetCurrentProcess(), handle, broker, out IntPtr inherited, 0, true, 2)) // DUPLICATE_SAME_ACCESS
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to transfer ACP pipe ownership to its launch broker.");
        }

        return inherited;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        internal int Size;
        internal IntPtr Reserved;
        internal IntPtr Desktop;
        internal IntPtr Title;
        internal int X;
        internal int Y;
        internal int XSize;
        internal int YSize;
        internal int XCountChars;
        internal int YCountChars;
        internal int FillAttribute;
        internal int Flags;
        internal short ShowWindow;
        internal short ReservedSize;
        internal IntPtr ReservedBytes;
        internal IntPtr StandardInput;
        internal IntPtr StandardOutput;
        internal IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformationEx
    {
        internal StartupInformation StartupInfo;
        internal IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal IntPtr Process;
        internal IntPtr Thread;
        internal int ProcessId;
        internal int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        internal JobBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref JobExtendedLimitInformation information, int length);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, int size);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess,
        out IntPtr target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, [MarshalAs(UnmanagedType.Bool)] out bool contained);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, int flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, [In, Out] char[] commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, int creationFlags,
        string? environment, string? currentDirectory, ref StartupInformationEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
