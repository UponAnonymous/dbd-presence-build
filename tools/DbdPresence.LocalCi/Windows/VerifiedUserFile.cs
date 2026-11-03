using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DbdPresence.LocalCi.Windows;

/// <summary>
/// Opens a file the owner controls (the owner's <c>.wslconfig</c>, the program file set-up was started from) for reading from the elevated program without following a link. Both live where the
/// owner's own programs can write, so a program running as the owner could put a symbolic link there that points at a file only Administrators can read (the saved token); read by path, the elevated
/// program would copy or show that file. The file is opened with <c>FILE_FLAG_OPEN_REPARSE_POINT</c> ("Normal reparse point processing will not occur; CreateFile will attempt to open the reparse point",
/// CreateFileW documentation) and the handle says what it is: a link or a folder is refused. It is shared for reading only, so nobody can write to it, rename it or delete it while it is held; what
/// is read is the file that was checked. PENDING-ENV: not run on a Windows PC yet.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class VerifiedUserFile
{
    /// <summary>The ordinary file, opened for reading; null when it is not there. Throws <see cref="IOException"/> for a link or a folder.</summary>
    public static FileStream? OpenRead(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string extended;
        try
        {
            extended = ExtendedPath.For(Path.GetFullPath(path));
        }
        catch (ArgumentException ex)
        {
            throw new IOException(ex.Message, ex);
        }

        var handle = NativeMethods.CreateFile(extended, NativeMethods.GenericRead, NativeMethods.ShareRead, nint.Zero, NativeMethods.OpenExisting, NativeMethods.FlagOpenReparsePoint, nint.Zero);
        if (handle == NativeMethods.InvalidHandleValue)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is NativeMethods.ErrorFileNotFound or NativeMethods.ErrorPathNotFound)
            {
                return null;
            }

            // A folder cannot be opened like a file (no backup semantics are asked for): Windows answers "access denied", which would hide what it is.
            if (error == NativeMethods.ErrorAccessDenied && Directory.Exists(path))
            {
                throw new IOException($"{path} is a folder, not a file.");
            }

            throw new Win32Exception(error, $"Windows would not open {path}");
        }

        var owned = new SafeFileHandle(handle, ownsHandle: true);
        try
        {
            if (!NativeMethods.GetFileInformationByHandle(handle, out var information))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Windows would not say what {path} is");
            }

            if ((information.FileAttributes & NativeMethods.AttributeReparsePoint) != 0)
            {
                throw new IOException($"{path} is a link (a symbolic link or a junction), not an ordinary file. This program does not read through links; replace it with a normal file.");
            }

            if ((information.FileAttributes & NativeMethods.AttributeDirectory) != 0)
            {
                throw new IOException($"{path} is a folder, not a file.");
            }

            return new FileStream(owned, FileAccess.Read, bufferSize: 4096, isAsync: false);
        }
        catch
        {
            owned.Dispose();
            throw;
        }
    }
}
