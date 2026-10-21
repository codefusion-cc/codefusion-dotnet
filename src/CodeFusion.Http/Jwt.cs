using System;
using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeFusion.Http;

/// <summary>
/// HS256 JWTs: <c>{"sub": "&lt;id&gt;", "iat", "exp": iat + <see cref="Lifetime"/>}</c> (30 days unless set), and
/// the string claims <see cref="JwtOptions.Claims"/> declares, issued and verified over bytes with nothing allocated.
/// HS256 only, with no iss, aud or jti: a token can't be revoked before its exp except by changing the secret, which
/// revokes them all. <c>builder.AddJwtAuth(secret)</c> registers one per connection, so a handler that takes a
/// <see cref="Jwt"/> can <see cref="Issue(long)"/> a token into the request's arena; requests that take a
/// <see cref="CodeFusion.Http.Viewer"/> are verified with the same secret (signature, then exp, then sub and the claims, every request).
/// </summary>
public sealed class Jwt : IRequestScoped
{
    /// <summary>The shortest secret, in UTF-8 bytes: HS256 wants a key at least as long as its 32-byte hash. Only
    /// <c>allowShortSecret</c> takes a shorter one.</summary>
    public const int MinSecretBytes = 32;

    /// <summary>The longest token this server accepts.</summary>
    public const int MaxTokenBytes = 4096;

    /// <summary>The most claims <see cref="JwtOptions.Claims"/> may declare.</summary>
    public const int MaxClaims = 16;

    /// <summary>The most bytes <see cref="Issue(long, Span{byte})"/> writes: header, payload with three 20-digit
    /// numbers, signature. With claims, <see cref="MaxIssuedBytesWith"/>.</summary>
    public const int MaxIssuedBytes = 36 + 1 + (MaxPayloadBytes + 2) / 3 * 4 + 1 + 43;

    const int MaxPayloadBytes = 24 + 3 * 20; // {"sub":"","iat":,"exp":} and the numbers

    // {"alg":"HS256","typ":"JWT"}
    static ReadOnlySpan<byte> HeaderB64 => "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"u8;
    readonly byte[] _key;
    readonly long _ttl;
    readonly byte[] _scheme; // the scheme and its space
    readonly JwtClaim[] _claims;
    IResponse? _response;
    // Unpadded base64url and the two dots. .NET's decoder would also skip whitespace and accept padding; JWTs have neither.
    static readonly SearchValues<byte> TokenChars = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_."u8);
    // RFC 9110's tchar
    static readonly SearchValues<char> SchemeChars = SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    [ThreadStatic] static HMACSHA256? t_hmac;
    [ThreadStatic] static byte[]? t_hmacKey;

    /// <summary>Tokens signed with <paramref name="secret"/>'s UTF-8 bytes (at least <see cref="MinSecretBytes"/>),
    /// valid for 30 days.</summary>
    public Jwt(string secret) : this(secret, new JwtOptions()) { }

    /// <summary>Tokens signed with <paramref name="secret"/>'s UTF-8 bytes (at least <see cref="MinSecretBytes"/>),
    /// valid for <paramref name="lifetime"/> (whole seconds, at least one) from their issue.</summary>
    public Jwt(string secret, TimeSpan lifetime) : this(secret, Options(lifetime, false)) { }

    /// <summary>
    /// <see cref="Jwt(string, TimeSpan)"/>, and with <paramref name="allowShortSecret"/> a secret shorter than
    /// <see cref="MinSecretBytes"/> (but not empty) too, for a deployment handed a fixed one. Its tokens are standard
    /// HS256 (RFC 2104 pads a short key), but a short or guessable secret can be found offline from any one token,
    /// and then anyone can sign as anyone: only when the secret isn't yours to choose (SECURITY.md).
    /// </summary>
    public Jwt(string secret, TimeSpan lifetime, bool allowShortSecret)
        : this(secret, Options(lifetime, allowShortSecret)) { }

