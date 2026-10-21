using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using CodeFusion.Data;

namespace CodeFusion.Data.Postgres;

/// <summary>
/// Gets each row of a command's last statement, on the loop thread. A read's rows come straight from the receive
/// buffer as they arrive; a write's come once its transaction committed (rows of an attempt that rolled back are
/// never handed over). What it returns for the last row is the result's <see cref="PgResult.Value"/>, so a caller
/// needs no box for a count or an id. An exception fails that command alone.
/// </summary>
public delegate long PgRowHandler(in PgRow row, object? state);

/// <summary>How a command ended; see <see cref="PgResult"/>.</summary>
public enum PgStatus : byte
{
    /// <summary>The statements ran (and a write committed).</summary>
    Ok,
    /// <summary>Postgres refused the statement: <see cref="PgResult.Error"/> has its SQLSTATE and message.</summary>
    ServerError,
    /// <summary>The queue was full; nothing was sent.</summary>
    Busy,
    /// <summary>The caller's token fired before a loop took the command; nothing was sent.</summary>
    Canceled,
    /// <summary>The connection broke or couldn't be opened before the command's answer. A write's outcome is unknown.</summary>
    Broken,
    /// <summary>The row handler threw <see cref="PgResult.Exception"/>. A write committed all the same.</summary>
    HandlerFailed,
    /// <summary>The reader or writer was completed and takes no more commands.</summary>
    Stopped,
}

/// <summary>How a command ended. A status, not an exception, so a refused or failed command costs no throw.</summary>
public readonly struct PgResult
{
    readonly Exception? _exception;

    /// <summary>How the command ended.</summary>
    public PgStatus Status { get; }
    /// <summary>Rows of the last statement.</summary>
    public int Rows { get; }
    /// <summary>What the handler returned for the last row; 0 without rows.</summary>
    public long Value { get; }
    /// <summary>For a write, how many times its statements ran to the end (a group that rolls back runs them again).</summary>
    public int Runs { get; }

    internal PgResult(PgStatus status, int rows = 0, long value = 0, int runs = 0, Exception? exception = null)
    {
        Status = status;
        Rows = rows;
        Value = value;
        Runs = runs;
        _exception = exception;
    }

    internal static PgResult Busy => new(PgStatus.Busy);
    internal static PgResult Canceled => new(PgStatus.Canceled);
    internal static PgResult Stopped => new(PgStatus.Stopped);

    /// <summary>The statements ran (and a write committed).</summary>
    public bool IsOk => Status == PgStatus.Ok;
    /// <summary>The server's error (<see cref="PgStatus.ServerError"/>), else null.</summary>
    public PgException? Error => Status == PgStatus.ServerError ? (PgException)_exception! : null;
    /// <summary>The server error's SQLSTATE, else null.</summary>
    public string? SqlState => Error?.SqlState;
    /// <summary>What went wrong: the server's error, the handler's exception, or why the connection broke.</summary>
    public Exception? Exception => _exception;

    /// <summary>Throws what the status stands for, for callers that would rather catch.</summary>
    public PgResult ThrowIfFailed() => Status switch
    {
        PgStatus.Ok => this,
        PgStatus.Busy => throw new PgBusyException("too many commands are waiting"),
        PgStatus.Canceled => throw new OperationCanceledException(),
        PgStatus.Stopped => throw new InvalidOperationException("the reader or writer is stopped"),
        _ => throw _exception!,
    };

    /// <summary>The status, rows, value and error, for logs and test failures.</summary>
    public override string ToString() => $"{Status} rows={Rows} value={Value}{(_exception is null ? "" : " " + _exception.Message)}";
}

