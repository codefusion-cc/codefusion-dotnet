using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace CodeFusion.Data.Sqlite;

/// <summary>
/// One command: its statements and their parameters, copied into a buffer as they are bound, and for a write the rows
/// its statements returned, kept until its group commits. Pooled with its buffers: a read goes back as soon as it ran
/// (to its thread's one-job cache, no lock), a write when its task is read.
/// </summary>
internal sealed class SqliteJob : IValueTaskSource<DataResult>, IThreadPoolWorkItem
{
    // parameter tags in Buf, and value tags in Saved
    internal const byte TagNull = 0, TagInt64 = 1, TagDouble = 2, TagText = 3;

    /// <summary>A kept column's size before its text: SQLite's type, its integer, its double, its text's length (-1 when
    /// SQLite gives no text: NULL).</summary>
    internal const int SavedColumn = 1 + 8 + 8 + 4;

    InlineValueTaskSourceCore<DataResult> _core;
    /// <summary>Its generation and hold, and in Debug its steps: CodeFusion.Data's, as every provider's job.</summary>
    JobLife _life = new("SQLite", queuesUnheld: false);

    /// <summary>Changes each time the job goes back to the pool, so a stale command or held copy can't touch it.</summary>
    internal int Generation => _life.Generation;

    // ---- the command ----
    internal byte[] Buf = new byte[256];
    internal int Len;
    internal SqliteStatement[] Statements = new SqliteStatement[4];
    /// <summary>Where each statement's parameters start in <see cref="Buf"/>.</summary>
    internal int[] Starts = new int[4];
    internal int Count;
    int _params;
    int _arrayLenAt = -1, _arrayCount;

    // ---- a write's submission and run ----
    internal unsafe delegate*<in SavedRow, object?, long> Reader;
    internal object? State;
    internal CancellationToken Aborted;
    /// <summary>The writer it was queued on, which counts what its completion's continuation throws.</summary>
    internal SqliteWriter? Writer;
    internal byte[] Saved = new byte[256];
    internal int SavedLen, SavedRows;
    internal DataResult Failure;
    internal bool Failed;
    /// <summary>A read run in an event-loop batch (it saw writes not committed yet): completed with them, counted apart.</summary>
    internal bool BatchedRead;

    SqliteJob()
    {
        // Debug: as a returned job's, so the first rent checks the same way
        JobLife.Poison(Buf);
        JobLife.Poison(Saved);
    }

    /// <summary>A job from the pool (a read is rented and given back on one thread: that thread's own, no lock).</summary>
    internal static SqliteJob Rent(SqliteStatement first, SqliteDataSource owner)
    {
        var job = JobPool<SqliteJob>.Take(local: true) ?? new SqliteJob();
        job._life.CheckPoisoned(job.Buf);
        job._life.CheckPoisoned(job.Saved);
        job._life.To(JobStep.Building);
        job._life.CountOut(owner.Jobs);
        job.Statement(first);
        return job;
    }

    internal unsafe void Return()
    {
        _life.CheckReturnable();
        Array.Clear(Statements, 0, Count);
        Count = Len = SavedLen = SavedRows = _params = 0;
        _arrayLenAt = -1;
        Reader = null;
        State = null;
        Aborted = default;
        Failure = default;
        Writer = null;
        _pending = default;
        Failed = false;
        BatchedRead = false;
        if (Buf.Length > JobPool.MaxPooledBytes) Buf = new byte[256];
        if (Saved.Length > JobPool.MaxPooledBytes) Saved = new byte[256];
        _core.Reset();
        _life.Returned();
        JobLife.Poison(Buf);
        JobLife.Poison(Saved);
        JobPool<SqliteJob>.Give(this, local: true);
    }

    internal void Hold() => _life.Hold();

    /// <summary>Takes the held job for its one run or discard: of two callers only one wins, a stale one never.</summary>
    internal bool Claim(int generation) => _life.Claim(generation);

    internal ValueTask<DataResult> Task => new(this, _core.Version);

    // ---------- building ----------

