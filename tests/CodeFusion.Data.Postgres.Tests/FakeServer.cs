using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CodeFusion.Data.Postgres;

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// A scripted Postgres behind <see cref="PgTransport"/>, written from the protocol documentation
/// (https://www.postgresql.org/docs/current/protocol-message-formats.html), not from the client. It runs the
/// startup (trust unless <see cref="Password"/> is set), keeps each connection's prepared statements as a server
/// does (a Bind of an unknown statement fails with 26000), and answers each batch up to a Sync with what
/// <see cref="Respond"/> writes, cut into receives of at most <see cref="Chunk"/> bytes.
/// </summary>
sealed class FakeServer
{
    readonly List<FakeConnection> _connections = [];

    /// <summary>Answers one batch: every message after the previous Sync, up to this Sync. Unset: <see cref="Auto"/>.</summary>
    public Action<FakeConnection, IReadOnlyList<ClientMessage>>? Respond;
    /// <summary>The rows <see cref="Auto"/> returns for a Bind: each row a list of column values (null for NULL).</summary>
    public Func<Bind, IEnumerable<byte[]?[]>> Rows = _ => [];
    /// <summary>The column types (pg_type OIDs) a Describe of a statement answers, by its SQL; null for NoData. Unset:
    /// guessed from the first row <see cref="Rows"/> gives the statement's first Bind in the batch (8 bytes int8, 4
    /// int4, 2 int2, 1 bool, anything else text), and NoData without one.</summary>
    public Func<string, uint[]?>? Columns;
    /// <summary>The most bytes one receive gets: 1 feeds the client a byte at a time.</summary>
    public int Chunk = int.MaxValue;
    /// <summary>How authentication goes: null trusts; otherwise "cleartext", "md5" or "scram" with <see cref="Password"/>.</summary>
    public string? Auth;
    public string Password = "secret";
    /// <summary>SCRAM: send this server signature instead of the right one.</summary>
    public byte[]? ForgedSignature;
    /// <summary>SCRAM: the iteration count the server asks for.</summary>
    public int Iterations = 4096;
    /// <summary>SCRAM: answer the client's proof with AuthenticationOk at once, never proving the server knows the password.</summary>
    public bool SkipFinal;
    /// <summary>SCRAM: the mechanisms the server offers, in order.</summary>
    public string[] Mechanisms = ["SCRAM-SHA-256-PLUS", "SCRAM-SHA-256"];
    /// <summary>Answer the startup packet with these bytes instead of authenticating (malformed or hostile startups).</summary>
    public byte[]? StartupReply;
    /// <summary>Refuse new connections, as a server that is down.</summary>
    public volatile bool Down;
    /// <summary>How often the client tried to connect, refused ones included.</summary>
    public int Connects => Volatile.Read(ref _connects);
    int _connects;

    /// <summary>Settings for this server: with <see cref="Password"/> unless it trusts every login.</summary>
    public PgSettings Settings() => new()
    {
        User = "u",
        Database = "d",
        Password = Auth is null && StartupReply is null ? null : Password,
        Transport = _ =>
        {
            Interlocked.Increment(ref _connects);
            if (Down) throw new SocketException((int)SocketError.ConnectionRefused);
            var c = new FakeConnection(this);
            lock (_connections) _connections.Add(c);
            return c;
        },
    };

    public FakeConnection[] Connections
    {
        get { lock (_connections) return [.. _connections]; }
    }

    /// <summary>Answers like Postgres: ParseComplete, BindComplete, the rows and CommandComplete per statement, then ReadyForQuery.</summary>
    public void Auto(FakeConnection c, IReadOnlyList<ClientMessage> batch)
    {
        var w = new ServerWriter();
        bool failed = false;
        foreach (var m in batch)
        {
            if (failed) continue; // after an error the server skips to the Sync
            switch (m.Type)
            {
                case 'P':
                    var (name, sql) = m.Parse();
                    c.Prepared.Add(name);
                    c.Sql[name] = sql;
                    w.Empty('1');
                    break;
                case 'D':
                    if (m.Body[0] != (byte)'S') throw new InvalidDataException("a Describe of a portal");
                    int p = 1;
                    string described = ClientMessage.CStr(m.Body, ref p);
                    uint[] parameters = ClientMessage.ParseTypes(batch, described);
                    w.Message('t', [.. ServerWriter.BE16((short)parameters.Length), .. parameters.SelectMany(t => ServerWriter.BE32((int)t))]);
                    uint[]? columns = Columns is { } f ? f(c.Sql[described]) : Guess(batch, described);
                    if (columns is null) w.Empty('n');
                    else w.RowDescription(columns);
                    break;
                case 'B':
                    var bind = m.Bind();
                    if (!c.Prepared.Contains(bind.Statement))
                    {
                        w.Error("ERROR", "26000", $"prepared statement \"{bind.Statement}\" does not exist");
                        failed = true;
                        break;
                    }
                    w.Empty('2');
                    int n = 0;
                    foreach (var row in Rows(bind))
                    {
                        w.DataRow(row);
                        n++;
                    }
                    w.Complete($"SELECT {n}");
                    break;
            }
        }
        w.Ready();
        c.Write(w.ToArray());
    }

