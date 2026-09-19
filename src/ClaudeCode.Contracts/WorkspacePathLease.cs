using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClaudeCode.Contracts;

/// <summary>
/// Retains the filesystem objects used to authorize one operation. File I/O uses handles, not
/// FullPath. Only a document lease makes FullPath safe for Windows host APIs while it is alive.
/// Links are rejected during acquisition, including links whose current targets are in the workspace.
/// </summary>
public sealed class WorkspacePathLease : IDisposable
{
    private readonly List<SafeFileHandle> _directories = new List<SafeFileHandle>();
    private readonly bool _windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private readonly string _leaf;
    private readonly bool _document;
    private SafeFileHandle? _file;
    private bool _disposed;

    private WorkspacePathLease(string fullPath, bool document)
    {
        FullPath = fullPath;
        _leaf = Path.GetFileName(fullPath);
        _document = document;
    }

    public string FullPath { get; }

    internal static WorkspacePathLease Acquire(string? root, string? path, bool document)
    {
        if (!WorkspacePathGuard.TryResolveWithinWorkspace(root, path, out string fullPath))
            throw new UnauthorizedAccessException("The path is outside the workspace or cannot be resolved safely.");
        var lease = new WorkspacePathLease(fullPath, document);
        try
        {
            if (!lease._windows && (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || document))
                throw new PlatformNotSupportedException("Confined file operations support Windows and Linux; document paths require Windows.");
            string parent = Path.GetDirectoryName(fullPath) ?? throw new UnauthorizedAccessException("A workspace file path is required.");
            if (lease._leaf.Length == 0 || (lease._windows && lease._leaf.IndexOf(':') >= 0))
                throw new UnauthorizedAccessException("A regular file path is required.");
            lease.OpenDirectories(parent);
            lease._file = lease.OpenLeaf(document);
            if (document && lease._file is null)
                throw new UnauthorizedAccessException("A document lease requires an existing file.");
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Pins an existing Windows editor path for a host callback. Missing files have no live
    /// disk-backed editor document. Linux has no VS host and must use the handle-based I/O methods.
    /// </summary>
    public IDisposable? ProtectDocument()
    {
        ThrowIfDisposed();
        return _windows ? OpenLeaf(document: true) : null;
    }

    public string ReadAllText()
    {
        ThrowIfDisposed();
        if (_file is null) throw new FileNotFoundException("The workspace file does not exist.", FullPath);
        using var retained = new HandleReference(_file);
        using var stream = OpenRetainedRead();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Reads through the retained handle without surrendering it: a <see cref="FileStream"/> built
    /// directly over <c>_file</c> closes that handle on dispose, which both breaks every later
    /// operation on the lease and drops the sharing pin the lease exists to hold. Callers must
    /// hold a <see cref="HandleReference"/> for the returned stream's lifetime, because the raw
    /// handle value borrowed here outlives <see cref="SafeHandle"/>'s own reference counting.
    /// </summary>
    private FileStream OpenRetainedRead()
    {
        var borrowed = new SafeFileHandle(_file!.DangerousGetHandle(), ownsHandle: false);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(borrowed, FileAccess.Read);
            // The handle's file position is shared with every earlier read, so rewind explicitly.
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }
        catch
        {
            stream?.Dispose();
            borrowed.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Keeps the retained handle's reference count raised for the duration of a read, so a
    /// concurrent <see cref="Dispose"/> cannot close the kernel handle — and let the OS recycle
    /// its value under a different object — while a borrowed wrapper is still reading through it.
    /// </summary>
    private readonly struct HandleReference : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly bool _added;

        public HandleReference(SafeFileHandle handle)
        {
            bool added = false;
            handle.DangerousAddRef(ref added);
            _handle = handle;
            _added = added;
        }

        public void Dispose()
        {
            if (_added) _handle.DangerousRelease();
        }
    }

    /// <summary>Preserves a supported BOM and atomically replaces the entry in the pinned parent directory.</summary>
    public void WriteAllText(string content)
    {
        ThrowIfDisposed();
        byte[]? securityDescriptor = _windows && _file is not null ? ReadWindowsDacl(_file) : null;
        Encoding encoding = DetectEncoding();
        // The leaf is never retained without delete sharing during an atomic replacement. The
        // destination rename replaces a directory entry; it does not follow a newly inserted link.
        _file?.Dispose();
        _file = null;
        string temporary = ".claude-" + Guid.NewGuid().ToString("N") + ".tmp";
        string temporaryPath = Path.Combine(Path.GetDirectoryName(FullPath)!, temporary);
        bool created = false;
        try
        {
            using (var handle = CreateTemporary(temporary, temporaryPath, securityDescriptor))
            {
                created = true;
                using var stream = new FileStream(handle, FileAccess.Write);
                using var writer = new StreamWriter(stream, encoding, 1024, leaveOpen: true);
                writer.Write(content);
                writer.Flush();
                stream.Flush();
            }

            if (_windows)
            {
                if (!MoveFileExW(temporaryPath, FullPath, 1)) throw NativeIOException("Could not replace the workspace file.");
            }
            else if (RenameAt(DirectoryHandle, WorkspacePathGuard.GetUnixPathBytes(temporary), DirectoryHandle, WorkspacePathGuard.GetUnixPathBytes(_leaf)) != 0)
            {
                throw NativeIOException("Could not replace the workspace file.");
            }
        }
        catch (Exception operationError)
        {
            if (created)
            {
                try
                {
                    if (_windows) File.Delete(temporaryPath);
                    else if (UnlinkAt(DirectoryHandle, WorkspacePathGuard.GetUnixPathBytes(temporary), 0) != 0)
                        throw NativeIOException("Could not remove the temporary workspace file.");
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("The workspace write and temporary-file cleanup both failed.", operationError, cleanupError);
                }
            }
            throw;
        }

        // The lease outlives the replacement: reopen the new leaf through the still-pinned parent
        // so later reads, DACL capture and BOM detection keep working against the current entry,
        // and a document lease regains the no-delete pin its contract promises.
        try
        {
            _file = OpenLeaf(_document);
        }
        // The replacement already committed, so a transient failure to reopen the new entry (an
        // indexer or scanner holding it, a tightened DACL) must not be reported to the caller as a
        // failed write. A null leaf handle is a supported state for a file lease; a document lease
        // whose whole contract is the pin still fails loudly rather than living on unpinned.
        catch (IOException) when (!_document) { _file = null; }
        catch (UnauthorizedAccessException) when (!_document) { _file = null; }
    }

    private Encoding DetectEncoding()
    {
        if (_file is null) return new UTF8Encoding(false);
        var buffer = new byte[4];
        using var retained = new HandleReference(_file);
        using var stream = OpenRetainedRead();
        int count = stream.Read(buffer, 0, buffer.Length);
        if (count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) return new UTF8Encoding(true);
        if (count >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) return Encoding.Unicode;
        if (count >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF) return Encoding.BigEndianUnicode;
        return new UTF8Encoding(false);
    }

    private SafeFileHandle CreateTemporary(string name, string fullPath, byte[]? securityDescriptor)
    {
        if (_windows)
        {
            SafeFileHandle handle;
            if (securityDescriptor is null)
            {
                handle = CreateFileW(fullPath, 0x40000000, 0, IntPtr.Zero, 1, OpenReparsePoint, IntPtr.Zero);
            }
            else
            {
                // Supply the original DACL at creation: applying it later leaves a window in which
                // a broader inherited ACL can grant another process a read handle to private content.
                var pinned = GCHandle.Alloc(securityDescriptor, GCHandleType.Pinned);
                try
                {
                    var attributes = new SecurityAttributes
                    {
                        Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                        Descriptor = pinned.AddrOfPinnedObject(),
                        InheritHandle = false,
                    };
                    handle = CreateFileWithSecurityW(fullPath, 0x40000000, 0, ref attributes, 1, OpenReparsePoint, IntPtr.Zero);
                }
                finally
                {
                    pinned.Free();
                }
            }
            if (!handle.IsInvalid) return handle;
            var error = NativeIOException("Could not create a temporary workspace file.");
            handle.Dispose();
            throw error;
        }
        int fd = OpenAt(DirectoryHandle, WorkspacePathGuard.GetUnixPathBytes(name), OpenWriteOnly | OpenCreate | OpenExclusive | OpenNoFollow | OpenCloseOnExec, 384);
        if (fd < 0) throw NativeIOException("Could not create a temporary workspace file.");
        return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
    }

    private static byte[] ReadWindowsDacl(SafeFileHandle file)
    {
        if (!GetKernelObjectSecurity(file, 4, null, 0, out uint length) && Marshal.GetLastWin32Error() != 122)
            throw NativeIOException("Could not read the workspace file permissions.");
        var descriptor = new byte[checked((int)length)];
        if (!GetKernelObjectSecurity(file, 4, descriptor, length, out _))
            throw NativeIOException("Could not read the workspace file permissions.");
        return descriptor;
    }

    private int DirectoryHandle => _directories[_directories.Count - 1].DangerousGetHandle().ToInt32();

    private void OpenDirectories(string parent)
    {
        string root = Path.GetPathRoot(parent)!;
        if (_windows)
        {
            string current = root;
            _directories.Add(OpenWindowsDirectory(current));
            foreach (string part in parent.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                _directories.Add(OpenWindowsDirectory(current));
            }
            return;
        }

        int fd = OpenAt(-100, WorkspacePathGuard.GetUnixPathBytes(root), OpenDirectory | OpenNoFollow | OpenCloseOnExec, 0);
        if (fd < 0) throw NativeIOException("Could not pin the filesystem root.");
        _directories.Add(new SafeFileHandle(new IntPtr(fd), ownsHandle: true));
        foreach (string part in parent.Substring(root.Length).Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            fd = OpenAt(DirectoryHandle, WorkspacePathGuard.GetUnixPathBytes(part), OpenDirectory | OpenNoFollow | OpenCloseOnExec, 0);
            if (fd < 0) throw new UnauthorizedAccessException("The workspace path contains an inaccessible directory or symbolic link.", NativeIOException("Directory acquisition failed."));
            _directories.Add(new SafeFileHandle(new IntPtr(fd), ownsHandle: true));
        }
    }

    private static SafeFileHandle OpenWindowsDirectory(string path)
    {
        var handle = CreateFileW(path, 0, 1, IntPtr.Zero, 3, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        return VerifyWindowsHandle(handle, directory: true);
    }

    private SafeFileHandle? OpenLeaf(bool document)
    {
        if (_windows)
        {
            // A zero-access metadata handle cannot enforce the document's no-delete sharing.
            // A document lease shares read and write so the host editor can still open and save
            // the file, and withholds only FILE_SHARE_DELETE so the path cannot be replaced.
            var handle = CreateFileW(FullPath, GenericRead, document ? FileShareReadWrite : FileShareReadWriteDelete, IntPtr.Zero, 3, OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid && Marshal.GetLastWin32Error() == 2)
            {
                handle.Dispose();
                return null;
            }
            return VerifyWindowsHandle(handle, directory: false);
        }

        int fd = OpenAt(DirectoryHandle, WorkspacePathGuard.GetUnixPathBytes(_leaf), OpenNoFollow | OpenCloseOnExec | OpenNonBlock, 0);
        if (fd < 0)
        {
            if (Marshal.GetLastWin32Error() == 2) return null;
            throw new UnauthorizedAccessException("The workspace file cannot be opened without following a symbolic link.", NativeIOException("File acquisition failed."));
        }
        return new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
    }

    private static SafeFileHandle VerifyWindowsHandle(SafeFileHandle handle, bool directory)
    {
        if (handle.IsInvalid)
        {
            var error = NativeIOException("Could not pin the workspace path.");
            handle.Dispose();
            throw error;
        }
        if (!GetFileInformationByHandle(handle, out var information)
            || (information.Attributes & 0x400) != 0
            || ((information.Attributes & 0x10) != 0) != directory)
        {
            handle.Dispose();
            throw new UnauthorizedAccessException("Workspace operations require ordinary files and directories, not reparse points.");
        }
        return handle;
    }

    private static IOException NativeIOException(string message) => new IOException(message, new Win32Exception(Marshal.GetLastWin32Error()));

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WorkspacePathLease));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file?.Dispose();
        for (int i = _directories.Count - 1; i >= 0; i--) _directories[i].Dispose();
    }

    private const uint GenericRead = 0x80000000;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
    private const uint FileShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
    private const int OpenWriteOnly = 1;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenNonBlock = 0x800;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileWithSecurityW(string path, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint information, [Out] byte[]? descriptor, uint length, out uint requiredLength);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string source, string destination, uint flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, byte[] path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int RenameAt(int oldDirectory, byte[] oldName, int newDirectory, byte[] newName);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(int directory, byte[] path, int flags);
}
