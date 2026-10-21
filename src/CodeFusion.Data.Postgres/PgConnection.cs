using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodeFusion.Data.Postgres;

/// <summary>
/// One connection, owned by one loop thread: it sends a group's statements as one batch ending in one Sync, then
/// parses the replies in place as they arrive. Statements are parsed once per connection (named) and afterwards
/// only bound and executed, with every parameter and column in binary. Every reply is checked against what was
/// sent, so a desynchronised or hostile stream breaks the connection instead of answering the wrong command.
/// </summary>
sealed class PgConnection : IDisposable
{
    /// <summary>The largest message accepted (Postgres's own limit for a value is 1 GB).</summary>
    internal const int MaxMessage = 1 << 30;
    /// <summary>A batch up to this size goes out in one blocking send: it fits the socket buffers, so it can't wait
    /// on Postgres. A larger one is sent so that the replies are taken in whenever the socket takes no more.</summary>
    const int SendPiece = 64 << 10;
    /// <summary>Receive buffers grown past this for a huge row go back to the normal size afterwards.</summary>
    const int MaxKeptReceive = 8 << 20;

    /// <summary>Returned by <see cref="ReadReplies"/> when every statement succeeded and the commit failed.</summary>
    internal const int CommitFailed = -2;

    readonly PgTransport _t;
    readonly PgSettings _s;

    byte[] _send = new byte[16 << 10];
    int _sendLen;
    byte[] _recv;
    int _rpos, _rend, _need;

    /// <summary>By statement id: 0 not parsed here, 1 Parse sent and not answered yet, 2 parsed.</summary>
    byte[] _prepared = new byte[64];
    /// <summary>By statement id: its columns' types (pg_type OIDs), from the RowDescription that answered its Describe.</summary>
    uint[]?[] _columns = new uint[]?[64];
    /// <summary>The steps of the batch whose replies are being read.</summary>
    Step[] _steps = new Step[64];
    int _stepCount;
    /// <summary>The steps of the batch being built (Begin to Flush).</summary>
    Step[] _build = new Step[64];
    int _buildCount;
    /// <summary>The batches sent and not yet read, oldest first: Postgres answers them in order.</summary>
    Step[][] _sent = [new Step[64], new Step[64], new Step[64], new Step[64]];
    int[] _sentCounts = new int[4];
    int _sentHead, _sentCount;
    /// <summary>Jobs whose statement ended in the current receive buffer, answered before the next receive.</summary>
    int[] _answered = new int[32];
    int _answeredCount;

    internal int[] ColumnStart = new int[32], ColumnLength = new int[32];

    /// <summary>The server's process id: pg_terminate_backend and pg_cancel_backend take it.</summary>
    public int ProcessId { get; private set; }

    /// <summary>One statement sent in the current batch.</summary>
    struct Step
    {
        public int Slot;
        /// <summary>A merged statement's: the slots it ran for are <see cref="Slot"/> to this (exclusive); else 0.</summary>
        public int BatchEnd;
        public int StatementId;
        public bool Parse;
        public bool Last;
    }

    /// <summary>The failure <see cref="ReadReplies"/> last returned was a merged statement's: the slot it named is the
    /// run's first, not necessarily the write whose values failed.</summary>
    internal bool FailedInBatch;

    /// <summary>Where each parameter of each write of a merged run starts in its job's buffer: scratch, kept.</summary>
    int[] _offsets = new int[64];

    /// <summary>Takes in the replies waiting while a large batch goes out.</summary>
    readonly Action _takeIn;

    PgConnection(PgTransport t, PgSettings s)
    {
        _t = t;
        _s = s;
        _recv = new byte[Math.Max(64, s.ReceiveBufferSize)];
        _takeIn = () =>
        {
            while (Receive(wait: false) > 0) { }
        };
    }

    public void Dispose()
    {
        try
        {
            // Terminate: the server ends the session at once instead of noticing the closed socket later
            if (ProcessId != 0) _t.Send([(byte)'X', 0, 0, 0, 4]);
        }
        catch (Exception e)
        {
            // already gone; anything else than a connection failing is a bug
            if (!PgLoops.Expected(e)) PgLoops.UnexpectedOnThisThread(e);
        }
        _t.Dispose();
    }

    // ---------- startup ----------

    public static PgConnection Open(PgSettings s)
    {
        if (s.Transport is null) s.CheckTlsEnvironment();
        // disposed below if the connection can't start; once it has, the connection owns it
#pragma warning disable CA2000
        var t = s.Transport is { } open ? open(s) : SocketTransport.Connect(s);
#pragma warning restore CA2000
        try
        {
            var c = new PgConnection(t, s);
            c.Startup();
            t.Started();
            return c;
        }
        catch
        {
            t.Dispose();
            throw;
        }
    }

    void Startup()
    {
        // a NUL would end the name early and turn the rest into startup parameters of its own
        if (_s.User.Contains('\0', StringComparison.Ordinal) || _s.Database.Contains('\0', StringComparison.Ordinal))
            throw new PgConnectionException("the user or database name holds NUL");
        var w = new Builder();
        w.I32(0);
        w.I32(196608); // protocol 3.0
        w.CStr("user"); w.CStr(_s.User);
        w.CStr("database"); w.CStr(_s.Database);
        w.CStr("client_encoding"); w.CStr("UTF8");
        foreach (var (name, value) in _s.Parameters)
        {
            // the list is the app's to fill, past Set: a NUL would start keys of its own (user, options)
            if (PgSettings.Refused(name, value) is { } why) throw new PgConnectionException("startup parameter refused: " + why);
            w.CStr(name);
            w.CStr(value);
        }
        w.Byte(0);
        w.PatchLength(0);
        _t.Send(w.Span);

        PgScram? scram = null;
        bool asked = false;
        try
        {
            Handshake(ref scram, ref asked);
        }
        finally
        {
            scram?.Dispose(); // wipes the password's bytes should the exchange end before the proof
        }
    }