    /// <summary>The types of the first row the statement's first Bind in the batch returns, by their sizes.</summary>
    uint[]? Guess(IReadOnlyList<ClientMessage> batch, string statement)
    {
        foreach (var m in batch)
            if (m.Type == 'B' && m.Bind() is { } bind && bind.Statement == statement)
                return Rows(bind).FirstOrDefault() is { } row
                    ? [.. row.Select(v => v?.Length switch { 8 => 20u, 4 => 23u, 2 => 21u, 1 => 16u, _ => 25u })]
                    : null;
        return null;
    }

    internal void Answer(FakeConnection c, IReadOnlyList<ClientMessage> batch) => (Respond ?? Auto)(c, batch);
}

/// <summary>A message the client sent.</summary>
sealed record ClientMessage(char Type, byte[] Body)
{
    public (string Name, string Sql) Parse()
    {
        int p = 0;
        return (CStr(Body, ref p), CStr(Body, ref p));
    }

    /// <summary>The parameter types the Parse of <paramref name="statement"/> in <paramref name="batch"/> declared.</summary>
    public static uint[] ParseTypes(IReadOnlyList<ClientMessage> batch, string statement)
    {
        foreach (var m in batch)
        {
            if (m.Type != 'P') continue;
            int p = 0;
            if (CStr(m.Body, ref p) != statement) continue;
            CStr(m.Body, ref p);
            int n = BinaryPrimitives.ReadInt16BigEndian(m.Body.AsSpan(p));
            return [.. Enumerable.Range(0, n).Select(i => BinaryPrimitives.ReadUInt32BigEndian(m.Body.AsSpan(p + 2 + 4 * i)))];
        }
        throw new InvalidDataException("a Describe without its Parse");
    }

    /// <summary>A Bind as the protocol defines it; asserts binary parameters and results.</summary>
    public Bind Bind()
    {
        var b = Body.AsSpan();
        int p = 0;
        string portal = CStr(Body, ref p), statement = CStr(Body, ref p);
        if (portal != "") throw new InvalidDataException("named portal");
        short formats = I16(b, ref p);
        for (int i = 0; i < formats; i++)
            if (I16(b, ref p) != 1) throw new InvalidDataException("a text parameter");
        short n = I16(b, ref p);
        if (formats != 1 && formats != n) throw new InvalidDataException("parameter formats don't match the count");
        var values = new byte[]?[n];
        for (int i = 0; i < n; i++)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(b[p..]);
            p += 4;
            if (len >= 0)
            {
                values[i] = b.Slice(p, len).ToArray();
                p += len;
            }
            else if (len != -1) throw new InvalidDataException("bad parameter length");
        }
        short results = I16(b, ref p);
        for (int i = 0; i < results; i++)
            if (I16(b, ref p) != 1) throw new InvalidDataException("a text result column");
        if (p != b.Length) throw new InvalidDataException("bytes after the Bind");
        return new Bind(statement, values);
    }

    internal static string CStr(byte[] b, ref int p)
    {
        int z = Array.IndexOf(b, (byte)0, p);
        if (z < 0) throw new InvalidDataException("unterminated string");
        string s = Encoding.UTF8.GetString(b, p, z - p);
        p = z + 1;
        return s;
    }

    static short I16(ReadOnlySpan<byte> b, ref int p)
    {
        short v = BinaryPrimitives.ReadInt16BigEndian(b[p..]);
        p += 2;
        return v;
    }
}

sealed record Bind(string Statement, byte[]?[] Params)
{
    public long Int8(int i) => BinaryPrimitives.ReadInt64BigEndian(Params[i]);
    public string? Text(int i) => Params[i] is { } b ? Encoding.UTF8.GetString(b) : null;

