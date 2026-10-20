using System;
using System.Threading.Tasks;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>
/// After refusing a malformed chunked body, the server answers 400 and reads and drops what the client still sends,
/// then closes. The refused request's parser state used to stay behind, and the first bytes that came during that
/// linger made PrepareReceive compact chunk framing at offsets past the emptied buffer: the connection's loop threw.
/// </summary>
public class LingerTests
{
    [Theory]
    [InlineData("5\r\nhello\r\nZZ\r\n")]          // a bad chunk size after a good chunk
    [InlineData("3\r\nabc\r\n2\r\nde\r\n-1\r\n")]
    [InlineData("5\r\nhello\r\n5\r\nwor")]         // then too much: the body limit
    public async Task MoreBytesAfterARefusedChunkedBodyAreDropped(string chunks)
    {
        await using var t = TestServer.Start(TestServer.Echo, o => o.MaxRequestBodySize = 8);
        using Client c = await Client.ConnectAsync(t.EndPoint);
        await c.SendAsync("POST /x HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\n\r\n" + chunks);
        Response r = await c.ReadAsync();
        Assert.True(r.Status is 400 or 413, r.StatusLine);
        // the server lingers: what comes now is read and dropped, then the connection ends cleanly
        for (int i = 0; i < 5; i++)
        {
            await c.SendAsync(new string('x', 3000));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        c.Socket.Shutdown(System.Net.Sockets.SocketShutdown.Send);
        Assert.True(await c.EndsWithFinAsync());
        Assert.Equal(0, t.Server.FailedConnections);
    }
}
