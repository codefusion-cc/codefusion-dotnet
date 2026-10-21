using System;
using System.Text;
using CodeFusion.Http;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>The response head's parts. In the quiet collection: no server's heartbeat updates the date meanwhile.</summary>
[Collection(Quiet.Name)]
public class HeadTests
{
    [Fact]
    public void TheDateLineIsTheRfc1123TimeAndTheLastOneKeepsItsBytesForOneMoreUpdate()
    {
        var t = new DateTime(2026, 10, 20, 7, 30, 0, DateTimeKind.Utc);
        for (int i = 0; i < 3; i++) // each buffer in turn
        {
            HttpDate.Update(t.AddSeconds(i));
            ReadOnlySpan<byte> last = HttpDate.Line;
            Assert.Equal($"Date: {t.AddSeconds(i):R}\r\n", Encoding.ASCII.GetString(last));
            HttpDate.Update(t.AddSeconds(i + 1));
            Assert.Equal($"Date: {t.AddSeconds(i + 1):R}\r\n", Encoding.ASCII.GetString(HttpDate.Line));
            Assert.Equal($"Date: {t.AddSeconds(i):R}\r\n", Encoding.ASCII.GetString(last)); // a response copying it isn't torn
        }
    }

    [Theory]
    [InlineData(299)]
    [InlineData(418)]
    public void AStatusWithoutAReasonPhraseGoesOutWithAnEmptyOne(int status)
    {
        var d = new byte[ResponseHead.Reserve];
        int n = ResponseHead.Write(d, status, -1, default, default, retryAfter: 0, close: false, keepAlive: false);
        string head = Encoding.ASCII.GetString(d, 0, n);
        Assert.StartsWith($"HTTP/1.1 {status} \r\nDate: ", head, StringComparison.Ordinal);
        Assert.EndsWith(" GMT\r\n\r\n", head, StringComparison.Ordinal);
    }
}
