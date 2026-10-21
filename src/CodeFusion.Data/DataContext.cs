using System;
using System.ComponentModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace CodeFusion.Data;

/// <summary>
/// The base of a data context: a <c>partial</c> class with a <see cref="Table{T}"/> property per table and query
/// methods whose bodies define their queries in LINQ. The query generator compiles each query method at build
/// time and takes over every call to it, so the body never runs. Derive from <see cref="DataContext{TProvider}"/>,
/// which names the provider.
/// <para>
/// A context is per connection (<c>AddDatabase</c> registers it so) and is bound to each request before its handler
/// runs: a reply writes into that request's response, and an awaited row is copied into its arena. It is not
/// thread-safe, as a connection's requests run one after another.
/// </para>
/// </summary>
public abstract class DataContext : IRequestScoped
{
    DataSource? _source;
    IResponse? _response;
    ReplyRun? _reply;
    WriteRun? _write;
    object?[] _slots = [];
    object?[] _lists = [];
    // Lists lent to the bound request's awaited results: they hold its rows until the next request binds.
    RowState[] _lent = new RowState[2];
    int _lentCount;
    // Commands built by a query call and not run yet: discarded when the next request binds, if the handler
    // dropped its result.
    Held[] _held = new Held[4];
    int _heldCount;

    private protected DataContext() { }

    /// <summary>The source this context's commands run on.</summary>
    public DataSource Source => _source ?? throw new InvalidOperationException("the context has no source: make it with DataSource.CreateContext or AddDatabase");

    /// <summary>The response of the request the context is bound to; null before the first.</summary>
    public IResponse? Response => _response;

    internal void Attach(DataSource source)
    {
        if (_source is not null) throw new InvalidOperationException("the context already has a source");
        _source = source;
    }

    /// <inheritdoc/>
    public void Bind(IResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        DiscardHeld();
        ReturnLent();
        _response = response;
    }

    /// <inheritdoc/>
    /// <remarks>The lists and arenas the request's Eaches lent go back to the pools every connection shares, so a
    /// <see cref="Rows{T}"/> must not outlive its request. Without a server that calls this, they go back at the next
    /// <see cref="Bind"/>.</remarks>
    public void Unbind() => ReturnLent();

    // ---------- for query definitions ----------
    // Instance members, though none touches the instance: a query method calls them on its context, as LINQ reads.
#pragma warning disable CA1822

    /// <summary>The table of <typeparamref name="T"/>, for a property: <c>public Table&lt;User&gt; Users =&gt; Table&lt;User&gt;();</c>.
    /// Its name is the property's in snake_case, unless the entity has <c>[Table]</c>.</summary>
    protected Table<T> Table<T>() where T : class => Data.Table<T>.Instance;

    /// <summary>Inserts <paramref name="row"/> (an object initializer naming the columns to set) into <paramref name="table"/>.</summary>
    [QueryDefinition] protected Insert<T> Insert<T>(Table<T> table, T row) where T : class => Definition.Never<Insert<T>>();

    /// <summary>Inserts a row whose values may use its own new key, the lambda's parameter: <c>n =&gt; new Article { Slug = SlugWithId(title, n.Id) }</c>.
    /// The key is drawn before the row is written.</summary>
    [QueryDefinition] protected Insert<T> Insert<T>(Table<T> table, Func<T, T> row) where T : class => Definition.Never<Insert<T>>();

    /// <summary>Inserts the rows of <paramref name="rows"/>, a query whose <c>Select</c> makes entities.</summary>
    [QueryDefinition] protected Insert<T> Insert<T>(Table<T> table, Query<T> rows) where T : class => Definition.Never<Insert<T>>();

    /// <summary>
    /// A query written in SQL, for what LINQ can't say well. Each interpolation hole is a parameter, bound at build
    /// time: <c>Sql&lt;ProfileView&gt;($"SELECT ... WHERE username = {name}")</c>, a constant string's too (it is a
    /// value, never SQL). A hole calling a <see cref="SqlTemplateAttribute"/> method is that SQL: the way to share a
    /// fragment. Its columns are <typeparamref name="T"/>'s values in declaration order, nested views flattened.
    /// </summary>
    [QueryDefinition] protected SqlQuery<T> Sql<T>(SqlString sql) where T : struct => Definition.Never<SqlQuery<T>>();

    /// <summary>A write in SQL that gives back nothing, alone or after another (<c>Then</c>).</summary>
    [QueryDefinition] protected Write Sql(SqlString sql) => Definition.Never<Write>();

#pragma warning restore CA1822

