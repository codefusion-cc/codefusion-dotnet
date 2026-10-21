using System;
using System.Buffers;
using System.Threading.Tasks;
using CodeFusion.Data;

namespace CodeFusion.TechEmpower;

/// <summary>The constant parts of the replies.</summary>
static class Pages
{
    static readonly byte[] HelloWorldBytes = "Hello, World!"u8.ToArray();

    public static Utf8 HelloWorldText => new(HelloWorldBytes, 0, HelloWorldBytes.Length);

    /// <summary>The plaintext test's body. TFB lets the body's bytes be reused; the head is written per request.</summary>
    public static readonly ContentReply HelloWorld = new(MediaType.PlainText, "Hello, World!"u8);

    public static readonly ErrorReply Missing = new(500, """{"error":"world row missing"}"""u8);

    public static readonly ErrorReply WriteFailed = new(500, """{"error":"update failed"}"""u8);

    /// <summary>A World id or a new randomNumber: 1 to 10,000.</summary>
    public static int RandomId() => Random.Shared.Next(1, 10001);
}

/// <summary>What a connection reuses for every request: the rows of a multi-row reply, the fortunes' order.</summary>
sealed class Scratch : IRequestScoped
{
    public readonly WorldRow[] Worlds = new WorldRow[500];
    public int[] Order = new int[16];

    public void Bind(IResponse response) { }
}

/// <summary>
/// The fortunes page: the table's rows and one added at request time, sorted by message, every message HTML-escaped,
/// as UTF-8. The rows come from an awaited list; only their order is sorted, through the connection's index array.
/// </summary>
readonly struct FortunesPage(Rows<FortuneRow> rows, Scratch scratch) : IReply
{
    static readonly byte[] Added = "Additional fortune added at request time."u8.ToArray();

    static readonly SearchValues<byte> Specials = SearchValues.Create("&<>\"'"u8);

    public ValueTask WriteAsync<TResponse>(TResponse response) where TResponse : IResponse
    {
        if (rows.Failed) return rows.Error!.WriteAsync(response);
        int n = rows.Count + 1;
        if (scratch.Order.Length < n) scratch.Order = new int[Math.Max(n, scratch.Order.Length * 2)];
        var order = scratch.Order.AsSpan(0, n);
        for (int i = 0; i < n; i++) order[i] = i;
        // not Span.Sort: with a struct comparer it boxes the comparer and makes a delegate on every call
        SortOrder(order, new ByMessage(rows));
        response.Status = 200;
        response.ContentType = MediaType.Html;
        // the whole page in one span, sized for every character escaped at its longest: one GetSpan, one Advance
        int most = Head.Length + Tail.Length;
        for (int i = 0; i < n; i++) most += Row.Length + 11 + 6 * Message(i).Length;
        var body = response.Body;
        var page = body.GetSpan(most);
        int at = Put(page, 0, Head);
        foreach (int i in order)
        {
            at = Put(page, at, "<tr><td>"u8);
            System.Buffers.Text.Utf8Formatter.TryFormat(i < rows.Count ? rows[i].Id : 0, page[at..], out int digits);
            at = Put(page, at + digits, "</td><td>"u8);
            at = Escaped(page, at, Message(i));
            at = Put(page, at, "</td></tr>"u8);
        }
        at = Put(page, at, Tail);
        body.Advance(at);
        return default;
    }

    static ReadOnlySpan<byte> Head => "<!DOCTYPE html><html><head><title>Fortunes</title></head><body><table><tr><th>id</th><th>message</th></tr>"u8;
    static ReadOnlySpan<byte> Tail => "</table></body></html>"u8;
    static ReadOnlySpan<byte> Row => "<tr><td></td><td></td></tr>"u8;

    ReadOnlySpan<byte> Message(int i) => i < rows.Count ? rows[i].Message.Span : Added;

    static int Put(Span<byte> page, int at, ReadOnlySpan<byte> text)
    {
        text.CopyTo(page[at..]);
        return at + text.Length;
    }

    /// <summary>Text as HTML: the five characters markup gives a meaning escaped, everything else as it is (UTF-8,
    /// as the page's charset says).</summary>
    static int Escaped(Span<byte> page, int at, ReadOnlySpan<byte> text)
    {
        while (true)
        {
            int special = text.IndexOfAny(Specials);
            if (special < 0) return Put(page, at, text);
            at = Put(page, at, text[..special]);
            at = Put(page, at, text[special] switch
            {
                (byte)'&' => "&amp;"u8,
                (byte)'<' => "&lt;"u8,
                (byte)'>' => "&gt;"u8,
                (byte)'"' => "&quot;"u8,
                _ => "&#39;"u8,
            });
            text = text[(special + 1)..];
        }
    }

    /// <summary>Sorts without allocating: insertion sort for a short list (TFB's has 13), heapsort for a long one.</summary>
    static void SortOrder(Span<int> order, ByMessage by)
    {
        if (order.Length <= 16)
        {
            for (int i = 1; i < order.Length; i++)
            {
                int item = order[i], j = i - 1;
                for (; j >= 0 && by.Compare(order[j], item) > 0; j--) order[j + 1] = order[j];
                order[j + 1] = item;
            }
            return;
        }
        for (int i = order.Length / 2 - 1; i >= 0; i--) SiftDown(order, i, order.Length, by);
        for (int end = order.Length - 1; end > 0; end--)
        {
            (order[0], order[end]) = (order[end], order[0]);
            SiftDown(order, 0, end, by);
        }
    }

    static void SiftDown(Span<int> heap, int at, int length, ByMessage by)
    {
        while (true)
        {
            int child = 2 * at + 1;
            if (child >= length) return;
            if (child + 1 < length && by.Compare(heap[child + 1], heap[child]) > 0) child++;
            if (by.Compare(heap[at], heap[child]) >= 0) return;
            (heap[at], heap[child]) = (heap[child], heap[at]);
            at = child;
        }
    }

    /// <summary>Message order: byte order of UTF-8, which is code point order.</summary>
    readonly struct ByMessage(Rows<FortuneRow> rows)
    {
        public int Compare(int x, int y) => Message(x).SequenceCompareTo(Message(y));

        ReadOnlySpan<byte> Message(int i) => i < rows.Count ? rows[i].Message.Span : Added;
    }
}
