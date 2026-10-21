namespace CodeFusion.Http;

/// <summary>
/// Who sent the request, for a handler that takes one: the user id in the Authorization header's JWT
/// (<c>Token &lt;jwt&gt;</c>, or the scheme <see cref="JwtOptions.Scheme"/> set), or 0 for a request without the
/// header. A header that is present but not a valid token
/// is answered with 401 before the handler runs; so is a request without one on an endpoint marked
/// <c>.AuthRequired()</c>. Needs <c>builder.AddJwtAuth(secret)</c>.
/// </summary>
public readonly struct Viewer
{
    /// <summary>The user id from the token; 0 when anonymous.</summary>
    public long Id { get; }

    /// <summary>The token the request presented; empty when anonymous. It points into the request's buffer, which
    /// the connection's next request reuses: valid only while this request is handled, so copy it to keep it
    /// (CFH0004 refuses a <see cref="Viewer"/> kept in a class's field).</summary>
    public Utf8 Token { get; }

    /// <summary>Whether a valid token was presented.</summary>
    public bool IsAuthenticated => Id > 0;

    /// <summary>The viewer <paramref name="id"/> who presented <paramref name="token"/>.</summary>
    public Viewer(long id, Utf8 token)
    {
        Id = id;
        Token = token;
    }
}
