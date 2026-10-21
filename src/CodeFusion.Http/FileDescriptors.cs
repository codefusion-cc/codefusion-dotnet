using System;
using System.Runtime.InteropServices;

namespace CodeFusion.Http;

/// <summary>
/// The process's descriptor limit, which sets the default <see cref="HttpServerOptions.MaxConnections"/>. Past it
/// accept fails, and so does everything else that opens a descriptor: the database, a log file, the runtime itself,
/// which then ends the process when a managed exception has to cross native frames ($12 entry at 65,535).
/// </summary>
internal static unsafe partial class FileDescriptors
{
    /// <summary>Descriptors kept for the rest of the process: 1024, or half of a limit under 2048 (the runtime alone
    /// holds about 50 on macOS).</summary>
    public const int Reserve = 1024;

    /// <summary>The soft RLIMIT_NOFILE (the runtime raises it to the hard one as it starts); -1 where there is none
    /// or it can't be read.</summary>
    public static long SoftLimit()
    {
        // struct rlimit is two rlim_t, 8 bytes each in a 64-bit process; 32-bit Linux gets no default limit
        if (!Environment.Is64BitProcess || (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())) return -1;
        long* limit = stackalloc long[2]; // the soft limit, then the hard one
        return getrlimit(OperatingSystem.IsLinux() ? 7 : 8, limit) == 0 ? limit[0] : -1;
    }

    /// <summary>Connections a soft limit leaves room for, with <see cref="Reserve"/> kept back; unlimited (int.MaxValue)
    /// when the limit is unknown or infinite.</summary>
    public static int MaxConnections(long softLimit)
    {
        if (softLimit <= 0 || softLimit >= int.MaxValue) return int.MaxValue; // RLIM_INFINITY is -1 on Linux, 2^63-1 on macOS
        return (int)Math.Max(1, softLimit - Math.Min(Reserve, softLimit / 2));
    }

    [LibraryImport("libc"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int getrlimit(int resource, long* limit);
}
