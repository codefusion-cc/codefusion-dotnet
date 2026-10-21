using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CodeFusion.Data.Generator;

sealed partial class Translator
{
    /// <summary>The table the write of a Query.Then changes, while its Select is translated: that statement reads it as
    /// its snapshot has it, from before the write.</summary>
    EntityInfo? _written;

    /// <summary>Whether <paramref name="s"/> is Query.Then (a write from each row found), not Write.Then.</summary>
    bool IsFoundThen(Step s) => s.Name == "Then" && Known.Is(s.Call.TargetMethod.ContainingType, K.Query);

    static bool SameTable(EntityInfo a, EntityInfo b) => a.Table == b.Table && a.Schema == b.Schema;

    /// <summary>A read of <paramref name="table"/>, which the write before the Select changes: refused, as the
    /// statement would read it as it was before the write.</summary>
    void Unwritten(string table, string? schema, Location? at)
    {
        if (_written is { } w && w.Table == table && w.Schema == schema)
            throw new Refused(at, $"a read of {table} in the Select after Then, whose write changes {table} (the statement reads it as it was before the write): " +
                "give back the value written, or write the query with Sql, so it");
    }

    void Unwritten(EntityInfo e, Location? at) => Unwritten(e.Table, e.Schema, at);

    /// <summary>
    /// Query.Then: the rows the query finds (a CTE), for each a write made from it (a CTE that writes, or the
    /// statement itself), and the rows as found given back. One statement, so one round trip, atomic, and every part
    /// reads the same snapshot.
    /// <code>
    /// WITH t AS (SELECT u.id, u.username FROM users u WHERE u.username = $1),
    ///   w AS (INSERT INTO follows (follower_id, followee_id) SELECT $2, t.id FROM t ON CONFLICT DO NOTHING)
    /// SELECT t.username FROM t
    /// </code>
    /// </summary>
    void FoundWrite(TableSource t, List<Step> steps, int then)
    {
        var step = steps[then];
        var at = step.Call.Syntax.GetLocation();
        Expect(steps.Take(then).All(s => s.Name == "Where"), at, "operators before Then other than Where (it writes from each row found)");
        var rest = steps.Skip(then + 1).ToList();
        bool select = rest.Count > 0;
        if (select) Expect(rest.Count == 1 && rest[0].Name == "Select", at, "Then that doesn't end in .Select(...)");
        Expect(select == (_q.Shape != Shape.Write), at, select ? "Then with a Select in a method that returns no rows" : "Then without a Select in a method that returns rows (end it with .Select(...))");
        if (!D.WritableCtes) throw new Refused(at, $"Then in {D.Name}, which has no CTE that writes (the write from the rows found is one statement): write it with Sql, so it");
        _q.IsWrite = true;
        StartStatement();
        var e = t.Entity;
        var lambda = step.Lambda(0);
        string found = Aliases.Take("t");
        var find = new Select();
        var findRow = FromTable(find, e, LambdaName(steps.Take(then)) ?? lambda.Symbol.Parameters[0].Name);
        foreach (var w in steps.Take(then)) find.Where.Add(Condition(Apply(w.Lambda(0), w.Env, new RowValue(findRow)), w.Call.Syntax.GetLocation()));
        // the row as the write sees it, with nothing to join to; as the Select sees it, joined in the statement's select
        var writeRow = new Row(e, found, null, RowMode.Cte);
        var final = new Select { From = found };
        var row = new Row(e, found, final, RowMode.Cte);
        var write = Body(lambda.Body, lambda.Syntax.GetLocation());
        string writeSql = WriteFrom(Parse(write, step.Env.With(lambda.Symbol.Parameters[0], new RowValue(writeRow))), e, found, write.Syntax.GetLocation(), out var written);
        if (select)
        {
            var proj = rest[0];
            _written = written;
            try
            {
                var cols = new List<string>();
                Leaves(Apply(proj.Lambda(0), proj.Env, new RowValue(row)), _q.Row!, proj.Call.Syntax.GetLocation(), cols);
                foreach (var c in cols) final.Columns.Add((c, null));
            }
            finally
            {
                _written = null;
            }
        }
        foreach (var c in writeRow.Needed.Concat(row.Needed).Distinct())
            find.Columns.Add((Column(findRow, e.Columns.Values.First(x => x.Name == c), at), null));
        string head = $"WITH {found} AS ({find.Render(D)})";
        Add(select ? $"{head}, {Aliases.Take("w")} AS ({writeSql}) {final.Render(D)}" : $"{head} {writeSql}", null);
    }