    /// <summary>A binary text[] as array_recv reads it: dimensions, null flag, element type, then each element.</summary>
    public string?[]? TextArray(int i)
    {
        if (Params[i] is not { } b) return null;
        int ndim = BinaryPrimitives.ReadInt32BigEndian(b), hasNull = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(4));
        uint oid = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(8));
        if (oid != 25 || hasNull is not (0 or 1)) throw new InvalidDataException("bad array header");
        if (ndim == 0)
        {
            if (b.Length != 12) throw new InvalidDataException("bytes after an empty array");
            return [];
        }
        if (ndim != 1) throw new InvalidDataException("not one dimension");
        int n = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(12)), lower = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16));
        if (n < 1) throw new InvalidDataException("a dimension without elements: Postgres sends an empty array with none");
        if (lower != 1) throw new InvalidDataException("lower bound not 1");
        var result = new string?[n];
        int p = 20;
        bool sawNull = false;
        for (int k = 0; k < n; k++)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p));
            p += 4;
            if (len < 0) { sawNull = true; continue; }
            result[k] = Encoding.UTF8.GetString(b, p, len);
            p += len;
        }
        if (p != b.Length) throw new InvalidDataException("bytes after the array");
        if (sawNull != (hasNull == 1)) throw new InvalidDataException("null flag wrong");
        return result;
    }
}

/// <summary>Server messages, built byte by byte from the documentation.</summary>
sealed class ServerWriter
{
    readonly List<byte> _b = [];

    public byte[] ToArray() => [.. _b];
    public int Length => _b.Count;

    public ServerWriter Message(char type, ReadOnlySpan<byte> body)
    {
        _b.Add((byte)type);
        I32(4 + body.Length);
        foreach (byte x in body) _b.Add(x);
        return this;
    }

    public ServerWriter Empty(char type) => Message(type, []);

    /// <summary>A RowDescription of columns of <paramref name="types"/> (pg_type OIDs), binary.</summary>
    public ServerWriter RowDescription(params uint[] types)
    {
        var body = new List<byte>();
        body.AddRange(BE16((short)types.Length));
        for (int i = 0; i < types.Length; i++)
        {
            body.AddRange(Z($"c{i}"));
            body.AddRange(BE32(0));
            body.AddRange(BE16(0));
            body.AddRange(BE32((int)types[i]));
            body.AddRange(BE16(-1));
            body.AddRange(BE32(-1));
            body.AddRange(BE16(0));
        }
        return Message('T', body.ToArray());
    }

    /// <summary>What a Parse and the Describe after it get: ParseComplete, ParameterDescription (none), and the
    /// statement's columns (<paramref name="columns"/> types; none: NoData).</summary>
    public ServerWriter Parsed(params uint[] columns)
    {
        Empty('1').Message('t', [0, 0]);
        return columns.Length == 0 ? Empty('n') : RowDescription(columns);
    }

    public ServerWriter DataRow(byte[]?[] columns)
    {
        var body = new List<byte>();
        body.AddRange(BE16((short)columns.Length));
        foreach (var c in columns)
        {
            body.AddRange(BE32(c?.Length ?? -1));
            if (c is not null) body.AddRange(c);
        }
        return Message('D', body.ToArray());
    }

    public ServerWriter Complete(string tag) => Message('C', Z(tag));
    public ServerWriter Ready(char status = 'I') => Message('Z', [(byte)status]);
    public ServerWriter Notice(string message) => Message('N', [.. Field('S', "NOTICE"), .. Field('V', "NOTICE"), .. Field('C', "00000"), .. Field('M', message), 0]);
    public ServerWriter Parameter(string name, string value) => Message('S', [.. Z(name), .. Z(value)]);

    public ServerWriter Error(string severity, string code, string message) =>
        Message('E', [.. Field('S', severity), .. Field('V', severity), .. Field('C', code), .. Field('M', message), 0]);

    public ServerWriter Auth(int code, ReadOnlySpan<byte> data = default) => Message('R', [.. BE32(code), .. data]);

    public ServerWriter Raw(params byte[] bytes)
    {
        _b.AddRange(bytes);
        return this;
    }

    void I32(int v) => _b.AddRange(BE32(v));
    static byte[] Field(char f, string v) => [(byte)f, .. Z(v)];
    public static byte[] Z(string s) => [.. Encoding.UTF8.GetBytes(s), 0];
    public static byte[] BE32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); return b; }
    public static byte[] BE16(short v) { var b = new byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, v); return b; }
    public static byte[] BE64(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); return b; }
}

/// <summary>One accepted connection: the client's bytes come in through <see cref="Send"/>, the server's go out
/// through a queue the client's blocking <see cref="Receive"/> drains.</summary>
sealed class FakeConnection(FakeServer server) : PgTransport
{
    readonly object _lock = new();
    readonly Queue<byte[]> _out = new();
    readonly List<byte> _in = [];
    readonly List<ClientMessage> _batch = [];
    byte[]? _chunk;
    int _chunkAt;
    bool _closed, _started;
    PgScramServer? _scram;

