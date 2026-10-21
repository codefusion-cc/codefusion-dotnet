using System;
using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace CodeFusion.Data.Tests;

/// <summary>
/// A response as CodeFusion.Http's connection is one: a body buffer and a JSON writer over it, reused for every
/// request, so writing into it allocates nothing once warm. The writer validates, so a reply that writes broken JSON
/// fails the test.
/// </summary>
public sealed class FakeResponse : IResponse
{
    static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    readonly ArrayBufferWriter<byte> _body = new(64 << 10);
    readonly Utf8JsonWriter _json;
    CancellationTokenSource? _aborted;

    public FakeResponse() => _json = new Utf8JsonWriter(_body, Options);

    public int Status { get; set; } = 200;
    public int RetryAfter { get; set; }
    public MediaType? ContentType { get; set; }
    public Utf8JsonWriter Json => _json;
    public IBufferWriter<byte> Body => _body;
    public FakeArena Arena { get; } = new();
    IRequestArena IResponse.Arena => Arena;
    public CancellationToken Aborted => _aborted?.Token ?? default;
    public int Resets { get; private set; }

    public void Reset()
    {
        _json.Reset();
        _body.ResetWrittenCount();
        Status = 200;
        RetryAfter = 0;
        ContentType = null;
        Resets++;
    }

    /// <summary>Ready for the next request on the connection.</summary>
    public void Next()
    {
        _json.Reset();
        _body.ResetWrittenCount();
        Status = 200;
        RetryAfter = 0;
        ContentType = null;
        Resets = 0;
        Arena.Reset();
    }

    /// <summary>The client went away.</summary>
    public void Abort()
    {
        _aborted ??= new CancellationTokenSource();
        _aborted.Cancel();
    }

    public void Unabort() => _aborted = null;

    public ReadOnlySpan<byte> Bytes
    {
        get
        {
            _json.Flush();
            return _body.WrittenSpan;
        }
    }

    public string Text => Encoding.UTF8.GetString(Bytes);
}

/// <summary>A request's arena: one buffer, handed out front to back and reset for the next request.</summary>
public sealed class FakeArena : IRequestArena
{
    byte[] _buffer = new byte[16 << 10];
    int _used;

    public int Allocations { get; private set; }
    public int Used => _used;

    public ArenaBytes Allocate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Allocations++;
        if (_buffer.Length - _used < length)
        {
            // a new buffer: the bytes handed out before stay valid in the old one
            _buffer = new byte[Math.Max(_buffer.Length * 2, length)];
            _used = 0;
        }
        var bytes = new ArenaBytes(_buffer, _used, length);
        _used += length;
        return bytes;
    }

    public void Reset()
    {
        _used = 0;
        Allocations = 0;
    }

    /// <summary>Overwrites what was handed out, as the next request would: a value still pointing here shows it.</summary>
    public void Scribble()
    {
        _buffer.AsSpan(0, _used).Fill((byte)'#');
        _used = 0;
    }
}
