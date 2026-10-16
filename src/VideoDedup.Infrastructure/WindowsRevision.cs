using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

public sealed class WorkFileAccess : IWorkFileAccess
{
    public MediaWorkItem Capture(Guid fileId, string path) => new(fileId, path, MediaFileAccess.Revision(path));
}

internal static class WindowsRevision
{
    public static string Capture(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info) || !GetFileInformationByHandleEx(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>()))
            throw new MediaProcessingException(MediaFailure.AccessDenied);
        return $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}:{basic.ChangeTime:X16}";
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out BasicInfo info, uint size);
}