    public readonly HashSet<string> Prepared = [];
    /// <summary>Each prepared statement's SQL, by its name.</summary>
    public readonly Dictionary<string, string> Sql = [];
    public readonly Dictionary<string, string> StartupParameters = [];
    /// <summary>Every batch, in order.</summary>
    public readonly List<IReadOnlyList<ClientMessage>> Batches = [];
    /// <summary>Every Send the client made (one per group, if it batches as it should).</summary>
    public int Sends;
    public bool Terminated;
    /// <summary>The client let go of the connection (closed its socket).</summary>
    public volatile bool Disposed;
    /// <summary>The body of every password message the client sent (cleartext, md5 or a SCRAM step).</summary>
    public readonly List<byte[]> Passwords = [];

    /// <summary>Queues server bytes for the client.</summary>
    public void Write(byte[] bytes)
    {
        lock (_lock)
        {
            if (bytes.Length > 0) _out.Enqueue(bytes);
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Closes the connection from the server's side after what is queued (a crash or restart).</summary>
    public void Close()
    {
        lock (_lock)
        {
            _closed = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Resets the connection: what is queued is lost, and the client's next receive or send fails.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _out.Clear();
            _chunk = null;
            _closed = _reset = true;
            Monitor.PulseAll(_lock);
        }
    }

    bool _reset;

    public override int Receive(Span<byte> buffer)
    {
        lock (_lock)
        {
            while (_chunk is null || _chunkAt == _chunk.Length)
            {
                if (_out.TryDequeue(out _chunk)) { _chunkAt = 0; continue; }
                _chunk = null;
                if (_reset) throw new SocketException((int)SocketError.ConnectionReset);
                if (_closed) return 0;
                if (!Monitor.Wait(_lock, TimeSpan.FromSeconds(20))) throw new TimeoutException("the client waited 20 s for the fake server");
            }
            int n = Math.Min(Math.Min(buffer.Length, server.Chunk), _chunk.Length - _chunkAt);
            _chunk.AsSpan(_chunkAt, n).CopyTo(buffer);
            _chunkAt += n;
            return n;
        }
    }

    /// <summary>As a socket's non-blocking receive: 0 with nothing waiting, and a closed or reset connection throws,
    /// as <see cref="SocketTransport"/>'s does.</summary>
    public override int ReceiveWaiting(Span<byte> buffer)
    {
        lock (_lock)
            if (Available == 0)
            {
                if (_reset) throw new SocketException((int)SocketError.ConnectionReset);
                if (_closed) throw new PgConnectionException("Postgres closed the connection");
                return 0;
            }
        return Receive(buffer);
    }

    /// <summary>Bytes queued for the client.</summary>
    public int Available
    {
        get
        {
            lock (_lock) return (_chunk is null ? 0 : _chunk.Length - _chunkAt) + _out.Sum(c => c.Length);
        }
    }

    public override void Send(ReadOnlySpan<byte> data, Action? whileBlocked = null)
    {
        lock (_lock)
            if (_closed) throw new SocketException((int)SocketError.ConnectionReset);
        Sends++;
        _in.AddRange(data.ToArray());
        while (true)
        {
            if (!_started)
            {
                if (_in.Count < 4) return;
                int len = BinaryPrimitives.ReadInt32BigEndian(_in.ToArray());
                if (_in.Count < len) return;
                byte[] packet = [.. _in.Take(len)];
                _in.RemoveRange(0, len);
                _started = true;
                Startup(packet);
                continue;
            }
            if (_in.Count < 5) return;
            int mlen = BinaryPrimitives.ReadInt32BigEndian(_in.Skip(1).Take(4).ToArray());
            if (_in.Count < 1 + mlen) return;
            var m = new ClientMessage((char)_in[0], [.. _in.Skip(5).Take(mlen - 4)]);
            _in.RemoveRange(0, 1 + mlen);
            Handle(m);
        }
    }

    void Startup(byte[] packet)
    {
        if (BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(4)) != 196608) throw new InvalidDataException("not protocol 3.0");
        int p = 8;
        while (packet[p] != 0)
        {
            string k = ClientMessage.CStr(packet, ref p), v = ClientMessage.CStr(packet, ref p);
            StartupParameters[k] = v;
        }
        if (server.StartupReply is { } scripted)
        {
            Write(scripted);
            return;
        }
        var w = new ServerWriter();
        switch (server.Auth)
        {
            case null: Ready(); return;
            case "cleartext": w.Auth(3); break;
            case "md5": w.Auth(5, [1, 2, 3, 4]); break;
            case "scram": w.Auth(10, [.. server.Mechanisms.SelectMany(ServerWriter.Z), 0]); break;
        }
        Write(w.ToArray());
    }

    void Ready()
    {
        Write(new ServerWriter().Auth(0).Message('K', [0, 0, 0, 42, 0, 0, 0, 7]).Parameter("integer_datetimes", "on")
            .Parameter("server_version", "18.0").Ready().ToArray());
    }

    void Refuse() => Write(new ServerWriter().Error("FATAL", "28P01", "password authentication failed for user \"u\"").ToArray());

    void Handle(ClientMessage m)
    {
        switch (m.Type)
        {
            case 'p':
                lock (Passwords) Passwords.Add(m.Body);
                Password(m.Body);
                return;
            case 'X':
                Terminated = true;
                Close();
                return;
            case 'S':
                IReadOnlyList<ClientMessage> batch = [.. _batch];
                _batch.Clear();
                lock (Batches) Batches.Add(batch);
                server.Answer(this, batch);
                return;
            default:
                _batch.Add(m);
                return;
        }
    }

    void Password(byte[] body)
    {
        switch (server.Auth)
        {
            case "cleartext":
                if (body.SequenceEqual(ServerWriter.Z(server.Password))) Ready(); else Refuse();
                return;
            case "md5":
                string inner = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(server.Password + "u")));
                string want = "md5" + Convert.ToHexStringLower(MD5.HashData([.. Encoding.ASCII.GetBytes(inner), 1, 2, 3, 4]));
                if (body.SequenceEqual(ServerWriter.Z(want))) Ready(); else Refuse();
                return;
            case "scram" when _scram is null:
                int p = 0;
                if (ClientMessage.CStr(body, ref p) != "SCRAM-SHA-256") throw new InvalidDataException("not SCRAM-SHA-256");
                int len = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(p));
                _scram = new PgScramServer(server.Password, server.Iterations);
                Write(new ServerWriter().Auth(11, _scram.First(body.AsSpan(p + 4, len))).ToArray());
                return;
            case "scram":
                if (_scram.Final(body) is not { } signature)
                {
                    Refuse();
                    return;
                }
                if (!server.SkipFinal)
                    Write(new ServerWriter().Auth(12, Encoding.ASCII.GetBytes("v=" + Convert.ToBase64String(server.ForgedSignature ?? signature))).ToArray());
                Ready();
                return;
        }
    }

    public override void Dispose()
    {
        Disposed = true;
        Close();
    }
}