    /// <summary>The options of the overloads that take a lifetime, checked under its own name.</summary>
    internal static JwtOptions Options(TimeSpan lifetime, bool allowShortSecret)
    {
        if (lifetime < TimeSpan.FromSeconds(1) || lifetime.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "whole seconds, at least one");
        return new JwtOptions { Lifetime = lifetime, AllowShortSecret = allowShortSecret };
    }

    /// <summary>Tokens signed with <paramref name="secret"/>'s UTF-8 bytes, issued and read as
    /// <paramref name="options"/> say: their lifetime, whether a short secret is taken, the header's scheme and the
    /// claims beside sub. The options are read now; changing them later changes nothing.</summary>
    public Jwt(string secret, JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (!options.AllowShortSecret && Encoding.UTF8.GetByteCount(secret) < MinSecretBytes)
            throw new ArgumentException($"the secret is shorter than {MinSecretBytes} bytes of UTF-8 (allowShortSecret takes it)", nameof(secret));
        TimeSpan lifetime = options.Lifetime;
        if (lifetime < TimeSpan.FromSeconds(1) || lifetime.Ticks % TimeSpan.TicksPerSecond != 0)
            throw new ArgumentOutOfRangeException(nameof(options), lifetime, "the lifetime is whole seconds, at least one");
        string scheme = options.Scheme;
        if (scheme is null) throw new ArgumentNullException(nameof(options), "the scheme is null");
        if (scheme.Length == 0 || !Ascii.IsValid(scheme) || scheme.AsSpan().ContainsAnyExcept(SchemeChars))
            throw new ArgumentException($"the scheme '{scheme}' isn't one HTTP token: letters, digits and !#$%&'*+-.^_`|~", nameof(options));
        _claims = Claims(options);
        _key = Encoding.UTF8.GetBytes(secret);
        Lifetime = lifetime;
        _ttl = (long)lifetime.TotalSeconds;
        Scheme = scheme;
        _scheme = Encoding.ASCII.GetBytes(scheme + " ");
    }

    static JwtClaim[] Claims(JwtOptions options)
    {
        var claims = new JwtClaim[options.Claims.Count];
        options.Claims.CopyTo(claims, 0);
        if (claims.Length > MaxClaims) throw new ArgumentException($"more than {MaxClaims} claims", nameof(options));
        for (int i = 0; i < claims.Length; i++)
        {
            JwtClaim c = claims[i] ?? throw new ArgumentException("a claim is null", nameof(options));
            if (c.MinBytes < 0 || c.MaxBytes < c.MinBytes || c.MaxBytes > MaxTokenBytes)
                throw new ArgumentException($"the claim '{c.Name}' needs 0 <= MinBytes <= MaxBytes <= {MaxTokenBytes}", nameof(options));
            for (int j = 0; j < i; j++)
                if (claims[j].Name == c.Name) throw new ArgumentException($"the claim '{c.Name}' is declared twice", nameof(options));
        }
        return claims;
    }

    Jwt(Jwt prototype)
    {
        _key = prototype._key;
        Lifetime = prototype.Lifetime;
        _ttl = prototype._ttl;
        Scheme = prototype.Scheme;
        _scheme = prototype._scheme;
        _claims = prototype._claims;
    }

    /// <summary>Another instance with the same secret and options, for another connection.</summary>
    internal Jwt Copy() => new(this);

    /// <summary>The Authorization header's scheme, which the token follows after one space: <c>Token</c> unless
    /// <see cref="JwtOptions.Scheme"/> said otherwise.</summary>
    public string Scheme { get; }

    /// <summary>How long an issued token is valid: its exp is its iat plus this, in whole seconds.</summary>
    public TimeSpan Lifetime { get; }

    /// <summary>How many claims beside sub the tokens carry.</summary>
    internal int ClaimCount => _claims.Length;

    /// <summary>The index of the declared claim <paramref name="name"/>, or -1.</summary>
    internal int ClaimIndex(ReadOnlySpan<byte> name)
    {
        for (int i = 0; i < _claims.Length; i++)
            if (name.SequenceEqual(_claims[i].NameBytes)) return i;
        return -1;
    }

    /// <summary>Called before each handler call on its connection: <see cref="Issue(long)"/> writes into this
    /// request's arena.</summary>
    public void Bind(IResponse response) => _response = response;

