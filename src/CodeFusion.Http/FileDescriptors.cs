using System;
using System.Runtime.InteropServices;

namespace CodeFusion.Http;

/// <summary>
/// The process's descriptor limit, which sets the default <see cref="HttpServerOptions.MaxConnections"/>, raised to the
/// hard limit first. Past it
/// accept fails, and so does everything else that opens a descriptor: the database, a log file, the runtime itself,
/// which then ends the process when a managed exception has to cross native frames ($12 entry at 65,535).
/// </summary>
internal static unsafe partial class FileDescriptors
{
    /// <summary>Descriptors kept for the rest of the process: 1024, or half of a limit under 2048 (the runtime alone
    /// holds about 50 on macOS).</summary>
    public const int Reserve = 1024;

    /// <summary>The soft RLIMIT_NOFILE; -1 where there is none or it can't be read.</summary>
    public static long SoftLimit() => Limits(out long soft, out _) ? soft : -1;

    /// <summary>Raises the soft limit to <see cref="Raised"/>, as CoreCLR does as it starts and NativeAOT doesn't; the
    /// soft limit before and after (the same when it can't be raised), or -1 where there is none or it can't be
    /// read.</summary>
    public static (long Before, long After) RaiseSoftLimit()
    {
        if (!Limits(out long soft, out long hard)) return (-1, -1);
        long raised = Raised(soft, hard, OperatingSystem.IsMacOS());
        return (soft, raised != soft && SetLimits(raised, hard) ? raised : soft);
    }

    /// <summary>The soft limit to raise to: the hard one, on macOS at most OPEN_MAX (10,240), past which its
    /// setrlimit may refuse; never lower than the soft limit already is. -1 is RLIM_INFINITY on Linux.</summary>
    public static long Raised(long soft, long hard, bool macOS)
    {
        ulong to = macOS ? Math.Min((ulong)hard, OpenMax) : (ulong)hard;
        return to > (ulong)soft ? (long)to : soft;
    }

    const ulong OpenMax = 10240;

    /// <summary>The soft and hard RLIMIT_NOFILE; false where there are none or they can't be read.</summary>
    public static bool Limits(out long soft, out long hard)
    {
        soft = hard = -1;
        // struct rlimit is two rlim_t, 8 bytes each in a 64-bit process; 32-bit Linux gets no default limit
        if (!Environment.Is64BitProcess || (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())) return false;
        long* limit = stackalloc long[2]; // the soft limit, then the hard one
        if (getrlimit(Resource, limit) != 0) return false;
        (soft, hard) = (limit[0], limit[1]);
        return true;
    }

    /// <summary>Sets the soft and hard RLIMIT_NOFILE; false where it can't.</summary>
    public static bool SetLimits(long soft, long hard)
    {
        if (!Environment.Is64BitProcess || (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())) return false;
        long* limit = stackalloc long[2];
        (limit[0], limit[1]) = (soft, hard);
        return setrlimit(Resource, limit) == 0;
    }

    static int Resource => OperatingSystem.IsLinux() ? 7 : 8;

    /// <summary>Connections a soft limit leaves room for, with <see cref="Reserve"/> kept back; unlimited (int.MaxValue)
    /// when the limit is unknown or infinite.</summary>
    public static int MaxConnections(long softLimit)
    {
        if (softLimit <= 0 || softLimit >= int.MaxValue) return int.MaxValue; // RLIM_INFINITY is -1 on Linux, 2^63-1 on macOS
        return (int)Math.Max(1, softLimit - Math.Min(Reserve, softLimit / 2));
    }

    /// <summary>Closes a descriptor nothing owns yet.</summary>
    public static void Close(int fd) => _ = close(fd); // failing, it can only have been closed already

    [LibraryImport("libc"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int close(int fd);

    [LibraryImport("libc"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int getrlimit(int resource, long* limit);

    [LibraryImport("libc"), DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int setrlimit(int resource, long* limit);
}
