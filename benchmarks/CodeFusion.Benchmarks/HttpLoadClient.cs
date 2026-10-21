using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace CodeFusion.Benchmarks;

/// <summary>
/// A closed-loop HTTP/1.1 load client: each keep-alive connection sends a request, reads the whole response,
/// and sends the next. It runs on socket callbacks with buffers made up front, so it allocates nothing per
/// request and a MemoryDiagnoser in the same process counts only the server.
/// </summary>
public sealed class HttpLoadClient : IDisposable
{
    readonly Conn[] _conns;
    readonly ManualResetEventSlim _done = new();
    int _running;
    long _requests;

    /// <summary>Connects and sends one request on each connection, to learn the response length, which must be
    /// the same for every request. Connections take turns through <paramref name="requests"/>.</summary>
    public HttpLoadClient(IPEndPoint server, int connections, params byte[][] requests)
    {
        _conns = new Conn[connections];
        for (int i = 0; i < connections; i++) _conns[i] = new Conn(this, server, requests, i);
    }

    /// <summary>Requests answered since the client was made.</summary>
    public long Requests => Interlocked.Read(ref _requests);

    /// <summary>Sends <paramref name="perConnection"/> requests on every connection and waits for all answers.</summary>
    public void Run(int perConnection)
    {
        _done.Reset();
        _running = _conns.Length;
        foreach (var c in _conns) c.Start(perConnection);
        if (!_done.Wait(TimeSpan.FromMinutes(2))) throw new TimeoutException("the server stopped answering");
    }

    void Finished(int answered)
    {
        Interlocked.Add(ref _requests, answered);
        if (Interlocked.Decrement(ref _running) == 0) _done.Set();
    }

    public void Dispose()
    {
        foreach (var c in _conns) c.Dispose();
        _done.Dispose();
    }

    sealed class Conn : IDisposable
    {
        readonly HttpLoadClient _owner;
        readonly Socket _socket;
        readonly byte[][] _requests;
        byte[] _request;
        int _next;
        readonly byte[] _buf;
        readonly int _responseLength;
        readonly SocketAsyncEventArgs _args;
        int _left, _total, _got;

        public Conn(HttpLoadClient owner, IPEndPoint server, byte[][] requests, int first)
        {
            _owner = owner;
            _requests = requests;
            _next = first % requests.Length;
            _request = requests[_next];
            _socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            _socket.Connect(server);
            _responseLength = FirstResponse(new byte[64 * 1024]);
            _buf = new byte[_responseLength]; // idle connections should cost the client as little as they can
            _args = new SocketAsyncEventArgs(unsafeSuppressExecutionContextFlow: true);
            _args.Completed += (_, e) => Step(e.LastOperation == SocketAsyncOperation.Send);
        }

        int FirstResponse(byte[] buf)
        {
            _socket.Send(_request);
            int n = 0;
            while (true)
            {
                n += _socket.Receive(buf.AsSpan(n));
                int head = buf.AsSpan(0, n).IndexOf("\r\n\r\n"u8);
                if (head < 0) continue;
                int at = buf.AsSpan(0, head).IndexOf("Content-Length: "u8);
                int length = 0;
                for (int i = at + 16; buf[i] != '\r'; i++) length = length * 10 + buf[i] - '0';
                while (n < head + 4 + length) n += _socket.Receive(buf.AsSpan(n));
                if (n != head + 4 + length) throw new InvalidOperationException("unexpected bytes after the response");
                return n;
            }
        }

        public void Start(int requests)
        {
            _left = _total = requests;
            Send();
        }

        void Send()
        {
            NextRequest();
            _args.SetBuffer(_request, 0, _request.Length);
            if (!_socket.SendAsync(_args)) Step(sent: true);
        }

        /// <summary>One completion, and then every completion that follows synchronously, in a loop rather than
        /// by recursion.</summary>
        void Step(bool sent)
        {
            while (true)
            {
                if (_args.SocketError != SocketError.Success) throw new SocketException((int)_args.SocketError);
                if (sent)
                {
                    if (_args.BytesTransferred != _request.Length) throw new InvalidOperationException("short send");
                    _got = 0;
                }
                else
                {
                    if (_args.BytesTransferred == 0) throw new InvalidOperationException("the server closed the connection");
                    _got += _args.BytesTransferred;
                    if (_got == _responseLength)
                    {
                        if (--_left == 0)
                        {
                            _owner.Finished(_total);
                            return;
                        }
                        NextRequest();
                        _args.SetBuffer(_request, 0, _request.Length);
                        if (_socket.SendAsync(_args)) return;
                        sent = true;
                        continue;
                    }
                }
                _args.SetBuffer(_buf, _got, _responseLength - _got);
                if (_socket.ReceiveAsync(_args)) return;
                sent = false;
            }
        }

        void NextRequest()
        {
            _next = (_next + 1) % _requests.Length;
            _request = _requests[_next];
        }

        public void Dispose()
        {
            _socket.Dispose();
            _args.Dispose();
        }
    }
}
