using System;
using System.Buffers.Binary;
using System.Text;
using CodeFusion.Data.Postgres;

namespace CodeFusion.Fuzz;

/// <summary>
/// The Postgres client's parsing of what the server sends, and of its own connection settings. Invariant for all:
/// the only exceptions are the documented ones (PgConnectionException for a broken stream, PgException for the
/// server's ErrorResponse, FormatException and ArgumentException for settings, InvalidCastException for a row
/// getter asked for the wrong type); never IndexOutOfRange, NullReference, overflow, or a hang.
/// </summary>
static class PgTargets
{
    // ---------- DataRow ----------

    /// <summary>PgRow.Parse, then every getter on every column. The columns must tile the body exactly.</summary>
    public static void Row(ReadOnlySpan<byte> input)
    {
        int[] start = new int[1], len = new int[1]; // small, so the growth path runs
        // as many columns as the row says, of types the input's hash picks among those the getters read
        int n = input.Length >= 2 ? Math.Max(0, (int)BinaryPrimitives.ReadInt16BigEndian(input)) : 0;
        int h = HttpParserTarget.Hash(input);
        var types = new uint[n];
        for (int c = 0; c < n; c++) types[c] = ColumnTypes[((h >> (c % 16)) & 15) % ColumnTypes.Length];
        scoped PgRow row;
        try
        {
            row = PgRow.Parse(input, ref start, ref len, 0, types);
        }
        catch (PgConnectionException)
        {
            return;
        }
        if (row.FieldCount != BinaryPrimitives.ReadInt16BigEndian(input)) throw new FuzzFinding("pg-row: the column count", $"{row.FieldCount}");
        int at = 2;
        for (int c = 0; c < row.FieldCount; c++)
        {
            if (start[c] != at + 4 || len[c] < -1) throw new FuzzFinding("pg-row: columns don't tile the body", $"column {c} at {start[c]}+{len[c]}, expected at {at + 4}");
            at = start[c] + Math.Max(0, len[c]);
            if (row.IsNull(c) != (len[c] == -1)) throw new FuzzFinding("pg-row: IsNull", $"column {c}");
            for (int getter = 0; getter < 9; getter++)
            {
                try
                {
                    Get(row, c, getter, input, start[c], len[c]);
                }
                catch (InvalidCastException)
                {
                }
            }
        }
        if (at != input.Length) throw new FuzzFinding("pg-row: columns don't tile the body", $"they end at {at} of {input.Length}");
    }

    static readonly uint[] ColumnTypes = [16, 20, 21, 23, 25, 114, 700, 701, 1114, 1184, 3802, 17];

    static void Get(in PgRow row, int c, int getter, ReadOnlySpan<byte> body, int start, int len)
    {
        switch (getter)
        {
            case 0:
                if (!row.GetBytes(c).SequenceEqual(body.Slice(start, len))) throw new FuzzFinding("pg-row: GetBytes", $"column {c}");
                break;
            case 1: row.GetInt64(c); break;
            case 2: row.GetBoolean(c); break;
            case 3: row.GetDouble(c); break;
            case 4: row.GetDateTime(c); break;
            case 5: row.GetUnixMicroseconds(c); break;
            case 6: row.GetPgMicroseconds(c); break;
            case 7:
                Span<byte> text = stackalloc byte[CodeFusion.Timestamp.FormattedLength];
                row.GetTimestamp(c).TryFormat(text, out _);
                break;
            default:
                row.GetTimestamp(c).TryToDateTime(out _);
                break;
        }
    }

    // ---------- ErrorResponse ----------

    public static void Error(ReadOnlySpan<byte> input)
    {
        PgException e;
        try
        {
            e = PgConnection.ParseError(input);
        }
        catch (PgConnectionException)
        {
            return;
        }
        if (e.Severity is null || e.SqlState is null || e.MessageText is null) throw new FuzzFinding("pg-error: a null field", e.Message);
        _ = e.IsFatal;
    }

    // ---------- startup and authentication ----------

    /// <summary>
    /// The server's whole side of a startup, as the bytes: PgConnection.Open against a transport that answers
    /// with them, in pieces of a size the input's hash picks, and the password, the weak-auth switch and the
    /// receive buffer also picked by it. The client's SCRAM nonce is random, so a server-first-message can't
    /// know it: the 24 bytes of <see cref="NonceMarker"/> are replaced with it once the client has sent it.
    /// </summary>
    public static void Startup(ReadOnlySpan<byte> input)
    {
        int h = HttpParserTarget.Hash(input);
        var transport = new FuzzTransport(input.ToArray(), Chunks[(h >> 3) & 3]);
        var settings = new PgSettings
        {
            User = "u",
            Database = "d",
            Password = (h & 1) == 0 ? "pencil" : null,
            AllowWeakPasswordAuth = (h & 2) != 0,
            ReceiveBufferSize = (h & 4) != 0 ? 64 : 256 << 10,
            Transport = _ => transport,
        };
        long before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            using var c = PgConnection.Open(settings);
        }
        catch (Exception e) when (e is PgConnectionException or PgException)
        {
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long budget = (1 << 20) + settings.ReceiveBufferSize + 16L * input.Length;
        if (allocated > budget)
            throw new FuzzFinding("pg-startup: allocates far more than the server sent", $"{allocated} bytes for {input.Length} received");
    }