    /// <summary>The server's side of the startup, to ReadyForQuery: authentication, its key and parameters.</summary>
    void Handshake(ref PgScram? scram, ref bool asked)
    {
        while (true)
        {
            byte type;
            ReadOnlySpan<byte> body;
            while (!TryNext(out type, out body)) Fill();
            switch (type)
            {
                case (byte)'R':
                    Authenticate(body, ref scram, ref asked);
                    break;
                case (byte)'E':
                    throw ParseError(body); // wrong password, no such database or user: FATAL
                case (byte)'K':
                    if (body.Length < 8) throw Protocol("malformed BackendKeyData");
                    ProcessId = BinaryPrimitives.ReadInt32BigEndian(body);
                    break;
                case (byte)'S':
                    CheckParameter(body);
                    break;
                case (byte)'N':
                    break;
                case (byte)'Z':
                    if (body.Length != 1 || body[0] != (byte)'I') throw Protocol("malformed ReadyForQuery");
                    if (scram is { Verified: false }) throw Protocol("ready before SCRAM finished");
                    return;
                default:
                    throw Protocol($"unexpected message '{(char)type}' during startup");
            }
        }
    }

    /// <summary>One authentication request. A SCRAM exchange must run to the verified server signature before
    /// AuthenticationOk counts, and no other method may start once it has begun. With a password set, a server that
    /// never asks for it (trust) is refused unless weak password authentication is allowed: one posing as Postgres
    /// would get every statement and answer it.</summary>
    void Authenticate(ReadOnlySpan<byte> body, ref PgScram? scram, ref bool asked)
    {
        if (body.Length < 4) throw Protocol("malformed authentication request");
        int code = BinaryPrimitives.ReadInt32BigEndian(body);
        var data = body[4..];
        if (scram is not null && code is not (0 or 11 or 12)) throw Protocol($"authentication request {code} during SCRAM");
        switch (code)
        {
            case 0: // AuthenticationOk
                if (data.Length != 0) throw Protocol("malformed AuthenticationOk");
                if (scram is { Verified: false }) throw Protocol("AuthenticationOk without the server's SCRAM signature");
                if (!asked && _s.Password is not null && !_s.AllowWeakPasswordAuth)
                    throw new PgConnectionException("the server let the user in without asking for the password that is set; set AllowWeakPasswordAuth to allow it");
                return;
            case 3: // cleartext
                if (data.Length != 0) throw Protocol("malformed AuthenticationCleartextPassword");
                Weak("a cleartext password");
                asked = true;
                string password = Password();
                byte[] cleartext = new byte[Encoding.UTF8.GetByteCount(password) + 1];
                Encoding.UTF8.GetBytes(password, cleartext);
                SendPassword(cleartext);
                return;
            case 5: // md5
                if (data.Length != 4) throw Protocol("malformed AuthenticationMD5Password");
                Weak("an md5 password");
                asked = true;
                SendPassword(PgMd5.Response(_s.User, Password(), data));
                return;
            case 10: // SASL: the mechanisms the server offers
                bool offered = false;
                while (data.Length > 0 && data[0] != 0)
                {
                    int z = data.IndexOf((byte)0);
                    if (z < 0) throw Protocol("malformed AuthenticationSASL");
                    offered |= data[..z].SequenceEqual("SCRAM-SHA-256"u8);
                    data = data[(z + 1)..];
                }
                if (!offered) throw new PgConnectionException("the server offers no SCRAM-SHA-256");
                asked = true;
                scram = new PgScram(Password(), minIterations: _s.AllowWeakPasswordAuth ? 1 : PgScram.MinIterations);
                byte[] first = scram.ClientFirst();
                var w = new Builder();
                w.Byte((byte)'p');
                w.I32(0);
                w.CStr("SCRAM-SHA-256");
                w.I32(first.Length);
                w.Bytes(first);
                w.PatchLength(1);
                _t.Send(w.Span);
                return;
            case 11: // SASLContinue: the server-first-message
                if (scram is null || scram.Verified) throw Protocol("SASL continue out of order");
                SendPassword(scram.ClientFinal(data));
                return;
            case 12: // SASLFinal: the server's signature
                if (scram is null || scram.Verified) throw Protocol("SASL final out of order");
                scram.VerifyServerFinal(data);
                return;
            default:
                throw new PgConnectionException($"authentication method {code} isn't supported");
        }
    }

    /// <summary>Without TLS anyone in the path can pose as the server and ask for the password in the clear or as
    /// a crackable md5 hash; SCRAM gives neither away. So those two run only where the settings allow them.</summary>
    void Weak(string what)
    {
        if (!_s.AllowWeakPasswordAuth) throw new PgConnectionException($"the server asks for {what}; set AllowWeakPasswordAuth to allow it, or use SCRAM");
    }

    string Password()
    {
        string p = _s.Password ?? throw new PgConnectionException("the server asks for a password and none is set");
        if (p.Contains('\0', StringComparison.Ordinal)) throw new PgConnectionException("a password can't hold NUL");
        // a lone surrogate would go out as U+FFFD: another password than the one set
        for (int i = 0, used; i < p.Length; i += used)
            if (Rune.DecodeFromUtf16(p.AsSpan(i), out _, out used) != System.Buffers.OperationStatus.Done)
                throw new PgConnectionException("a password can't hold a lone surrogate");
        return p;
    }

