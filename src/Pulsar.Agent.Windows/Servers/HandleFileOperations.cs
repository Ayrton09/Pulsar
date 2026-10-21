using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Pulsar.Agent.Windows.Servers;

/// <summary>
/// Deletes and renames done on an opened handle, never on a path looked up again: the file manager's (as the server's
/// account) and the agent's own access to server directories (<see cref="WindowsServerFiles"/>) share them.
/// </summary>
internal static class HandleFileOperations
{
    /// <summary>
    /// Deletes what the handle is (a link, never its target) as soon as possible, read-only or not: the error, or
    /// <see cref="WIN32_ERROR.ERROR_SUCCESS"/>.
    /// </summary>
    public static unsafe WIN32_ERROR MarkDeleted(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var flags = FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
            | FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
        var extended = new FILE_DISPOSITION_INFO_EX { Flags = flags };
        if (PInvoke.SetFileInformationByHandle((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfoEx, &extended,
            (uint)sizeof(FILE_DISPOSITION_INFO_EX)))
        {
            return WIN32_ERROR.ERROR_SUCCESS;
        }

        // File systems without POSIX deletes (FAT, older ReFS): the classic delete, when the last handle closes.
        var error = (WIN32_ERROR)Marshal.GetLastPInvokeError();
        if (error is not (WIN32_ERROR.ERROR_INVALID_PARAMETER or WIN32_ERROR.ERROR_NOT_SUPPORTED or WIN32_ERROR.ERROR_INVALID_FUNCTION))
        {
            return error;
        }

        var classic = new FILE_DISPOSITION_INFO { DeleteFile = true };
        return PInvoke.SetFileInformationByHandle((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfo, &classic,
            (uint)sizeof(FILE_DISPOSITION_INFO))
            ? WIN32_ERROR.ERROR_SUCCESS
            : (WIN32_ERROR)Marshal.GetLastPInvokeError();
    }

    /// <summary>
    /// Renames what the handle is to <paramref name="name"/> inside the directory <paramref name="directory"/> is: the
    /// target folder is the one already checked, not a path looked up again. The Win32 call refuses a root directory,
    /// so this goes to the native one (FILE_RENAME_INFORMATION, the same structure). The NTSTATUS.
    /// </summary>
    /// <remarks>
    /// POSIX semantics, as deletes: a file replaced while another program reads it (a virus scanner, the search
    /// indexer, a game reading its configuration) is replaced anyway, where a classic rename refuses with access
    /// denied; that program keeps reading the old content. A read-only file is replaced as it would be deleted.
    /// </remarks>
    public static int Rename(SafeFileHandle handle, SafeFileHandle directory, string name, bool replace)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(name);
        var flags = NtFile.FileRenamePosixSemantics | NtFile.FileRenameIgnoreReadOnlyAttribute | (replace ? NtFile.FileRenameReplaceIfExists : 0u);
        var status = SetRename(handle, directory, name, flags, NtFile.FileRenameInformationEx);

        // File systems without POSIX renames (FAT, older ReFS): the classic rename.
        return status is NtFile.StatusInvalidParameter or NtFile.StatusNotSupported or NtFile.StatusInvalidInfoClass
            ? SetRename(handle, directory, name, replace ? 1u : 0u, NtFile.FileRenameInformation)
            : status;
    }

    private static unsafe int SetRename(SafeFileHandle handle, SafeFileHandle directory, string name, uint flags, int informationClass)
    {
        // FILE_RENAME_INFORMATION: ReplaceIfExists (a BOOLEAN) or, for the extended class, Flags (a ULONG) in the same
        // place; the root directory handle; the name's length in bytes; the name.
        var rootOffset = IntPtr.Size;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var buffer = new byte[nameOffset + ((name.Length + 1) * sizeof(char))];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, flags);
        var added = false;
        directory.DangerousAddRef(ref added);
        try
        {
            if (IntPtr.Size == 8)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(rootOffset), directory.DangerousGetHandle());
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(rootOffset), (int)directory.DangerousGetHandle());
            }

            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lengthOffset), (uint)(name.Length * sizeof(char)));
            MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(buffer.AsSpan(nameOffset));
            fixed (byte* info = buffer)
            {
                return NtFile.NtSetInformationFile(handle, out _, info, (uint)buffer.Length, informationClass);
            }
        }
        finally
        {
            if (added)
            {
                directory.DangerousRelease();
            }
        }
    }
}