    internal void Statement(SqliteStatement s)
    {
        EndStatement();
        if (Count == Statements.Length)
        {
            Array.Resize(ref Statements, Count * 2);
            Array.Resize(ref Starts, Count * 2);
        }
        Statements[Count] = s;
        Starts[Count] = Len;
        Count++;
        _params = 0;
    }

    internal void EndStatement()
    {
        if (Count == 0) return;
        if (_arrayLenAt >= 0) throw new InvalidOperationException("a text array parameter wasn't ended");
        var s = Statements[Count - 1];
        if (_params != s.ParamCount) throw new InvalidOperationException($"{s} takes {s.ParamCount} parameters, got {_params}");
    }

    void Param(byte tag, int payload)
    {
        if (_arrayLenAt >= 0) throw new InvalidOperationException("a parameter inside a text array");
        var s = Statements[Count - 1];
        if (_params == s.ParamCount) throw new InvalidOperationException($"{s} takes {s.ParamCount} parameters");
        _params++;
        Ensure(1 + payload);
        Buf[Len++] = tag;
    }

    internal void Int64(long v)
    {
        Param(TagInt64, 8);
        BinaryPrimitives.WriteInt64LittleEndian(Buf.AsSpan(Len), v);
        Len += 8;
    }

    internal void Double(double v)
    {
        Param(TagDouble, 8);
        BinaryPrimitives.WriteDoubleLittleEndian(Buf.AsSpan(Len), v);
        Len += 8;
    }

    internal void Null() => Param(TagNull, 0);

    internal void Text(ReadOnlySpan<byte> utf8)
    {
        Param(TagText, 4 + utf8.Length);
        BinaryPrimitives.WriteInt32LittleEndian(Buf.AsSpan(Len), utf8.Length);
        utf8.CopyTo(Buf.AsSpan(Len + 4));
        Len += 4 + utf8.Length;
    }

    internal void Text(ReadOnlySpan<char> s)
    {
        Param(TagText, 4 + Encoding.UTF8.GetMaxByteCount(s.Length));
        int n = Encoding.UTF8.GetBytes(s, Buf.AsSpan(Len + 4));
        BinaryPrimitives.WriteInt32LittleEndian(Buf.AsSpan(Len), n);
        Len += 4 + n;
    }

    // A text array is bound as one JSON text, which SQL reads with json_each: {0} IN (SELECT value FROM json_each(?n)).

    internal void BeginTextArray()
    {
        Param(TagText, 5);
        _arrayLenAt = Len;
        Len += 4;
        Buf[Len++] = (byte)'[';
        _arrayCount = 0;
    }

    internal void Element(ReadOnlySpan<byte> utf8)
    {
        if (_arrayLenAt < 0) throw new InvalidOperationException("an element outside a text array");
        Ensure(utf8.Length * 6 + 3);
        if (_arrayCount++ > 0) Buf[Len++] = (byte)',';
        Buf[Len++] = (byte)'"';
        Len += JsonText.Escape(utf8, Buf.AsSpan(Len));
        Buf[Len++] = (byte)'"';
    }

    internal void EndTextArray()
    {
        if (_arrayLenAt < 0) throw new InvalidOperationException("EndTextArray without BeginTextArray");
        Ensure(1);
        Buf[Len++] = (byte)']';
        BinaryPrimitives.WriteInt32LittleEndian(Buf.AsSpan(_arrayLenAt), Len - _arrayLenAt - 4);
        _arrayLenAt = -1;
    }

    void Ensure(int n)
    {
        if (Len + n > Buf.Length) Array.Resize(ref Buf, Math.Max(Len + n, Buf.Length * 2));
    }

    // ---------- a write's rows ----------

