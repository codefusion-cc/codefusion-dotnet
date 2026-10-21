using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CodeFusion.Data.Postgres;
using FsCheck;
using FsCheck.Fluent;
using Xunit;

#pragma warning disable xUnit1069, xUnit1051

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// SASLprep (RFC 4013) as Postgres runs it (pg_saslprep): the RFC's examples, each way preparing can refuse a password
/// (then its raw bytes are hashed, as libpq does), and logins against a real server, which prepares the password
/// itself when it makes the role's SCRAM verifier.
/// </summary>
public sealed class PgSaslPrepTests
{
    /// <summary>Whether .NET can normalize here (ICU; not with InvariantGlobalization).</summary>
    static readonly bool Icu = "Ⅸ".Normalize(NormalizationForm.FormKC) == "IX";

    static string Prepared(string password, bool normalizes = true) => Encoding.UTF8.GetString(PgSaslPrep.Prepare(password, normalizes));

    [Theory]
    [InlineData("I­X", "IX")]                           // RFC 4013's examples: soft hyphen mapped to nothing
    [InlineData("user", "user")]
    [InlineData("USER", "USER")]
    [InlineData("ª", "a")]                               // NFKC
    [InlineData("Ⅸ", "IX")]
    [InlineData("\u0007", "\u0007")]                          // prohibited, but ASCII goes as it is (as in Postgres)
    [InlineData("ا1", "ا1")]              // right-to-left text ending in a digit: refused, so the raw bytes
    [InlineData("a b", "a b")]                           // a non-ASCII space is a space
    [InlineData("a　b c", "a b c")]
    [InlineData("a‍b﻿c", "abc")]                   // mapped to nothing
    [InlineData("​", " ")]                               // zero width space: C.1.2's space before B.1's nothing
    [InlineData("é", "é")]                         // composed
    [InlineData("가", "가")]                    // Hangul jamo composed
    [InlineData("ﬁx", "fix")]
    [InlineData("ＡＢ", "AB")]                        // full width
    [InlineData("pä ss", "pä ss")]
    [InlineData("אב", "אב")]              // right-to-left throughout
    [InlineData("אaב", "אaב")]            // mixed with left-to-right: refused, raw
    [InlineData("אבa", "אבa")]
    [InlineData("pwé", "pwé")]          // private use: prohibited, raw
    [InlineData("\U0001F600é", "\U0001F600é")]      // unassigned in Unicode 3.2: raw
    [InlineData("\U0001F100é", "\U0001F100é")]      // the same though NFKC would change it: Postgres checks before normalizing
    [InlineData("­", "­")]                          // empty once mapped: raw
    [InlineData("", "")]
    public void PasswordsArePreparedAsRfc4013AndPostgresSay(string password, string expected)
    {
        Assert.SkipUnless(Icu, "NFKC needs ICU (InvariantGlobalization is on)");
        Assert.Equal(expected, Prepared(password));
    }

