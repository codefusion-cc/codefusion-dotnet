using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// e_sqlite3 is the package's native asset, which the host finds through the app's deps.json; no library planted elsewhere is loaded.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

namespace CodeFusion.Data.Sqlite;

/// <summary>
/// The sqlite3 C API, called directly: <c>e_sqlite3</c> from the SourceGear.sqlite3 package (one native library per RID,
/// nothing managed in between). Calls that can't run user code or block carry <see cref="SuppressGCTransitionAttribute"/>,
/// so a column read is a plain call. <c>step</c> can call the provider's SQL functions back and wait on the file, so it
/// keeps the transition.
/// </summary>
internal static unsafe partial class Sqlite3
{
    const string Lib = "e_sqlite3";

    // result codes
    public const int Ok = 0, Error = 1, Busy = 5, Locked = 6, NoMem = 7, ReadOnly = 8, Interrupt = 9, IoErr = 10, Corrupt = 11,
        Full = 13, Constraint = 19, Misuse = 21, NotADb = 26, Row = 100, Done = 101;
    public const int ConstraintUnique = Constraint | (8 << 8), ConstraintPrimaryKey = Constraint | (6 << 8);

    // open flags
    public const int OpenReadWrite = 0x2, OpenCreate = 0x4, OpenNoMutex = 0x8000, OpenPrivateCache = 0x40000, OpenExResCode = 0x02000000;

    public const uint PreparePersistent = 0x01;

    // column types
    public const int TypeInteger = 1, TypeFloat = 2, TypeText = 3, TypeBlob = 4, TypeNull = 5;

    // function flags
    public const int Utf8 = 1, Innocuous = 0x200000;

    /// <summary>The oldest SQLite the generated SQL runs on: ORDER BY inside an aggregate (3.44).</summary>
    public const int MinVersion = 3_044_000;

    const int ConfigMemStatus = 9;

    [LibraryImport(Lib, EntryPoint = "sqlite3_libversion_number")]
    public static partial int LibVersionNumber();

    [LibraryImport(Lib, EntryPoint = "sqlite3_initialize")]
    public static partial int Initialize();

    // sqlite3_config is variadic. On Apple's arm64 ABI a variadic argument goes on the stack while a fixed one goes in a
    // register, so the call is declared with eight register arguments and the value as the ninth, the first stack slot,
    // which is where the callee's va_arg reads it. Everywhere else the value goes where a second fixed argument would,
    // which is where va_arg reads it there.
    [LibraryImport(Lib, EntryPoint = "sqlite3_config")]
    private static partial int ConfigRegisters(int op, int value);

    [LibraryImport(Lib, EntryPoint = "sqlite3_config")]
    private static partial int ConfigAppleArm64(int op, nint r1, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, long stack);

    static int Config(int op, int value) =>
        OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ConfigAppleArm64(op, 0, 0, 0, 0, 0, 0, 0, value)
            : ConfigRegisters(op, value);

    /// <summary>Turns off SQLite's memory statistics, which take a process-wide mutex on every allocation. Only before
    /// SQLite is initialized: false when something in the process initialized it first, which costs only the mutex.</summary>
    public static bool DisableMemoryStatus() => Config(ConfigMemStatus, 0) == Ok;

    [LibraryImport(Lib, EntryPoint = "sqlite3_memory_used")]
    public static partial long MemoryUsed();

    [LibraryImport(Lib, EntryPoint = "sqlite3_open_v2")]
    public static partial int Open(byte* filename, out nint db, int flags, byte* vfs);

    [LibraryImport(Lib, EntryPoint = "sqlite3_close_v2")]
    public static partial int Close(nint db);

    [LibraryImport(Lib, EntryPoint = "sqlite3_busy_timeout")]
    public static partial int BusyTimeout(nint db, int ms);

    [LibraryImport(Lib, EntryPoint = "sqlite3_exec")]
    public static partial int Exec(nint db, byte* sql, nint callback, nint arg, nint errmsg);

    [LibraryImport(Lib, EntryPoint = "sqlite3_prepare_v3")]
    public static partial int Prepare(nint db, byte* sql, int bytes, uint flags, out nint stmt, out byte* tail);

    [LibraryImport(Lib, EntryPoint = "sqlite3_finalize")]
    public static partial int Finalize(nint stmt);

    [LibraryImport(Lib, EntryPoint = "sqlite3_step")]
    public static partial int Step(nint stmt);

    [LibraryImport(Lib, EntryPoint = "sqlite3_reset"), SuppressGCTransition]
    public static partial int Reset(nint stmt);

    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_int64"), SuppressGCTransition]
    public static partial int BindInt64(nint stmt, int index, long value);

    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_double"), SuppressGCTransition]
    public static partial int BindDouble(nint stmt, int index, double value);

    /// <summary>Text bound in place (SQLITE_STATIC, <paramref name="destructor"/> 0): the bytes must stay put until the
    /// statement is reset.</summary>
    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_text"), SuppressGCTransition]
    public static partial int BindText(nint stmt, int index, byte* text, int bytes, nint destructor);

    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_null"), SuppressGCTransition]
    public static partial int BindNull(nint stmt, int index);

