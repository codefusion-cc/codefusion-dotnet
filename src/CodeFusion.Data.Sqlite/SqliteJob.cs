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
    const int MaxPooled = 4096;
    /// <summary>Buffers past this size go with a returned job instead of being pooled.</summary>
    const int MaxPooledBytes = 64 << 10;
    static readonly SqliteJob?[] Pool = new SqliteJob?[MaxPooled];
    static readonly Lock PoolLock = new();
    static int _pooled;
    /// <summary>A read is rented and given back on one thread: that thread's job, no lock. (A shared lock here capped
    /// reads at about 2 M/s whatever the thread count.)</summary>
    [ThreadStatic] static SqliteJob? t_cached;

    // parameter tags in Buf, and value tags in Saved
    internal const byte TagNull = 0, TagInt64 = 1, TagDouble = 2, TagText = 3;

    InlineValueTaskSourceCore<DataResult> _core;
    /// <summary>Changes each time the job goes back to the pool, so a stale command or held copy can't touch it.</summary>
    internal int Generation;
    long _heldAt = Unclaimed;
    const long Unclaimed = -1;

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

    SqliteJob() => FillReturned();

    internal static SqliteJob Rent(SqliteStatement first, SqliteDataSource owner)
    {
        SqliteJob? job = t_cached;
        if (job is not null) t_cached = null;
        else
            lock (PoolLock)
                if (_pooled > 0)
                {
                    job = Pool[--_pooled];
                    Pool[_pooled] = null;
                }
        job ??= new SqliteJob();
        job.CheckUnwritten();
        job.To(Life.Building);
        job.RentedBy(owner);
        job.Statement(first);
        return job;
    }

    internal unsafe void Return()
    {
        CheckReturnable();
        Generation++;
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
        if (Buf.Length > MaxPooledBytes) Buf = new byte[256];
        if (Saved.Length > MaxPooledBytes) Saved = new byte[256];
        _core.Reset();
        To(Life.Pooled);
        FillReturned();
        if (t_cached is null)
        {
            t_cached = this;
            return;
        }
        lock (PoolLock)
            if (_pooled < MaxPooled) Pool[_pooled++] = this;
    }

    internal void Hold()
    {
        To(Life.Held);
        Volatile.Write(ref _heldAt, (uint)Generation);
    }

    /// <summary>Takes the held job for its one run or discard: of two callers only one wins, a stale one never.</summary>
    internal bool Claim(int generation) => Interlocked.CompareExchange(ref _heldAt, Unclaimed, (uint)generation) == (uint)generation;

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
        CheckIs(Life.Queued);
        int n = Sqlite3.ColumnCount(stmt);
        EnsureSaved(4);
        BinaryPrimitives.WriteInt32LittleEndian(Saved.AsSpan(SavedLen), n);
        SavedLen += 4;
        for (int c = 0; c < n; c++)
        {
            switch (Sqlite3.ColumnType(stmt, c))
            {
                case Sqlite3.TypeNull:
                    EnsureSaved(1);
                    Saved[SavedLen++] = TagNull;
                    break;
                case Sqlite3.TypeInteger:
                    EnsureSaved(9);
                    Saved[SavedLen] = TagInt64;
                    BinaryPrimitives.WriteInt64LittleEndian(Saved.AsSpan(SavedLen + 1), Sqlite3.ColumnInt64(stmt, c));
                    SavedLen += 9;
                    break;
                case Sqlite3.TypeFloat:
                    EnsureSaved(9);
                    Saved[SavedLen] = TagDouble;
                    BinaryPrimitives.WriteDoubleLittleEndian(Saved.AsSpan(SavedLen + 1), Sqlite3.ColumnDouble(stmt, c));
                    SavedLen += 9;
                    break;
                default:
                    // text first, then its length, as SQLite asks
                    byte* p = Sqlite3.ColumnText(stmt, c);
                    int len = Sqlite3.ColumnBytes(stmt, c);
                    EnsureSaved(5 + len);
                    Saved[SavedLen] = TagText;
                    BinaryPrimitives.WriteInt32LittleEndian(Saved.AsSpan(SavedLen + 1), len);
                    new ReadOnlySpan<byte>(p, len).CopyTo(Saved.AsSpan(SavedLen + 5));
                    SavedLen += 5 + len;
                    break;
            }
        }
        SavedRows++;
    }

    void EnsureSaved(int n)
    {
        if (SavedLen + n > Saved.Length) Array.Resize(ref Saved, Math.Max(SavedLen + n, Saved.Length * 2));
    }

    /// <summary>Forgets an attempt whose transaction rolled back: the write runs again.</summary>
    internal void Restart()
    {
        CheckIs(Life.Queued);
        SavedLen = SavedRows = 0;
        Failed = false;
        Failure = default;
    }

    /// <summary>Hands the kept rows to the reader, after the commit. What the reader throws fails the write as
    /// ReaderFailed; it committed all the same.</summary>
    internal unsafe DataResult HandRows()
    {
        CheckIs(Life.Queued);
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
                    at += Saved[at] switch { TagNull => 1, TagInt64 or TagDouble => 9, _ => 5 + BinaryPrimitives.ReadInt32LittleEndian(Saved.AsSpan(at + 1)) };
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
    internal void Queued() => To(Life.Queued);

    /// <summary>
    /// Completes the write. <paramref name="inline"/>: its caller continues here, on the writer's thread; else on the
    /// thread pool (the job is the work item: nothing allocated). After this the job isn't ours.
    /// </summary>
    internal void Complete(in DataResult result, bool inline)
    {
        To(Life.Completed);
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

    // ---------- Debug checks: each call compiles away in Release ----------

    /// <summary>Where a job is in its life. Debug builds check each step: a step out of order hands one command's rows
    /// or result to another.</summary>
    internal enum Life : byte { Pooled, Building, Held, Queued, Completed }
    Life _life;

    [Conditional("DEBUG")]
    internal void To(Life next)
    {
        bool allowed = (_life, next) switch
        {
            (Life.Pooled, Life.Building) => true,
            (Life.Building, Life.Held) or (Life.Held, Life.Queued) => true,
            (Life.Queued, Life.Completed) => true,
            (not Life.Pooled, Life.Pooled) => true, // read (Completed), run inline (Held), or given back unrun
            _ => false,
        };
        if (!allowed)
            throw new InvalidOperationException(next == Life.Completed
                ? $"a SQLite job was completed while {_life} (twice, or after its return)"
                : $"a SQLite job went from {_life} to {next}");
        _life = next;
    }

    [Conditional("DEBUG")]
    void CheckReturnable()
    {
        if (_life == Life.Pooled) throw new InvalidOperationException("a SQLite job was returned twice");
    }

    /// <summary>The writer touches a job's run only while it is queued: after its completion it may be someone else's.</summary>
    [Conditional("DEBUG")]
    internal void CheckIs(Life life)
    {
        if (_life != life) throw new InvalidOperationException($"a SQLite job was used while {_life}, not {life}");
    }

    const byte Returned = 0xDD;

    /// <summary>Fills a returned job's buffers with 0xDD, so a reader that kept one sees garbage.</summary>
    [Conditional("DEBUG")]
    void FillReturned()
    {
        Buf.AsSpan().Fill(Returned);
        Saved.AsSpan().Fill(Returned);
        if (_owner is { } owner) Interlocked.Decrement(ref owner.JobsOut);
        _owner = null;
    }

    /// <summary>A job's buffers sat in the pool untouched: anything but 0xDD was written after its return.</summary>
    [Conditional("DEBUG")]
    void CheckUnwritten()
    {
        if (Buf.AsSpan().IndexOfAnyExcept(Returned) >= 0 || Saved.AsSpan().IndexOfAnyExcept(Returned) >= 0)
            throw new InvalidOperationException("a pooled SQLite job's buffer was written to after its return");
    }

    /// <summary>Debug: the source it was rented from, which counts it out until it is back in the pool.</summary>
    SqliteDataSource? _owner;

    [Conditional("DEBUG")]
    void RentedBy(SqliteDataSource owner)
    {
        _owner = owner;
        Interlocked.Increment(ref owner.JobsOut);
    }
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

    public bool IsNull(int c) => _saved[_at[c]] == SqliteJob.TagNull;

    public ReadOnlySpan<byte> GetBytes(int c)
    {
        int at = _at[c];
        return _saved[at] switch
        {
            SqliteJob.TagText => _saved.Slice(at + 5, BinaryPrimitives.ReadInt32LittleEndian(_saved[(at + 1)..])),
            SqliteJob.TagNull => throw new InvalidOperationException($"column {c} is NULL"),
            _ => throw new InvalidOperationException($"column {c} is a number, not text"),
        };
    }

    public ReadOnlySpan<byte> GetJson(int c) => GetBytes(c);

    public long GetInt64(int c)
    {
        int at = _at[c];
        return _saved[at] switch
        {
            SqliteJob.TagInt64 => BinaryPrimitives.ReadInt64LittleEndian(_saved[(at + 1)..]),
            SqliteJob.TagDouble => (long)BinaryPrimitives.ReadDoubleLittleEndian(_saved[(at + 1)..]),
            SqliteJob.TagNull => throw new InvalidOperationException($"column {c} is NULL"),
            _ => throw new InvalidOperationException($"column {c} is text, not a number"),
        };
    }

    public bool GetBoolean(int c) => GetInt64(c) != 0;

    public double GetDouble(int c)
    {
        int at = _at[c];
        return _saved[at] switch
        {
            SqliteJob.TagDouble => BinaryPrimitives.ReadDoubleLittleEndian(_saved[(at + 1)..]),
            SqliteJob.TagInt64 => BinaryPrimitives.ReadInt64LittleEndian(_saved[(at + 1)..]),
            SqliteJob.TagNull => throw new InvalidOperationException($"column {c} is NULL"),
            _ => throw new InvalidOperationException($"column {c} is text, not a number"),
        };
    }

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
