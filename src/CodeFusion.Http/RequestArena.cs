using System;

namespace CodeFusion.Http;

/// <summary>
/// A request's arena: bytes cut from pooled chunks, reset when the request's response is done. One per connection,
/// reused by every request on it: it keeps one chunk while the connection has requests coming, so a busy connection
/// takes nothing from the pool per request, and gives it back when the connection goes idle (<see cref="Release"/>).
/// </summary>
internal sealed class RequestArena(BufferPool chunks) : IRequestArena
{
    byte[]? _chunk;
    int _used;
    byte[]?[] _filled = new byte[4][];
    int _filledCount;

    public ArenaBytes Allocate(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (_chunk is null || _chunk.Length - _used < length) Next(length);
        var bytes = new ArenaBytes(_chunk!, _used, length);
        _used += length;
        return bytes;
    }

    /// <summary>Starts a new chunk with room for <paramref name="length"/> bytes; a larger request than a chunk gets
    /// an array of its own from the shared pool.</summary>
    void Next(int length)
    {
        if (_chunk is not null)
        {
            if (_filledCount == _filled.Length) Array.Resize(ref _filled, _filled.Length * 2);
            _filled[_filledCount++] = _chunk;
        }
        _chunk = length <= chunks.Size ? chunks.Rent() : chunks.RentLarger(length);
        _used = 0;
    }

    /// <summary>Starts over: what was handed out is no longer valid. A pool chunk is kept for the next request; the
    /// others go back.</summary>
    public void Reset()
    {
        if (_chunk is null) return;
        if (_chunk.Length != chunks.Size)
        {
            chunks.Return(_chunk);
            _chunk = null;
        }
        _used = 0;
        for (int i = 0; i < _filledCount; i++)
        {
            chunks.Return(_filled[i]!);
            _filled[i] = null;
        }
        _filledCount = 0;
    }

    /// <summary>Gives every chunk back, the kept one too: the connection is idle or closed.</summary>
    public void Release()
    {
        Reset();
        if (_chunk is null) return;
        chunks.Return(_chunk);
        _chunk = null;
    }
}