    /// <summary>A PasswordMessage. Both copies of <paramref name="payload"/>, it and the message, are wiped once sent.</summary>
    void SendPassword(byte[] payload)
    {
        var w = new Builder(5 + payload.Length);
        try
        {
            w.Byte((byte)'p');
            w.I32(0);
            w.Bytes(payload);
            w.PatchLength(1);
            _t.Send(w.Span);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            w.Wipe();
        }
    }

    /// <summary>Binary timestamps are int64 microseconds only with integer_datetimes, on since Postgres 10. Text goes
    /// both ways as UTF-8, so a client_encoding set otherwise (options, a SET statement) ends the connection.</summary>
    static void CheckParameter(ReadOnlySpan<byte> body)
    {
        int z = body.IndexOf((byte)0);
        // name NUL value NUL: the name's NUL can't be the last byte
        if (z < 0 || z == body.Length - 1 || body[^1] != 0) throw Protocol("malformed ParameterStatus");
        ReadOnlySpan<byte> name = body[..z], value = body[(z + 1)..^1];
        if (name.SequenceEqual("integer_datetimes"u8) && !value.SequenceEqual("on"u8))
            throw new PgConnectionException("the server keeps timestamps as floats (integer_datetimes is off)");
        if (name.SequenceEqual("client_encoding"u8) && !value.SequenceEqual("UTF8"u8))
            throw new PgConnectionException($"client_encoding became {Encoding.UTF8.GetString(value)}; the client speaks UTF-8 only");
    }

    /// <summary>The few startup messages; built once per connection.</summary>
    sealed class Builder(int capacity = 256)
    {
        byte[] _b = new byte[capacity];
        int _n;

        public void Wipe() => CryptographicOperations.ZeroMemory(_b);

        public ReadOnlySpan<byte> Span => _b.AsSpan(0, _n);
        public void Byte(byte v) { Ensure(1); _b[_n++] = v; }
        public void I32(int v) { Ensure(4); BinaryPrimitives.WriteInt32BigEndian(_b.AsSpan(_n), v); _n += 4; }
        public void Bytes(ReadOnlySpan<byte> v) { Ensure(v.Length); v.CopyTo(_b.AsSpan(_n)); _n += v.Length; }
        public void CStr(string s) { Bytes(Encoding.UTF8.GetBytes(s)); Byte(0); }
        public void PatchLength(int at) => BinaryPrimitives.WriteInt32BigEndian(_b.AsSpan(at), _n - at);
        void Ensure(int n) { if (_b.Length - _n < n) Array.Resize(ref _b, Math.Max(_b.Length * 2, _n + n)); }
    }

    /// <summary>
    /// Whether the server ended the session while it sat idle between groups: it sends a FATAL error then
    /// (a restart, idle_session_timeout, pg_terminate_backend) and closes the socket, or the socket was reset. Takes
    /// in what is waiting without waiting for more; a notice or a parameter's new value keeps the connection.
    /// </summary>
    internal bool EndedWhileIdle()
    {
        try
        {
            while (Receive(wait: false) > 0)
                while (TryNext(out byte type, out var body))
                    switch (type)
                    {
                        case (byte)'N':
                        case (byte)'A':
                            break;
                        case (byte)'S':
                            CheckParameter(body);
                            break;
                        default:
                            return true; // an ErrorResponse (only FATAL comes unasked), or anything a server doesn't send idle
                    }
            return false;
        }
        catch (PgConnectionException)
        {
            return true; // closed, reset, or a malformed message
        }
    }

    // ---------- sending a batch ----------

    internal void Begin()
    {
        _sendLen = 0;
        _buildCount = 0;
    }

    /// <summary>Batches sent whose replies haven't been read: more than one when groups are pipelined.</summary>
    internal int InFlight => _sentCount;

    /// <summary>Whether a job of the group binds a statement whose Parse went out in a batch not answered yet. Postgres
    /// skips that Parse if a statement before it in its batch fails, so a batch binding it must not go out before
    /// that batch's replies are read.</summary>
    internal bool BindsPendingParse(PgJob?[] group, int from, int to)
    {
        for (int j = from; j < to; j++)
        {
            var job = group[j]!;
            for (int i = 0; i < job.Count; i++)
                if (job.Statements[i].Id < _prepared.Length && _prepared[job.Statements[i].Id] == 1) return true;
        }
        return false;
    }

    /// <summary>Adds the job in group slot <paramref name="slot"/>: a Parse for each statement new to this connection,
    /// then its Bind and Execute.</summary>
    internal void Add(PgJob job, int slot)
    {
        for (int i = 0; i < job.Count; i++)
        {
            var s = job.Statements[i];
            if (s.Id >= _prepared.Length)
            {
                Array.Resize(ref _prepared, Math.Max(_prepared.Length * 2, s.Id + 1));
                Array.Resize(ref _columns, _prepared.Length);
            }
            bool parse = _prepared[s.Id] == 0;
            if (parse)
            {
                Append(s.Parse);
                _prepared[s.Id] = 1;
            }
            int start = job.Start(i);
            Append(job.Buf.AsSpan(start, job.Ends[i] - start));
            if (_buildCount == _build.Length) Array.Resize(ref _build, _buildCount * 2);
            _build[_buildCount++] = new Step { Slot = slot, StatementId = s.Id, Parse = parse, Last = i == job.Count - 1 };
        }
    }