    static readonly int[] Chunks = [int.MaxValue, 1, 7, 64];

    /// <summary>Stands for the client's nonce in a seed's server-first-message (base64 of 18 bytes is 24).</summary>
    internal static ReadOnlySpan<byte> NonceMarker => "NONCENONCENONCENONCENONC"u8;

    sealed class FuzzTransport(byte[] reply, int chunk) : PgTransport
    {
        int _at;

        public override int Receive(Span<byte> buffer)
        {
            int n = Math.Min(Math.Min(buffer.Length, chunk), reply.Length - _at);
            reply.AsSpan(_at, n).CopyTo(buffer);
            _at += n;
            return n; // 0 once it is all sent: the server closed
        }

        public override int ReceiveWaiting(Span<byte> buffer) => _at < reply.Length ? Receive(buffer) : 0;

        public override void Send(ReadOnlySpan<byte> data, Action? whileBlocked = null)
        {
            // SASLInitialResponse: 'p', length, "SCRAM-SHA-256\0", length, "n,,n=,r=<nonce>"
            if (data.Length < 5 || data[0] != 'p' || !data[5..].StartsWith("SCRAM-SHA-256\0"u8)) return;
            int r = data.LastIndexOf("r="u8);
            if (r < 0) return;
            ReadOnlySpan<byte> nonce = data[(r + 2)..];
            if (nonce.Length != NonceMarker.Length) return;
            Span<byte> rest = reply.AsSpan(_at);
            for (int i; (i = rest.IndexOf(NonceMarker)) >= 0; rest = rest[(i + nonce.Length)..])
                nonce.CopyTo(rest[i..]);
        }

        public override void Dispose() { }
    }

    // ---------- SCRAM, alone ----------

    // RFC 7677's test vector: user "user", password "pencil".
    const string Nonce = "rOprNGfwEbeRWgbNEkqO";
    internal static ReadOnlySpan<byte> ServerFirst => "r=rOprNGfwEbeRWgbNEkqO%hvYDpWUa2RaTCAfuxFIlj)hNlF$k0,s=W22ZaJ0SNY7soEsUEjb6gQ==,i=4096"u8;
    static readonly byte[] ServerSignature = Convert.FromBase64String("6rriTRBi23WpRR/wtup+mMhUZUn/dB5nLTJRsjl95G4=");

    /// <summary>PgScram.ClientFinal on a server-first-message: refused, or a client-final-message for our nonce;
    /// and a second one is always refused.</summary>
    public static void ScramClientFinal(ReadOnlySpan<byte> input)
    {
        var scram = new PgScram("pencil", "user", Nonce);
        byte[] final;
        try
        {
            final = scram.ClientFinal(input);
        }
        catch (PgConnectionException)
        {
            return;
        }
        string f = Encoding.ASCII.GetString(final);
        if (!f.StartsWith("c=biws,r=" + Nonce, StringComparison.Ordinal) || !f.Contains(",p=", StringComparison.Ordinal))
            throw new FuzzFinding("pg-scram-first: a malformed client-final-message", f);
        try
        {
            scram.ClientFinal(input);
        }
        catch (PgConnectionException)
        {
            return;
        }
        throw new FuzzFinding("pg-scram-first: a second server-first-message is taken", Findings.Show(input));
    }

    [ThreadStatic] static PgScram? t_signed;

    /// <summary>PgScram.VerifyServerFinal after the RFC's exchange: refused, unless the verifier is the RFC's
    /// server signature.</summary>
    public static void ScramServerFinal(ReadOnlySpan<byte> input)
    {
        PgScram scram = t_signed ??= Signed();
        try
        {
            scram.VerifyServerFinal(input);
        }
        catch (PgConnectionException)
        {
            return;
        }
        string verifier = Encoding.ASCII.GetString(input).Split(',')[0];
        Span<byte> signature = stackalloc byte[64];
        if (!verifier.StartsWith("v=", StringComparison.Ordinal) || !Convert.TryFromBase64String(verifier[2..], signature, out int n)
            || !signature[..n].SequenceEqual(ServerSignature))
            throw new FuzzFinding("pg-scram-final: a wrong server signature passes", Findings.Show(input));
    }

    static PgScram Signed()
    {
        var scram = new PgScram("pencil", "user", Nonce);
        scram.ClientFinal(ServerFirst);
        return scram;
    }

    // ---------- connection settings ----------

    /// <summary>PgSettings.Parse on any text: settings that keep their own rules, or a FormatException or
    /// ArgumentException.</summary>
    public static void Settings(ReadOnlySpan<byte> input)
    {
        string s = Encoding.UTF8.GetString(input);
        PgSettings p;
        try
        {
            p = PgSettings.Parse(s);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return;
        }
        if (p.User.Length == 0 || p.Database.Length == 0 || p.Port is <= 0 or > 65535 || p.Host is null)
            throw new FuzzFinding("pg-settings: parsed settings break their own rules", $"user {p.User}, database {p.Database}, port {p.Port}");
    }
}
