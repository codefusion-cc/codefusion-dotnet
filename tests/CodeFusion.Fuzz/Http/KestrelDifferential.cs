using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CodeFusion.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace CodeFusion.Fuzz;

/// <summary>
/// The same bytes through RequestParser and through Kestrel's HTTP/1.1 connection (its parser and its message
/// bodies), as the first request on a connection. Where both take a request they must agree on its method, its
/// decoded path, its query, its body and the byte it ends at: a disagreement there is the request-smuggling class,
/// and a finding. Where one side refuses and the other doesn't, the class is recorded (<see cref="Observed"/>), not
/// failed: refusing is always safe for the side that refuses, and the report says which side and why.
/// </summary>
static class KestrelDifferential
{
    public static void Run(ReadOnlySpan<byte> input)
    {
        Outcome ours = Connection.Parse(input, HttpParserTarget.Default, []);
        KestrelResult k = KestrelHost.Instance.Value.Parse(input.ToArray());
        switch (ours.Status, k.Kind)
        {
            case (0, KestrelKind.Accepted):
                Same("method", ours.Method, k.Method, input, ours, k);
                Same("path", Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(ours.Path)), k.Path, input, ours, k);
                Same("query", ours.Query, k.Query, input, ours, k);
                Same("body", ours.Body, Encoding.Latin1.GetString(k.Body), input, ours, k);
                Same("end", ours.Consumed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    k.End.ToString(System.Globalization.CultureInfo.InvariantCulture), input, ours, k);
                break;
            case (0, KestrelKind.Waiting):
                throw new FuzzFinding("http-kestrel: ours ends the request, Kestrel waits for more", $"ours: {ours}; Kestrel waits at byte {k.End}");
            case (0, KestrelKind.Nothing):
                throw new FuzzFinding("http-kestrel: ours takes a request, Kestrel sees none", ours.ToString());
            case (-1, KestrelKind.Accepted):
                throw new FuzzFinding("http-kestrel: Kestrel ends the request, ours waits for more", $"Kestrel: {k}");
            case (0, KestrelKind.Refused):
                Observed.Record($"http-kestrel: only Kestrel refuses: {k.Reason}", input);
                break;
            case (> 0, KestrelKind.Accepted):
                Observed.Record($"http-kestrel: only ours refuses ({ours.Status}): {Why.Refused(input)}", input);
                break;
            case (> 0, KestrelKind.Waiting):
                Observed.Record($"http-kestrel: ours refuses ({ours.Status}), Kestrel still waits: {Why.Refused(input)}", input);
                break;
            case (-1, KestrelKind.Refused):
                Observed.Record($"http-kestrel: Kestrel refuses, ours still waits: {k.Reason}", input);
                break;
        }
    }

    static void Same(string field, string ours, string kestrel, ReadOnlySpan<byte> input, Outcome o, KestrelResult k)
    {
        if (ours != kestrel)
            throw new FuzzFinding($"http-kestrel: both take the request, the {field} differs",
                $"ours {Findings.Show(Encoding.Latin1.GetBytes(ours))}, Kestrel {Findings.Show(Encoding.Latin1.GetBytes(kestrel))}; input {Findings.Show(input)}; ours: {o}; Kestrel: {k}");
    }
}

enum KestrelKind { Nothing, Accepted, Refused, Waiting }

sealed class KestrelResult
{
    public KestrelKind Kind;
    public string Method = "", Path = "", Query = "";
    public byte[] Body = [];
    /// <summary>Accepted: the byte after the request. Waiting: where Kestrel had consumed to.</summary>
    public long End;
    public string? Reason;
    public int Status;

    public override string ToString() => Kind switch
    {
        KestrelKind.Accepted => $"{Method} {Path}?{Query} body {Findings.Show(Body)} end {End}",
        KestrelKind.Refused => $"refused {Status}: {Reason}",
        _ => Kind.ToString(),
    };
}

/// <summary>
/// Kestrel in this process behind an in-memory transport: each input is a connection whose bytes are all there
/// at once. A probe on the connection's input says when Kestrel asks for more with everything examined: it is
/// waiting for the rest of a request, so the input ends there (EOF) and the request counts as incomplete.
/// </summary>
sealed class KestrelHost : IConnectionListenerFactory, IConnectionListener, IHttpApplication<IFeatureCollection>, ILoggerFactory, ILogger
{
    public static readonly Lazy<KestrelHost> Instance = new(() => new KestrelHost(), LazyThreadSafetyMode.ExecutionAndPublication);

