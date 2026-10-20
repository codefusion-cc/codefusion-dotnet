// Frozen copy of the contest server's Security/Auth.cs (PR #8's rewrite): the outcomes the generated binders must match.
using System;

namespace CodeFusion.Http.Tests.Reference;

public static class ContestAuth
{
    /// <summary>
    /// The viewer's user id from the first Authorization header: 0 when none was sent (<paramref name="present"/>
    /// false); -1 when one was sent but isn't "Token &lt;valid jwt&gt;". A header sent empty (or only whitespace) is
    /// present and invalid, not anonymous. Surrounding spaces and tabs are trimmed, as Kestrel did.
    /// </summary>
    public static long Viewer(bool present, ReadOnlySpan<byte> header)
    {
        if (!present) return 0;
        header = Trim(header);
        if (!header.StartsWith(Scheme)) return -1;
        long id = ContestJwt.Verify(header[Scheme.Length..]);
        return id > 0 ? id : -1;
    }

    /// <summary>The token the request presented (call after Viewer accepted the header).</summary>
    public static ReadOnlySpan<byte> Token(ReadOnlySpan<byte> header) => Trim(header)[Scheme.Length..];

    static ReadOnlySpan<byte> Scheme => "Token "u8;

    static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s) => s.Trim(" \t"u8);
}
