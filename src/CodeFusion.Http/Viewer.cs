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

    internal Viewer(long id, Utf8 token, RequestContext request) : this(id, token) => _request = request;

    /// <summary>
    /// The value of the claim <see cref="JwtOptions.Claims"/> declared as <paramref name="name"/>, such as
    /// <c>viewer.Claim("username"u8)</c>: unescaped UTF-8 in the request's arena, valid until the response has been
    /// sent. Empty when anonymous, or when an optional claim is absent (<see cref="TryGetClaim"/> tells those apart).
    /// A name that wasn't declared throws, so a misspelling can't read as empty.
    /// </summary>
    public Utf8 Claim(ReadOnlySpan<byte> name)
    {
        TryGetClaim(name, out Utf8 value);
        return value;
    }

    /// <summary>The value of the declared claim <paramref name="name"/>: false when anonymous or when an optional
    /// claim is absent. A name that wasn't declared throws.</summary>
    public bool TryGetClaim(ReadOnlySpan<byte> name, out Utf8 value)
    {
        value = default;
        return _request is not null && _request.TryGetClaim(name, IsAuthenticated, out value);
    }
}