    [LibraryImport(Lib, EntryPoint = "sqlite3_bind_parameter_count"), SuppressGCTransition]
    public static partial int BindParameterCount(nint stmt);

    [LibraryImport(Lib, EntryPoint = "sqlite3_column_count"), SuppressGCTransition]
    public static partial int ColumnCount(nint stmt);

    [LibraryImport(Lib, EntryPoint = "sqlite3_column_type"), SuppressGCTransition]
    public static partial int ColumnType(nint stmt, int column);

    [LibraryImport(Lib, EntryPoint = "sqlite3_column_int64"), SuppressGCTransition]
    public static partial long ColumnInt64(nint stmt, int column);

    [LibraryImport(Lib, EntryPoint = "sqlite3_column_double"), SuppressGCTransition]
    public static partial double ColumnDouble(nint stmt, int column);

    [LibraryImport(Lib, EntryPoint = "sqlite3_column_text"), SuppressGCTransition]
    public static partial byte* ColumnText(nint stmt, int column);

    [LibraryImport(Lib, EntryPoint = "sqlite3_column_bytes"), SuppressGCTransition]
    public static partial int ColumnBytes(nint stmt, int column);

    [LibraryImport(Lib, EntryPoint = "sqlite3_errmsg")]
    public static partial byte* ErrMsg(nint db);

    [LibraryImport(Lib, EntryPoint = "sqlite3_extended_errcode"), SuppressGCTransition]
    public static partial int ExtendedErrCode(nint db);

    [LibraryImport(Lib, EntryPoint = "sqlite3_get_autocommit"), SuppressGCTransition]
    public static partial int GetAutocommit(nint db);

    /// <summary>Stops what <paramref name="db"/> is running; safe from any thread.</summary>
    [LibraryImport(Lib, EntryPoint = "sqlite3_interrupt")]
    public static partial void InterruptRunning(nint db);

    [LibraryImport(Lib, EntryPoint = "sqlite3_wal_checkpoint_v2")]
    public static partial int WalCheckpoint(nint db, byte* schema, int mode, out int logFrames, out int checkpointed);

    /// <summary>Called after each commit with the WAL's size in frames; replaces the automatic checkpoint's hook.</summary>
    [LibraryImport(Lib, EntryPoint = "sqlite3_wal_hook")]
    public static partial nint WalHook(nint db, delegate* unmanaged[Cdecl]<nint, nint, byte*, int, int> callback, nint arg);

    [LibraryImport(Lib, EntryPoint = "sqlite3_create_function_v2")]
    public static partial int CreateFunction(nint db, byte* name, int args, int flags, nint app,
        delegate* unmanaged[Cdecl]<nint, int, nint*, void> func, nint step, nint final, nint destroy);

    [LibraryImport(Lib, EntryPoint = "sqlite3_user_data"), SuppressGCTransition]
    public static partial nint UserData(nint context);

    [LibraryImport(Lib, EntryPoint = "sqlite3_result_int64"), SuppressGCTransition]
    public static partial void ResultInt64(nint context, long value);

    /// <summary>The message of a failed call on <paramref name="db"/>, for an exception (cold).</summary>
    public static string Message(nint db) => db == 0 ? "no connection" : Marshal.PtrToStringUTF8((nint)ErrMsg(db)) ?? "";

    /// <summary>The name of an extended result code's primary code, as SQLite's docs write it.</summary>
    public static string CodeName(int extended) => (extended & 0xFF) switch
    {
        Busy => "SQLITE_BUSY",
        Locked => "SQLITE_LOCKED",
        NoMem => "SQLITE_NOMEM",
        ReadOnly => "SQLITE_READONLY",
        Interrupt => "SQLITE_INTERRUPT",
        IoErr => "SQLITE_IOERR",
        Corrupt => "SQLITE_CORRUPT",
        Full => "SQLITE_FULL",
        Constraint => extended == ConstraintUnique ? "SQLITE_CONSTRAINT_UNIQUE" : extended == ConstraintPrimaryKey ? "SQLITE_CONSTRAINT_PRIMARYKEY" : "SQLITE_CONSTRAINT",
        NotADb => "SQLITE_NOTADB",
        Misuse => "SQLITE_MISUSE",
        _ => "SQLITE_ERROR",
    };
}

/// <summary>The SQL functions every connection gets.</summary>
internal static unsafe class SqlFunctions
{
    /// <summary><c>cf_now()</c>: the instant the write unit started, in microseconds since 1970, as Postgres's now() is its
    /// transaction's start. Its user data points at the connection's clock, which the writer sets per group; a reader's
    /// clock is 0, so it reads the time at each call.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static void Now(nint context, int argc, nint* argv)
    {
        long now = *(long*)Sqlite3.UserData(context);
        if (now == 0) now = Clock.UnixMicroseconds();
        Sqlite3.ResultInt64(context, now);
    }

    /// <summary>The WAL hook: notes the WAL's size in frames where the writer reads it after the group.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int WalFrames(nint arg, nint db, byte* schema, int frames)
    {
        *(int*)arg = frames;
        return Sqlite3.Ok;
    }
}

/// <summary>The time, for <c>cf_now()</c>.</summary>
internal static class Clock
{
    public static long UnixMicroseconds() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
}
