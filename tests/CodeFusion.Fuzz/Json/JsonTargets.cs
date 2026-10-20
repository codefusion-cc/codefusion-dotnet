using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CodeFusion.Http;
using CodeFusion.Http.Tests.Sample;

namespace CodeFusion.Fuzz;

/// <summary>
/// The generated binders of the sample app's bodies (NewArticle: envelope, nested object, list, numbers, bools,
/// validation attributes; Login) over the bytes, in a request buffer with other bytes around them. Invariants: no
/// exception; a refusal is a 4xx; a body System.Text.Json can't parse is refused; what binds is valid UTF-8 and
/// meets the type's own rules (required texts non-empty, [MinLength(1)], no empty tag).
/// </summary>
static class JsonBodyTarget
{
    static readonly ErrorReply InvalidBody = new HttpAppErrors().InvalidBody;
    [ThreadStatic] static RequestArena? t_arena;

    public static void Run(ReadOnlySpan<byte> input)
    {
        var buffer = new byte[input.Length + 16];
        buffer.AsSpan().Fill((byte)'"');
        input.CopyTo(buffer.AsSpan(8));
        var arena = t_arena ??= new RequestArena(new BufferPool(4096, 16));
        bool json = IsJson(input);
        try
        {
            var source = new BodySource(buffer, 8, input.Length, arena, InvalidBody);
            if (NewArticle.__BindBody(source, out NewArticle a) is { } refused) Refusal(refused);
            else
            {
                Bound(json, input, "NewArticle");
                Text(a.Title, "title", required: true);
                if (a.Description is { } d) Text(d, "description", required: true);
                foreach (Utf8 tag in a.TagList) Text(tag, "a tag", required: true);
                if (a.Meta is { Remark: { } note }) Text(note, "meta.note", required: false);
            }
            arena.Reset();
            if (Login.__BindBody(source, out Login l) is { } refusedLogin) Refusal(refusedLogin);
            else
            {
                Bound(json, input, "Login");
                Text(l.Email, "email", required: true);
                Text(l.Password, "password", required: true);
            }
        }
        finally
        {
            arena.Reset();
        }
    }

    internal static bool IsJson(ReadOnlySpan<byte> input)
    {
        try
        {
            using var _ = JsonDocument.Parse(input.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static void Refusal(ErrorReply e)
    {
        if (e.Status is < 400 or > 499) throw new FuzzFinding("json-body: a refusal that isn't a 4xx", e.Status.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    static void Bound(bool json, ReadOnlySpan<byte> input, string type)
    {
        if (!json) throw new FuzzFinding("json-body: a body that isn't JSON binds", $"{type} from {Findings.Show(input)}");
    }

    static void Text(Utf8 t, string field, bool required)
    {
        if (!System.Text.Unicode.Utf8.IsValid(t.Span)) throw new FuzzFinding("json-body: a bound text isn't UTF-8", $"{field}: {Findings.Show(t.Span)}");
        if (required && t.IsEmpty) throw new FuzzFinding("json-body: a required text binds empty", field);
    }
}

/// <summary>
/// The sample app over loopback, a body sent to each of its body endpoints in turn on one kept-alive connection:
/// POST /api/articles (authenticated, NewArticle, 201), /api/users/login (Login, 200 or 401) and /api/slow (Login,
/// after a thread-pool hop). Invariants: the handler and binder never throw (the app's error log stays empty),
/// the status is one the endpoint can answer (a refusal or its reply, never a 500), and the reply is JSON.
/// </summary>
static class JsonEndpointTarget
{
    static readonly Lazy<Client> Shared = new(() => new Client());

    public static void Run(ReadOnlySpan<byte> input)
    {
        Client c = Shared.Value;
        c.Check("/api/articles", input, auth: true, [201, 422]);
        c.Check("/api/users/login", input, auth: false, [200, 401, 422]);
        c.Check("/api/slow", input, auth: false, [200, 422]);
    }

    sealed class Client
    {
        readonly HttpApp _app;
        readonly string _token;
        readonly ConcurrentQueue<Exception> _errors = new();
        Socket _socket = null!;
        readonly byte[] _in = new byte[1 << 16];

        public Client()
        {
            _app = SampleApp.Build(b =>
            {
                b.Errors.Internal = new ErrorReply(500, """{"errors":{"body":["internal"]}}"""u8);
                b.Errors.Log = (_, e) => _errors.Enqueue(e);
            });
            _app.Start();
            Span<byte> token = stackalloc byte[Jwt.MaxIssuedBytes];
            _token = Encoding.ASCII.GetString(token[..new Jwt(SampleApp.Secret).Issue(9, token)]);
            Connect();
        }

        void Connect()
        {
            _socket?.Dispose();
            _socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveTimeout = 20_000, SendTimeout = 20_000 };
            _socket.Connect(_app.LocalEndPoint);
        }

        public void Check(string path, ReadOnlySpan<byte> body, bool auth, int[] allowed)
        {
            string head = $"POST {path} HTTP/1.1\r\nHost: x\r\n{(auth ? $"Authorization: Token {_token}\r\n" : "")}Content-Length: {body.Length}\r\n\r\n";
            _socket.Send([.. Encoding.ASCII.GetBytes(head), .. body]);
            var (status, reply, close) = Read(path);
            if (_errors.TryDequeue(out Exception? e))
                throw new FuzzFinding("json-endpoint: the handler or binder threw", $"{path}: {e.GetType().Name}: {e.Message}");
            if (Array.IndexOf(allowed, status) < 0)
                throw new FuzzFinding($"json-endpoint: {path} answers {status}", Encoding.UTF8.GetString(reply));
            if (!JsonBodyTarget.IsJson(reply))
                throw new FuzzFinding("json-endpoint: the reply isn't JSON", $"{path} {status}: {Findings.Show(reply)}");
            if (close) Connect();
        }

        /// <summary>One response: its status, its body, and whether the server closes after it.</summary>
        (int Status, byte[] Body, bool Close) Read(string path)
        {
            int n = 0, headEnd;
            while ((headEnd = _in.AsSpan(0, n).IndexOf("\r\n\r\n"u8)) < 0)
                n += Receive(n, path);
            string head = Encoding.ASCII.GetString(_in, 0, headEnd);
            int status = int.Parse(head.AsSpan(9, 3), System.Globalization.CultureInfo.InvariantCulture);
            int length = 0;
            bool close = false;
            foreach (string line in head.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line.AsSpan(15).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                else if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) && line.Contains("close", StringComparison.OrdinalIgnoreCase))
                    close = true;
            }
            int start = headEnd + 4;
            while (n < start + length) n += Receive(n, path);
            if (n > start + length) throw new FuzzFinding("json-endpoint: bytes after the reply", $"{path}: {Findings.Show(_in.AsSpan(start + length, n - start - length))}");
            return (status, _in.AsSpan(start, length).ToArray(), close);
        }

        int Receive(int at, string path)
        {
            int got = _socket.Receive(_in, at, _in.Length - at, SocketFlags.None);
            if (got == 0) throw new FuzzFinding("json-endpoint: the server closed the connection", path);
            return got;
        }
    }
}