    readonly Channel<ConnectionContext> _accepted = Channel.CreateUnbounded<ConnectionContext>();
    readonly ConcurrentDictionary<string, MemoryConnection> _connections = new();
    long _ids;

    KestrelHost()
    {
        var ours = new HttpServerOptions();
        var options = new KestrelServerOptions { AddServerHeader = false };
        options.Limits.MaxRequestLineSize = ours.MaxRequestLineSize;
        options.Limits.MaxRequestHeadersTotalSize = ours.MaxRequestHeadersTotalSize;
        options.Limits.MaxRequestHeaderCount = ours.MaxRequestHeaderCount;
        options.Limits.MaxRequestBodySize = ours.MaxRequestBodySize;
        options.ApplicationServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        options.Listen(new IPEndPoint(IPAddress.Loopback, 1));
        var server = new KestrelServer(Options.Create(options), this, this);
        server.StartAsync(this, CancellationToken.None).GetAwaiter().GetResult();
    }

    public KestrelResult Parse(byte[] input)
    {
        string id = "fuzz-" + Interlocked.Increment(ref _ids).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var c = new MemoryConnection(id, input);
        _connections[id] = c;
        try
        {
            if (!_accepted.Writer.TryWrite(c.Context)) throw new InvalidOperationException("Kestrel's accept queue is closed");
            if (!c.Closed.Task.Wait(TimeSpan.FromSeconds(20)))
                throw new FuzzFinding("http-kestrel: Kestrel hung", $"the connection didn't end in 20 s; input {Findings.Show(input)}");
            return c.Result();
        }
        finally
        {
            _connections.TryRemove(id, out _);
        }
    }

    // ---- the transport ----

    EndPoint IConnectionListener.EndPoint => new IPEndPoint(IPAddress.Loopback, 1);

    ValueTask<IConnectionListener> IConnectionListenerFactory.BindAsync(EndPoint endpoint, CancellationToken cancellationToken) => new(this);

    async ValueTask<ConnectionContext?> IConnectionListener.AcceptAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _accepted.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ChannelClosedException)
        {
            return null;
        }
    }

    ValueTask IConnectionListener.UnbindAsync(CancellationToken cancellationToken) => default;

    ValueTask IAsyncDisposable.DisposeAsync() => default;

    // ---- the application: the first request of each connection, its body read to the end ----

    IFeatureCollection IHttpApplication<IFeatureCollection>.CreateContext(IFeatureCollection contextFeatures) => contextFeatures;

    async Task IHttpApplication<IFeatureCollection>.ProcessRequestAsync(IFeatureCollection f)
    {
        if (!_connections.TryGetValue(f.Get<IHttpConnectionFeature>()!.ConnectionId, out var c) || c.First is not null) return;
        var req = f.Get<IHttpRequestFeature>()!;
        var first = new KestrelResult { Method = req.Method, Path = req.PathBase + req.Path, Query = req.QueryString.StartsWith('?') ? req.QueryString[1..] : req.QueryString };
        c.First = first;
        try
        {
            using var body = new MemoryStream();
            await req.Body.CopyToAsync(body).ConfigureAwait(false);
            first.Body = body.ToArray();
            first.End = c.Input.Consumed;
            first.Kind = KestrelKind.Accepted;
        }
        catch (BadHttpRequestException e)
        {
            first.Kind = KestrelKind.Refused;
            first.Reason = Reason(e.Message);
            first.Status = e.StatusCode;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            // Kestrel refuses some trailers this way (a non-ASCII byte in one, for example)
            first.Kind = KestrelKind.Refused;
            first.Reason = e.GetType().Name + ": " + Reason(e.Message);
        }
        finally
        {
            c.FirstOver = true;
        }
    }

    void IHttpApplication<IFeatureCollection>.DisposeContext(IFeatureCollection context, Exception? exception) { }

    // ---- logs: the reason Kestrel refused a request before the application saw it ----

    ILogger ILoggerFactory.CreateLogger(string categoryName) => this;

    void ILoggerFactory.AddProvider(ILoggerProvider provider) { }

    void IDisposable.Dispose() { }

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    bool ILogger.IsEnabled(LogLevel logLevel) => true;

    void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (exception is not BadHttpRequestException bad || state is not IReadOnlyList<KeyValuePair<string, object?>> values) return;
        foreach (var (key, value) in values)
            if (key == "ConnectionId" && value is string id && _connections.TryGetValue(id, out var c))
                c.Rejection ??= (Reason(bad.Message), bad.StatusCode);
    }

    /// <summary>Kestrel's message without the bytes it quotes, so that one rule is one class.</summary>
    static string Reason(string message)
    {
        int quote = message.IndexOf('\'', StringComparison.Ordinal);
        return (quote < 0 ? message : message[..quote]).Trim().TrimEnd(':');
    }
}

