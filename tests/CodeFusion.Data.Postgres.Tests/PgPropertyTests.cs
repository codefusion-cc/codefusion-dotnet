using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using CodeFusion.Data.Postgres;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

#pragma warning disable xUnit1031 // properties run synchronously; the loops complete on their own threads

namespace CodeFusion.Data.Postgres.Tests;

/// <summary>
/// Property tests of the parameter encoders and the message parser: whatever the values and however the replies
/// are cut, what arrives is what was sent. Against the scripted server's decoder (written from the docs), and,
/// with CODEFUSION_TEST_DB, against Postgres itself.
/// </summary>
public sealed class PgPropertyTests
{
    static readonly PgStatement Three = new("three", PgType.Int8, PgType.Text, PgType.TextArray);

    static readonly Lazy<(FakeServer Server, PgReader Reader)> Fake = new(() =>
    {
        var server = new FakeServer { Rows = b => [[ServerWriter.BE64(b.Int8(0))]] };
        var reader = new PgReader(server.Settings());
        reader.Start(2);
        return (server, reader);
    });

    /// <summary>Strings .NET can't encode as UTF-8 (a lone surrogate) are refused when the command is built.</summary>
    static bool Encodable(string? s)
    {
        if (s is null) return true;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (char.IsSurrogate(s[i])) return false;
        }
        return true;
    }

    [Property(MaxTest = 300)]
    public bool ParametersArriveInPostgresBinaryFormat(string? text, string?[]? array, bool nullText, bool nullElement)
    {
        var (server, reader) = Fake.Value;
        array ??= [];
        if (nullText) text = null;
        if (nullElement) array = [.. array, null]; // the generator alone rarely makes NULLs
        bool encodable = Encodable(text) && array.All(Encodable);
        long key = Random.Shared.NextInt64();
        var cmd = reader.Command(Three).Int8(key);
        try
        {
            var res = cmd.Text(text).TextArray(array).Run(null, null).AsTask().GetAwaiter().GetResult();
            if (!encodable || !res.IsOk) return false;
        }
        catch (ArgumentException)
        {
            // EncoderFallbackException: a lone surrogate. The command is left as it was, still holding its pooled
            // job; Hold refuses the unfinished command and gives the job back.
            try
            {
                cmd.Hold();
                return false;
            }
            catch (InvalidOperationException)
            {
                return !encodable;
            }
        }
        finally
        {
            LoopChecks.AssertClean(reader);
        }
        var bind = server.Connections.SelectMany(c => { lock (c.Batches) return c.Batches.ToArray(); }).SelectMany(b => b)
            .Where(m => m.Type == 'B').Select(m => m.Bind()).Single(b => b.Int8(0) == key);
        return bind.Text(1) == text && bind.TextArray(2)!.SequenceEqual(array);
    }

    [Property(MaxTest = 300)]
    public bool Int8ParametersKeepEveryBit(long value)
    {
        var (server, reader) = Fake.Value;
        var res = reader.Command(Three).Int8(value).Text("").TextArray([]).Run(static (in PgRow r, object? _) => r.GetInt64(0), null)
            .AsTask().GetAwaiter().GetResult();
        LoopChecks.AssertClean(reader);
        return res.IsOk && res.Value == value;
    }

    /// <summary>Rows of random columns (NULLs, empty and binary values), cut into random receives.</summary>
    [Property(MaxTest = 200)]
    public bool RowsArriveWholeHoweverTheRepliesAreCut(byte[]?[][] rows, PositiveInt chunk)
    {
        rows ??= [];
        // every row as wide as the first, as a statement's rows are
        int width = rows.Length > 0 ? rows[0]?.Length ?? 0 : 0;
        rows = [.. rows.Select(r => (r ?? []).Concat(Enumerable.Repeat<byte[]?>(null, width)).Take(width).ToArray())];
        var server = new FakeServer { Rows = _ => rows, Chunk = chunk.Get % 97 + 1 };
        var reader = new PgReader(server.Settings());
        try
        {
            reader.Start(1);
            var seen = new List<byte[]?[]>();
            var res = reader.Command(Three).Int8(1).Text("x").TextArray([]).Run(static (in PgRow r, object? s) =>
            {
                var cols = new byte[]?[r.FieldCount];
                for (int c = 0; c < cols.Length; c++) cols[c] = r.IsNull(c) ? null : r.GetBytes(c).ToArray();
                ((List<byte[]?[]>)s!).Add(cols);
                return r.Index;
            }, seen).AsTask().GetAwaiter().GetResult();
            return res.IsOk && res.Rows == rows.Length && seen.Count == rows.Length
                && seen.Zip(rows).All(p => p.First.Length == p.Second.Length
                    && p.First.Zip(p.Second).All(c => c.First is null ? c.Second is null : c.Second is not null && c.First.SequenceEqual(c.Second)));
        }
        finally
        {
            LoopChecks.Stop(reader);
        }
    }

    static readonly Lazy<PgReader?> Real = new(() =>
    {
        if (PgTestDb.ConnectionString is null) return null;
        var reader = new PgReader(PgTestDb.Settings());
        reader.Start(2);
        return reader;
    });

    static readonly PgStatement RoundTrip = new("SELECT $1::int8, $2::text, convert_to(array_to_json($3::text[])::text, 'UTF8')",
        PgType.Int8, PgType.Text, PgType.TextArray);

    sealed class Back
    {
        public long Number;
        public string? Text;
        public string?[]? Array;
    }

    [Property(MaxTest = 150)]
    public bool PostgresReadsBackWhatWasSent(long number, string? text, string?[]? array, bool nullText, bool nullElement)
    {
        if (Real.Value is not { } reader) return true; // CODEFUSION_TEST_DB is not set
        array ??= [];
        if (nullText) text = null;
        if (nullElement) array = [null, .. array];
        if (!Encodable(text) || !array.All(Encodable)) return true; // refused before sending: the other property
        var back = new Back();
        var res = reader.Command(RoundTrip).Int8(number).Text(text).TextArray(array).Run(static (in PgRow r, object? s) =>
        {
            var b = (Back)s!;
            b.Number = r.GetInt64(0);
            b.Text = r.IsNull(1) ? null : Encoding.UTF8.GetString(r.GetBytes(1));
            b.Array = JsonSerializer.Deserialize<string?[]>(r.GetBytes(2));
            return 0;
        }, back).AsTask().GetAwaiter().GetResult();
        LoopChecks.AssertClean(reader);
        // Postgres text can't hold NUL: such a value fails its own statement, with the encoding error
        if ((text?.Contains('\0') ?? false) || array.Any(a => a?.Contains('\0') ?? false))
            return res.Status == PgStatus.ServerError && res.SqlState == "22021";
        return res.IsOk && back.Number == number && back.Text == text && back.Array!.SequenceEqual(array);
    }

    /// <summary>The ways to write one DEALLOCATE: the keywords in any case, PREPARE or not, comments and space
    /// anywhere between, a bare name in any case or a quoted one, a semicolon after.</summary>
    [Property(MaxTest = 500)]
    public bool ADeallocateOfAClientsStatementIsReadWhateverItsSpelling(NonNegativeInt id, bool prepare, bool quoted, bool semicolon, int seed)
    {
        var random = new Random(seed);
        string Case(string word) => string.Concat(word.Select(c => random.Next(2) == 0 ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)));
        string Gap() => random.Next(4) switch { 0 => " ", 1 => "\n\t", 2 => " /* a /* nested */ one */ ", _ => " -- a line\n" };
        string name = quoted ? $"\"s{id.Get}\"" : Case($"s{id.Get}");
        string sql = (random.Next(2) == 0 ? "" : Gap()) + Case("deallocate") + Gap() + (prepare ? Case("prepare") + Gap() : "") + name
            + (semicolon ? Gap() + ";" : "") + (random.Next(2) == 0 ? "" : Gap());
        return PgConnection.Deallocated(sql) == id.Get;
    }

    [Theory]
    [InlineData("DEALLOCATE myplan", -1)]
    [InlineData("DEALLOCATE prepare", -1)] // a statement named prepare
    [InlineData("DEALLOCATE PREPARE s", -1)]
    [InlineData("DEALLOCATE s01", -1)]
    [InlineData("DEALLOCATE \"S1\"", -1)] // quoted: another name than s1
    [InlineData("DEALLOCATE \"s1\"\"\"", -1)]
    [InlineData("DEALLOCATE s99999999999", -1)]
    [InlineData("DEALLOCATE s1x", -1)]
    [InlineData("DEALLOCATE S1", 1)]
    [InlineData("DEALLOCATE s1; SELECT 1", -2)]
    [InlineData("DEALLOCATE \"s1", -2)]
    [InlineData("DEALLOCATE /* never closed", -2)]
    [InlineData("DEALLOCATE", -2)]
    [InlineData("SELECT 1", -2)]
    [InlineData("", -2)]
    public void ADeallocateNamesTheClientsStatementOnlyInItsForm(string sql, int id) => Assert.Equal(id, PgConnection.Deallocated(sql));
}