    /// <summary>Copies the current row of <paramref name="stmt"/>: a column count, then per column a tag and its value.</summary>
    internal unsafe void Save(nint stmt)
    {
        _life.CheckIs(JobStep.Queued);
#if DEBUG
        SaveFault?.Invoke();
#endif
        int n = Sqlite3.ColumnCount(stmt);
        EnsureSaved(4);
        BinaryPrimitives.WriteInt32LittleEndian(Saved.AsSpan(SavedLen), n);
        SavedLen += 4;
        for (int c = 0; c < n; c++)
        {
            // what each of SQLite's column calls returns, in an order none changes the next: the type, the integer, the
            // double, then the text (which turns a number into text in place). A SavedRow hands back exactly these, so a
            // row read after the COMMIT reads as it would have live (SqliteRow), conversions and NULLs alike.
            int type = Sqlite3.ColumnType(stmt, c);
            long integer = Sqlite3.ColumnInt64(stmt, c);
            double real = Sqlite3.ColumnDouble(stmt, c);
            byte* text = Sqlite3.ColumnText(stmt, c);
            int len = text is null ? -1 : Sqlite3.ColumnBytes(stmt, c);
            EnsureSaved(SavedColumn + Math.Max(len, 0));
            var into = Saved.AsSpan(SavedLen);
            into[0] = (byte)type;
            BinaryPrimitives.WriteInt64LittleEndian(into[1..], integer);
            BinaryPrimitives.WriteDoubleLittleEndian(into[9..], real);
            BinaryPrimitives.WriteInt32LittleEndian(into[17..], len);
            if (len > 0) new ReadOnlySpan<byte>(text, len).CopyTo(into[SavedColumn..]);
            SavedLen += SavedColumn + Math.Max(len, 0);
        }
        SavedRows++;
    }

#if DEBUG
    /// <summary>Debug, tests: runs as a row is kept, so a test can fail there as running out of memory would.</summary>
    internal static Action? SaveFault;
#endif

    void EnsureSaved(int n)
    {
        if (SavedLen + n > Saved.Length) Array.Resize(ref Saved, Math.Max(SavedLen + n, Saved.Length * 2));
    }

    /// <summary>Forgets an attempt whose transaction rolled back: the write runs again.</summary>
    internal void Restart()
    {
        _life.CheckIs(JobStep.Queued);
        SavedLen = SavedRows = 0;
        Failed = false;
        Failure = default;
    }

    /// <summary>Hands the kept rows to the reader, after the commit. What the reader throws fails the write as
    /// ReaderFailed; it committed all the same.</summary>
    internal unsafe DataResult HandRows()
    {
        _life.CheckIs(JobStep.Queued);
        long value = 0;
        if (Reader is not null)
        {
            Span<int> offsets = stackalloc int[64];
            for (int at = 0, row = 0; row < SavedRows; row++)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(Saved.AsSpan(at));
                at += 4;
                if (n > offsets.Length) offsets = new int[n];
                for (int c = 0; c < n; c++)
                {
                    offsets[c] = at;
                    at += SavedColumn + Math.Max(BinaryPrimitives.ReadInt32LittleEndian(Saved.AsSpan(at + 17)), 0);
                }
                try
                {
                    value = Reader(new SavedRow(Saved.AsSpan(0, SavedLen), offsets[..n]), State);
                }
                catch (Exception e)
                {
                    return new DataResult(DataStatus.ReaderFailed, SavedRows, value, e);
                }
            }
        }
        return new DataResult(DataStatus.Ok, SavedRows, value);
    }

    // ---------- completion ----------

    /// <summary>Queued for the writer: no more building, no second run.</summary>
    internal void Queued() => _life.To(JobStep.Queued);

    /// <summary>
    /// Completes the write. <paramref name="inline"/>: its caller continues here, on the writer's thread; else on the
    /// thread pool (the job is the work item: nothing allocated). After this the job isn't ours.
    /// </summary>
    internal void Complete(in DataResult result, bool inline)
    {
        _life.To(JobStep.Completed);
        if (!inline)
        {
            _pending = result;
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
            return;
        }
        SetResult(result);
    }

    DataResult _pending;

    void IThreadPoolWorkItem.Execute() => SetResult(_pending);

    void SetResult(in DataResult result)
    {
        try
        {
            _core.SetResult(result);
        }
        catch (Exception e)
        {
            // a continuation that throws (not an async method's, whose exceptions go to its task) must not end the writer
            Writer?.Unexpected(e);
        }
    }

    DataResult IValueTaskSource<DataResult>.GetResult(short token)
    {
        var result = _core.GetResult(token); // throws for a stale token, before the job could go back twice
        Return();
        return result;
    }

    ValueTaskSourceStatus IValueTaskSource<DataResult>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<DataResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);
}

