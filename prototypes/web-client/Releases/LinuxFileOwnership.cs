using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AetherSDR.Web.Releases;

internal readonly record struct LinuxFileOwnership(uint UserId, uint GroupId)
{
    private const int AtFileDescriptorCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x000007ff;

    [SupportedOSPlatform("linux")]
    internal static LinuxFileOwnership Read(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Unix ownership inspection requires Linux.");
        }
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException(
                "Unix ownership inspection requires one absolute path.");
        }

        int result = Statx(
            AtFileDescriptorCwd,
            Path.GetFullPath(path),
            AtSymlinkNoFollow,
            StatxBasicStats,
            out LinuxStatx state);
        if (result != 0)
        {
            throw new IOException(
                "Unix ownership could not be inspected.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return new LinuxFileOwnership(state.UserId, state.GroupId);
    }

    [SupportedOSPlatform("linux")]
    internal void ApplyAndVerify(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Unix ownership restoration requires Linux.");
        }
        string fullPath = Path.GetFullPath(path);
        if (Chown(fullPath, UserId, GroupId) != 0)
        {
            throw new IOException(
                "Original Unix ownership could not be restored.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        if (Read(fullPath) != this)
        {
            throw new IOException(
                "Restored Unix ownership did not match the authenticated backup metadata.");
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct LinuxStatx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        out LinuxStatx state);

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint owner, uint group);
}