    /// <summary>
    /// Adds group slots <paramref name="from"/> to <paramref name="to"/> (exclusive), writes of one statement with a
    /// <see cref="PgStatement.Batch"/>, as one Bind and Execute of the batch statement: its parameter i is the array of
    /// every write's parameter i, in order. Each element is the write's own bytes as its Bind holds them (a length,
    /// then the value), which is how an array's elements go too.
    /// </summary>
    internal void AddBatch(PgJob?[] group, int from, int to, PgStatement batch)
    {
        if (batch.Id >= _prepared.Length)
        {
            Array.Resize(ref _prepared, Math.Max(_prepared.Length * 2, batch.Id + 1));
            Array.Resize(ref _columns, _prepared.Length);
        }
        bool parse = _prepared[batch.Id] == 0;
        if (parse)
        {
            Append(batch.Parse);
            _prepared[batch.Id] = 1;
        }
        int n = to - from, pc = batch.ParamCount, head = 5 + group[from]!.Statements[0].BindHead.Length;
        if (_offsets.Length < n * pc) _offsets = new int[Math.Max(n * pc, _offsets.Length * 2)];
        Span<int> sizes = pc <= 32 ? stackalloc int[pc] : new int[pc];
        Span<bool> nulls = pc <= 32 ? stackalloc bool[pc] : new bool[pc];
        sizes.Clear();
        nulls.Clear();
        for (int j = 0; j < n; j++)
        {
            var buf = group[from + j]!.Buf;
            int at = head;
            for (int p = 0; p < pc; p++)
            {
                _offsets[j * pc + p] = at;
                int len = BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(at));
                if (len < 0) nulls[p] = true;
                int whole = 4 + Math.Max(len, 0);
                sizes[p] += whole;
                at += whole;
            }
        }
        // Bind: its head, each array (its own length, then 1 dimension, the null flag, the element type, the count,
        // the lower bound, the elements), one result format (binary); then Execute
        int body = 4 + batch.BindHead.Length + 4;
        for (int p = 0; p < pc; p++) body += 4 + 20 + sizes[p];
        int need = 1 + body + 10;
        if (_send.Length - _sendLen < need) Array.Resize(ref _send, Math.Max(_send.Length * 2, _sendLen + need));
        var d = _send.AsSpan(_sendLen, need);
        d[0] = (byte)'B';
        BinaryPrimitives.WriteInt32BigEndian(d[1..], body);
        int w = 5;
        batch.BindHead.CopyTo(d[w..]);
        w += batch.BindHead.Length;
        var elements = batch.ElementTypes!;
        for (int p = 0; p < pc; p++)
        {
            BinaryPrimitives.WriteInt32BigEndian(d[w..], 20 + sizes[p]);
            BinaryPrimitives.WriteInt32BigEndian(d[(w + 4)..], 1);
            BinaryPrimitives.WriteInt32BigEndian(d[(w + 8)..], nulls[p] ? 1 : 0);
            BinaryPrimitives.WriteUInt32BigEndian(d[(w + 12)..], elements[p]);
            BinaryPrimitives.WriteInt32BigEndian(d[(w + 16)..], n);
            BinaryPrimitives.WriteInt32BigEndian(d[(w + 20)..], 1);
            w += 24;
            for (int j = 0; j < n; j++)
            {
                var buf = group[from + j]!.Buf;
                int at = _offsets[j * pc + p];
                int whole = 4 + Math.Max(BinaryPrimitives.ReadInt32BigEndian(buf.AsSpan(at)), 0);
                buf.AsSpan(at, whole).CopyTo(d[w..]);
                w += whole;
            }
        }
        BinaryPrimitives.WriteInt16BigEndian(d[w..], 1);
        BinaryPrimitives.WriteInt16BigEndian(d[(w + 2)..], 1);
        w += 4;
        d[w] = (byte)'E';
        BinaryPrimitives.WriteInt32BigEndian(d[(w + 1)..], 9);
        d[w + 5] = 0;
        BinaryPrimitives.WriteInt32BigEndian(d[(w + 6)..], 0);
        _sendLen += need;
        if (_buildCount == _build.Length) Array.Resize(ref _build, _buildCount * 2);
        _build[_buildCount++] = new Step { Slot = from, BatchEnd = to, StatementId = batch.Id, Parse = parse, Last = true };
    }

    /// <summary>Ends the batch with a Sync and sends it.</summary>
    internal void Flush()
    {
        Append([(byte)'S', 0, 0, 0, 4]);
        _t.Send(_send.AsSpan(0, _sendLen), _sendLen > SendPiece ? _takeIn : null);
        if (_send.Length > 1 << 20) _send = new byte[16 << 10];
        // in line behind the batches before it: its steps take a free slot's place, that slot's array is the next build's
        if (_sentCount == _sent.Length)
        {
            var grown = new Step[_sent.Length * 2][];
            var counts = new int[grown.Length];
            for (int i = 0; i < _sentCount; i++)
            {
                grown[i] = _sent[(_sentHead + i) % _sent.Length];
                counts[i] = _sentCounts[(_sentHead + i) % _sent.Length];
            }
            for (int i = _sentCount; i < grown.Length; i++) grown[i] = new Step[64];
            _sent = grown;
            _sentCounts = counts;
            _sentHead = 0;
        }
        int tail = (_sentHead + _sentCount) % _sent.Length;
        (_sent[tail], _build) = (_build, _sent[tail]);
        _sentCounts[tail] = _buildCount;
        _sentCount++;
        _buildCount = 0;
    }

    void Append(ReadOnlySpan<byte> b)
    {
        if (_send.Length - _sendLen < b.Length) Array.Resize(ref _send, Math.Max(_send.Length * 2, _sendLen + b.Length));
        b.CopyTo(_send.AsSpan(_sendLen));
        _sendLen += b.Length;
    }

    // ---------- reading the replies ----------

    /// <summary>
    /// Reads the batch's replies up to ReadyForQuery. Each statement's rows go to its job: in place to the handler
    /// (<paramref name="keep"/> false: reads, answered as soon as the statement ends) or kept in the job for after
    /// the commit (<paramref name="keep"/> true: writes). Returns -1 when every statement succeeded, else the slot
    /// of the job whose statement failed (or <see cref="CommitFailed"/>) with the server's error; Postgres skipped
    /// everything after it. A FATAL error, a broken stream or a protocol violation throws
    /// <see cref="PgConnectionException"/>: the connection is done.
    /// </summary>
    internal int ReadReplies(PgJob?[] group, bool keep, out PgException? error)
    {
        if (_sentCount == 0) throw new InvalidOperationException("no batch to read the replies of");
        // the oldest batch sent: its steps are read now, its slot free for the next Flush
        (_steps, _sent[_sentHead]) = (_sent[_sentHead], _steps);
        _stepCount = _sentCounts[_sentHead];
        _sentHead = (_sentHead + 1) % _sent.Length;
        _sentCount--;
        error = null;
        FailedInBatch = false;
        int failed = -1;
        int step = 0;
        // within a step: 0 expects ParseComplete (if a Parse went), 3 ParameterDescription and 4 RowDescription (its
        // Describe's), 1 BindComplete, 2 rows and the end
        int phase = _stepCount > 0 && _steps[0].Parse ? 0 : 1;
        _answeredCount = 0;
        try
        {
            while (true)
            {
                CheckReplies(group, step, phase, failed, error);
                if (!TryNext(out byte type, out var body))
                {
                    Answer(group);
                    Fill();
                    continue;
                }
                switch (type)
                {
                    case (byte)'D':
                        if (error is not null || step == _stepCount || phase != 2 || _steps[step].BatchEnd > 0) throw Protocol("DataRow out of place");
                        if (_steps[step].Last) Row(group[_steps[step].Slot]!, body, keep, _steps[step].StatementId);
                        break;
                    case (byte)'C': // CommandComplete
                        if (keep && ControlsTransaction(body)) throw OwnTransaction(body);
                        if (DropsStatements(body, group, step)) throw DroppedStatements(body);
                        goto case (byte)'I';
                    case (byte)'I': // EmptyQueryResponse
                        if (error is not null || step == _stepCount || phase != 2) throw Protocol("CommandComplete out of place");
                        if (_steps[step].BatchEnd > 0)
                            for (int s = _steps[step].Slot; s < _steps[step].BatchEnd; s++) Ended(group, s, keep);
                        else if (_steps[step].Last) Ended(group, _steps[step].Slot, keep);
                        step++;
                        phase = step < _stepCount && _steps[step].Parse ? 0 : 1;
                        break;
                    case (byte)'1': // ParseComplete
                        if (error is not null || step == _stepCount || phase != 0 || body.Length != 0) throw Protocol("ParseComplete out of place");
                        _prepared[_steps[step].StatementId] = 2;
                        _columns[_steps[step].StatementId] = null;
                        phase = 3;
                        break;
                    case (byte)'t': // ParameterDescription
                        if (error is not null || step == _stepCount || phase != 3) throw Protocol("ParameterDescription out of place");
                        if (body.Length < 2 || body.Length != 2 + 4 * BinaryPrimitives.ReadUInt16BigEndian(body)) throw Protocol("malformed ParameterDescription");
                        phase = 4;
                        break;
                    case (byte)'T': // RowDescription
                        if (error is not null || step == _stepCount || phase != 4) throw Protocol("RowDescription out of place");
                        _columns[_steps[step].StatementId] = ColumnTypes(body);
                        phase = 1;
                        break;
                    case (byte)'n': // NoData: a statement without rows
                        if (error is not null || step == _stepCount || phase != 4 || body.Length != 0) throw Protocol("NoData out of place");
                        _columns[_steps[step].StatementId] = [];
                        phase = 1;
                        break;
                    case (byte)'2': // BindComplete
                        if (error is not null || step == _stepCount || phase != 1 || body.Length != 0) throw Protocol("BindComplete out of place");
                        phase = 2;
                        break;
                    case (byte)'E':
                        var e = ParseError(body);
                        if (e.IsFatal) throw new PgConnectionException("Postgres ended the connection: " + e.Message, e);
                        if (error is not null) throw Protocol("a second ErrorResponse before ReadyForQuery");
                        error = e;
                        failed = step == _stepCount ? CommitFailed : _steps[step].Slot;
                        FailedInBatch = step < _stepCount && _steps[step].BatchEnd > 0;
                        break;
                    case (byte)'Z':
                        if (body.Length != 1) throw Protocol("malformed ReadyForQuery");
                        if (error is null && step != _stepCount) throw Protocol("ReadyForQuery before every statement ended");
                        // The batch ran in the implicit transaction its Sync ends, so the server is idle again unless a
                        // statement opened a transaction of its own: then nothing of the group committed and the session
                        // is in a state no later group expects
                        if (body[0] != (byte)'I')
                            throw new PgConnectionException($"a statement left a transaction open (ReadyForQuery '{(char)body[0]}'): nothing of its group committed");
                        Answer(group);
                        return failed;
                    case (byte)'N': // NoticeResponse
                    case (byte)'A': // NotificationResponse
                        break;
                    case (byte)'S':
                        CheckParameter(body);
                        break;
                    default:
                        throw Protocol($"unexpected message '{(char)type}'");
                }
            }
        }
        catch
        {
            Answer(group); // reads whose statements ended before the break keep their answers
            throw;
        }
        finally
        {
            // a Parse the server skipped or refused left no statement behind: parse it again next time
            for (int i = 0; i < _stepCount; i++)
                if (_steps[i].Parse && _prepared[_steps[i].StatementId] == 1) _prepared[_steps[i].StatementId] = 0;
            if (_rpos == _rend)
            {
                _rpos = _rend = 0;
                if (_recv.Length > MaxKeptReceive) _recv = new byte[Math.Max(64, _s.ReceiveBufferSize)];
            }
        }
    }

    /// <summary>A write's statement that commits, rolls back, opens or splits the transaction its group shares: the
    /// writes before it may have committed (or not) on their own, so neither their outcome nor a rerun can be right.
    /// SQL's PREPARE and the like share only the first word, so the tag's whole first word or two are compared.</summary>
    static bool ControlsTransaction(ReadOnlySpan<byte> tag)
    {
        if (tag.Length > 0 && tag[^1] == 0) tag = tag[..^1];
        return tag.SequenceEqual("BEGIN"u8) || tag.SequenceEqual("START TRANSACTION"u8) || tag.SequenceEqual("COMMIT"u8)
            || tag.SequenceEqual("ROLLBACK"u8) || tag.SequenceEqual("SAVEPOINT"u8) || tag.SequenceEqual("RELEASE"u8)
            || tag.SequenceEqual("PREPARE TRANSACTION"u8) || tag.SequenceEqual("COMMIT PREPARED"u8) || tag.SequenceEqual("ROLLBACK PREPARED"u8);
    }

    static PgConnectionException OwnTransaction(ReadOnlySpan<byte> tag) =>
        new($"a write ran {Encoding.UTF8.GetString(tag.TrimEnd((byte)0))}, which ends or splits the transaction its group commits in");

    /// <summary>
    /// A statement that dropped what this connection prepared (DEALLOCATE ALL, DISCARD ALL, or a DEALLOCATE of a
    /// statement it parsed): the next Bind of a statement it thinks is parsed would fail, so the connection is replaced
    /// and the next one parses afresh. Its group is Broken, as for a statement that controls the transaction. The tag
    /// of one DEALLOCATE doesn't say which statement it dropped: the SQL that ran does, and the app's own (DEALLOCATE
    /// myplan) leaves the connection be; SQL the client can't read that from counts as dropping one of its own. A
    /// DEALLOCATE that a function or a DO block runs completes with that statement's tag, and isn't seen.
    /// </summary>
    bool DropsStatements(ReadOnlySpan<byte> tag, PgJob?[] group, int step)
    {
        if (tag.Length > 0 && tag[^1] == 0) tag = tag[..^1];
        if (tag.SequenceEqual("DEALLOCATE ALL"u8) || tag.SequenceEqual("DISCARD ALL"u8)) return true;
        if (!tag.SequenceEqual("DEALLOCATE"u8)) return false;
        string? sql = null;
        if (step < _stepCount && group[_steps[step].Slot] is { } job)
            for (int i = 0; i < job.Count; i++)
                if (job.Statements[i].Id == _steps[step].StatementId) sql = job.Statements[i].Sql;
        int id = sql is null ? Unreadable : Deallocated(sql);
        return id == Unreadable || id >= 0 && id < _prepared.Length && _prepared[id] != 0;
    }

    const int Unreadable = -2, NotOurs = -1;

    /// <summary>The id of the client's statement <c>s{Id}</c> that <paramref name="sql"/>, a <c>DEALLOCATE [PREPARE]
    /// name</c>, drops; <see cref="NotOurs"/> for another name, <see cref="Unreadable"/> for SQL it can't read so.</summary>
    internal static int Deallocated(ReadOnlySpan<char> sql)
    {
        if (!Word(ref sql, out var first) || !first.Equals("DEALLOCATE", StringComparison.OrdinalIgnoreCase)) return Unreadable;
        if (!Name(ref sql, out var name, out bool quoted)) return Unreadable;
        // DEALLOCATE PREPARE name, or a statement named prepare
        var rest = sql;
        if (!quoted && name.Equals("PREPARE", StringComparison.OrdinalIgnoreCase) && Name(ref rest, out var named, out bool q))
        {
            name = named;
            quoted = q;
            sql = rest;
        }
        Skip(ref sql);
        if (sql.StartsWith(";")) sql = sql[1..];
        Skip(ref sql);
        if (!sql.IsEmpty) return Unreadable;
        // a bare name is folded to lower case, as Postgres folds it; the client's names are s and digits
        if (name.Length < 2 || name[0] != 's' && (quoted || name[0] != 'S')) return NotOurs;
        int id = 0;
        foreach (char c in name[1..])
        {
            if (c is < '0' or > '9' || id > (int.MaxValue - 9) / 10) return NotOurs;
            id = id * 10 + (c - '0');
        }
        return name.Length > 2 && name[1] == '0' ? NotOurs : id;
    }

    /// <summary>Past whitespace and comments.</summary>
    static void Skip(ref ReadOnlySpan<char> sql)
    {
        while (true)
        {
            sql = sql.TrimStart();
            if (sql.StartsWith("--"))
            {
                int end = sql.IndexOf('\n');
                sql = end < 0 ? default : sql[(end + 1)..];
            }
            else if (sql.StartsWith("/*"))
            {
                int depth = 0, i = 0;
                while (i < sql.Length)
                {
                    if (sql[i..].StartsWith("/*"))
                    {
                        depth++;
                        i += 2;
                    }
                    else if (sql[i..].StartsWith("*/"))
                    {
                        i += 2;
                        if (--depth == 0) break;
                    }
                    else i++;
                }
                sql = sql[Math.Min(i, sql.Length)..];
            }
            else return;
        }
    }

    /// <summary>A bare word: letters, digits, _ and $, not starting with a digit.</summary>
    static bool Word(ref ReadOnlySpan<char> sql, out ReadOnlySpan<char> word)
    {
        Skip(ref sql);
        int n = 0;
        while (n < sql.Length && (char.IsAsciiLetter(sql[n]) || sql[n] == '_' || n > 0 && (char.IsAsciiDigit(sql[n]) || sql[n] == '$'))) n++;
        word = sql[..n];
        sql = sql[n..];
        return n > 0;
    }

    /// <summary>A name: a bare word, or a quoted one (a doubled quote is one in it).</summary>
    static bool Name(ref ReadOnlySpan<char> sql, out ReadOnlySpan<char> name, out bool quoted)
    {
        Skip(ref sql);
        quoted = sql.StartsWith("\"");
        if (!quoted) return Word(ref sql, out name);
        // a doubled quote is one in the name; kept doubled here, as no name of the client's has one
        int end = 1;
        while (end < sql.Length && !(sql[end] == '"' && (end + 1 == sql.Length || sql[end + 1] != '"'))) end += sql[end] == '"' ? 2 : 1;
        if (end >= sql.Length)
        {
            name = default;
            return false;
        }
        name = sql[1..end];
        sql = sql[(end + 1)..];
        return !name.IsEmpty;
    }

    static PgConnectionException DroppedStatements(ReadOnlySpan<byte> tag) =>
        new($"a statement ran {Encoding.UTF8.GetString(tag.TrimEnd((byte)0))}, which drops the statements the connection prepared");

    /// <summary>A RowDescription's column types; the names and the rest are of no use here.</summary>
    static uint[] ColumnTypes(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) throw Protocol("malformed RowDescription");
        int n = BinaryPrimitives.ReadInt16BigEndian(body);
        if (n < 0) throw Protocol("malformed RowDescription");
        var types = new uint[n];
        body = body[2..];
        for (int i = 0; i < n; i++)
        {
            // name, then table OID (4), column number (2), type OID (4), size (2), modifier (4), format (2)
            int z = body.IndexOf((byte)0);
            if (z < 0 || body.Length - z - 1 < 18) throw Protocol("malformed RowDescription");
            types[i] = BinaryPrimitives.ReadUInt32BigEndian(body[(z + 7)..]);
            body = body[(z + 19)..];
        }
        if (body.Length != 0) throw Protocol("malformed RowDescription");
        return types;
    }

    /// <summary>The column types of statement <paramref name="id"/>, described when it was parsed here.</summary>
    internal ReadOnlySpan<uint> Columns(int id) =>
        _columns[id] ?? throw new PgConnectionException("rows of a statement this connection never described");

    void Row(PgJob job, ReadOnlySpan<byte> body, bool keep, int statement)
    {
        job.CheckIs(PgJob.Life.Queued);
        if (keep)
        {
            job.Save(body);
            job.Rows++;
            return;
        }
        if (job.Failure is null && job.Handler is { } handler)
        {
            var row = PgRow.Parse(body, ref ColumnStart, ref ColumnLength, job.Rows, Columns(statement));
            try
            {
                job.Value = handler(in row, job.State);
            }
            catch (Exception e)
            {
                job.Failure = e; // a bug in one handler fails its own command; the rest of the stream is fine
            }
        }
        job.Rows++;
    }

    /// <summary>A job's last statement ended: a write ran once more; a read is answered before the next receive.</summary>
    void Ended(PgJob?[] group, int slot, bool keep)
    {
        var job = group[slot]!;
        job.CheckIs(PgJob.Life.Queued);
        job.Runs++;
        if (keep) return;
        if (_answeredCount == _answered.Length) Array.Resize(ref _answered, _answeredCount * 2);
        _answered[_answeredCount++] = slot;
    }

    /// <summary>Completes the reads whose statements ended. Their callers continue on this thread meanwhile.</summary>
    void Answer(PgJob?[] group)
    {
        // taken first: should a completion throw, the rest stay in the group for the loop to fail, none twice
        int n = _answeredCount;
        _answeredCount = 0;
        for (int i = 0; i < n; i++)
        {
            int slot = _answered[i];
            var job = group[slot]!;
            group[slot] = null; // after Complete the job may already be someone else's
            job.Complete(job.Answer());
        }
    }

    /// <summary>
    /// Debug: where <see cref="ReadReplies"/> is agrees with the batch sent. <paramref name="step"/> runs from 0 to
    /// the step count; within a step, phase 0 (ParseComplete due) only where a Parse went, then 1 (BindComplete due),
    /// then 2 (rows); past the last step only ReadyForQuery is due (phase 1). A failure has its error and the slot it
    /// blames. A statement parsed in this batch is pending (1) until its ParseComplete, parsed (2) after. Each step not
    /// yet ended still has its job in the group, and the receive window lies within the buffer.
    /// </summary>
    [Conditional("DEBUG")]
    void CheckReplies(PgJob?[] group, int step, int phase, int failed, PgException? error)
    {
        if ((uint)step > (uint)_stepCount || phase is < 0 or > 4) Broken($"step {step} of {_stepCount}, phase {phase}");
        if (step == _stepCount ? phase != 1 : phase is 0 or 3 or 4 && !_steps[step].Parse)
            Broken($"phase {phase} at step {step} of {_stepCount}, which {(step < _stepCount && _steps[step].Parse ? "sent" : "sent no")} Parse");
        if ((error is null) != (failed == -1) || (failed != -1 && failed != CommitFailed && (uint)failed >= (uint)group.Length))
            Broken($"failed {failed} with {(error is null ? "no error" : "an error")}");
        for (int i = 0; i < _stepCount; i++)
        {
            ref readonly Step st = ref _steps[i];
            if ((uint)st.Slot >= (uint)group.Length) Broken($"step {i} in slot {st.Slot} of {group.Length}");
            if (i >= step && group[st.Slot] is null) Broken($"step {i} not ended has no job");
            if (st.Parse)
            {
                int want = i > step || (i == step && phase == 0) ? 1 : 2;
                if (_prepared[st.StatementId] != want) Broken($"statement {st.StatementId} at step {i} is {_prepared[st.StatementId]}, not {want}");
                // described once its Describe was answered: past its step, or at it from BindComplete on
                if ((i < step || (i == step && phase is 1 or 2)) && _columns[st.StatementId] is null) Broken($"statement {st.StatementId} at step {i} not described");
            }
        }
        if (_rpos < 0 || _rpos > _rend || _rend > _recv.Length || _answeredCount > _stepCount)
            Broken($"receive window [{_rpos}, {_rend}) of {_recv.Length}, {_answeredCount} answered of {_stepCount}");
    }

    static void Broken(string what) => throw new InvalidOperationException("PgConnection invariant broken: " + what);

    /// <summary>The next whole message in the buffer, or false when it needs another receive.</summary>
    bool TryNext(out byte type, out ReadOnlySpan<byte> body)
    {
        int avail = _rend - _rpos;
        if (avail >= 5)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(_recv.AsSpan(_rpos + 1));
            if (len is < 4 or > MaxMessage) throw Protocol($"message length {len}");
            if (avail > len)
            {
                type = _recv[_rpos];
                body = _recv.AsSpan(_rpos + 5, len - 4);
                _rpos += 1 + len;
                return true;
            }
            _need = 1 + len;
        }
        else _need = 5;
        type = 0;
        body = default;
        return false;
    }

    /// <summary>Waits for more bytes. A message longer than the buffer gets room as its bytes come (the buffer doubles
    /// when the message fills it, up to the message's length), not when its length is announced: five bytes
    /// announcing a gigabyte don't make the client allocate one.</summary>
    void Fill()
    {
        int avail = _rend - _rpos;
        if (avail == _recv.Length && _need > _recv.Length)
        {
            var bigger = new byte[Math.Min(_need, _recv.Length * 2L)];
            _recv.AsSpan(_rpos, avail).CopyTo(bigger);
            _recv = bigger;
            _rpos = 0;
            _rend = avail;
        }
        Receive(wait: true);
    }

    /// <summary>One receive into the free end of the buffer (only what is waiting, unless <paramref name="wait"/>);
    /// the partial message there moves to the front first.</summary>
    int Receive(bool wait)
    {
        if (_rpos > 0)
        {
            int avail = _rend - _rpos;
            _recv.AsSpan(_rpos, avail).CopyTo(_recv);
            _rpos = 0;
            _rend = avail;
        }
        if (_rend == _recv.Length)
        {
            // replies taken in while a big batch goes out, kept until it is out: up to what one message may be, so a
            // server (or anyone on the path) that only writes can't make the client allocate without end
            if (_recv.Length >= _s.MaxTakeIn) throw new PgConnectionException($"Postgres sent over {_s.MaxTakeIn >> 20} MB while the batch was going out");
            Array.Resize(ref _recv, (int)Math.Min(_recv.Length * 2L, _s.MaxTakeIn));
        }
        int n;
        try
        {
            n = wait ? _t.Receive(_recv.AsSpan(_rend)) : _t.ReceiveWaiting(_recv.AsSpan(_rend));
        }
        catch (Exception e) when (e is not PgConnectionException)
        {
            throw new PgConnectionException("lost the connection to Postgres: " + e.Message, e);
        }
        if (wait && n <= 0) throw new PgConnectionException("Postgres closed the connection");
        _rend += n;
        return n;
    }

    internal static PgException ParseError(ReadOnlySpan<byte> body)
    {
        string? severity = null, localized = null, message = null, detail = null, constraint = null;
        string code = "";
        while (true)
        {
            if (body.Length == 0) throw Protocol("malformed ErrorResponse");
            byte field = body[0];
            if (field == 0) break;
            int z = body[1..].IndexOf((byte)0);
            if (z < 0) throw Protocol("malformed ErrorResponse");
            var v = body.Slice(1, z);
            body = body[(z + 2)..];
            switch (field)
            {
                case (byte)'V': severity = Encoding.UTF8.GetString(v); break;
                case (byte)'S': localized = Encoding.UTF8.GetString(v); break;
                case (byte)'C': code = v.Length == 5 ? PgErrorCodes.Intern(v) : Encoding.UTF8.GetString(v); break;
                case (byte)'M': message = Encoding.UTF8.GetString(v); break;
                case (byte)'D': detail = Encoding.UTF8.GetString(v); break;
                case (byte)'n': constraint = Encoding.UTF8.GetString(v); break;
            }
        }
        return new PgException(severity ?? localized ?? "ERROR", code, message ?? "", detail, constraint);
    }

    static PgConnectionException Protocol(string what) => new("protocol error from Postgres: " + what);
}