/// <summary>A row of a write, kept until its group committed: read from the job's buffer.</summary>
internal readonly ref struct SavedRow : IDataRow
{
    readonly ReadOnlySpan<byte> _saved;
    readonly ReadOnlySpan<int> _at;

    internal SavedRow(ReadOnlySpan<byte> saved, ReadOnlySpan<int> offsets)
    {
        _saved = saved;
        _at = offsets;
    }

    public int FieldCount => _at.Length;

    // what SQLite's column calls returned for the column, kept by SqliteJob.Save: the same answers SqliteRow gives live

    public bool IsNull(int c) => _saved[_at[c]] == Sqlite3.TypeNull;

    public ReadOnlySpan<byte> GetBytes(int c)
    {
        int at = _at[c];
        int len = BinaryPrimitives.ReadInt32LittleEndian(_saved[(at + 17)..]);
        if (len < 0) NullColumn(c); // as SqliteRow: SQLite gave no text
        return _saved.Slice(at + SqliteJob.SavedColumn, len);
    }

    public ReadOnlySpan<byte> GetJson(int c) => GetBytes(c);

    public long GetInt64(int c) => BinaryPrimitives.ReadInt64LittleEndian(_saved[(_at[c] + 1)..]);

    public bool GetBoolean(int c) => GetInt64(c) != 0;

    public double GetDouble(int c) => BinaryPrimitives.ReadDoubleLittleEndian(_saved[(_at[c] + 9)..]);

    static void NullColumn(int c) => throw new InvalidOperationException($"column {c} is NULL");

    public Timestamp GetTimestamp(int c) => new(GetInt64(c));
}

/// <summary>The current row of a running statement, read in place: valid until the next step.</summary>
internal readonly unsafe ref struct SqliteRow : IDataRow
{
    readonly nint _stmt;

    internal SqliteRow(nint stmt) => _stmt = stmt;

    public int FieldCount => Sqlite3.ColumnCount(_stmt);

    public bool IsNull(int c) => Sqlite3.ColumnType(_stmt, c) == Sqlite3.TypeNull;

    public ReadOnlySpan<byte> GetBytes(int c)
    {
        // text first, then its length, as SQLite asks: the length is then of the UTF-8 text
        byte* p = Sqlite3.ColumnText(_stmt, c);
        if (p is null) NullColumn(c);
        return new ReadOnlySpan<byte>(p, Sqlite3.ColumnBytes(_stmt, c));
    }

    public ReadOnlySpan<byte> GetJson(int c) => GetBytes(c);

    public long GetInt64(int c) => Sqlite3.ColumnInt64(_stmt, c);

    public bool GetBoolean(int c) => Sqlite3.ColumnInt64(_stmt, c) != 0;

    public double GetDouble(int c) => Sqlite3.ColumnDouble(_stmt, c);

    public Timestamp GetTimestamp(int c) => new(Sqlite3.ColumnInt64(_stmt, c));

    static void NullColumn(int c) => throw new InvalidOperationException($"column {c} is NULL");
}

/// <summary>A JSON string's content: <c>"</c>, <c>\</c> and control characters escaped, the rest as it is.</summary>
internal static class JsonText
{
    /// <summary>Writes <paramref name="utf8"/> escaped into <paramref name="into"/> (at least 6 bytes per input byte); the bytes written.</summary>
    public static int Escape(ReadOnlySpan<byte> utf8, Span<byte> into)
    {
        int n = 0;
        foreach (byte b in utf8)
        {
            if (b is (byte)'"' or (byte)'\\')
            {
                into[n++] = (byte)'\\';
                into[n++] = b;
            }
            else if (b < 0x20)
            {
                "\\u00"u8.CopyTo(into[n..]);
                into[n + 4] = Hex[b >> 4];
                into[n + 5] = Hex[b & 15];
                n += 6;
            }
            else into[n++] = b;
        }
        return n;
    }

    static ReadOnlySpan<byte> Hex => "0123456789abcdef"u8;
}
