using System;
using System.Buffers.Binary;
using System.Text;
using System.Threading;

namespace CodeFusion.Data.Postgres;

/// <summary>The parameter types a statement declares (pg_type OIDs). Parameters go in binary, so the server must
/// know each one's type up front instead of inferring it.</summary>
// The values are Postgres's own OIDs, where 0 would ask the server to infer the type: the one thing this enum is
// there to prevent.
#pragma warning disable CA1008
public enum PgType : uint
#pragma warning restore CA1008
{
    /// <summary>bool, from <see cref="PgCommand.Bool"/>.</summary>
    Bool = 16,
    /// <summary>bigint, from <see cref="PgCommand.Int8"/>.</summary>
    Int8 = 20,
    /// <summary>integer, from <see cref="PgCommand.Int4"/>.</summary>
    Int4 = 23,
    /// <summary>text, from <see cref="PgCommand.Text(System.ReadOnlySpan{byte})"/>.</summary>
    Text = 25,
    /// <summary>double precision, from <see cref="PgCommand.Float8"/>.</summary>
    Float8 = 701,
    /// <summary>text[], from <see cref="PgCommand.BeginTextArray"/> and its elements.</summary>
    TextArray = 1009,
    /// <summary>timestamptz, from <see cref="PgCommand.Timestamptz"/>.</summary>
    Timestamptz = 1184,
}

/// <summary>
/// A statement every connection parses once, as <c>s{Id}</c>, the first time it runs there; afterwards it is only
/// bound and executed. Create each once (a static field): every instance is another server-side statement.
/// </summary>
public sealed class PgStatement
{
    static int _count;

    /// <summary>The statement's SQL, with $1, $2 for its parameters.</summary>
    public string Sql { get; }
    /// <summary>How many parameters each command must add.</summary>
    public int ParamCount { get; }
    internal readonly int Id;
    /// <summary>The Parse message and a Describe of the statement, sent before its first Bind on a connection: the
    /// RowDescription that answers says each column's type.</summary>
    internal readonly byte[] Parse;
    /// <summary>The Bind message's head up to the parameter values: portal, statement name, formats, count.</summary>
    internal readonly byte[] BindHead;
    /// <summary>For an update of one row by its key (<see cref="Postgres.KeyedUpdate"/>): the statement that runs a
    /// group's run of them at once, each parameter an array of theirs.</summary>
    internal PgStatement? Batch;
    /// <summary>On a <see cref="Batch"/>: each array parameter's element type.</summary>
    internal uint[]? ElementTypes;
    /// <summary>On a <see cref="Batch"/>: which parameter is the key (an int or a bigint).</summary>
    internal int KeyParameter;

    /// <summary>
    /// Commands of this statement run alone: on a connection of their own, each in a transaction of its own, never in
    /// a group. For a statement slow enough to hold up a group commit or a read group (a sign-up whose unique checks
    /// wait on locks, a report), at the cost of a round trip and a commit each.
    /// </summary>
    public bool RunsAlone
    {
        get => _alone;
        init => _alone = value;
    }

    bool _alone;

    /// <summary>Marks a statement made elsewhere (a query method's, by its provider) as running alone.</summary>
    internal void MarkAlone() => _alone = true;

    /// <summary>A statement with <paramref name="sql"/> and one parameter of each of <paramref name="parameters"/>, in order.</summary>
    public PgStatement(string sql, params ReadOnlySpan<PgType> parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (sql.Contains('\0', StringComparison.Ordinal)) throw new ArgumentException("SQL can't hold NUL", nameof(sql));
        if (parameters.Length > short.MaxValue) throw new ArgumentException("too many parameters", nameof(parameters));
        Id = Interlocked.Increment(ref _count) - 1;
        Sql = sql;
        ParamCount = parameters.Length;
        byte[] name = Encoding.ASCII.GetBytes($"s{Id}\0");

        int len = 4 + name.Length + Encoding.UTF8.GetByteCount(sql) + 1 + 2 + 4 * parameters.Length;
        Parse = new byte[1 + len + 1 + 4 + 1 + name.Length];
        var b = Parse.AsSpan();
        b[0] = (byte)'P';
        BinaryPrimitives.WriteInt32BigEndian(b[1..], len);
        int p = 5;
        name.CopyTo(b[p..]);
        p += name.Length;
        p += Encoding.UTF8.GetBytes(sql, b[p..]);
        b[p++] = 0;
        BinaryPrimitives.WriteInt16BigEndian(b[p..], (short)parameters.Length);
        p += 2;
        foreach (var t in parameters)
        {
            BinaryPrimitives.WriteUInt32BigEndian(b[p..], (uint)t);
            p += 4;
        }
        b[p++] = (byte)'D'; // Describe the statement
        BinaryPrimitives.WriteInt32BigEndian(b[p..], 4 + 1 + name.Length);
        p += 4;
        b[p++] = (byte)'S';
        name.CopyTo(b[p..]);

        // unnamed portal, the statement, one format code (binary) for every parameter, the parameter count
        BindHead = new byte[1 + name.Length + 6];
        name.CopyTo(BindHead.AsSpan(1));
        BinaryPrimitives.WriteInt16BigEndian(BindHead.AsSpan(1 + name.Length), 1);
        BinaryPrimitives.WriteInt16BigEndian(BindHead.AsSpan(3 + name.Length), 1);
        BinaryPrimitives.WriteInt16BigEndian(BindHead.AsSpan(5 + name.Length), (short)parameters.Length);
    }

    /// <summary>The server-side name and the SQL, for logs.</summary>
    public override string ToString() => $"s{Id}: {Sql}";
}