    // ---------- for generated code ----------

    /// <summary>Notes a command built for a result, so it is given back if the result never runs.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Hold(in PendingCommand command, QueryOps ops)
    {
        if (_heldCount == _held.Length) Array.Resize(ref _held, _held.Length * 2);
        _held[_heldCount++] = new Held(command, ops);
    }

    /// <summary>The command is running: it is no longer the context's to give back. False when it isn't held,
    /// because it ran already, or was discarded with an earlier request.</summary>
    bool Release(in PendingCommand command)
    {
        for (int i = _heldCount - 1; i >= 0; i--)
            if (ReferenceEquals(_held[i].Command.Job, command.Job) && _held[i].Command.Token == command.Token)
            {
                _held[i] = _held[--_heldCount];
                _held[_heldCount] = default;
                return true;
            }
        return false;
    }

    void DiscardHeld()
    {
        while (_heldCount > 0)
        {
            var h = _held[--_heldCount];
            _held[_heldCount] = default;
            h.Ops.Discard(h.Command);
        }
    }

    readonly record struct Held(PendingCommand Command, QueryOps Ops);

    static void NotHeld() =>
        throw new InvalidOperationException("the result was used already, or outlived its request: run each result once, in the request that made it");

    /// <summary>Writes a result into <paramref name="response"/>.</summary>
    internal ValueTask Reply(IResponse response, in PendingCommand command, QueryOps ops, in ReplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!Release(command)) NotHeld();
        var reply = _reply ??= new ReplyRun(this);
        return reply.Start(response, command, ops, options);
    }

    /// <summary>Runs a result for its first row, copied into the bound request's arena.</summary>
    internal RowAwaiter<T> Await<T>(in PendingCommand command, QueryOps ops, in ReplyOptions options) where T : struct
    {
        if (!Release(command)) NotHeld();
        if (_response is not { } response)
        {
            ops.Discard(command);
            throw new InvalidOperationException("awaiting a result needs a request: bind the context first");
        }
        RowSlot<T> slot;
        if (_eachArena is { } eachArena)
        {
            // a read of an Each: it may finish on a provider thread while another read of it finishes on another, so
            // its row's text goes into the Each's own arena, and its slot is one the Each took from the Each pool
            if (_eachSlots is RowSlot<T>?[] slots && _eachSlot < slots.Length && slots[_eachSlot] is { } next)
            {
                slot = next;
                _eachSlot++;
            }
            else slot = EachPool<T>.RentSlot(); // a read awaiting more than one result: the GC takes the extra slot
            slot.Start(eachArena);
        }
        else
        {
            slot = RentSlot<T>();
            slot.Start(response.Arena);
        }
        return new RowAwaiter<T>(this, slot, ops, options, ops.RunCopy(command, slot, response.Aborted));
    }

    /// <summary>Runs a result for every row, copied into the bound request's arena.</summary>
    internal RowsAwaiter<T> AwaitRows<T>(in PendingCommand command, QueryOps ops, in ReplyOptions options)
    {
        if (!Release(command)) NotHeld();
        if (_response is not { } response)
        {
            ops.Discard(command);
            throw new InvalidOperationException("awaiting a result needs a request: bind the context first");
        }
        var list = RentList<T>();
        list.Start(response.Arena);
        if (_lentCount == _lent.Length) Array.Resize(ref _lent, _lent.Length * 2);
        _lent[_lentCount++] = list;
        return new RowsAwaiter<T>(this, list, ops, options, ops.RunCopy(command, list, response.Aborted));
    }

    /// <summary>Starts <paramref name="count"/> reads together and gathers their rows (<c>Each</c>).</summary>
    internal ValueTask<Rows<T>> RunEach<TContext, T>(int count, Func<TContext, int, One<T>> read) where TContext : DataContext where T : struct
    {
        if (_response is null) throw new InvalidOperationException("awaiting a result needs a request: bind the context first");
        // the list, the run, the reads' slots and the arena for their text come from pools shared by every context,
        // so a connection keeps none of them between requests; the list and the arena are lent until the request ends
        var list = EachLists<T>.Rent();
        list.Start(null!);
        Lend(list);
        if (count == 0)
        {
            list.Taken = true;
            return new ValueTask<Rows<T>>(new Rows<T>(list));
        }
        var arena = EachArena.Rent();
        Lend(arena);
        var run = RunPool<EachRun<T>>.Rent();
        _eachArena = arena;
        _eachSlots = run.RentSlots(count);
        _eachSlot = 0;
        try
        {
            return run.Start((TContext)this, list, arena, count, read);
        }
        finally
        {
            _eachArena = null;
            _eachSlots = null;
        }
    }

    void Lend(RowState state)
    {
        if (_lentCount == _lent.Length) Array.Resize(ref _lent, _lent.Length * 2);
        _lent[_lentCount++] = state;
    }

    /// <summary>An Each is starting its reads: their rows' text goes into its arena.</summary>
    EachArena? _eachArena;

    /// <summary>Its reads' slots (a <c>RowSlot&lt;T&gt;[]</c>), and the next to use.</summary>
    object? _eachSlots;
    int _eachSlot;

    /// <summary>Runs a write that gives back nothing, for its outcome. Its state is its own: a reply may be reading
    /// its rows meanwhile, into the reply's.</summary>
    internal ValueTask<DataResult> Run(in PendingCommand command, QueryOps ops)
    {
        if (!Release(command)) NotHeld();
        var state = _write ??= new WriteRun();
        state.Start();
        return ops.RunJson(command, state, _response?.Aborted ?? default);
    }

    /// <summary>The state of an awaited write that gives back nothing: its reader reads no rows.</summary>
    sealed class WriteRun : RowState
    {
        public void Start() => StartRun();
    }

    // One free list of slots per row type, indexed by the type's number: an awaited row costs no allocation once
    // the context has a slot for its type.
    static int _slotTypes;

    static class SlotIndex<T>
    {
        public static readonly int Value = Interlocked.Increment(ref _slotTypes) - 1;
    }

    RowSlot<T> RentSlot<T>() where T : struct
    {
        int i = SlotIndex<T>.Value;
        if (i >= _slots.Length) Array.Resize(ref _slots, Math.Max(i + 1, _slots.Length * 2));
        if (_slots[i] is RowSlot<T> slot)
        {
            _slots[i] = slot.Next;
            slot.Next = null;
            _slotCounts[i]--;
            return slot;
        }
        return new RowSlot<T>();
    }

    internal void ReturnSlot<T>(RowSlot<T> slot) where T : struct
    {
        int i = SlotIndex<T>.Value;
        if (i >= _slotCounts.Length) Array.Resize(ref _slotCounts, _slots.Length);
        // an Each of hundreds of reads lends as many slots: past KeptSlots of a type they go, so a connection
        // doesn't keep its largest Each's slots (and their arenas) for good
        if (_slotCounts[i] >= KeptSlots) return;
        _slotCounts[i]++;
        slot.Next = (RowSlot<T>?)_slots[i];
        _slots[i] = slot;
    }

    const int KeptSlots = 64;
    int[] _slotCounts = [];

    /// <summary>The slots of <typeparamref name="T"/> on the free list. For tests.</summary>
    internal int FreeSlots<T>() where T : struct
    {
        int i = SlotIndex<T>.Value;
        int n = 0;
        for (var slot = i < _slots.Length ? _slots[i] as RowSlot<T> : null; slot is not null; slot = slot.Next) n++;
        return n;
    }

    // Lists have free lists of their own, by the same type numbers.
    static int _listTypes;

    static class ListIndex<T>
    {
        public static readonly int Value = Interlocked.Increment(ref _listTypes) - 1;
    }

    RowList<T> RentList<T>()
    {
        int i = ListIndex<T>.Value;
        if (i >= _lists.Length) Array.Resize(ref _lists, Math.Max(i + 1, _lists.Length * 2));
        if (_lists[i] is RowList<T> list)
        {
            _lists[i] = list.Next;
            list.Next = null;
            return list;
        }
        return new RowList<T>();
    }

    void ReturnList<T>(RowList<T> list)
    {
        int i = ListIndex<T>.Value;
        list.Next = (RowList<T>?)_lists[i];
        _lists[i] = list;
    }

    /// <summary>The previous request's lists go back to their free lists. One its handler never took may still be
    /// filling, so it is let go instead.</summary>
    void ReturnLent()
    {
        while (_lentCount > 0)
        {
            var state = _lent[--_lentCount];
            _lent[_lentCount] = null!;
            state.ReturnTo(this);
        }
    }

    internal void ReturnLentList<T>(RowList<T> list)
    {
        if (!list.Taken) return;
        list.Clear();
        if (list.Pooled) EachLists<T>.Return(list);
        else ReturnList(list);
    }

    /// <summary>
    /// A reply in progress: the response, the query, and its run's continuation, made once per context. A reply that
    /// waits for its rows returns a ValueTask backed by this, so it allocates nothing.
    /// </summary>
    sealed class ReplyRun : RowState, IValueTaskSource
    {
        readonly DataContext _context;
        readonly Action _onRun;
        // Core's race-free core: the runtime's can land a completion in the next reply when the handler reads this
        // one's result and starts the next in the window between marking it ready and claiming the continuation slot
        InlineValueTaskSourceCore<bool> _core;
        IResponse? _response;
        QueryOps? _ops;
        ReplyOptions _options;
        ValueTask<DataResult> _run;
        bool _busy;

        public ReplyRun(DataContext context)
        {
            _context = context;
            _onRun = OnRun;
        }

        public ValueTask Start(IResponse response, in PendingCommand command, QueryOps ops, in ReplyOptions options)
        {
            if (_busy)
            {
                ops.Discard(command);
                throw new InvalidOperationException("the context is writing another reply: a connection writes one response at a time");
            }
            var json = response.Json;
            StartRun();
            Json = json;
            _response = response;
            _ops = ops;
            _options = options;
            ops.Begin(json);
#pragma warning disable CA2012 // kept to be read once: at once if it completed, else by its continuation
            ValueTask<DataResult> run;
            try
            {
                run = ops.RunJson(command, this, response.Aborted);
            }
#pragma warning restore CA2012
            catch
            {
                Clear();
                throw;
            }
            if (run.IsCompleted)
            {
                // refused at once (busy, canceled) or answered already: no waiting
                try
                {
#pragma warning disable CA2012 // completed: IsCompleted was checked, and this is its one read
                    Finish(run.Result);
#pragma warning restore CA2012
                }
                finally
                {
                    Clear();
                }
                return default;
            }
            _busy = true;
            _run = run;
            _core.Reset();
            run.GetAwaiter().UnsafeOnCompleted(_onRun);
            return new ValueTask(this, _core.Version);
        }

        void OnRun()
        {
            try
            {
#pragma warning disable CA2012 // the run's continuation: it completed, and this is its one read
                Finish(_run.Result);
#pragma warning restore CA2012
                Clear();
                _core.SetResult(true);
            }
            catch (Exception e)
            {
                Clear();
                _core.SetException(e);
            }
        }

        void Finish(in DataResult r)
        {
            var response = _response!;
            var ops = _ops!;
            var errors = _context.Source.Errors;
            ErrorReply? error = null;
            if (r.Status != DataStatus.Ok)
            {
                error = errors.For(r.Status, ops.IsWrite, _options.Conflict);
                if (DataSource.Reports(r.Status) && !response.Aborted.IsCancellationRequested)
                    _context.Source.OnFailure?.Invoke(ops.Name, r);
            }
            else if (ops.Kind == ResultKind.One && (r.Rows == 0 || r.Value == Refused))
                error = r.Rows == 0 ? _options.Empty ?? errors.NotFound : _options.Refused ?? errors.Forbidden;
            else if (ops.Kind == ResultKind.Many && r.Rows == 0 && ops.HasSource)
                error = _options.Empty ?? errors.NotFound;
            if (error is not null)
            {
#pragma warning disable CA2012 // an ErrorReply writes its bytes and completes at once
                error.WriteAsync(response).GetAwaiter().GetResult();
#pragma warning restore CA2012
                return;
            }
            if (ops.Kind == ResultKind.Write)
            {
                response.Reset();
                response.Status = _options.Status != 0 ? _options.Status : ops.Status;
                return;
            }
            var json = Json;
            ops.End(json, r);
            json.Flush();
            response.Status = _options.Status != 0 ? _options.Status : ops.Status;
        }

        void Clear()
        {
            _busy = false;
            _response = null;
            _ops = null;
            _run = default;
            _options = default;
            Json = null!;
        }

        void IValueTaskSource.GetResult(short token) => _core.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

        void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);
    }
}

/// <summary>A data context on <typeparamref name="TProvider"/>, the provider the generator compiles its queries for.</summary>
/// <typeparam name="TProvider">The provider struct, such as <c>CodeFusion.Data.Postgres.Postgres</c>.</typeparam>
public abstract class DataContext<TProvider> : DataContext where TProvider : struct, IDataProvider
{
    /// <summary>For the derived context.</summary>
    // Apps derive from this one, never from DataContext itself, whose protected members are the queries' vocabulary.
#pragma warning disable RS0022
    protected DataContext() { }
#pragma warning restore RS0022
}

/// <summary>The replies a result gives instead of its rows, set by OrNotFound and its kin.</summary>
readonly record struct ReplyOptions(ErrorReply? Empty, ErrorReply? Refused, ErrorReply? Conflict, int Status);
