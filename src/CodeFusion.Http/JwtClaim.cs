using System;
using System.Text;

namespace CodeFusion.Http;

/// <summary>
/// A string claim the app's tokens carry beside <c>sub</c>, such as <c>username</c>: declared on
/// <see cref="JwtOptions.Claims"/>, checked in every token after its signature and exp, and read by a handler as
/// <see cref="Viewer.Claim"/>, a view of the token's decoded payload in the request's arena. A token whose claim
/// breaks a rule here (absent while <see cref="Required"/>, not a string, not text, outside its lengths) is genuine but
/// has an invalid payload, a 401.
/// </summary>
public sealed class JwtClaim
{
    /// <summary>The longest name, in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 64;

    static readonly UTF8Encoding Strict = new(false, true);

    /// <summary>The claim <paramref name="name"/>: not empty, at most <see cref="MaxNameBytes"/> of UTF-8, no quote,
    /// backslash or control character, and not sub, iat, exp or nbf, which the verifier reads itself. Names are
    /// compared as JSON compares them: after unescaping, case included.</summary>
    public JwtClaim(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name is "" or "sub" or "iat" or "exp" or "nbf")
            throw new ArgumentException($"'{name}' can't be a declared claim: it is empty, or one the verifier reads itself", nameof(name));
        byte[] bytes;
        try
        {
            bytes = Strict.GetBytes(name);
        }
        catch (EncoderFallbackException e)
        {
            throw new ArgumentException("the claim's name isn't text: it has a lone surrogate", nameof(name), e);
        }
        if (bytes.Length > MaxNameBytes) throw new ArgumentException($"the claim's name is longer than {MaxNameBytes} bytes of UTF-8", nameof(name));
        foreach (byte b in bytes)
            if (b is < 0x20 or (byte)'"' or (byte)'\\')
                throw new ArgumentException("the claim's name has a quote, a backslash or a control character", nameof(name));
        Name = name;
        NameBytes = bytes;
    }

    /// <summary>The claim's name in the token's payload.</summary>
    public string Name { get; }

    internal byte[] NameBytes { get; }

    /// <summary>A token without the claim (or with it null) has an invalid payload. True unless set; an optional claim
    /// that is absent reads as empty, and <see cref="Viewer.TryGetClaim"/> tells the two apart.</summary>
    public bool Required { get; init; } = true;

    /// <summary>The fewest bytes of UTF-8 the value may have once unescaped: 1 refuses an empty string. 0 unless set.</summary>
    public int MinBytes { get; init; }

    /// <summary>The most bytes of UTF-8 the value may have once unescaped, at most <see cref="Jwt.MaxTokenBytes"/>
    /// (which bounds it anyway) and the default.</summary>
    public int MaxBytes { get; init; } = Jwt.MaxTokenBytes;
}
