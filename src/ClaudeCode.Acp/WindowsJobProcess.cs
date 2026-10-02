using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeCode.Acp;

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

    /// <summary>
    /// Gets the process.
    /// </summary>
    internal Process Process { get; }

    /// <summary>
    /// Gets the standard input.
    /// </summary>
    internal Stream StandardInput { get; }

    /// <summary>
    /// Gets the standard output.
    /// </summary>
    internal Stream StandardOutput { get; }

    /// <summary>
    /// Gets the standard error.
    /// </summary>
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
            limits._basicLimitInformation._limitFlags = 0x2000;
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobExtendedLimitInformation>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure ACP process containment.");
            }

            string brokerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var brokerStartup = new StartupInformationEx
            {
                _startupInfo = new StartupInformation { _size = Marshal.SizeOf<StartupInformation>() },
            };
            if (!CreateProcessW(brokerPath, new char[1], IntPtr.Zero, IntPtr.Zero, false,
                0x08000004, null, null, ref brokerStartup, out broker))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the suspended ACP launch broker.");
            }

            if (!AssignProcessToJobObject(job, broker._process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to contain the ACP launch broker.");
            }

            CreateRedirectedPipe(out childInput, out parentInput);
            CreateRedirectedPipe(out parentOutput, out childOutput);
            CreateRedirectedPipe(out parentError, out childError);
            IntPtr inheritedInput = DuplicateIntoBroker(childInput, broker._process);
            IntPtr inheritedOutput = DuplicateIntoBroker(childOutput, broker._process);
            IntPtr inheritedError = DuplicateIntoBroker(childError, broker._process);

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
            Marshal.WriteIntPtr(parentAttribute, broker._process);
            if (!UpdateProcThreadAttribute(attributeList, 0, new IntPtr(0x20002), handleList,
                new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!UpdateProcThreadAttribute(attributeList, 0, new IntPtr(0x20000), parentAttribute,
                new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startup = new StartupInformationEx
            {
                _startupInfo = new StartupInformation
                {
                    _size = Marshal.SizeOf<StartupInformationEx>(),
                    _flags = 0x100,
                    _standardInput = inheritedInput,
                    _standardOutput = inheritedOutput,
                    _standardError = inheritedError,
                },
                _attributeList = attributeList,
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
            if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                0x08080404, environment.ToString(), string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                ref startup, out nativeProcess))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to start the ACP adapter.");
            }

            if (!IsProcessInJob(nativeProcess._process, job, out bool contained))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to verify the ACP adapter's process job.");
            }

            if (!contained)
            {
                throw new InvalidOperationException("The ACP adapter did not inherit its required process job.");
            }

            if (!TerminateProcess(broker._process, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to terminate the suspended ACP launch broker.");
            }

            if (WaitForSingleObject(broker._process, 2000) != 0)
            {
                throw new TimeoutException("The suspended ACP launch broker did not terminate.");
            }

            process = Process.GetProcessById(nativeProcess._processId);
            process.EnableRaisingEvents = true;
            input = new FileStream(parentInput, FileAccess.Write, bufferSize: 1);
            output = new FileStream(parentOutput, FileAccess.Read);
            error = new StreamReader(new FileStream(parentError, FileAccess.Read), Console.OutputEncoding, detectEncodingFromByteOrderMarks: true);
            if (ResumeThread(nativeProcess._thread) == uint.MaxValue)
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
                    if (nativeProcess._process != IntPtr.Zero)
                    {
                        TerminateProcess(nativeProcess._process, 1);
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
                if (nativeProcess._thread != IntPtr.Zero)
                {
                    CloseHandle(nativeProcess._thread);
                }

                if (nativeProcess._process != IntPtr.Zero)
                {
                    CloseHandle(nativeProcess._process);
                }

                if (broker._thread != IntPtr.Zero)
                {
                    CloseHandle(broker._thread);
                }

                if (broker._process != IntPtr.Zero)
                {
                    TerminateProcess(broker._process, 1);
                    CloseHandle(broker._process);
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
        var security = new SecurityAttributes { _length = Marshal.SizeOf<SecurityAttributes>() };
        if (!CreatePipe(out read, out write, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static IntPtr DuplicateIntoBroker(SafeFileHandle handle, IntPtr broker)
    {
        if (!DuplicateHandle(GetCurrentProcess(), handle, broker, out IntPtr inherited, 0, true, 2))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to transfer ACP pipe ownership to its launch broker.");
        }

        return inherited;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        /// <summary>
        /// The length.
        /// </summary>
        internal int _length;
        /// <summary>
        /// The security descriptor.
        /// </summary>
        internal IntPtr _securityDescriptor;
        /// <summary>
        /// The inherit handle.
        /// </summary>
        [MarshalAs(UnmanagedType.Bool)] internal bool _inheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInformation
    {
        /// <summary>
        /// The size.
        /// </summary>
        internal int _size;
        /// <summary>
        /// The reserved.
        /// </summary>
        internal IntPtr _reserved;
        /// <summary>
        /// The desktop.
        /// </summary>
        internal IntPtr _desktop;
        /// <summary>
        /// The title.
        /// </summary>
        internal IntPtr _title;
        /// <summary>
        /// The x.
        /// </summary>
        internal int _x;
        /// <summary>
        /// The y.
        /// </summary>
        internal int _y;
        /// <summary>
        /// The xsize.
        /// </summary>
        internal int _xSize;
        /// <summary>
        /// The ysize.
        /// </summary>
        internal int _ySize;
        /// <summary>
        /// The xcount chars.
        /// </summary>
        internal int _xCountChars;
        /// <summary>
        /// The ycount chars.
        /// </summary>
        internal int _yCountChars;
        /// <summary>
        /// The fill attribute.
        /// </summary>
        internal int _fillAttribute;
        /// <summary>
        /// The flags.
        /// </summary>
        internal int _flags;
        /// <summary>
        /// The show window.
        /// </summary>
        internal short _showWindow;
        /// <summary>
        /// The reserved size.
        /// </summary>
        internal short _reservedSize;
        /// <summary>
        /// The reserved bytes.
        /// </summary>
        internal IntPtr _reservedBytes;
        /// <summary>
        /// The standard input.
        /// </summary>
        internal IntPtr _standardInput;
        /// <summary>
        /// The standard output.
        /// </summary>
        internal IntPtr _standardOutput;
        /// <summary>
        /// The standard error.
        /// </summary>
        internal IntPtr _standardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformationEx
    {
        /// <summary>
        /// The startup info.
        /// </summary>
        internal StartupInformation _startupInfo;
        /// <summary>
        /// The attribute list.
        /// </summary>
        internal IntPtr _attributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        /// <summary>
        /// The process.
        /// </summary>
        internal IntPtr _process;
        /// <summary>
        /// The thread.
        /// </summary>
        internal IntPtr _thread;
        /// <summary>
        /// The process id.
        /// </summary>
        internal int _processId;
        /// <summary>
        /// The thread id.
        /// </summary>
        internal int _threadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        /// <summary>
        /// The per process user time limit.
        /// </summary>
        internal long _perProcessUserTimeLimit;
        /// <summary>
        /// The per job user time limit.
        /// </summary>
        internal long _perJobUserTimeLimit;
        /// <summary>
        /// The limit flags.
        /// </summary>
        internal uint _limitFlags;
        /// <summary>
        /// The minimum working set size.
        /// </summary>
        internal UIntPtr _minimumWorkingSetSize;
        /// <summary>
        /// The maximum working set size.
        /// </summary>
        internal UIntPtr _maximumWorkingSetSize;
        /// <summary>
        /// The active process limit.
        /// </summary>
        internal uint _activeProcessLimit;
        /// <summary>
        /// The affinity.
        /// </summary>
        internal UIntPtr _affinity;
        /// <summary>
        /// The priority class.
        /// </summary>
        internal uint _priorityClass;
        /// <summary>
        /// The scheduling class.
        /// </summary>
        internal uint _schedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        /// <summary>
        /// The read operation count.
        /// </summary>
        internal ulong _readOperationCount;
        /// <summary>
        /// The write operation count.
        /// </summary>
        internal ulong _writeOperationCount;
        /// <summary>
        /// The other operation count.
        /// </summary>
        internal ulong _otherOperationCount;
        /// <summary>
        /// The read transfer count.
        /// </summary>
        internal ulong _readTransferCount;
        /// <summary>
        /// The write transfer count.
        /// </summary>
        internal ulong _writeTransferCount;
        /// <summary>
        /// The other transfer count.
        /// </summary>
        internal ulong _otherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        /// <summary>
        /// The basic limit information.
        /// </summary>
        internal JobBasicLimitInformation _basicLimitInformation;
        /// <summary>
        /// The io info.
        /// </summary>
        internal IoCounters _ioInfo;
        /// <summary>
        /// The process memory limit.
        /// </summary>
        internal UIntPtr _processMemoryLimit;
        /// <summary>
        /// The job memory limit.
        /// </summary>
        internal UIntPtr _jobMemoryLimit;
        /// <summary>
        /// The peak process memory used.
        /// </summary>
        internal UIntPtr _peakProcessMemoryUsed;
        /// <summary>
        /// The peak job memory used.
        /// </summary>
        internal UIntPtr _peakJobMemoryUsed;
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
