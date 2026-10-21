using System;
using System.Collections.Generic;

namespace CodeFusion.Http;

/// <summary>
/// How <see cref="Jwt"/> issues and reads tokens, for <c>builder.AddJwtAuth(secret, options)</c>: their lifetime,
/// whether a short secret is taken, the Authorization header's scheme and the claims beside sub. Read once, when the
/// <see cref="Jwt"/> is made: changing the options later changes nothing.
/// </summary>
public sealed class JwtOptions
{
    /// <summary>How long an issued token is valid: whole seconds, at least one. 30 days unless set.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Takes a secret shorter than <see cref="Jwt.MinSecretBytes"/> (but not empty), for a deployment handed
    /// a fixed one. Such a secret can be found offline from any one token, and then anyone can sign as anyone
    /// (SECURITY.md). Off unless set.</summary>
    public bool AllowShortSecret { get; set; }

    /// <summary>
    /// The Authorization header's scheme: the header is this, one space and the token, once surrounding spaces and
    /// tabs are trimmed. Matched exactly, case included. One RFC 9110 token (letters, digits and
    /// <c>!#$%&amp;'*+-.^_`|~</c>). <c>Token</c> (RealWorld's) unless set; <c>Bearer</c> for RFC 6750.
    /// </summary>
    public string Scheme { get; set; } = "Token";

    /// <summary>
    /// The string claims the app reads beside sub, at most <see cref="Jwt.MaxClaims"/>, each name once. Every token is
    /// checked for them; a handler reads them as <c>viewer.Claim("username"u8)</c>. None unless added.
    /// </summary>
    public IList<JwtClaim> Claims { get; } = new List<JwtClaim>();
}
