using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeFusion.Http;

namespace CodeFusion.Fuzz;

/// <summary>
/// The body binders' reader (JsonCursor) against Utf8JsonReader with the binders' options: the same tokens one by one
/// (type, start, bytes, escape, and a number's Int64, Int32 and Double readings) up to where either stops, and malformed
/// exactly where the reader throws or reads nothing. A cursor stricter than the reader would answer good bodies as
/// malformed; a looser one would bind what isn't JSON.
/// </summary>
static class JsonSyntaxTarget
{
    static readonly JsonReaderOptions Options = new() { MaxDepth = 64 };

    public static void Run(ReadOnlySpan<byte> input)
    {
        var ours = new System.Collections.Generic.List<string>();
        var c = new JsonCursor(input, 64);
        while (c.Read())
            ours.Add(Token(c.TokenType, c.TokenStart, c.ValueSpan, c.ValueIsEscaped, c.TokenType == JsonTokenType.Number
                ? Numbers(c.TryGetInt64(out long l), l, c.TryGetInt32(out int i), i, c.TryGetDouble(out double d), d) : ""));
        var theirs = new System.Collections.Generic.List<string>();
        bool threw = false;
        try
        {
            var r = new Utf8JsonReader(input, Options);
            while (r.Read())
                theirs.Add(Token(r.TokenType, r.TokenStartIndex, r.ValueSpan, r.ValueIsEscaped, r.TokenType == JsonTokenType.Number
                    ? Numbers(r.TryGetInt64(out long l), l, r.TryGetInt32(out int i), i, r.TryGetDouble(out double d), d) : ""));
        }
        catch (JsonException)
        {
            threw = true;
        }
        bool reader = !threw && theirs.Count > 0;
        if (reader == c.Malformed || reader != JsonSyntax.IsValid(input, 64))
            throw new FuzzFinding($"json-syntax: the reader {(reader ? "takes" : "refuses")} what the cursor {(c.Malformed ? "refuses" : "takes")}", Findings.Show(input));
        int both = Math.Min(ours.Count, theirs.Count);
        for (int k = 0; k < both; k++)
            if (ours[k] != theirs[k]) throw new FuzzFinding("json-syntax: a token differs from the reader's", $"{ours[k]} vs {theirs[k]}: {Findings.Show(input)}");
        if (reader && ours.Count != theirs.Count) throw new FuzzFinding("json-syntax: a token count differs from the reader's", Findings.Show(input));
    }

    static string Token(JsonTokenType type, long start, ReadOnlySpan<byte> bytes, bool escaped, string number) =>
        $"{type}@{start}:{Convert.ToHexString(bytes)}{(escaped ? "\\" : "")} {number}";

    static string Numbers(bool i64, long l, bool i32, int i, bool dbl, double d) =>
        $"{(i64 ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")} {(i32 ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")} {(dbl ? BitConverter.DoubleToInt64Bits(d).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")}";
}

/// <summary>
/// The bytes as a token's payload, signed with the secret, through Jwt.Check with a required username claim of at
/// most 64 bytes. Invariants: no exception; Verify agrees; a payload JsonDocument can't parse is invalid; a valid
/// token's sub and username are the last of their kind as JsonDocument reads them, its username UTF-8 within its
/// bounds; a token too long is invalid.
/// </summary>
static class JwtClaimsTarget
{
    const string Secret = "a-secret-of-exactly-thirty-two-b";
    const long Now = 1_700_000_000_000;
    static readonly Jwt Jwt = Make();

    static Jwt Make()
    {
        var o = new JwtOptions { Scheme = "Bearer" };
        o.Claims.Add(new JwtClaim("username") { MaxBytes = 64 });
        return new Jwt(Secret, o);
    }

    sealed class Arena : IRequestArena
    {
        public ArenaBytes Allocate(int length) => new(new byte[length + 3], 3, length);
    }

    public static void Run(ReadOnlySpan<byte> input)
    {
        // {"alg":"HS256","typ":"JWT"}
        string signing = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." + Base64Url.EncodeToString(input);
        string token = signing + "." + Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.ASCII.GetBytes(signing)));
        byte[] t = Encoding.ASCII.GetBytes(token);
        int[] claims = new int[2];
        TokenCheck check = Jwt.Check(t, Now, new Arena(), claims, out long id, out ArenaBytes payload);
        long verified = Jwt.Verify(t, Now);
        if (verified != (check == TokenCheck.Valid ? id : 0))
            throw new FuzzFinding("jwt-claims: Verify and Check disagree", $"{check} {id}, Verify {verified}: {Findings.Show(input)}");
        if (t.Length > Jwt.MaxTokenBytes)
        {
            if (check != TokenCheck.Invalid) throw new FuzzFinding("jwt-claims: a token past the limit is taken", Findings.Show(input));
            return;
        }
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(input.ToArray());
        }
        catch (JsonException)
        {
            if (check != TokenCheck.Invalid) throw new FuzzFinding("jwt-claims: a payload that isn't JSON is " + check, Findings.Show(input));
            return;
        }
        using (doc)
        {
            if (check != TokenCheck.Valid) return;
            if (id <= 0) throw new FuzzFinding("jwt-claims: valid with no user", Findings.Show(input));
            ReadOnlySpan<byte> name = claims[0] < 0 ? default : payload.Span.Slice(claims[0], claims[1]);
            if (claims[0] < 0 || name.Length > 64 || !System.Text.Unicode.Utf8.IsValid(name))
                throw new FuzzFinding("jwt-claims: valid with a username that breaks its rule", Findings.Show(input));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new FuzzFinding("jwt-claims: valid but not an object", Findings.Show(input));
            string? sub = null, username = null;
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (p.NameEquals("sub"u8)) sub = p.Value.GetString();
                else if (p.NameEquals("username"u8)) username = p.Value.GetString();
            }
            if (sub != id.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(sub?.Length ?? 0, '0'))
                throw new FuzzFinding("jwt-claims: sub differs from JsonDocument's", $"{id} vs {sub}: {Findings.Show(input)}");
            if (username != Encoding.UTF8.GetString(name))
                throw new FuzzFinding("jwt-claims: username differs from JsonDocument's", $"{Findings.Show(name)} vs {username}: {Findings.Show(input)}");
        }
    }
}