/// <summary>The server's half of SCRAM-SHA-256 (RFC 5802 section 3), to check the client's proof and sign.</summary>
sealed class PgScramServer(string password, int iterations)
{
    readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    string _clientFirstBare = "", _serverFirst = "", _nonce = "";

    public byte[] First(ReadOnlySpan<byte> clientFirst)
    {
        string s = Encoding.ASCII.GetString(clientFirst);
        if (!s.StartsWith("n,,", StringComparison.Ordinal)) throw new InvalidDataException("channel binding flag");
        _clientFirstBare = s[3..];
        string clientNonce = _clientFirstBare.Split(',').Single(x => x.StartsWith("r=", StringComparison.Ordinal))[2..];
        _nonce = clientNonce + "srv" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(9));
        _serverFirst = $"r={_nonce},s={Convert.ToBase64String(_salt)},i={iterations}";
        return Encoding.ASCII.GetBytes(_serverFirst);
    }

    /// <summary>The server signature if the client's proof is right, else null.</summary>
    public byte[]? Final(byte[] body)
    {
        string s = Encoding.ASCII.GetString(body);
        int p = s.LastIndexOf(",p=", StringComparison.Ordinal);
        string withoutProof = s[..p];
        if (withoutProof != $"c=biws,r={_nonce}") return null;
        byte[] proof = Convert.FromBase64String(s[(p + 3)..]);
        byte[] salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), _salt, iterations, HashAlgorithmName.SHA256, 32);
        byte[] clientKey = HMACSHA256.HashData(salted, "Client Key"u8);
        byte[] auth = Encoding.ASCII.GetBytes($"{_clientFirstBare},{_serverFirst},{withoutProof}");
        byte[] signature = HMACSHA256.HashData(SHA256.HashData(clientKey), auth);
        for (int i = 0; i < 32; i++) signature[i] ^= proof[i];
        if (!signature.SequenceEqual(clientKey)) return null;
        return HMACSHA256.HashData(HMACSHA256.HashData(salted, "Server Key"u8), auth);
    }
}