    /// <summary>A token for <paramref name="userId"/>, issued now, in the request's arena: valid until the response
    /// has been sent. Only on the instance a handler was given, and only when no declared claim is required.</summary>
    public Utf8 Issue(long userId) => Issue(userId, ReadOnlySpan<Utf8>.Empty);

    /// <summary>A token for <paramref name="userId"/> carrying <paramref name="claims"/>, the declared claims' values
    /// in their order (or none, when none is required), issued now in the request's arena: valid until the response
    /// has been sent. Only on the instance a handler was given. A value, user or token the verifier would refuse throws
    /// before the arena is asked for anything.</summary>
    public Utf8 Issue(long userId, params ReadOnlySpan<Utf8> claims)
    {
        if (_response is null) throw new InvalidOperationException("only the Jwt a handler is given can issue a token into its request's arena");
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int size = ExactPayload(userId, now, claims); // everything is checked before the arena is asked
        ArenaBytes dest = _response.Arena.Allocate(ExactTokenBytes(size));
        int n = Issue(userId, now, claims, dest.Span);
        return dest.Text(0, n);
    }

    /// <summary>HMAC-SHA256 with a keyed instance kept per thread: the one-shot HashData sets the key up again on
    /// every call, which costs about as much as the MAC. Issue and Verify never await, so no other caller shares it.
    /// TryComputeHash on a kept instance allocates nothing (JwtAllocationTests).</summary>
    void Sign(ReadOnlySpan<byte> input, Span<byte> mac)
    {
        var key = _key;
        if (t_hmacKey != key)
        {
            t_hmac?.Dispose();
            t_hmac = new HMACSHA256(key);
            t_hmacKey = key;
        }
        t_hmac!.TryComputeHash(input, mac, out _);
    }