/// <summary>One connection: the probe Kestrel reads from, the pipe it answers into, what was seen.</summary>
sealed class MemoryConnection
{
    readonly Pipe _output = new(new PipeOptions(pauseWriterThreshold: 0, useSynchronizationContext: false));
    readonly MemoryStream _response = new();

    public readonly Probe Input;
    public readonly ConnectionContext Context;
    public readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public KestrelResult? First;
    public volatile bool FirstOver;
    public bool WaitedInFirst;
    public (string Reason, int Status)? Rejection;

    public MemoryConnection(string id, byte[] input)
    {
        Input = new Probe(this, input);
        Context = new Disposable(id, this) { Transport = new Duplex(Input, _output.Writer) };
        _ = DrainAsync();
    }

    /// <summary>Kestrel disposes the connection when it is done with it; the transport then ends its pipes. The
    /// base class's DisposeAsync isn't called: it disposes the token source that an Abort just before it has
    /// queued a Cancel for, which then throws on a thread-pool thread.</summary>
    sealed class Disposable(string id, MemoryConnection owner) : DefaultConnectionContext(id)
    {
#pragma warning disable CA2215 // on purpose: see above
        public override ValueTask DisposeAsync()
        {
            owner._output.Writer.Complete();
            return default;
        }
#pragma warning restore CA2215
    }

    async Task DrainAsync()
    {
        while (true)
        {
            ReadResult r = await _output.Reader.ReadAsync().ConfigureAwait(false);
            foreach (ReadOnlyMemory<byte> m in r.Buffer) _response.Write(m.Span);
            _output.Reader.AdvanceTo(r.Buffer.End);
            if (r.IsCompleted || r.IsCanceled) break;
        }
        await _output.Reader.CompleteAsync().ConfigureAwait(false);
        Closed.TrySetResult();
    }

    public KestrelResult Result()
    {
        if (WaitedInFirst) return new KestrelResult { Kind = KestrelKind.Waiting, End = Input.Consumed };
        if (First is { } first) return first;
        if (Rejection is { } r) return new KestrelResult { Kind = KestrelKind.Refused, Reason = r.Reason, Status = r.Status };
        if (_response.Length > 0)
            return new KestrelResult { Kind = KestrelKind.Refused, Reason = "no reason logged", Status = StatusOf(_response.ToArray()) };
        return new KestrelResult { Kind = KestrelKind.Nothing };
    }

    static int StatusOf(byte[] response) =>
        response.Length >= 12 && int.TryParse(response.AsSpan(9, 3), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int s) ? s : 0;

    sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }
}

/// <summary>
/// The input Kestrel reads, all of it there from the start. Tracks how far Kestrel has consumed and examined. A
/// read with everything examined would wait for bytes that never come: it marks the connection as waiting (in its
/// first request, if that isn't over) and ends the input there, so Kestrel sees the end of the stream.
/// </summary>
sealed class Probe(MemoryConnection owner, byte[] input) : PipeReader
{
    readonly ReadOnlySequence<byte> _all = new(input);
    long _examined;
    bool _ended, _canceled;

    public long Consumed { get; private set; }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_ended && !_canceled && _examined >= input.Length)
        {
            if (!owner.FirstOver) owner.WaitedInFirst = true;
            _ended = true;
        }
        return new(Result());
    }

    /// <summary>As a pipe's: nothing when every byte is examined and the end hasn't come.</summary>
    public override bool TryRead(out ReadResult result)
    {
        if (!_ended && !_canceled && _examined >= input.Length)
        {
            result = default;
            return false;
        }
        result = Result();
        return true;
    }

    ReadResult Result()
    {
        bool canceled = _canceled;
        _canceled = false;
        return new ReadResult(_all.Slice(Consumed), canceled, _ended);
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        Consumed = _all.Slice(_all.Start, consumed).Length;
        _examined = Math.Max(Consumed, _all.Slice(_all.Start, examined).Length);
    }

    public override void CancelPendingRead() => _canceled = true;

    public override void Complete(Exception? exception = null) => _ended = true;
}
