using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>
/// String claims beside sub (<see cref="JwtOptions.Claims"/>): declared once, checked on every token after its
/// signature and exp, and read as views of the decoded payload in the request's arena. A token whose signature, header,
/// JSON or exp fails is invalid; one that passes those but whose sub or a declared claim breaks its rule has an invalid
/// payload.
/// </summary>
public sealed class JwtClaimTests
{
    internal const long Now = 1_700_000_000;
    const long NowMs = Now * 1000;

    // {"alg":"HS256","typ":"JWT"}
    const string Header = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9";

    internal static Jwt Username(int maxBytes = Jwt.MaxTokenBytes, int minBytes = 0, bool required = true, string scheme = "Bearer")
    {
        var o = new JwtOptions { Scheme = scheme };
        o.Claims.Add(new JwtClaim("username") { MaxBytes = maxBytes, MinBytes = minBytes, Required = required });
        return new Jwt(JwtTests.Secret, o);
    }

    /// <summary>A token of <paramref name="payload"/>'s exact bytes, signed with the test secret.</summary>
    internal static string Signed(byte[] payload, string header = Header, string secret = JwtTests.Secret)
    {
        string signing = header + "." + Base64Url.EncodeToString(payload);
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(signing));
        return signing + "." + Base64Url.EncodeToString(mac);
    }

    internal static string Signed(string payloadJson) => Signed(Encoding.UTF8.GetBytes(payloadJson));

    /// <summary>Counts what the verifier takes from the arena.</summary>
    internal sealed class CountingArena : IRequestArena
    {
        public int Allocations;

        public ArenaBytes Allocate(int length)
        {
            Allocations++;
            return new ArenaBytes(new byte[length + 7], 7, length); // not at 0: views must carry their offset
        }
    }

    internal readonly record struct Outcome(TokenCheck Check, long Id, string?[] Claims, int Allocations);

    internal static Outcome Read(Jwt jwt, string token, long nowMs = NowMs)
    {
        var arena = new CountingArena();
        int[] claims = new int[2 * jwt.ClaimCount];
        TokenCheck check = jwt.Check(Encoding.UTF8.GetBytes(token), nowMs, arena, claims, out long id, out ArenaBytes payload);
        var values = new string?[jwt.ClaimCount];
        for (int i = 0; i < values.Length; i++)
            if (check == TokenCheck.Valid && claims[2 * i] >= 0) values[i] = Encoding.UTF8.GetString(payload.Span.Slice(claims[2 * i], claims[2 * i + 1]));
        return new Outcome(check, id, values, arena.Allocations);
    }

    static string P(string username, string sub = "\"42\"", long exp = Now + 60) => $$"""{"sub":{{sub}},"username":{{username}},"iat":{{Now}},"exp":{{exp}}}""";

    // ---- declaring ----

    [Theory]
    [InlineData("")]
    [InlineData("sub")]
    [InlineData("iat")]
    [InlineData("exp")]
    [InlineData("nbf")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    public void AClaimNameTheVerifierCantReadIsRefused(string name) =>
        Assert.Throws<ArgumentException>(() => new JwtClaim(name));

    [Fact]
    public void ClaimsAreCheckedWhenTheJwtIsMade()
    {
        Assert.Throws<ArgumentNullException>(() => new JwtClaim(null!));
        Assert.Throws<ArgumentException>(() => new JwtClaim(new string('a', 65)));
        _ = new JwtClaim(new string('a', 64));
        _ = new JwtClaim("名前");
        static Jwt With(params JwtClaim[] claims)
        {
            var o = new JwtOptions();
            foreach (JwtClaim c in claims) o.Claims.Add(c);
            return new Jwt(JwtTests.Secret, o);
        }
        Assert.Throws<ArgumentException>(() => With(new JwtClaim("a"), new JwtClaim("a")));
        Assert.Throws<ArgumentException>(() => With(new JwtClaim("a") { MinBytes = -1 }));
        Assert.Throws<ArgumentException>(() => With(new JwtClaim("a") { MinBytes = 5, MaxBytes = 4 }));
        Assert.Throws<ArgumentException>(() => With(new JwtClaim("a") { MaxBytes = Jwt.MaxTokenBytes + 1 }));
        Assert.Throws<ArgumentException>(() => With([null!]));
        Assert.Throws<ArgumentException>(() => new JwtClaim("a\ud800"));            // a lone surrogate isn't text
        var many = new JwtClaim[Jwt.MaxClaims + 1];
        for (int i = 0; i < many.Length; i++) many[i] = new JwtClaim("c" + i);
        Assert.Throws<ArgumentException>(() => With(many));
        Assert.Equal(Jwt.MaxClaims, With(many[..^1]).ClaimCount);
        Assert.Equal(2, With(new JwtClaim("a"), new JwtClaim("A")).ClaimCount); // names are case-sensitive, as JSON's
        var defaults = new JwtClaim("x");
        Assert.Equal(("x", true, 0, Jwt.MaxTokenBytes), (defaults.Name, defaults.Required, defaults.MinBytes, defaults.MaxBytes));
    }

    [Fact]
    public void TheOptionsAreReadOnceWhenTheJwtIsMade()
    {
        var o = new JwtOptions { Scheme = "Bearer" };
        o.Claims.Add(new JwtClaim("username"));
        var jwt = new Jwt(JwtTests.Secret, o);
        o.Claims.Clear();
        o.Scheme = "Token";
        Assert.Equal((1, "Bearer"), (jwt.ClaimCount, jwt.Scheme));
        Assert.Equal(TokenCheck.InvalidPayload, Read(jwt, Signed($$"""{"sub":"42","exp":{{Now + 60}}}""")).Check);
    }

    // ---- reading ----

    [Theory]
    [InlineData("\"jake\"", "jake")]
    [InlineData("\"\"", "")]
    [InlineData("\"j\\u00e9ake\"", "jéake")]
    [InlineData("\"a\\\"b\\\\c\\/d\\n\"", "a\"b\\c/d\n")]
    [InlineData("\"日本語\"", "日本語")]
    [InlineData("\"\\ud83d\\ude00\"", "😀")]
    [InlineData("\"😀\"", "😀")]
    public void AStringClaimIsReadAsItsUnescapedText(string json, string expected)
    {
        Outcome o = Read(Username(), Signed(P(json)));
        Assert.Equal((TokenCheck.Valid, 42L, expected), (o.Check, o.Id, o.Claims[0]));
        Assert.Equal(1, o.Allocations);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[\"jake\"]")]
    [InlineData("\"\\ud800\"")]          // a lone surrogate doesn't unescape to text
    [InlineData("\"\\udc00x\"")]
    public void AClaimThatIsntTextIsAnInvalidPayload(string json) =>
        Assert.Equal(TokenCheck.InvalidPayload, Read(Username(), Signed(P(json))).Check);

    [Fact]
    public void ARawClaimThatIsntUtf8IsAnInvalidPayloadOrMalformed()
    {
        byte[] payload = Encoding.UTF8.GetBytes(P("\"ja\u00ffke\""));
        int at = Array.IndexOf(payload, (byte)0xC3);
        payload[at] = 0xFF; // a byte no UTF-8 has
        Assert.NotEqual(TokenCheck.Valid, Read(Username(), Signed(payload)).Check);
    }

    [Fact]
    public void ARequiredClaimMustBeThere()
    {
        string none = Signed($$"""{"sub":"42","exp":{{Now + 60}}}""");
        Assert.Equal(TokenCheck.InvalidPayload, Read(Username(), none).Check);
        Outcome optional = Read(Username(required: false), none);
        Assert.Equal((TokenCheck.Valid, 42L, (string?)null), (optional.Check, optional.Id, optional.Claims[0]));
        // null is no claim, as a body field's null is no field
        Outcome nulled = Read(Username(required: false), Signed(P("null")));
        Assert.Equal((TokenCheck.Valid, (string?)null), (nulled.Check, nulled.Claims[0]));
        Assert.Equal(0, Username().Verify(Encoding.ASCII.GetBytes(none), NowMs));
        Assert.Equal(42, Username(required: false).Verify(Encoding.ASCII.GetBytes(none), NowMs));
        Assert.Equal(42, Username().Verify(Encoding.ASCII.GetBytes(Signed(P("\"jake\""))), NowMs));
    }

    [Theory]
    [InlineData(0, 4, "\"\"", true)]
    [InlineData(1, 4, "\"\"", false)]
    [InlineData(1, 4, "\"a\"", true)]
    [InlineData(1, 4, "\"abcd\"", true)]
    [InlineData(1, 4, "\"abcde\"", false)]
    [InlineData(1, 4, "\"é\\u00e9\"", true)]        // bytes once unescaped: 4
    [InlineData(1, 4, "\"éé\\u00e9\"", false)]      // 6
    [InlineData(1, 4, "\"\\u0061\\u0062\\u0063\\u0064\"", true)]
    public void TheClaimsLengthIsItsUnescapedUtf8Bytes(int min, int max, string json, bool valid) =>
        Assert.Equal(valid ? TokenCheck.Valid : TokenCheck.InvalidPayload, Read(Username(max, min), Signed(P(json))).Check);

    [Fact]
    public void AHugeClaimIsReadWhileTheTokenFitsItsLimit()
    {
        string big = new('x', 2900);
        string token = Signed(P("\"" + big + "\""));
        Assert.True(token.Length <= Jwt.MaxTokenBytes);
        Assert.Equal(big, Read(Username(), token).Claims[0]);
        Assert.Equal(TokenCheck.InvalidPayload, Read(Username(maxBytes: 64), token).Check);
        string over = Signed(P("\"" + new string('x', 3100) + "\""));
        Assert.True(over.Length > Jwt.MaxTokenBytes);
        Assert.Equal(TokenCheck.Invalid, Read(Username(), over).Check);
    }

    [Fact]
    public void TheLastOfRepeatedClaimsCountsButAnyBadOneSpoilsIt()
    {
        Assert.Equal("b", Read(Username(), Signed($$"""{"sub":"42","username":"a","username":"b","exp":{{Now + 60}}}""")).Claims[0]);
        Assert.Equal(TokenCheck.InvalidPayload, Read(Username(), Signed($$"""{"sub":"42","username":5,"username":"b","exp":{{Now + 60}}}""")).Check);
        Assert.Equal(TokenCheck.InvalidPayload, Read(Username(), Signed($$"""{"sub":"42","username":"a","username":null,"exp":{{Now + 60}}}""")).Check);
        Assert.Equal(TokenCheck.InvalidPayload, Read(Username(), Signed($$"""{"sub":"x","sub":"42","username":"a","exp":{{Now + 60}}}""")).Check);
        Assert.Equal(TokenCheck.InvalidPayload, Read(Username(), Signed($$"""{"sub":"42","sub":"0","username":"a","exp":{{Now + 60}}}""")).Check);
    }

    [Fact]
    public void AnEscapedNameIsTheClaimItSpells()
    {
        Assert.Equal("jake", Read(Username(), Signed($$"""{"sub":"42","user\u006eame":"jake","exp":{{Now + 60}}}""")).Claims[0]);
        Assert.Equal(TokenCheck.InvalidPayload, Read(Username(), Signed($$"""{"sub":"42","Username":"jake","exp":{{Now + 60}}}""")).Check);
        Assert.Equal(TokenCheck.Invalid, Read(Username(), Signed($$"""{"sub":"42","\ud800":"jake","username":"a","exp":{{Now + 60}}}""")).Check);
    }

    [Fact]
    public void ALongDeclaredNameMatchesThroughEscapes()
    {
        string name = new('n', 64);
        var o = new JwtOptions();
        o.Claims.Add(new JwtClaim(name));
        var jwt = new Jwt(JwtTests.Secret, o);
        var escaped = new StringBuilder();
        foreach (char c in name) escaped.Append("\\u006e");
        Assert.Equal("v", Read(jwt, Signed($$"""{"sub":"42","{{escaped}}":"v","exp":{{Now + 60}}}""")).Claims[0]);
        Assert.Equal(TokenCheck.InvalidPayload, Read(jwt, Signed($$"""{"sub":"42","{{escaped}}n":"v","exp":{{Now + 60}}}""")).Check);
    }

    // sub isn't a positive integer string: the token is genuine, its payload isn't one this app reads.
    [Theory]
    [InlineData("\"0\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"abc\"")]
    [InlineData("\"1.5\"")]
    [InlineData("\" 42\"")]
    [InlineData("\"\"")]
    [InlineData("\"\\u0034\\u0032\"")]   // "42", escaped: one spelling only
    [InlineData("\"99999999999999999999\"")]
    [InlineData("\"000000000000000000042\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("[\"42\"]")]
    public void ASubThatIsntAPositiveIntegerStringIsAnInvalidPayload(string sub) =>
        Assert.Equal(TokenCheck.InvalidPayload, Read(Username(), Signed(P("\"jake\"", sub))).Check);

    [Fact]
    public void NoSubIsAnInvalidPayload() =>
        Assert.Equal(TokenCheck.InvalidPayload, Read(Username(), Signed($$"""{"username":"jake","exp":{{Now + 60}}}""")).Check);

    // what fails before the payload's own rules: the token is invalid, whatever its claims
    [Fact]
    public void ASignatureHeaderJsonOrExpThatFailsIsInvalidAndTheArenaIsntTouched()
    {
        Jwt jwt = Username();
        string good = Signed(P("\"jake\""));
        Assert.Equal(TokenCheck.Valid, Read(jwt, good).Check);
        string forged = good[..^2] + (good[^2] == 'A' ? "B" : "A") + good[^1];
        Outcome f = Read(jwt, forged);
        Assert.Equal((TokenCheck.Invalid, 0L, 0), (f.Check, f.Id, f.Allocations));
        Outcome other = Read(jwt, Signed(Encoding.UTF8.GetBytes(P("\"jake\"")), secret: JwtTests.Secret + "x"));
        Assert.Equal((TokenCheck.Invalid, 0), (other.Check, other.Allocations));
        // expired with a bad sub and a bad claim: expiry decides
        Assert.Equal(TokenCheck.Invalid, Read(jwt, Signed(P("5", "\"0\"", exp: Now))).Check);
        Assert.Equal(TokenCheck.Invalid, Read(jwt, Signed($$"""{"sub":"42","username":"jake"}""")).Check);
        Assert.Equal(TokenCheck.Invalid, Read(jwt, Signed($$"""{"sub":"42","username":"jake","exp":"{{Now + 60}}"}""")).Check);
        Assert.Equal(TokenCheck.Invalid, Read(jwt, Signed($$"""{"sub":"42","username":"jake","exp":{{Now + 60}},"nbf":{{Now + 10}}}""")).Check);
        Assert.Equal(TokenCheck.Invalid, Read(jwt, Signed("""["sub","42"]""")).Check);
        Assert.Equal(TokenCheck.Invalid, Read(jwt, Signed("""{"sub":"42","username":"ja""")).Check);
        Assert.Equal(TokenCheck.Invalid, Read(jwt, Signed("not json")).Check);
        Assert.Equal(TokenCheck.Invalid, Read(jwt, Signed(Encoding.UTF8.GetBytes(P("\"jake\"")), header: "eyJhbGciOiJub25lIn0")).Check); // {"alg":"none"}
        Assert.Equal(TokenCheck.Invalid, Read(jwt, "a.b").Check);
        Assert.Equal(TokenCheck.Invalid, Read(jwt, "").Check);
        Assert.Equal(TokenCheck.Invalid, Read(jwt, good + "=").Check);
        Assert.Equal(TokenCheck.Invalid, Read(jwt, "é" + good).Check);
    }

    // Found by the jwt-claims fuzz target: the payload's reader stopped at the root's end, so bytes after it went
    // unread. A signed token is still only JSON when nothing but whitespace follows, as JsonDocument requires.
    [Theory]
    [InlineData(" x")]
    [InlineData("+")]
    [InlineData("{}")]
    [InlineData("}")]
    [InlineData("\"")]
    [InlineData(",")]
    public void AnythingAfterThePayloadOrHeaderMakesTheTokenInvalid(string after)
    {
        string payload = $$"""{"sub":"42","username":"jake","exp":{{Now + 60}}}""";
        Assert.Equal(TokenCheck.Valid, Read(Username(), Signed(payload + " \n\t")).Check);
        Assert.Equal(TokenCheck.Invalid, Read(Username(), Signed(payload + after)).Check);
        var plain = new Jwt(JwtTests.Secret);
        Assert.Equal(42, plain.Verify(Encoding.ASCII.GetBytes(Signed(payload + " ")), NowMs));
        Assert.Equal(0, plain.Verify(Encoding.ASCII.GetBytes(Signed(payload + after)), NowMs));
        byte[] p = Encoding.UTF8.GetBytes(payload);
        string header = Base64Url.EncodeToString(Encoding.ASCII.GetBytes("""{"alg":"HS256"}""" + after));
        Assert.Equal(TokenCheck.Invalid, Read(Username(), Signed(p, header)).Check);
        Assert.Equal(TokenCheck.Valid, Read(Username(), Signed(p, Base64Url.EncodeToString(Encoding.ASCII.GetBytes("""{"alg":"HS256"} """)))).Check);
    }

    // Found in the second review: an exp of 1e400 reads as infinity, so a token carrying it never expired. A time that
    // isn't a finite number is a payload this app can't read; an exp that is past, -1e400 among them, stays expired.
    [Theory]
    [InlineData("\"exp\":1e400", nameof(TokenCheck.InvalidPayload))]
    [InlineData("\"exp\":1e400,\"nbf\":1", nameof(TokenCheck.InvalidPayload))]
    [InlineData("\"exp\":{later},\"nbf\":-1e400", nameof(TokenCheck.InvalidPayload))]
    [InlineData("\"exp\":-1e400", nameof(TokenCheck.Invalid))]
    [InlineData("\"exp\":{later},\"nbf\":1e400", nameof(TokenCheck.Invalid))]
    [InlineData("\"exp\":1e300", nameof(TokenCheck.Valid))]
    [InlineData("\"exp\":{later},\"nbf\":-1e300", nameof(TokenCheck.Valid))]
    public void ATimeThatIsntAFiniteNumberIsntAUsablePayload(string times, string outcome)
    {
        string payload = "{\"sub\":\"42\",\"username\":\"jake\"," + times.Replace("{later}", (Now + 60).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) + "}";
        Assert.Equal(Enum.Parse<TokenCheck>(outcome), Read(Username(), Signed(payload)).Check);
        long expected = outcome == nameof(TokenCheck.Valid) ? 42 : 0;
        Assert.Equal(expected, new Jwt(JwtTests.Secret).Verify(Encoding.ASCII.GetBytes(Signed(payload)), NowMs));
    }

    [Fact]
    public void ARawByteAfterThePayloadIsInvalid()
    {
        byte[] payload = [.. Encoding.UTF8.GetBytes($$"""{"sub":"42","username":"jake","exp":{{Now + 60}}}"""), 0xBF];
        Assert.Equal(TokenCheck.Invalid, Read(Username(), Signed(payload)).Check);
    }

    [Fact]
    public void WithoutDeclaredClaimsNothingIsTakenFromTheArena()
    {
        var jwt = new Jwt(JwtTests.Secret, new JwtOptions { Scheme = "Bearer" });
        Outcome o = Read(jwt, Signed(P("\"jake\"")));
        Assert.Equal((TokenCheck.Valid, 42L, 0), (o.Check, o.Id, o.Allocations));
        Assert.Equal(TokenCheck.InvalidPayload, Read(jwt, Signed(P("\"jake\"", "\"0\""))).Check);
    }

    // ---- the header ----

    [Theory]
    [InlineData(null, nameof(TokenCheck.Missing))]
    [InlineData("", nameof(TokenCheck.Missing))]
    [InlineData("Token {t}", nameof(TokenCheck.Missing))]
    [InlineData("bearer {t}", nameof(TokenCheck.Missing))]
    [InlineData("Bearer", nameof(TokenCheck.Missing))]
    [InlineData("Bearer {t}", nameof(TokenCheck.Valid))]
    [InlineData("Bearer  {t}", nameof(TokenCheck.Invalid))]
    [InlineData("Bearer {t}x", nameof(TokenCheck.Invalid))]
    [InlineData("Bearer x.y.z", nameof(TokenCheck.Invalid))]
    public void AHeaderWithoutTheSchemeHasNoToken(string? header, string outcome)
    {
        TokenCheck expected = Enum.Parse<TokenCheck>(outcome);
        Jwt jwt = Username();
        string token = Signed(P("\"jake\"", exp: DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60));
        byte[] h = header is null ? [] : Encoding.UTF8.GetBytes(header.Replace("{t}", token, StringComparison.Ordinal));
        int[] claims = new int[2];
        Assert.Equal(expected, jwt.Authenticate(header is not null, h, new CountingArena(), claims, out long id, out _));
        Assert.Equal(expected == TokenCheck.Valid ? 42 : 0, id);
    }

    // ---- issuing ----

    [Theory]
    [InlineData("jake")]
    [InlineData("")]
    [InlineData("a\"b\\c/d")]
    [InlineData("tab\tnewline\ncontrol\u0001del\u007f")]
    [InlineData("日本語😀")]
    [InlineData("\u2028\u2029")]
    public void IssuedClaimsReadBackAsTheyWere(string value)
    {
        Jwt jwt = Username();
        byte[] v = Encoding.UTF8.GetBytes(value);
        Utf8[] claims = [new Utf8(v, 0, v.Length)];
        var dest = new byte[jwt.MaxIssuedBytesWith(claims)];
        int n = jwt.Issue(42, Now, claims, dest);
        string token = Encoding.ASCII.GetString(dest, 0, n);
        Outcome o = Read(jwt, token);
        Assert.Equal((TokenCheck.Valid, 42L, value), (o.Check, o.Id, o.Claims[0]));
        // a standard token: any JSON reader sees the same claim
        using JsonDocument doc = JsonDocument.Parse(Base64Url.DecodeFromChars(token.Split('.')[1]));
        Assert.Equal(value, doc.RootElement.GetProperty("username").GetString());
        Assert.Equal("42", doc.RootElement.GetProperty("sub").GetString());
        Assert.Equal(Now + 30L * 24 * 3600, doc.RootElement.GetProperty("exp").GetInt64());
    }

    [Fact]
    public void IssuingChecksTheClaimsAsReadingWould()
    {
        Jwt jwt = Username(maxBytes: 8, minBytes: 1);
        var dest = new byte[4096];
        static Utf8 U(string s) { byte[] b = Encoding.UTF8.GetBytes(s); return new Utf8(b, 0, b.Length); }
        Assert.Throws<ArgumentException>(() => jwt.Issue(42, Now, [], dest));                 // a required claim left out
        Assert.Throws<ArgumentException>(() => jwt.Issue(42, Now, [U("a"), U("b")], dest));   // one too many
        Assert.Throws<ArgumentException>(() => jwt.Issue(42, Now, [U("")], dest));            // under MinBytes
        Assert.Throws<ArgumentException>(() => jwt.Issue(42, Now, [U("123456789")], dest));   // over MaxBytes
        Assert.Throws<ArgumentException>(() => jwt.Issue(42, Now, [new Utf8([0xFF], 0, 1)], dest)); // not UTF-8
        Assert.Throws<ArgumentException>(() => jwt.Issue(42, Now, [U("ok")], new byte[100]));       // dest too small
        Assert.True(jwt.Issue(42, Now, [U("ok")], new byte[jwt.MaxIssuedBytesWith([U("ok")])]) <= jwt.MaxIssuedBytesWith([U("ok")]));
        Assert.Throws<ArgumentException>(() => jwt.Issue(42, Now, dest));                      // the old overload has no claims
        Assert.True(jwt.Issue(42, Now, [U("12345678")], dest) > 0);

        Jwt optional = Username(required: false);
        int n = optional.Issue(42, Now, dest);
        Outcome o = Read(optional, Encoding.ASCII.GetString(dest, 0, n));
        Assert.Equal((TokenCheck.Valid, (string?)null), (o.Check, o.Claims[0]));

        // a token too long to verify isn't issued
        Jwt big = Username();
        Utf8 huge = U(new string('"', 2000));
        Assert.Throws<ArgumentException>(() => big.MaxIssuedBytesWith([huge]));        // its escapes alone are past the limit
        Assert.Throws<ArgumentException>(() => big.Issue(42, Now, [huge], new byte[8192]));
        Utf8 long3000 = U(new string('x', 3000));                                  // fits as JSON, not as base64
        Assert.Throws<ArgumentException>(() => big.Issue(42, Now, [long3000], new byte[big.MaxIssuedBytesWith([long3000])]));
        Utf8 fits = U(new string('x', 2900));
        var room = new byte[big.MaxIssuedBytesWith([fits])];
        Assert.True(room.Length <= Jwt.MaxTokenBytes + 100);
        Assert.Equal(TokenCheck.Valid, Read(big, Encoding.ASCII.GetString(room, 0, big.Issue(42, Now, [fits], room))).Check);
    }

    /// <summary>A response whose arena counts what it hands out; nothing else is used.</summary>
    sealed class ArenaOnly : IResponse
    {
        public readonly CountingArena Counted = new();
        public int Status { get; set; }
        public int RetryAfter { get; set; }
        public Utf8JsonWriter Json => throw new NotSupportedException();
        public System.Buffers.IBufferWriter<byte> Body => throw new NotSupportedException();
        public void Reset() { }
        public IRequestArena Arena => Counted;
        public System.Threading.CancellationToken Aborted => default;
        public MediaType? ContentType { get; set; }
    }

    // Found in review: the arena was asked for six times a value's length before the value was checked, so a request
    // that made a handler issue a 1 MB claim took about 8 MB from the pool before the 500.
    [Fact]
    public void AValueIssueRefusesTakesNothingFromTheArena()
    {
        Jwt jwt = Username(maxBytes: 8);
        var response = new ArenaOnly();
        jwt.Bind(response);
        static Utf8 U(string s) { byte[] b = Encoding.UTF8.GetBytes(s); return new Utf8(b, 0, b.Length); }
        Assert.Throws<ArgumentException>(() => jwt.Issue(42, U(new string('x', 1 << 20))));
        Assert.Throws<ArgumentException>(() => jwt.Issue(42));
        Assert.Throws<ArgumentException>(() => jwt.Issue(42, U("a"), U("b")));
        Assert.Equal(0, response.Counted.Allocations);
        Utf8 token = jwt.Issue(42, U("jake"));
        Assert.Equal(1, response.Counted.Allocations);
        Assert.Equal(TokenCheck.Valid, Read(jwt, token.ToString(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Check);
    }

    // Found in the second review: only the payload was held to the token's limit before the arena was asked, so a value
    // of about 2,950 to 4,010 bytes took 4 to 5 KB and was refused after it was written.
    [Fact]
    public void ATokenPastItsLimitIsRefusedBeforeTheArenaIsAsked()
    {
        Jwt jwt = Username(maxBytes: 4096);
        var response = new ArenaOnly();
        jwt.Bind(response);
        byte[] big = Encoding.ASCII.GetBytes(new string('x', 3200));
        Assert.Throws<ArgumentException>(() => jwt.Issue(42, new Utf8(big, 0, big.Length)));
        Assert.Equal(0, response.Counted.Allocations);
    }

    // The limit is the real token's, its numbers as long as they are: a value refused would have made a token past 4 KB,
    // and one taken makes exactly the token this formula says.
    [Fact]
    public void ATokenIsRefusedExactlyWhenItWouldPassTheLimit()
    {
        Jwt jwt = Username(maxBytes: 4096);
        var dest = new byte[8192];
        int refused = 0;
        for (int n = 2900; n <= 3100; n++)
        {
            byte[] v = Encoding.ASCII.GetBytes(new string('x', n));
            int payload = $"{{\"sub\":\"42\",\"iat\":{Now},\"exp\":{Now + 30L * 24 * 3600},\"username\":\"".Length + n + 2; // the closing quote and brace
            int expected = 36 + 1 + (payload * 4 + 2) / 3 + 1 + 43;
            if (expected > Jwt.MaxTokenBytes)
            {
                Assert.Throws<ArgumentException>(() => jwt.Issue(42, Now, [new Utf8(v, 0, v.Length)], dest));
                refused++;
            }
            else Assert.Equal(expected, jwt.Issue(42, Now, [new Utf8(v, 0, v.Length)], dest));
        }
        Assert.InRange(refused, 1, 200);
    }

    // Found in the second review: Issue wrote "sub":"0" and "sub":"-5", tokens the verifier refuses on every request.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(long.MinValue)]
    public void AUserTheVerifierWouldRefuseIsntIssued(long user)
    {
        var plain = new Jwt(JwtTests.Secret);
        var dest = new byte[Jwt.MaxIssuedBytes];
        Assert.Throws<ArgumentOutOfRangeException>(() => plain.Issue(user, Now, dest));
        Assert.Throws<ArgumentOutOfRangeException>(() => plain.Issue(user, dest));
        var response = new ArenaOnly();
        plain.Bind(response);
        Assert.Throws<ArgumentOutOfRangeException>(() => plain.Issue(user));
        Assert.Equal(0, response.Counted.Allocations);
        Assert.Equal(1, plain.Verify(dest.AsSpan(0, plain.Issue(1, Now, dest)), NowMs));
    }

    // Found in review: the overloads that take a lifetime named "options" in their exception, a parameter they don't have.
    [Fact]
    public void ABadLifetimeIsNamedAsTheCallerWroteIt()
    {
        Assert.Equal("lifetime", Assert.Throws<ArgumentOutOfRangeException>(() => new Jwt(JwtTests.Secret, TimeSpan.Zero)).ParamName);
        Assert.Equal("lifetime", Assert.Throws<ArgumentOutOfRangeException>(() => new Jwt(JwtTests.Secret, TimeSpan.FromMilliseconds(500), true)).ParamName);
        Assert.Equal("lifetime", Assert.Throws<ArgumentOutOfRangeException>(() => HttpApp.CreateBuilder([]).AddJwtAuth(JwtTests.Secret, TimeSpan.Zero)).ParamName);
        Assert.Equal("options", Assert.Throws<ArgumentOutOfRangeException>(() => new Jwt(JwtTests.Secret, new JwtOptions { Lifetime = TimeSpan.Zero })).ParamName);
    }

    // ---- against a plain reading of the rules ----

    static readonly string[] Subs = ["\"42\"", "\"7\"", "\"0\"", "\"-1\"", "\"abc\"", "42", "null", "\"\\u0034\"", "{}", "\"9223372036854775807\"", "\"9223372036854775808\""];
    static readonly string[] Names = ["\"jake\"", "\"\"", "\"j\\u00e9\"", "\"\\ud800\"", "5", "null", "true", "[]", "{\"a\":1}", "\"日本\"", "\"\\\"\""];
    static readonly string[] Keys = ["sub", "username", "exp", "nbf", "iat", "user\\u006eame", "Username", "x"];

    static Gen<string> Payloads()
    {
        var value = Gen.Elements([.. Subs, .. Names, $"{Now + 60}", $"{Now - 1}", $"{Now}", $"\"{Now + 60}\"", "1e99", "[1,[2]]"]);
        var member = from k in Gen.Elements(Keys) from v in value select $"\"{k}\":{v}";
        var exp = Gen.Elements($"\"exp\":{Now + 60}", $"\"exp\":{Now}", $"\"exp\":{Now + 60}.5", "");
        return from members in member.ListOf()
               from e in exp
               from at in Gen.Choose(0, 100)
               select "{" + string.Join(",", Insert(members, e, at)) + "}";
    }

    static string[] Insert(System.Collections.Generic.IEnumerable<string> members, string exp, int at)
    {
        var all = new System.Collections.Generic.List<string>(members);
        if (exp.Length > 0) all.Insert(at % (all.Count + 1), exp);
        return [.. all];
    }

    /// <summary>The rules as JsonDocument reads the payload: what doesn't parse, isn't an object, has a name that isn't
    /// text, or has an exp or nbf that fails is invalid; then sub and username decide.</summary>
    static (TokenCheck, long, string?) Rules(string json, int maxBytes)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return (TokenCheck.Invalid, 0, null);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (TokenCheck.Invalid, 0, null);
            double? exp = null, nbf = null;
            long sub = 0;
            string? name = null;
            bool bad = false;
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                string key;
                try
                {
                    key = p.Name;
                }
                catch (InvalidOperationException)
                {
                    return (TokenCheck.Invalid, 0, null);
                }
                JsonElement v = p.Value;
                switch (key)
                {
                    case "exp" or "nbf":
                        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double d)) return (TokenCheck.Invalid, 0, null);
                        if (key == "exp") exp = d;
                        else nbf = d;
                        break;
                    case "sub":
                        string raw = v.GetRawText();
                        if (v.ValueKind != JsonValueKind.String || raw.Contains('\\', StringComparison.Ordinal) || raw.Length - 2 is 0 or > 20
                            || !long.TryParse(raw[1..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out sub))
                        {
                            bad = true;
                            sub = 0;
                        }
                        break;
                    case "username":
                        if (v.ValueKind == JsonValueKind.Null)
                        {
                            name = null;
                            break;
                        }
                        try
                        {
                            name = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                        }
                        catch (InvalidOperationException)
                        {
                            name = null;
                        }
                        if (name is null || Encoding.UTF8.GetByteCount(name) > maxBytes) bad = true;
                        break;
                }
            }
            if (exp is not { } e || !(Now < e)) return (TokenCheck.Invalid, 0, null);
            if (nbf is { } n && Now < n) return (TokenCheck.Invalid, 0, null);
            if (bad || sub <= 0 || name is null) return (TokenCheck.InvalidPayload, 0, null);
            return (TokenCheck.Valid, sub, name);
        }
    }

    [Property(MaxTest = 3000)]
    public Property PayloadsAreReadAsTheRulesSay() => Prop.ForAll(Payloads().ToArbitrary(), json =>
    {
        var expected = Rules(json, 4);
        Outcome o = Read(Username(maxBytes: 4), Signed(json));
        Assert.Equal(expected, (o.Check, o.Id, o.Claims[0]));
        return true;
    });
}