    [Theory]
    [InlineData("Ⅸ")]
    [InlineData("é")]
    [InlineData("ﬁx")]
    [InlineData("가")]
    public void APasswordNfkcChangesIsRefusedWithoutANormalizerInsteadOfFailingTheLogin(string password)
    {
        var e = Assert.Throws<PgConnectionException>(() => PgSaslPrep.Prepare(password, normalizes: false));
        Assert.Contains("InvariantGlobalization", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pä ss", "pä ss")]
    [InlineData("a b­", "a b")]
    [InlineData("пароль", "пароль")] // Cyrillic
    [InlineData("密码", "密码")]                                                     // CJK
    [InlineData("가나", "가나")]                                                     // Hangul syllables
    public void APasswordNfkcLeavesAloneNeedsNoNormalizer(string password, string expected) =>
        Assert.Equal(expected, Prepared(password, normalizes: false));

    [Theory]
    [InlineData(1, 0xD800)]
    [InlineData(0, 0xDC00)]
    [InlineData(2, 0xD83D)]
    public void ALoneSurrogateIsRefused(int at, int surrogate)
    {
        // built here: a lone surrogate in a test's data doesn't survive its way to the test
        string password = "éb".Insert(at, ((char)surrogate).ToString());
        Assert.Throws<PgConnectionException>(() => PgSaslPrep.Prepare(password, normalizes: false));
    }

    /// <summary>Strings of characters from the ranges a password draws on: ASCII, Latin, combining marks, Hebrew,
    /// Arabic, jamo, spaces and format characters, CJK, compatibility forms, private use and beyond the BMP.</summary>
    static Arbitrary<string> Passwords()
    {
        int[][] blocks = [[0x20, 0x7E], [0xA0, 0x24F], [0x300, 0x36F], [0x5D0, 0x5EA], [0x620, 0x64A], [0x1100, 0x11FF],
            [0x2000, 0x206F], [0x2150, 0x218F], [0x3000, 0x30FF], [0xAC00, 0xAC20], [0xE000, 0xE010], [0xFB00, 0xFB4F],
            [0xFE00, 0xFE0F], [0xFF00, 0xFFEF], [0x1D400, 0x1D4FF], [0x1F100, 0x1F1FF], [0x1F600, 0x1F64F]];
        var cp = Gen.Elements(blocks).SelectMany(b => Gen.Choose(b[0], b[1]));
        return cp.ListOf().Select(l => string.Concat(l.Select(char.ConvertFromUtf32))).ToArbitrary();
    }

    [Fact]
    public void WithoutANormalizerAPasswordIsPreparedTheSameOrRefusedNeverPreparedWrong()
    {
        Assert.SkipUnless(Icu, "the reference normalization needs ICU");
        Prop.ForAll(Passwords(), password =>
        {
            byte[] withIcu = PgSaslPrep.Prepare(password, normalizes: true);
            try
            {
                return PgSaslPrep.Prepare(password, normalizes: false).AsSpan().SequenceEqual(withIcu);
            }
            catch (PgConnectionException)
            {
                return true; // it needed NFKC and said so
            }
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void APreparedPasswordIsNfkcOrTheRawBytes()
    {
        Assert.SkipUnless(Icu, "the reference normalization needs ICU");
        Prop.ForAll(Passwords(), password =>
        {
            string prepared = Prepared(password);
            return prepared == password || (prepared.Length > 0 && prepared.IsNormalized(NormalizationForm.FormKC));
        }).QuickCheckThrowOnFailure();
    }

    // ---------- a real server ----------

    [Theory(Timeout = 30_000)]
    [InlineData("a b")]
    [InlineData("I­X")]
    [InlineData("pä ss")]
    [InlineData("אב")]
    [InlineData("אa")]        // refused by SASLprep on both sides: the raw bytes
    [InlineData("pw")]
    [InlineData("\U0001F600pw")]
    [InlineData("­")]
    [InlineData("Ⅸ")]         // NFKC
    [InlineData("été")]
    [InlineData("ﬁＡ")]
    public async Task ARoleWhosePasswordPostgresPreparedLogsInWithTheSamePassword(string password)
    {
        PgTestDb.Require();
        await using var admin = PgTestDb.Admin();
        string role = "u_saslprep_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await admin.Scalar($"CREATE ROLE {role} LOGIN PASSWORD '{password.Replace("'", "''", StringComparison.Ordinal)}'");
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "42501")
        {
            Assert.Skip("the test database's user can't create roles");
        }
        try
        {
            var s = PgSettings.Parse(PgTestDb.ConnectionString!);
            s.User = role;
            s.Password = password;
            var r = new PgReader(s);
            PgResult res;
            try
            {
                r.Start(1);
                res = await r.Command(new PgStatement("SELECT 1")).Run(null, null);
            }
            finally
            {
                LoopChecks.Stop(r);
            }
            if (Icu || !NeedsNormalizer(password)) Assert.True(res.IsOk, res.ToString());
            else Assert.Contains("InvariantGlobalization", res.Exception!.Message, StringComparison.Ordinal);
        }
        finally
        {
            await admin.Scalar($"DROP ROLE IF EXISTS {role}");
        }
    }

    static bool NeedsNormalizer(string password)
    {
        try
        {
            PgSaslPrep.Prepare(password, normalizes: false);
            return false;
        }
        catch (PgConnectionException)
        {
            return true;
        }
    }
}
