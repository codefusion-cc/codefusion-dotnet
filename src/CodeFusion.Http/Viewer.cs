using System;

namespace CodeFusion.Http;

/// <summary>
/// Who sent the request, for a handler that takes one: the user id in the Authorization header's JWT
/// (<c>Token &lt;jwt&gt;</c>, or the scheme <see cref="JwtOptions.Scheme"/> set) and the claims
/// <see cref="JwtOptions.Claims"/> declared, or 0 for a request without the header. A header that is present but not a
/// valid token is answered with 401 before the handler runs; so is a request without one on an endpoint marked
/// <c>.AuthRequired()</c>. Needs <c>builder.AddJwtAuth(secret)</c>.
/// </summary>
public readonly struct Viewer
{
    readonly RequestContext? _request;
    readonly int _generation; // which of its connection's requests it is

    /// <summary>The user id from the token; 0 when anonymous.</summary>
    public long Id { get; }

    /// <summary>The token the request presented; empty when anonymous. It points into the request's buffer, which
    /// the connection's next request reuses: valid only while this request is handled, so copy it to keep it
    /// (CFH0004 refuses a <see cref="Viewer"/> kept in a class's field).</summary>
    public Utf8 Token { get; }

    /// <summary>Whether a valid token was presented.</summary>
    public bool IsAuthenticated => Id > 0;

    /// <summary>The viewer <paramref name="id"/> who presented <paramref name="token"/>, with no claims.</summary>
    public Viewer(long id, Utf8 token)
    {
        Id = id;
        Token = token;
    }

    internal Viewer(long id, Utf8 token, RequestContext request, int generation) : this(id, token)
    {
        _request = request;
        _generation = generation;
    }

    /// <summary>
    /// The value of the claim <see cref="JwtOptions.Claims"/> declared as <paramref name="name"/>, such as
    /// <c>viewer.Claim("username"u8)</c>: unescaped UTF-8 in the request's arena, valid until the response has been
    /// sent. Empty when anonymous, or when an optional claim is absent (<see cref="TryGetClaim"/> tells those apart).
    /// <para>Throws <see cref="ArgumentException"/> for a name that wasn't declared, and for every name on a Viewer made
    /// by hand (<c>new Viewer(id, token)</c> or <c>default</c>), which declares none, so a misspelling fails in a unit
    /// test too. Throws <see cref="InvalidOperationException"/> for a Viewer used after its request, on any thread. The
    /// value itself is a view: copy it to keep it past the response, or to hand it to another thread.</para>
    /// </summary>
    public Utf8 Claim(ReadOnlySpan<byte> name)
    {
        TryGetClaim(name, out Utf8 value);
        return value;
    }

    /// <summary>The value of the declared claim <paramref name="name"/>: false when anonymous or when an optional
    /// claim is absent. Throws as <see cref="Claim"/> does.</summary>
    public bool TryGetClaim(ReadOnlySpan<byte> name, out Utf8 value)
    {
        if (_request is null) throw new ArgumentException("this Viewer was made by hand, so it declares no claims: only one the dispatcher gives a handler carries them", nameof(name));
        return _request.TryGetClaim(name, IsAuthenticated, _generation, out value);
    }
}