    /// <summary>Writes a token for <paramref name="userId"/> into <paramref name="dest"/> (at least
    /// <see cref="MaxIssuedBytes"/>) and returns its length.</summary>
    public int Issue(long userId, Span<byte> dest) => Issue(userId, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), dest);

    /// <summary><see cref="Issue(long, Span{byte})"/> as issued at <paramref name="now"/> (Unix seconds).</summary>
    public int Issue(long userId, long now, Span<byte> dest) => Issue(userId, now, ReadOnlySpan<Utf8>.Empty, dest);

    /// <summary>The most bytes a token carrying <paramref name="claims"/> takes, its numbers at their longest: what
    /// <see cref="Issue(long, long, ReadOnlySpan{Utf8}, Span{byte})"/> needs in its destination. Throws as Issue does
    /// for values it refuses, so the size is never asked of the arena for them.</summary>
    public int MaxIssuedBytesWith(ReadOnlySpan<Utf8> claims) => TokenBytes(PayloadBytes(claims));

    static int TokenBytes(int payload) => 36 + 1 + (payload + 2) / 3 * 4 + 1 + 43;

    /// <summary>The payload's most bytes with <paramref name="claims"/> escaped, once they are checked: one value per
    /// declared claim or none (when none is required), each UTF-8 within its claim's lengths, and no more payload than a
    /// token may hold. Bounded, so it is safe to stackalloc.</summary>
    int PayloadBytes(ReadOnlySpan<Utf8> claims)
    {
        if (claims.Length != 0 && claims.Length != _claims.Length)
            throw new ArgumentException($"{claims.Length} claim values for {_claims.Length} declared claims: pass each in order, or none", nameof(claims));
        int n = MaxPayloadBytes;
        for (int i = 0; i < _claims.Length; i++)
        {
            JwtClaim c = _claims[i];
            if (claims.Length == 0)
            {
                if (c.Required) throw new ArgumentException($"the claim '{c.Name}' is required: pass the claims' values", nameof(claims));
                continue;
            }
            ReadOnlySpan<byte> v = claims[i].Span;
            if (!System.Text.Unicode.Utf8.IsValid(v) || v.Length < c.MinBytes || v.Length > c.MaxBytes)
                throw new ArgumentException($"the value of '{c.Name}' isn't UTF-8 of {c.MinBytes} to {c.MaxBytes} bytes", nameof(claims));
            n += 6 + c.NameBytes.Length + EscapedBytes(v); // ,"name":"value"
            if (n > MaxTokenBytes) throw TooLong();
        }
        return n;
    }

    /// <summary>The payload's exact bytes for this user, time and these claims, once all are checked: a positive user,
    /// claims as <see cref="PayloadBytes"/> takes them, and a whole token no longer than <see cref="MaxTokenBytes"/>.</summary>
    int ExactPayload(long userId, long now, ReadOnlySpan<Utf8> claims)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId); // the verifier refuses a sub that isn't positive
        int n = PayloadBytes(claims) - MaxPayloadBytes + 24 + Digits(userId) + Digits(now) + Digits(now + _ttl);
        if (ExactTokenBytes(n) > MaxTokenBytes) throw TooLong();
        return n;
    }

    static int ExactTokenBytes(int payload) => 36 + 1 + Base64Url.GetEncodedLength(payload) + 1 + 43;

    static int Digits(long v)
    {
        Span<byte> text = stackalloc byte[20];
        return Number(v, text);
    }

    static ArgumentException TooLong() =>
        new($"the claims make a token longer than {MaxTokenBytes} bytes, which no verifier here takes", "claims");

    /// <summary>
    /// Writes a token for <paramref name="userId"/>, issued at <paramref name="now"/> (Unix seconds), into
    /// <paramref name="dest"/> (at least <see cref="MaxIssuedBytesWith"/>) and returns its length.
    /// <paramref name="claims"/> are the declared claims' values in their order, or none when none is required; each
    /// must be UTF-8 within its claim's lengths, the user positive, and the whole token no longer than
    /// <see cref="MaxTokenBytes"/>, as the verifier would require; all of it is checked before anything is written.
    /// Every declared claim is written when values are passed.
    /// </summary>
    public int Issue(long userId, long now, ReadOnlySpan<Utf8> claims, Span<byte> dest)
    {
        int size = ExactPayload(userId, now, claims); // checks the user, the claims and the token's length first
        if (dest.Length < ExactTokenBytes(size)) throw new ArgumentException("dest is too small", nameof(dest));
        Span<byte> payload = stackalloc byte[size];
        int p = 0;
        "{\"sub\":\""u8.CopyTo(payload);
        p += 8;
        p += Number(userId, payload[p..]);
        "\",\"iat\":"u8.CopyTo(payload[p..]);
        p += 8;
        p += Number(now, payload[p..]);
        ",\"exp\":"u8.CopyTo(payload[p..]);
        p += 7;
        p += Number(now + _ttl, payload[p..]);
        for (int i = 0; i < claims.Length; i++)
        {
            ",\""u8.CopyTo(payload[p..]);
            p += 2;
            _claims[i].NameBytes.CopyTo(payload[p..]);
            p += _claims[i].NameBytes.Length;
            "\":\""u8.CopyTo(payload[p..]);
            p += 3;
            p += Escape(claims[i].Span, payload[p..]);
            payload[p++] = (byte)'"';
        }
        payload[p++] = (byte)'}';

        HeaderB64.CopyTo(dest);
        int n = HeaderB64.Length;
        dest[n++] = (byte)'.';
        n += Base64Url.EncodeToUtf8(payload[..p], dest[n..]);
        Span<byte> mac = stackalloc byte[32];
        Sign(dest[..n], mac);
        dest[n++] = (byte)'.';
        n += Base64Url.EncodeToUtf8(mac, dest[n..]);
        return n;
    }

    static int EscapedBytes(ReadOnlySpan<byte> s)
    {
        int n = s.Length;
        foreach (byte b in s)
        {
            if (b is (byte)'"' or (byte)'\\') n++;
            else if (b < 0x20) n += 5;
        }
        return n;
    }

    static int Number(long v, Span<byte> dest)
    {
        v.TryFormat(dest, out int n, default, System.Globalization.CultureInfo.InvariantCulture);
        return n;
    }

    /// <summary>A JSON string's content: quote and backslash escaped, control characters as \u00XX, the rest as it is.</summary>
    static int Escape(ReadOnlySpan<byte> s, Span<byte> d)
    {
        int n = 0;
        foreach (byte b in s)
        {
            if (b is (byte)'"' or (byte)'\\')
            {
                d[n++] = (byte)'\\';
                d[n++] = b;
            }
            else if (b < 0x20)
            {
                "\\u00"u8.CopyTo(d[n..]);
                d[n + 4] = "0123456789abcdef"u8[b >> 4];
                d[n + 5] = "0123456789abcdef"u8[b & 15];
                n += 6;
            }
            else d[n++] = b;
        }
        return n;
    }

    /// <summary>The token's user id, or 0 when the token is invalid, expired, not HS256, has no positive integer string
    /// sub, or breaks a declared claim's rule.</summary>
    public long Verify(ReadOnlySpan<byte> token) => Verify(token, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary><see cref="Verify(ReadOnlySpan{byte})"/> with exp and nbf checked against <paramref name="nowMilliseconds"/>
    /// (Unix time).</summary>
    public long Verify(ReadOnlySpan<byte> token, long nowMilliseconds)
    {
        Span<int> claims = stackalloc int[2 * _claims.Length];
        return Check(token, nowMilliseconds, null, claims, out long id, out _) == TokenCheck.Valid ? id : 0;
    }

    /// <summary>
    /// The token's worth at <paramref name="nowMilliseconds"/>, and when it is valid its user id and, for each declared
    /// claim, its value's start and length in <paramref name="payload"/> (start -1 when absent): two ints per claim in
    /// <paramref name="claims"/>. The payload is decoded into <paramref name="arena"/> only when claims are declared
    /// and the signature is good; with no arena it stays on the stack and <paramref name="payload"/> is empty.
    /// </summary>
    internal TokenCheck Check(ReadOnlySpan<byte> token, long nowMilliseconds, IRequestArena? arena, Span<int> claims, out long id, out ArenaBytes payload)
    {
        id = 0;
        payload = default;
        if (token.Length is 0 or > MaxTokenBytes || token.ContainsAnyExcept(TokenChars)) return TokenCheck.Invalid;
        int d1 = token.IndexOf((byte)'.');
        if (d1 <= 0) return TokenCheck.Invalid;
        int d2 = token[(d1 + 1)..].IndexOf((byte)'.');
        if (d2 <= 0) return TokenCheck.Invalid;
        d2 += d1 + 1;
        ReadOnlySpan<byte> sig = token[(d2 + 1)..];
        if (sig.IndexOf((byte)'.') >= 0) return TokenCheck.Invalid;

        // Signature first: nothing in an unauthenticated token is worth parsing.
        Span<byte> mac = stackalloc byte[32];
        Sign(token[..d2], mac);
        Span<byte> given = stackalloc byte[48];
        if (!Decode(sig, given, out int sigLen) || sigLen != 32) return TokenCheck.Invalid;
        if (!CryptographicOperations.FixedTimeEquals(mac, given[..32])) return TokenCheck.Invalid;

        // The header this server issues is compared as text; any other is parsed.
        if (!token[..d1].SequenceEqual(HeaderB64))
        {
            Span<byte> header = stackalloc byte[d1];
            if (!Decode(token[..d1], header, out int headerLen) || !HeaderIsHs256(header[..headerLen])) return TokenCheck.Invalid;
        }
        ReadOnlySpan<byte> payloadB64 = token[(d1 + 1)..d2];
        int size = Base64Url.GetMaxDecodedLength(payloadB64.Length);
        // with claims, a second half where escaped values are unescaped, each at its own offset
        int work = _claims.Length == 0 ? size : 2 * size;
        if (_claims.Length > 0 && arena is not null) payload = arena.Allocate(work);
        Span<byte> buffer = payload.Buffer is null ? stackalloc byte[work] : payload.Span;
        if (!Decode(payloadB64, buffer[..size], out int payloadLen)) return TokenCheck.Invalid;
        TokenCheck check = Payload(buffer[..payloadLen], buffer[size..], size, nowMilliseconds / 1000.0, claims, out id);
        if (check != TokenCheck.Valid) id = 0;
        return check;
    }

    /// <summary>Base64url without throwing: the Try forms only report a short destination and throw on bad input.</summary>
    static bool Decode(ReadOnlySpan<byte> s, Span<byte> dest, out int written) =>
        Base64Url.DecodeFromUtf8(s, dest, out _, out written) == OperationStatus.Done;

    static bool HeaderIsHs256(ReadOnlySpan<byte> json)
    {
        try
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
            bool hs256 = false;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                // a key or value that doesn't unescape (ValueTextEquals would throw) rejects the token
                if (!JsonBody.TryTextEquals(ref r, "alg"u8, out bool isAlg) || !r.Read()) return false;
                if (isAlg)
                {
                    if (r.TokenType != JsonTokenType.String || !JsonBody.TryTextEquals(ref r, "HS256"u8, out hs256)) return false;
                }
                else r.Skip();
            }
            return hs256 && r.TokenType == JsonTokenType.EndObject && !r.Read(); // nothing after it but whitespace
        }
        catch (JsonException)
        {
            return false;
        }
    }

    const int Other = 0, Sub = 1, Exp = 2, Nbf = 3, Declared = 4;

    /// <summary>Which claim the property name the reader is on names (a declared one is <c>Declared + i</c>); false
    /// when it doesn't unescape, which rejects the token (ValueTextEquals would throw).</summary>
    bool Claim(ref Utf8JsonReader r, out int claim)
    {
        claim = Other;
        if (!JsonBody.TryTextEquals(ref r, "sub"u8, out bool equal)) return false;
        if (equal) claim = Sub;
        else if (JsonBody.TryTextEquals(ref r, "exp"u8, out equal) && equal) claim = Exp;
        else if (JsonBody.TryTextEquals(ref r, "nbf"u8, out equal) && equal) claim = Nbf;
        else
        {
            for (int i = 0; i < _claims.Length; i++)
            {
                if (JsonBody.TryTextEquals(ref r, _claims[i].NameBytes, out equal) && equal)
                {
                    claim = Declared + i;
                    break;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// The payload's rules, read in one pass. What isn't a JSON object, has a name that doesn't unescape, or an exp
    /// or nbf that isn't a number or isn't met, is <see cref="TokenCheck.Invalid"/>. Then a sub that isn't a positive
    /// integer string, a required claim that is absent, or any occurrence of a claim that breaks its rule (the last
    /// occurrence is the value) is <see cref="TokenCheck.InvalidPayload"/>. A claim's place is in
    /// <paramref name="json"/>, or at <paramref name="scratchAt"/> plus the same offset when it had to be unescaped
    /// into <paramref name="scratch"/>.
    /// </summary>
    TokenCheck Payload(ReadOnlySpan<byte> json, Span<byte> scratch, int scratchAt, double now, Span<int> claims, out long sub)
    {
        sub = 0;
        for (int i = 0; i < claims.Length; i += 2)
        {
            claims[i] = -1;
            claims[i + 1] = 0;
        }
        try
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return TokenCheck.Invalid;
            double exp = double.NaN, nbf = double.NaN;
            bool bad = false;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (!Claim(ref r, out int claim) || !r.Read()) return TokenCheck.Invalid;
                switch (claim)
                {
                    case Sub:
                        if (r.TokenType == JsonTokenType.String && !r.ValueIsEscaped && r.ValueSpan.Length <= 20 && Query.TryParseUint(r.ValueSpan, out long s))
                        {
                            sub = s;
                        }
                        else
                        {
                            bad = true;
                            r.Skip();
                        }
                        break;
                    case Exp:
                        if (r.TokenType != JsonTokenType.Number || !r.TryGetDouble(out exp)) return TokenCheck.Invalid;
                        break;
                    case Nbf:
                        if (r.TokenType != JsonTokenType.Number || !r.TryGetDouble(out nbf)) return TokenCheck.Invalid;
                        break;
                    case Other:
                        r.Skip();
                        break;
                    default:
                        int c = 2 * (claim - Declared);
                        if (r.TokenType == JsonTokenType.Null)
                        {
                            claims[c] = -1; // null is no claim, as a body field's null is no field
                            claims[c + 1] = 0;
                        }
                        else if (r.TokenType != JsonTokenType.String || !Text(ref r, scratch, scratchAt, _claims[claim - Declared], out claims[c], out claims[c + 1]))
                        {
                            bad = true;
                            r.Skip();
                        }
                        break;
                }
            }
            if (r.TokenType != JsonTokenType.EndObject || r.Read()) return TokenCheck.Invalid; // nothing after it but whitespace
            if (double.IsNaN(exp) || !(now < exp)) return TokenCheck.Invalid; // exp is required
            if (!double.IsNaN(nbf) && now < nbf) return TokenCheck.Invalid;
            // 1e400 reads as an infinity: an exp that never comes, or an nbf that always passed, is no time at all
            if (bad || sub <= 0 || !double.IsFinite(exp) || double.IsInfinity(nbf)) return TokenCheck.InvalidPayload;
            for (int i = 0; i < _claims.Length; i++)
                if (_claims[i].Required && claims[2 * i] < 0) return TokenCheck.InvalidPayload;
            return TokenCheck.Valid;
        }
        catch (JsonException)
        {
            return TokenCheck.Invalid;
        }
    }

    /// <summary>The string the reader is on as UTF-8 text within the claim's lengths: in place, or unescaped into the
    /// scratch half at its own offset. False for a string that isn't text (invalid UTF-8, a lone surrogate) or is
    /// outside the lengths.</summary>
    static bool Text(ref Utf8JsonReader r, Span<byte> scratch, int scratchAt, JwtClaim claim, out int start, out int length)
    {
        ReadOnlySpan<byte> raw = r.ValueSpan;
        int at = (int)r.TokenStartIndex + 1;
        if (!r.ValueIsEscaped)
        {
            start = at;
            length = raw.Length;
            if (!System.Text.Unicode.Utf8.IsValid(raw)) return false;
        }
        else
        {
            start = scratchAt + at;
            if (!JsonBody.TryUnescape(raw, scratch.Slice(at, raw.Length), out length)) return false;
        }
        return length >= claim.MinBytes && length <= claim.MaxBytes;
    }

    /// <summary>
    /// The header's worth for an endpoint that reads it: <see cref="TokenCheck.Missing"/> when none was sent
    /// (<paramref name="present"/> false) or it isn't the scheme and one space once surrounding spaces and tabs are
    /// trimmed (an empty one included); otherwise the token's, as <see cref="Check"/> says, now.
    /// </summary>
    internal TokenCheck Authenticate(bool present, ReadOnlySpan<byte> header, IRequestArena arena, Span<int> claims, out long id, out ArenaBytes payload)
    {
        if (!present || !TryToken(header, out ReadOnlySpan<byte> token))
        {
            id = 0;
            payload = default;
            return TokenCheck.Missing;
        }
        return Check(token, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), arena, claims, out id, out payload);
    }

    /// <summary>The token in an Authorization header's value: what follows the scheme and one space once surrounding
    /// spaces and tabs are trimmed, as Kestrel did; false for another scheme, in any case.</summary>
    internal bool TryToken(ReadOnlySpan<byte> header, out ReadOnlySpan<byte> token)
    {
        header = header.Trim(" \t"u8);
        bool ok = header.StartsWith(_scheme);
        token = ok ? header[_scheme.Length..] : default;
        return ok;
    }
}

/// <summary>What a token is worth.</summary>
internal enum TokenCheck : byte
{
    /// <summary>A genuine token, in time, whose payload holds what the app reads.</summary>
    Valid,
    /// <summary>No header, or one without the scheme: no token at all.</summary>
    Missing,
    /// <summary>Not a token this app signed (signature, header, JSON), or out of its time (exp, nbf).</summary>
    Invalid,
    /// <summary>Genuine and in time, but sub isn't a positive integer string or a declared claim breaks its rule.</summary>
    InvalidPayload,
}
