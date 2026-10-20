using System;
using Xunit;

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// The take-in without recv (Windows and every OS but Linux and macOS): what it makes of a socket's answers, with a
/// socket whose answers are scripted, as no real one can be timed.
/// </summary>
public sealed class PgTransportTests
{
    /// <summary>A socket whose bytes come in when <see cref="Readable"/> is asked, as a reply landing between the
    /// first look and the poll would.</summary>
    sealed class Scripted(int arrives, bool closed) : SocketTransport.IWaitingSocket
    {
        int _waiting;

        public int Available => _waiting;

        public bool Readable()
        {
            _waiting += arrives;
            return _waiting > 0 || closed;
        }

        public int Receive(Span<byte> buffer)
        {
            int n = Math.Min(_waiting, buffer.Length);
            buffer[..n].Fill(7);
            _waiting -= n;
            return n;
        }
    }

    [Fact]
    public void AReplyThatCameAfterTheFirstLookIsTakenInNotTakenForAClose()
    {
        var buffer = new byte[16];
        Assert.Equal(5, SocketTransport.ReceiveWaiting(new Scripted(arrives: 5, closed: false), buffer));
        Assert.Equal(7, buffer[4]);
    }

    [Fact]
    public void ReadableWithNothingToReadIsAClose()
    {
        var e = Assert.Throws<PgConnectionException>(() => SocketTransport.ReceiveWaiting(new Scripted(arrives: 0, closed: true), new byte[16]));
        Assert.Contains("closed", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingWaitingIsNone() =>
        Assert.Equal(0, SocketTransport.ReceiveWaiting(new Scripted(arrives: 0, closed: false), new byte[16]));
}