/// <summary>
/// Builds one command: a statement and its parameters, then any further statements with <see cref="Then"/>, and
/// <see cref="Run"/> queues it. The parameters are copied into the command's pooled buffer as they are added, so
/// spans straight from a request's buffer will do. A ref struct: it can't outlive the call that builds it. A call
/// that throws (a lone surrogate, a parameter too many) gives the command back: its builder takes nothing more.
/// </summary>
public readonly ref struct PgCommand : IDataCommand<PgStatement>
{
    readonly PgLoops _owner;
    readonly PgJob _job;
    readonly int _generation;

    internal PgCommand(PgLoops owner, PgJob job)
    {
        _owner = owner;
        _job = job;
        _generation = job.Generation;
    }

    /// <summary>The job it builds, whatever its state. For tests.</summary>
    internal object RawJob => _job;

    PgJob Job => _job.Generation == _generation && !_job.Submitted && !_job.Held ? _job
        : throw new InvalidOperationException("the command was already run, held, or given back when building it threw");

    /// <summary>The build threw: the job goes back to the pool, since no caller can finish a command it lost to an
    /// exception, and this builder no longer reaches it.</summary>
    static void Abandon(PgJob job) => job.Discard();

    /// <summary>An int8 parameter.</summary>
    public PgCommand Int8(long v)
    {
        var job = Job;
        try
        {
            job.Int8(v);
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>An int4 parameter.</summary>
    public PgCommand Int4(int v)
    {
        var job = Job;
        try
        {
            job.Int4(v);
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>A bool parameter.</summary>
    public PgCommand Bool(bool v)
    {
        var job = Job;
        try
        {
            job.Bool(v);
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>A float8 parameter.</summary>
    public PgCommand Float8(double v)
    {
        var job = Job;
        try
        {
            job.Float8(v);
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>A timestamptz parameter: microseconds since 1970-01-01 UTC; long.MaxValue and long.MinValue are
    /// +infinity and -infinity. Postgres refuses what lies outside its range (4713 BC to 294276 AD).</summary>
    public PgCommand Timestamptz(long unixMicroseconds)
    {
        var job = Job;
        try
        {
            job.Timestamptz(unixMicroseconds);
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>text from UTF-8 bytes, copied as they are (the server refuses invalid UTF-8 and NUL).</summary>
    public PgCommand Text(ReadOnlySpan<byte> utf8)
    {
        var job = Job;
        try
        {
            job.Text(utf8);
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>text from UTF-16, encoded into the buffer. A lone surrogate throws instead of turning into U+FFFD.</summary>
    public PgCommand Text(ReadOnlySpan<char> s)
    {
        var job = Job;
        try
        {
            job.Text(s);
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>text, or SQL NULL for null.</summary>
    public PgCommand Text(string? s) => s is null ? Null() : Text(s.AsSpan());

    /// <summary>SQL NULL, for any parameter type.</summary>
    public PgCommand Null()
    {
        var job = Job;
        try
        {
            job.Null();
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>text[], or SQL NULL for null (a null array must not turn into an empty span).</summary>
    public PgCommand TextArray(string?[]? values) => values is null ? Null() : TextArray(values.AsSpan());

    /// <summary>text[]: one dimension, lower bound 1; null elements are SQL NULL.</summary>
    public PgCommand TextArray(ReadOnlySpan<string?> values)
    {
        var job = Job;
        try
        {
            job.BeginTextArray();
            foreach (string? v in values)
                if (v is null) job.NullElement();
                else job.Element(v.AsSpan());
            job.EndTextArray();
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>Starts a text[] parameter whose elements follow with <see cref="Element(ReadOnlySpan{byte})"/>.</summary>
    public PgCommand BeginTextArray()
    {
        var job = Job;
        try
        {
            job.BeginTextArray();
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>One element of the text[] parameter, as UTF-8 bytes.</summary>
    public PgCommand Element(ReadOnlySpan<byte> utf8)
    {
        var job = Job;
        try
        {
            job.Element(utf8);
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>One element of the text[] parameter, encoded to UTF-8 as it is copied.</summary>
    public PgCommand Element(ReadOnlySpan<char> s)
    {
        var job = Job;
        try
        {
            job.Element(s);
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>Ends the text[] parameter <see cref="BeginTextArray"/> started.</summary>
    public PgCommand EndTextArray()
    {
        var job = Job;
        try
        {
            job.EndTextArray();
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>Another statement in the same command; its parameters follow. A write's statements commit together,
    /// and only the last one's rows reach the handler (the others must return none).</summary>
    public PgCommand Then(PgStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var job = Job;
        try
        {
            job.Statement(statement);
        }
        catch
        {
            Abandon(job);
            throw;
        }
        return this;
    }

    /// <summary>
    /// Queues the command. <paramref name="handler"/> gets the last statement's rows with <paramref name="state"/>
    /// (null ignores them). The task completes once the command's statement ended (a read) or its transaction
    /// committed (a write), on the loop thread: continue quickly, and never block there. If
    /// <paramref name="aborted"/> fires before a loop takes the command, it ends <see cref="PgStatus.Canceled"/>
    /// without running. Await the task exactly once.
    /// </summary>
    public ValueTask<PgResult> Run(PgRowHandler? handler, object? state, CancellationToken aborted = default) =>
        _owner.Submit(Job, handler, state, aborted);

    /// <summary>
    /// Ends the command without queueing it: <see cref="PgHeld.Run"/> queues it later, once the caller knows how its
    /// rows are read, or <see cref="PgHeld.Discard"/> gives it back. A build mistake (a missing parameter) throws here.
    /// </summary>
    public PgHeld Hold()
    {
        var job = Job;
        try
        {
            job.Seal();
        }
        catch
        {
            Abandon(job);
            throw;
        }
        job.Hold();
        return new PgHeld(_owner, job, _generation);
    }

    // CodeFusion.Data's command contract, for the generated queries: each forwards to the builder above.
    void IDataCommand<PgStatement>.Int64(long value) => Int8(value);
    void IDataCommand<PgStatement>.Int32(int value) => Int4(value);
    void IDataCommand<PgStatement>.Boolean(bool value) => Bool(value);
    void IDataCommand<PgStatement>.Double(double value) => Float8(value);
    void IDataCommand<PgStatement>.Text(ReadOnlySpan<byte> utf8) => Text(utf8);
    void IDataCommand<PgStatement>.Text(ReadOnlySpan<char> text) => Text(text);
    void IDataCommand<PgStatement>.Timestamp(Timestamp value) => Timestamptz(value.UnixMicroseconds);
    void IDataCommand<PgStatement>.Null() => Null();
    void IDataCommand<PgStatement>.BeginTextArray() => BeginTextArray();
    void IDataCommand<PgStatement>.Element(ReadOnlySpan<byte> utf8) => Element(utf8);
    void IDataCommand<PgStatement>.EndTextArray() => EndTextArray();
    void IDataCommand<PgStatement>.Then(PgStatement statement) => Then(statement);
    PendingCommand IDataCommand<PgStatement>.Hold()
    {
        var held = Hold();
        return new PendingCommand(held.Owner, held.Job, held.Generation);
    }

    void IDataCommand<PgStatement>.Discard()
    {
        // a builder call that threw gave it back already
        if (_job.Generation == _generation && !_job.Submitted && !_job.Held) Abandon(_job);
    }
}

/// <summary>A command built and held (<see cref="PgCommand.Hold"/>), to run once or give back.</summary>
public readonly struct PgHeld
{
    internal readonly PgLoops Owner;
    internal readonly PgJob Job;
    internal readonly int Generation;

    internal PgHeld(PgLoops owner, PgJob job, int generation)
    {
        Owner = owner;
        Job = job;
        Generation = generation;
    }

    internal static PgHeld From(in PendingCommand command) =>
        command.Owner is PgLoops owner && command.Job is PgJob job ? new PgHeld(owner, job, command.Token)
        : throw new ArgumentException("not a Postgres command", nameof(command));

    /// <summary>The job, taken for its one run: copies of a held command may be run or given back on several
    /// threads at once, and only one of them gets it.</summary>
    PgJob Claim() => Job is not null && Job.Claim(Generation) ? Job
        : throw new InvalidOperationException("the command was already run or given back");

    /// <summary>Queues the command, as <see cref="PgCommand.Run"/> does. Await the task exactly once.</summary>
    public ValueTask<PgResult> Run(PgRowHandler? handler, object? state, CancellationToken aborted = default) =>
        Owner.Submit(Claim(), handler, state, aborted);

    /// <summary>Queues the command for CodeFusion.Data: its outcome as a <see cref="DataResult"/>.</summary>
    internal ValueTask<DataResult> RunData(PgRowHandler? handler, object? state, CancellationToken aborted) =>
        Owner.SubmitData(Claim(), handler, state, aborted);

    /// <summary>Gives the command back unrun. Nothing happens if it ran already or was given back.</summary>
    public void Discard()
    {
        if (Job is not null && Job.Claim(Generation)) Job.Discard();
    }
}

/// <summary>
/// A queued command: its Bind and Execute messages, ready to copy into a group's send, and its completion. Pooled,
/// with its buffers: <see cref="IValueTaskSource{TResult}.GetResult"/> hands it back, so steady state allocates nothing.
/// </summary>
sealed class PgJob : IValueTaskSource<PgResult>, IValueTaskSource<DataResult>
{
    // The jobs kept for reuse: a stack, so the jobs in use are the few warm ones, with their buffers grown to what they
    // carry. It keeps no more than the loops of the process can hold at once (each loops object's queue and its groups
    // in flight, see Reserve), never more than MaxPooled, and never more than were out together: a burst's jobs come
    // back and wait for the next one, so steady load makes none. And it holds at most Budget bytes, each job counted
    // whole (its object, about JobBytes, and its buffers): past that a returning job grown past SmallBuffer goes, and
    // a small one takes the place of the jobs at the bottom of the stack, the ones least recently used. The stack is a
    // ring, so they go from its bottom at no cost.
    static PgJob?[]? _pool;
    static readonly object PoolLock = new();
    static int _bottom, _pooled, _capacity;
    static long _pooledBytes;
    const int MaxPooled = 16384;
    const long Budget = 32 << 20;
    const int JobBytes = 512;
    /// <summary>A job whose buffers are past this goes straight to the shared stack, where it is counted, never into a
    /// thread's cache: so what the caches hold is at most CacheSize small jobs a thread.</summary>
    const int SmallBuffer = 4 << 10;

    /// <summary>Makes room in the pool for <paramref name="jobs"/> more (negative: gives it back), for loops that can
    /// hold that many commands at once.</summary>
    internal static void Reserve(int jobs)
    {
        lock (PoolLock)
        {
            _capacity = Math.Max(0, _capacity + jobs);
            while (_pooled > Math.Min(_capacity, MaxPooled)) DropBottom();
        }
    }

    /// <summary>Under the pool's lock: the least recently pooled job goes.</summary>
    static void DropBottom()
    {
        var job = _pool![_bottom]!;
        _pool[_bottom] = null;
        _bottom = (_bottom + 1) % MaxPooled;
        _pooled--;
        _pooledBytes -= job._kept;
        job._kept = 0;
    }

    /// <summary>Puts <paramref name="jobs"/> new jobs on the shared stack (as far as its room and budget go), made now so
    /// that steady load makes none: loops starting add what they can hold and what their threads' caches can keep
    /// aside, so a thread whose cache ran dry finds the stack never empty while other threads' caches hold jobs.</summary>
    internal static void Prewarm(int jobs)
    {
        for (int i = 0; i < jobs; i++)
        {
            var job = new PgJob();
            lock (PoolLock)
            {
                if (_pooled >= Math.Min(_capacity, MaxPooled) || _pooledBytes + job.Footprint > Budget) return;
                Push(job);
            }
        }
    }

    /// <summary>The jobs a thread's cache keeps aside, at most.</summary>
    internal const int CachedPerThread = CacheSize;

    /// <summary>What a pooled job holds, counted against <see cref="Budget"/>.</summary>
    long Footprint => JobBytes + Buf.Length + (Saved?.Length ?? 0);

    /// <summary>For tests: off, every job comes from and goes back to the shared stack, so the job a command takes is
    /// the one given back last.</summary>
    internal static bool UseThreadCaches = true;

    // Each thread keeps a few jobs of its own in front of the shared stack, and moves them to or from it a batch at a
    // time: a job is rented on a request's thread and returned on a loop's, so without it every command took the
    // shared lock twice. A thread that ends gives its jobs back (the cache's finalizer).
    const int CacheSize = 32, Batch = 16;
    [ThreadStatic] static Cache? t_cache;

    sealed class Cache
    {
        public readonly PgJob?[] Jobs = new PgJob?[CacheSize];
        public int Count;

        ~Cache()
        {
            lock (PoolLock)
                while (Count > 0)
                {
                    var job = Jobs[--Count]!;
                    Jobs[Count] = null;
                    Push(job);
                }
        }
    }

    /// <summary>Under the pool's lock: onto the top of the shared stack, the least recently pooled job going if it is
    /// full, so the job just returned (the warm one) is the next taken. Past the budget a job grown past
    /// <see cref="SmallBuffer"/> goes itself (a job grown for one large command keeps its buffers through every small
    /// one after, so letting those go is what brings the pool back under it, and costs no allocation); a small one
    /// takes the place of the least recently pooled.</summary>
    static void Push(PgJob job)
    {
        int room = Math.Min(_capacity, MaxPooled);
        long bytes = job.Footprint;
        if (room == 0 || _pooledBytes + bytes > Budget && (job.Buf.Length > SmallBuffer || job.Saved is { Length: > SmallBuffer } || bytes > Budget)) return;
        if (_pooled >= room) DropBottom();
        while (_pooledBytes + bytes > Budget) DropBottom();
        _pool ??= new PgJob?[MaxPooled];
        _pool[(_bottom + _pooled++) % MaxPooled] = job;
        _pooledBytes += job._kept = bytes;
    }

    static PgJob? Pop()
    {
        if (_pooled == 0) return null;
        int top = (_bottom + --_pooled) % MaxPooled;
        var job = _pool![top]!;
        _pool[top] = null;
        _pooledBytes -= job._kept;
        job._kept = 0;
        return job;
    }

    /// <summary>An empty cache takes a batch from the shared stack (none when it is empty: the job is made anew).</summary>
    static void Refill(Cache cache)
    {
        lock (PoolLock)
            while (cache.Count < Batch && Pop() is { } job) cache.Jobs[cache.Count++] = job;
    }

    /// <summary>A full cache gives a batch to the shared stack; past its limit they go.</summary>
    static void Spill(Cache cache)
    {
        lock (PoolLock)
            for (int i = 0; i < Batch; i++)
            {
                var job = cache.Jobs[--cache.Count]!;
                cache.Jobs[cache.Count] = null;
                Push(job);
            }
    }

    /// <summary>What this job counts against <see cref="Budget"/> while it is on the shared stack.</summary>
    long _kept;
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    /// <summary>Buffers past this size go with a returned job instead of being pooled.</summary>
    const int MaxPooledBytes = 64 << 10;

    // Completions run where SetResult is called, the loop thread: no thread-pool work item per command.
    // Core's race-free core (InlineValueTaskSourceCore says why): a caller that reads a result and builds its next
    // command on the same pooled job can't get this one's completion
    InlineValueTaskSourceCore<PgResult> _core;

    /// <summary>Changes each time the job goes back to the pool, so a stale <see cref="PgCommand"/> can't touch it.</summary>
    internal int Generation;
    /// <summary>Queued (or done): no more building, no second run.</summary>
    internal bool Submitted;
    /// <summary>Built and held (<see cref="PgCommand.Hold"/>): no more building, one run or a discard.</summary>
    internal bool Held;
    /// <summary>While held and not yet taken for its run or discard: the generation it was held at, else
    /// <see cref="Unclaimed"/>. Taken with one compare-exchange, so of two callers racing only one wins, and one with
    /// a stale <see cref="PgHeld"/> never does.</summary>
    long _heldAt = Unclaimed;
    const long Unclaimed = -1;

    /// <summary>Where a job is in its life. Debug builds check each step and the fields against it (Release ones
    /// never set it): a step out of order hands one command's rows or result to another.</summary>
    internal enum Life : byte { Pooled, Building, Held, Queued, Completed }
    Life _life;
    int _rentedGeneration;
    /// <summary>Debug: the loops it was rented for, which count it out until it is back in the pool.</summary>
    PgLoops? _owner;

    // ---- the command: Bind + Execute per statement, back to back ----
    internal byte[] Buf = new byte[512];
    internal int Len;
    internal PgStatement[] Statements = new PgStatement[4];
    /// <summary>Where each statement's messages end in <see cref="Buf"/>.</summary>
    internal int[] Ends = new int[4];
    internal int Count;
    PgStatement? _open;
    int _params, _bindAt, _arrayAt = -1, _arrayCount;
    bool _arrayNull;

    // ---- the submission ----
    internal PgRowHandler? Handler;
    internal object? State;
    internal CancellationToken Aborted;
    internal long Enqueued;

    // ---- a run ----
    internal int Rows;
    internal long Value;
    internal int Runs;
    /// <summary>The handler's exception: the command fails with it.</summary>
    internal Exception? Failure;
    /// <summary>A write's rows, kept until its commit: each as a length and a DataRow body.</summary>
    internal byte[]? Saved;
    internal int SavedLen;

    PgJob() => FillReturned(); // Debug: as a returned job's, so the first rent checks the same way

    internal static PgJob Rent(PgStatement first)
    {
        PgJob? job = null;
        if (UseThreadCaches)
        {
            var cache = t_cache ??= new Cache();
            if (cache.Count == 0) Refill(cache);
            if (cache.Count > 0)
            {
                job = cache.Jobs[--cache.Count];
                cache.Jobs[cache.Count] = null;
            }
        }
        else
            lock (PoolLock) job = Pop();
        job ??= new PgJob();
        job.CheckUnwritten();
        job.To(Life.Building);
        job.Statement(first);
        return job;
    }

    void Return()
    {
        CheckReturnable();
        Generation++;
        Submitted = false;
        Held = false;
        Handler = null;
        State = null;
        Aborted = default;
        Failure = null;
        Array.Clear(Statements, 0, Count);
        Count = Len = SavedLen = Rows = Runs = 0;
        Value = 0;
        _open = null;
        _arrayAt = -1;
        if (Buf.Length > MaxPooledBytes) Buf = new byte[512];
        if (Saved is { Length: > MaxPooledBytes }) Saved = null;
        _core.Reset();
        To(Life.Pooled);
        FillReturned();
        if (UseThreadCaches && Buf.Length <= SmallBuffer && (Saved is null || Saved.Length <= SmallBuffer))
        {
            var cache = t_cache ??= new Cache();
            if (cache.Count == CacheSize) Spill(cache);
            cache.Jobs[cache.Count++] = this;
        }
        else
            lock (PoolLock) Push(this);
    }

    /// <summary>How many jobs the shared stack may keep, and keeps now. For tests.</summary>
    internal static int Capacity
    {
        get { lock (PoolLock) return _capacity; }
    }

    internal static int Pooled
    {
        get { lock (PoolLock) return _pooled; }
    }

    /// <summary>Lets every job on the shared stack go. For tests that count allocations: what earlier tests left there
    /// (jobs grown for large commands) decides how many jobs a burst finds.</summary>
    internal static void ClearPool()
    {
        lock (PoolLock)
            while (_pooled > 0) DropBottom();
    }

    /// <summary>The bytes the shared stack's jobs hold, counted against the budget. For tests.</summary>
    internal static long PooledBytes
    {
        get { lock (PoolLock) return _pooledBytes; }
    }

    /// <summary>Back to the pool without having run (refused, cancelled at once, or the build failed).</summary>
    internal void Discard() => Return();

    /// <summary>Built: held for one later run or discard.</summary>
    internal void Hold()
    {
        Held = true;
        To(Life.Held);
        Volatile.Write(ref _heldAt, (uint)Generation);
    }

    /// <summary>Takes the held job for its run or discard; false if another caller took it, or it is no longer the
    /// generation <paramref name="generation"/> held.</summary>
    internal bool Claim(int generation) => Interlocked.CompareExchange(ref _heldAt, Unclaimed, (uint)generation) == (uint)generation;

    internal ValueTask<PgResult> Task => new(this, _core.Version);

    /// <summary>The same completion, read as CodeFusion.Data's result.</summary>
    internal ValueTask<DataResult> DataTask => new(this, _core.Version);

    // ---------- building ----------

    internal void Statement(PgStatement s)
    {
        EndStatement();
        if (Count == Statements.Length)
        {
            Array.Resize(ref Statements, Count * 2);
            Array.Resize(ref Ends, Count * 2);
        }
        Statements[Count] = s;
        _open = s;
        _params = 0;
        _bindAt = Len;
        Ensure(5 + s.BindHead.Length);
        Buf[Len] = (byte)'B';
        Len += 5; // the length goes in at the end
        s.BindHead.CopyTo(Buf, Len);
        Len += s.BindHead.Length;
    }

    void EndStatement()
    {
        if (_open is null) return;
        if (_arrayAt >= 0) throw new InvalidOperationException("a text[] parameter wasn't ended");
        if (_params != _open.ParamCount) throw new InvalidOperationException($"{_open} takes {_open.ParamCount} parameters, got {_params}");
        Ensure(4 + 10);
        I16(1);
        I16(1); // one format code for every result column: binary
        BinaryPrimitives.WriteInt32BigEndian(Buf.AsSpan(_bindAt + 1), Len - _bindAt - 1);
        Buf[Len++] = (byte)'E'; // Execute the unnamed portal, every row
        I32(9);
        Buf[Len++] = 0;
        I32(0);
        Ends[Count++] = Len;
        _open = null;
    }

    /// <summary>Closes the last statement before the job is queued.</summary>
    internal void Seal() => EndStatement();

    void Param(int len)
    {
        if (_open is null) throw new InvalidOperationException("a parameter before its statement");
        if (_arrayAt >= 0) throw new InvalidOperationException("a parameter inside a text[]");
        if (_params == _open.ParamCount) throw new InvalidOperationException($"{_open} takes {_open.ParamCount} parameters");
        _params++;
        Ensure(4 + Math.Max(len, 0));
        I32(len);
    }

    internal void Int8(long v)
    {
        Param(8);
        BinaryPrimitives.WriteInt64BigEndian(Buf.AsSpan(Len), v);
        Len += 8;
    }

    internal void Int4(int v)
    {
        Param(4);
        BinaryPrimitives.WriteInt32BigEndian(Buf.AsSpan(Len), v);
        Len += 4;
    }

    internal void Bool(bool v)
    {
        Param(1);
        Buf[Len++] = v ? (byte)1 : (byte)0;
    }

    internal void Float8(double v)
    {
        Param(8);
        BinaryPrimitives.WriteDoubleBigEndian(Buf.AsSpan(Len), v);
        Len += 8;
    }

    /// <summary>Microseconds since 1970 as Postgres's binary timestamptz: microseconds since 2000-01-01 UTC, with
    /// ±infinity as Postgres keeps them (long.MaxValue and long.MinValue, as <see cref="PgRow"/> reads them back).</summary>
    internal void Timestamptz(long unixMicroseconds)
    {
        const long UnixToPgMicros = 946684800000000;
        long pg = unixMicroseconds switch
        {
            long.MaxValue or long.MinValue => unixMicroseconds,
            // where the offset would reach -infinity or wrap, far before 4714 BC: one past -infinity, which Postgres
            // refuses as out of range like every other instant before its first
            <= long.MinValue + UnixToPgMicros => long.MinValue + 1,
            _ => unixMicroseconds - UnixToPgMicros,
        };
        Param(8);
        BinaryPrimitives.WriteInt64BigEndian(Buf.AsSpan(Len), pg);
        Len += 8;
    }

    internal void Null() => Param(-1);

    internal void Text(ReadOnlySpan<byte> utf8)
    {
        Param(utf8.Length);
        utf8.CopyTo(Buf.AsSpan(Len));
        Len += utf8.Length;
    }

    internal void Text(ReadOnlySpan<char> s)
    {
        // encoded past the end before anything moves: a lone surrogate throws and leaves the command as it was
        Ensure(checked(4 + s.Length * 3));
        int n = Utf8.GetBytes(s, Buf.AsSpan(Len + 4));
        Param(n);
        Len += n;
    }

    internal void BeginTextArray()
    {
        Param(0);
        _arrayAt = Len;
        _arrayCount = 0;
        _arrayNull = false;
        Ensure(20);
        I32(1); // one dimension
        I32(0); // has nulls: set at the end
        I32((int)PgType.Text);
        I32(0); // its length: set at the end
        I32(1); // lower bound
    }

    internal void Element(ReadOnlySpan<byte> utf8)
    {
        InArray();
        Ensure(4 + utf8.Length);
        I32(utf8.Length);
        utf8.CopyTo(Buf.AsSpan(Len));
        Len += utf8.Length;
        _arrayCount++;
    }

    internal void Element(ReadOnlySpan<char> s)
    {
        InArray();
        Ensure(checked(4 + s.Length * 3));
        // encoded before anything moves: a lone surrogate throws and leaves the buffer as it was
        int n = Utf8.GetBytes(s, Buf.AsSpan(Len + 4));
        I32(n);
        Len += n;
        _arrayCount++;
    }

    internal void NullElement()
    {
        InArray();
        Ensure(4);
        I32(-1);
        _arrayNull = true;
        _arrayCount++;
    }

    /// <summary>An element may come; it counts once it is in the buffer.</summary>
    void InArray()
    {
        if (_arrayAt < 0) throw new InvalidOperationException("an element outside a text[]");
    }

    internal void EndTextArray()
    {
        if (_arrayAt < 0) throw new InvalidOperationException("no text[] to end");
        var head = Buf.AsSpan(_arrayAt);
        if (_arrayCount == 0)
        {
            // an empty array has no dimensions: 12 bytes of header, no length or lower bound
            BinaryPrimitives.WriteInt32BigEndian(head, 0);
            Len -= 8;
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(head[4..], _arrayNull ? 1 : 0);
            BinaryPrimitives.WriteInt32BigEndian(head[12..], _arrayCount);
        }
        BinaryPrimitives.WriteInt32BigEndian(Buf.AsSpan(_arrayAt - 4), Len - _arrayAt);
        _arrayAt = -1;
    }

    void I16(short v)
    {
        BinaryPrimitives.WriteInt16BigEndian(Buf.AsSpan(Len), v);
        Len += 2;
    }

    void I32(int v)
    {
        BinaryPrimitives.WriteInt32BigEndian(Buf.AsSpan(Len), v);
        Len += 4;
    }

    void Ensure(int n)
    {
        if (Buf.Length - Len >= n) return;
        // a Bind message's length is an int32, and Postgres takes at most 1 GB per message
        long want = Math.Max((long)Buf.Length * 2, (long)Len + n);
        if (want > 1 << 30) throw new InvalidOperationException("a command over 1 GB");
        Array.Resize(ref Buf, (int)want);
    }

    internal int Start(int statement) => statement == 0 ? 0 : Ends[statement - 1];

    // ---------- a write's kept rows ----------

    internal void Save(ReadOnlySpan<byte> row)
    {
        CheckIs(Life.Queued);
        int need = SavedLen + 4 + row.Length;
        if (Saved is null || Saved.Length < need) Array.Resize(ref Saved, Math.Max(need, Math.Max(256, (Saved?.Length ?? 0) * 2)));
        BinaryPrimitives.WriteInt32BigEndian(Saved.AsSpan(SavedLen), row.Length);
        row.CopyTo(Saved.AsSpan(SavedLen + 4));
        SavedLen = need;
    }

    /// <summary>Forgets what an earlier attempt read: the write runs again.</summary>
    internal void Restart()
    {
        CheckIs(Life.Queued);
        SavedLen = 0;
        Rows = 0;
        Value = 0;
        Failure = null;
    }

    // ---------- completion ----------

    /// <summary>Completes the command. The caller continues here, on this thread; after this the job isn't ours.</summary>
    internal void Complete(in PgResult result)
    {
        // a second completion would complete a recycled job, someone else's command
        To(Life.Completed);
        try
        {
            _core.SetResult(result);
        }
        catch (Exception e)
        {
            // a continuation that throws (not one of an async method, whose exceptions go to its task) must not end
            // the loop; nor may a second completion, which the core refuses
            PgLoops.UnexpectedOnThisThread(e);
        }
    }

    /// <summary>The outcome of a run that reached its statement's end.</summary>
    internal PgResult Answer()
    {
        CheckIs(Life.Queued);
        return Failure is null
            ? new PgResult(PgStatus.Ok, Rows, Value, Runs)
            : new PgResult(PgStatus.HandlerFailed, Rows, Value, Runs, Failure);
    }

    // ---------- Debug checks: each call compiles away in Release ----------

    /// <summary>Moves the job to <paramref name="next"/>, if its life allows that step, and checks its fields.</summary>
    [Conditional("DEBUG")]
    internal void To(Life next)
    {
        bool allowed = (_life, next) switch
        {
            (Life.Pooled, Life.Building) => true,
            (Life.Building, Life.Held) or (Life.Building, Life.Queued) or (Life.Held, Life.Queued) => true,
            (Life.Queued, Life.Completed) => true,
            (not Life.Pooled, Life.Pooled) => true, // read (Completed), or discarded unrun
            _ => false,
        };
        if (!allowed)
            throw new InvalidOperationException(next == Life.Completed
                ? $"a Postgres job was completed while {_life} (twice, or after its return)"
                : $"a Postgres job went from {_life} to {next}");
        if (next == Life.Building) _rentedGeneration = Generation;
        _life = next;
        CheckInvariants();
    }

    [Conditional("DEBUG")]
    void CheckReturnable()
    {
        if (_life == Life.Pooled) throw new InvalidOperationException("a Postgres job was returned twice");
    }

    /// <summary>The loop touches a job's run only while it is queued: after its completion it may be someone else's.</summary>
    [Conditional("DEBUG")]
    internal void CheckIs(Life life)
    {
        if (_life != life) throw new InvalidOperationException($"a Postgres job was used while {_life}, not {life}");
        CheckInvariants();
    }

    /// <summary>Submitted, Held and Generation agree with where the job is.</summary>
    [Conditional("DEBUG")]
    void CheckInvariants()
    {
        bool ok = _life switch
        {
            Life.Pooled => !Submitted && !Held && Handler is null && State is null && Failure is null && Count == 0 && Len == 0 && _open is null,
            Life.Building => !Submitted && !Held && Generation == _rentedGeneration,
            Life.Held => Held && !Submitted && _open is null && Generation == _rentedGeneration,
            _ => Submitted && _open is null && Count > 0 && Generation == _rentedGeneration,
        };
        if (!ok)
            throw new InvalidOperationException($"a Postgres job {_life} with Submitted={Submitted}, Held={Held}, Generation={Generation} (rented at {_rentedGeneration}), {Count} statements");
    }

    /// <summary>Debug: counts the job out against <paramref name="loops"/> until it is back in the pool.</summary>
    [Conditional("DEBUG")]
    internal void RentedBy(PgLoops loops)
    {
        _owner = loops;
        Interlocked.Increment(ref loops.JobsOut);
    }

    /// <summary>Fills a returned job's buffers with 0xDD, so a reader that kept one sees garbage, and lets its loops
    /// know it is back.</summary>
    [Conditional("DEBUG")]
    void FillReturned()
    {
        Buf.AsSpan().Fill(Returned);
        Saved?.AsSpan().Fill(Returned);
        if (_owner is { } owner) Interlocked.Decrement(ref owner.JobsOut);
        _owner = null;
    }

    /// <summary>A job's buffers sat in the pool untouched: anything but 0xDD was written after its return.</summary>
    [Conditional("DEBUG")]
    void CheckUnwritten()
    {
        if (Buf.AsSpan().IndexOfAnyExcept(Returned) >= 0 || (Saved is not null && Saved.AsSpan().IndexOfAnyExcept(Returned) >= 0))
            throw new InvalidOperationException("a pooled Postgres job's buffer was written to after its return");
    }

    const byte Returned = 0xDD;

    PgResult IValueTaskSource<PgResult>.GetResult(short token)
    {
        PgResult result = _core.GetResult(token); // throws for a stale token, before the job could go back twice
        Return();
        return result;
    }

    ValueTaskSourceStatus IValueTaskSource<PgResult>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<PgResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);

    DataResult IValueTaskSource<DataResult>.GetResult(short token)
    {
        PgResult result = _core.GetResult(token);
        Return();
        return Map(result);
    }

    ValueTaskSourceStatus IValueTaskSource<DataResult>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<DataResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);

    /// <summary>A Postgres outcome as CodeFusion.Data's: the SQLSTATEs a caller acts on become statuses.</summary>
    internal static DataResult Map(in PgResult r) => r.Status switch
    {
        PgStatus.Ok => new DataResult(DataStatus.Ok, r.Rows, r.Value),
        PgStatus.Busy => DataResult.Of(DataStatus.Busy),
        PgStatus.Canceled => DataResult.Of(DataStatus.Canceled),
        PgStatus.Stopped => DataResult.Of(DataStatus.Stopped),
        PgStatus.Broken => new DataResult(DataStatus.Broken, r.Rows, r.Value, r.Exception),
        PgStatus.HandlerFailed => new DataResult(DataStatus.ReaderFailed, r.Rows, r.Value, r.Exception),
        _ => new DataResult(r.SqlState switch
        {
            PgErrorCodes.UniqueViolation => DataStatus.UniqueViolation,
            PgErrorCodes.LockNotAvailable => DataStatus.LockTimeout,
            PgErrorCodes.QueryCanceled => DataStatus.StatementTimeout,
            _ => DataStatus.Failed,
        }, r.Rows, r.Value, r.Exception, r.SqlState),
    };
}