    /// <summary>The write of Query.Then, from each row of <paramref name="found"/> (the CTE): an insert of one row
    /// per row found, or a delete of another table's rows, joined to the rows found.</summary>
    string WriteFrom(Chain chain, EntityInfo foundEntity, string found, Location at, out EntityInfo written)
    {
        var thens = Thens(chain);
        if (thens.Count > 0) throw new Refused(thens[0].Call.Syntax.GetLocation(), "a unit of writes in Then (it is one write from each row found: an insert, or a delete)");
        switch (chain.Source)
        {
            case InsertSource i:
            {
                written = i.Entity;
                NotTheFound(i.Entity, foundEntity, at);
                bool ignore = false;
                foreach (var s in chain.Steps)
                {
                    if (s.Name is "Then" or "ThenIf" or "ThenSelect")
                        throw new Refused(s.Call.Syntax.GetLocation(), "a unit of writes in Then (it is one write from each row found: an insert, or a delete)");
                    if (s.Name != "OrIgnore") throw new Refused(s.Call.Syntax.GetLocation(), $"'{s.Name}' after the insert in Then (the Select after Then gives back the rows found)");
                    ignore = true;
                }
                if (Unwrap(i.Row) is not IObjectCreationOperation)
                    throw new Refused(i.Row.Syntax.GetLocation(), $"an insert in Then that isn't 'new {i.Entity.Type.Name} {{ ... }}' (one row from each row found)");
                var (head, tail) = InsertHead(i.Entity, ignore, i.At);
                var (cols, vals) = Values((ObjectValue)Eval(i.Row, i.Env), i.Entity, i.At);
                return $"{head} ({string.Join(", ", cols)}) SELECT {string.Join(", ", vals)} FROM {found}{tail}";
            }
            case TableSource d when chain.Steps.Count > 0 && chain.Steps[chain.Steps.Count - 1].Name == "Delete":
            {
                written = d.Entity;
                NotTheFound(d.Entity, foundEntity, at);
                var wheres = chain.Steps.Take(chain.Steps.Count - 1).ToList();
                Expect(wheres.All(s => s.Name == "Where"), at, "operators before the delete in Then other than Where");
                string alias = Aliases.Take(LambdaName(wheres) ?? Initials(d.Entity.Table));
                var target = new Row(d.Entity, alias, null);
                var conds = wheres.SelectMany(s => Sql.Conjuncts(Condition(Apply(s.Lambda(0), s.Env, new RowValue(target)), s.Call.Syntax.GetLocation()))).ToList();
                string table = $"DELETE FROM {TableSql(d.Entity)} {alias}";
                // one condition linking a column to the rows found (x.followee_id = t.id): the column is one of their
                // values, which an index of the deleted rows serves; any other use of them joins them
                var linked = conds.Where(c => Mentions(c, found)).ToList();
                if (linked.Count == 1 && Sql.EqualsColumnOf(linked[0], found, out var other, out var column) && !Mentions(other, found))
                {
                    conds[conds.IndexOf(linked[0])] = Dialect.Format(D.InArray, other, $"ARRAY(SELECT {found}.{column} FROM {found})");
                    return $"{table} WHERE {string.Join(" AND ", conds.Select(c => Sql.Wrap(c, " OR ")))}";
                }
                return $"{table} USING {found}{(conds.Count > 0 ? " WHERE " + string.Join(" AND ", conds.Select(c => Sql.Wrap(c, " OR "))) : "")}";
            }
            default:
                throw new Refused(at, "a write in Then other than an insert of 'new T { ... }' or a delete (Where(...).Delete()): " +
                    "UpdateIf and DeleteIf write the rows found themselves");
        }
    }

    /// <summary>Whether <paramref name="sql"/> uses a column of the CTE <paramref name="alias"/> (<c>t.id</c>).</summary>
    static bool Mentions(string sql, string alias) =>
        System.Text.RegularExpressions.Regex.IsMatch(Sql.Code(sql), @"(?<![\w.""$])" + System.Text.RegularExpressions.Regex.Escape(alias) + @"\.");

    void NotTheFound(EntityInfo written, EntityInfo found, Location at)
    {
        if (SameTable(written, found))
            throw new Refused(at, $"a write in Then to {found.Table}, the table the rows are found in (UpdateIf and DeleteIf write the rows found)");
    }
}
