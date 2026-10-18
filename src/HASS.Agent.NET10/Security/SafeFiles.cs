using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HASS.Agent.Companion.Security;

/// <summary>
/// Writes into the shared settings folder (ProgramData), where every user of the PC may
/// create files, by the tray app and by the service (SYSTEM). The name is opened as it is: a
/// link is never followed, and a file that has another name elsewhere (a hard link) is not
/// written either, since the write would land in that other file. Both are removed, the name
/// only, and the file is made anew. The check and the write go through one handle, so
/// nothing can be swapped in between.
/// </summary>
internal static class SafeFiles
{
    public static void AppendAllText(string path, string text) => Write(path, text, append: true);

    /// <summary>The whole file, UTF-8 without a byte order mark, as File.WriteAllText writes it.</summary>
    public static void WriteAllText(string path, string text) => Write(path, text, append: false);

    private static void Write(string path, string text, bool append)
    {
        using var handle = OpenOwnFile(path, append);
        if (!append && (!SetFilePointerEx(handle, 0, IntPtr.Zero, FileBegin) || !SetEndOfFile(handle)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{path} could not be truncated");
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (!WriteFile(handle, ref bytes[offset], (uint)(bytes.Length - offset), out var written, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"{path} could not be written");
            }

            offset += (int)written;
        }
    }

    /// <summary>
    /// The file under the name, made when there is none, and only when it is a plain file
    /// of its own: a link, a hard link or a folder link (a junction) found there is removed
    /// and the name is tried again; a real folder under the name is refused. Appending opens
    /// with append access alone, so two processes appending (the tray app and the service
    /// share the log) never write over each other.
    /// </summary>
    private static SafeFileHandle OpenOwnFile(string path, bool append)
    {
        var access = (append ? FileAppendData : GenericWrite) | FileReadAttributes | Synchronize;
        const uint share = FileShareRead | FileShareWrite | FileShareDelete;
        for (var attempt = 0; ; attempt++)
        {
            // What is there, as it is: backup semantics so that a folder under the name opens
            // too, to be looked at (OPEN_EXISTING, as CreateFile wants for a folder).
            var handle = CreateFile(path, access, share, IntPtr.Zero,
                OpenExisting, FileAttributeNormal | FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error != ErrorFileNotFound)
                {
                    throw new Win32Exception(error, $"{path} could not be opened");
                }

                // Nothing under the name: a file made by this very call, so there is nothing to
                // check on it. One that appeared in between is looked at on the next round.
                handle = CreateFile(path, access, share, IntPtr.Zero, CreateNew, FileAttributeNormal, IntPtr.Zero);
                if (!handle.IsInvalid)
                {
                    return handle;
                }

                error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == ErrorFileExists && attempt < 2)
                {
                    continue;
                }

                throw new Win32Exception(error, $"{path} could not be made");
            }

            if (!GetFileInformationByHandle(handle, out var information))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }

            var isLink = (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0;
            var isFolder = (information.FileAttributes & (uint)FileAttributes.Directory) != 0;
            if (!isLink && !isFolder && information.NumberOfLinks <= 1)
            {
                return handle;
            }

            handle.Dispose();
            if (isFolder && !isLink)
            {
                throw new IOException($"{path} is a folder; not written.");
            }

            if (attempt == 2)
            {
                throw new IOException($"{path} keeps being a link; not written.");
            }

            // The name only: what it pointed to stays as it is.
            if (isFolder)
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    private const uint FileAppendData = 0x00000004;
    private const uint FileReadAttributes = 0x00000080;
    private const uint Synchronize = 0x00100000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const int ErrorFileNotFound = 2;
    private const int ErrorFileExists = 80;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileBegin = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle file, ref byte buffer, uint count, out uint written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, IntPtr newPosition, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEndOfFile(SafeFileHandle file);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
