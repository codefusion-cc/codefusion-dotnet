using System;
using System.Text;
using CodeFusion.Http;
using Xunit;

namespace CodeFusion.Http.Tests;

/// <summary>The token's design limits: a secret long enough for HS256, one spelling per signature, a lifetime.</summary>
public sealed class JwtTests
{
    internal const string Secret = "a-secret-of-exactly-thirty-two-b";
    const long Now = 1_700_000_000;

    static byte[] Issue(Jwt jwt, long user = 42)
    {
        var b = new byte[Jwt.MaxIssuedBytes];
        return b[..jwt.Issue(user, Now, b)];
    }

    [Fact]
    public void ASecretShorterThan32BytesIsRefused()
    {
        Assert.Equal(32, Encoding.UTF8.GetByteCount(Secret));
        _ = new Jwt(Secret);
        _ = new Jwt(new string('é', 16));                                                    // 32 bytes in 16 characters
        Assert.Throws<ArgumentException>(() => new Jwt(Secret[..31]));
        Assert.Throws<ArgumentException>(() => new Jwt(new string('é', 15) + "a"));          // 31 bytes
        Assert.Throws<ArgumentException>(() => new Jwt("short", TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentException>(() => HttpApp.CreateBuilder([]).AddJwtAuth("short"));
        Assert.Throws<ArgumentException>(() => HttpApp.CreateBuilder([]).AddJwtAuth(Secret[..31], TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentNullException>(() => new Jwt(null!));
    }

    /// <summary>43 base64url characters carry the 32-byte MAC with 2 bits to spare in the last one: a token whose
    /// last character differs only there would be a second spelling of the same signature.</summary>
    [Fact]
    public void ASignatureSpelledWithOtherUnusedBitsIsRefused()
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var jwt = new Jwt(Secret);
        byte[] token = Issue(jwt);
        Assert.Equal(42, jwt.Verify(token, Now * 1000));
        int last = Alphabet.IndexOf((char)token[^1], StringComparison.Ordinal);
        Assert.Equal(0, last & 3); // the issuer's spelling: the spare bits are zero
        for (int spare = 1; spare < 4; spare++)
        {
            byte[] other = (byte[])token.Clone();
            other[^1] = (byte)Alphabet[last | spare];
            Assert.Equal(0, jwt.Verify(other, Now * 1000));
        }
    }

    [Fact]
    public void TokensLiveThirtyDaysUnlessTheLifetimeIsSet()
    {
        var month = new Jwt(Secret);
        Assert.Equal(TimeSpan.FromDays(30), month.Lifetime);
        byte[] t = Issue(month);
        Assert.Equal(42, month.Verify(t, (Now + 30L * 24 * 3600 - 1) * 1000));
        Assert.Equal(0, month.Verify(t, (Now + 30L * 24 * 3600) * 1000));

        var hour = new Jwt(Secret, TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromHours(1), hour.Lifetime);
        byte[] h = Issue(hour);
        Assert.Equal(42, hour.Verify(h, (Now + 3599) * 1000));
        Assert.Equal(0, hour.Verify(h, (Now + 3600) * 1000));
        Assert.Equal(42, month.Verify(h, (Now + 3599) * 1000)); // the same secret verifies it: exp is in the token

        Assert.Throws<ArgumentOutOfRangeException>(() => new Jwt(Secret, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Jwt(Secret, TimeSpan.FromMilliseconds(999)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Jwt(Secret, TimeSpan.FromMilliseconds(1500))); // whole seconds
    }
}
