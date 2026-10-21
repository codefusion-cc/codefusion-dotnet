using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CodeFusion.Data.Generator;

/// <summary>What the write of a Query.Then leaves in its table, for the reads of its Select: an insert's row (with
/// OrIgnore, a row of its key, the one it wrote or one there before), or no more of the rows a delete's conditions
/// hold for.</summary>
sealed class WriteEffect(EntityInfo table)
{
    public EntityInfo Table { get; } = table;
    /// <summary>The inserted row; null for a delete.</summary>
    public ObjectValue? Inserted { get; init; }
    /// <summary>The insert's row as written, and its environment, to make it again from another row found.</summary>
    public IOperation? Row { get; init; }
    public Env? Env { get; init; }
    /// <summary>OrIgnore; whether the row sets the table's whole key, and whether a read relies on it: then the insert
    /// skips only a row of that key (its conflict target, <see cref="KeyConflict"/>), so a row of it is there after.</summary>
    public bool Ignored { get; init; }
    public string? KeyConflict { get; init; }
    public bool Targeted { get; set; }
    /// <summary>The delete's Where steps, whose environment holds the row found.</summary>
    public List<Step>? Deleted { get; init; }
    /// <summary>The rows found: the CTE's name, their entity, and the parameter of Then's lambda.</summary>
    public string Found { get; init; } = "";
    public EntityInfo FoundEntity { get; init; } = null!;
    public ISymbol FoundParameter { get; init; } = null!;
    /// <summary>The columns the insert sets, by name; whether a read of the table written is corrected; and the tables the
    /// Select reads, as SQL, for the statement's own check.</summary>
    public List<string> Columns { get; init; } = new();
    public bool Corrected { get; set; }
    public List<string> Reads { get; } = new();

    public void Read(string table)
    {
        if (!Reads.Contains(table)) Reads.Add(table);
    }
}

sealed partial class Translator
{
    /// <summary>The write of a Query.Then, while its Select is translated: the statement reads the table written as its
    /// snapshot has it, from before the write, so only a count or an Any of it is read, corrected by the write.</summary>
    WriteEffect? _written;

    /// <summary>Where an ignored insert's conflict target goes, decided once its Select is translated.</summary>
    const string ConflictMark = "\u0001conflict\u0001";

    /// <summary>Whether <paramref name="s"/> is Query.Then (a write from each row found), not Write.Then.</summary>
    bool IsFoundThen(Step s) => s.Name == "Then" && Known.Is(s.Call.TargetMethod.ContainingType, K.Query);

    static bool SameTable(EntityInfo a, EntityInfo b) => a.Table == b.Table && a.Schema == b.Schema;

    /// <summary>
    /// A read of <paramref name="table"/> in the Select after Then. The table written is refused, as the statement
    /// reads it as it was before the write (Count and Any of it are corrected instead). After a delete, so is a table
    /// whose rows lead to the table deleted from by a navigation, which a foreign key may cascade into. Every table
    /// read is kept for the statement's own check of the foreign keys the model doesn't see (<see cref="Dialect.ThenGuard"/>).
    /// </summary>
    void Unwritten(string table, string? schema, Location? at, EntityInfo? read = null)
    {
        if (_written is not { } w) return;
        if (w.Table.Table == table && w.Table.Schema == schema)
            throw new Refused(at, $"a read of {table} in the Select after Then, whose write changes {table} (the statement reads it as it was before the write; " +
                "only Count and Any of a navigation are read as the write left it): give back the value written, or write the query with Sql, so it");
        if (w.Deleted is not null && read is not null && CascadesInto(read, w.Table, new HashSet<string>()) is { } via)
            throw new Refused(at, $"a read of {table} after a delete of {w.Table.Table}, which may cascade into it ({via} leads to {w.Table.Table}): " +
                "read it in a query of its own, so it");
        w.Read(schema is null ? D.Quote(table) : D.Quote(schema) + "." + D.Quote(table));
    }

    void Unwritten(EntityInfo e, Location? at) => Unwritten(e.Table, e.Schema, at, e);

    /// <summary>The navigation by which <paramref name="e"/>'s rows lead to <paramref name="deleted"/>'s, if any: the
    /// foreign key a delete may cascade along.</summary>
    string? CascadesInto(EntityInfo e, EntityInfo deleted, HashSet<string> seen)
    {
        if (!seen.Add(e.Schema + "." + e.Table)) return null;
        foreach (var one in e.Ones.Values)
        {
            var target = Entity(one.Target, null);
            if (SameTable(target, deleted)) return $"{e.Type.Name}.{one.Property.Name}";
            if (CascadesInto(target, deleted, seen) is { } via) return via;
        }
        return null;
    }

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
        string writeSql = WriteFrom(Parse(write, step.Env.With(lambda.Symbol.Parameters[0], new RowValue(writeRow))), e, found, lambda.Symbol.Parameters[0], write.Syntax.GetLocation(), out var written);
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
        writeSql = writeSql.Replace(ConflictMark, written.Targeted ? written.KeyConflict : " ON CONFLICT DO NOTHING");
        if (select)
        {
            // every row found is written from; the reply is the one of the smallest key
            Expect(e.KeyColumns.Count > 0, at, $"Then's Select over {e.Type.Name}, which has no key to give back the first row found by ([Key], or an Id)");
            foreach (var k in e.KeyColumns) final.OrderBy.Add((Column(row, k, at), false));
            final.Limit = "1";
            // the statement checks first that the tables read are as the write left them, which the model can't see all of
            if (written.Corrected || written.Reads.Count > 0)
            {
                if (D.ThenGuard.Length == 0) throw new Refused(at, $"Then whose Select reads tables in {D.Name}, which can't check its tables' triggers and foreign keys, so it");
                written.Read(TableSql(written.Table));
                written.Read(TableSql(e));
                string columns = written.Columns.Count == 0 ? "''" : string.Join(", ", written.Columns.Select(Sql.String));
                final.Where.Add(Dialect.Format(D.ThenGuard, Sql.String(TableSql(written.Table)), columns, string.Join(", ", written.Reads.Select(Sql.String)),
                    written.Deleted is not null ? D.True : D.False, written.Corrected ? D.True : D.False));
            }
        }
        foreach (var c in writeRow.Needed.Concat(row.Needed).Distinct())
            find.Columns.Add((Column(findRow, e.Columns.Values.First(x => x.Name == c), at), null));
        string head = $"WITH {found} AS ({find.Render(D)})";
        Add(select ? $"{head}, {Aliases.Take("w")} AS ({writeSql}) {final.Render(D)}" : $"{head} {writeSql}", null);
    }

    /// <summary>The write of Query.Then, from each row of <paramref name="found"/> (the CTE): an insert of one row
    /// per row found, or a delete of another table's rows, joined to the rows found.</summary>
    string WriteFrom(Chain chain, EntityInfo foundEntity, string found, ISymbol foundParameter, Location at, out WriteEffect written)
    {
        var thens = Thens(chain);
        if (thens.Count > 0) throw new Refused(thens[0].Call.Syntax.GetLocation(), "a unit of writes in Then (it is one write from each row found: an insert, or a delete)");
        switch (chain.Source)
        {
            case InsertSource i:
            {
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
                var row = (ObjectValue)Eval(i.Row, i.Env);
                var (head, tail) = InsertHead(i.Entity, ignore, i.At);
                // a row that sets its whole key skips only a row of that key, so the Select knows one is there after
                var keys = i.Entity.KeyColumns;
                string? keyConflict = ignore && D.InsertOrIgnore == 0 && keys.Count > 0 && keys.All(k => row.Get(k.Property.Name) is not null)
                    ? $" ON CONFLICT ({string.Join(", ", keys.Select(k => D.Quote(k.Name)))}) DO NOTHING" : null;
                // the target is set once the Select is translated: the key, if a read relies on a row of it being there
                if (keyConflict is not null) tail = ConflictMark;
                written = new WriteEffect(i.Entity)
                {
                    Inserted = row, Row = i.Row, Env = i.Env, Ignored = ignore, KeyConflict = keyConflict, Found = found, FoundEntity = foundEntity, FoundParameter = foundParameter,
                    Columns = row.Members.Select(m => i.Entity.Columns.TryGetValue(m.Name, out var c) ? c.Name : null).OfType<string>().Distinct().ToList(),
                };
                var (cols, vals) = Values(row, i.Entity, i.At);
                return $"{head} ({string.Join(", ", cols)}) SELECT {string.Join(", ", vals)} FROM {found}{tail}";
            }
            case TableSource d when chain.Steps.Count > 0 && chain.Steps[chain.Steps.Count - 1].Name == "Delete":
            {
                NotTheFound(d.Entity, foundEntity, at);
                var wheres = chain.Steps.Take(chain.Steps.Count - 1).ToList();
                Expect(wheres.All(s => s.Name == "Where"), at, "operators before the delete in Then other than Where");
                written = new WriteEffect(d.Entity) { Deleted = wheres, Found = found, FoundEntity = foundEntity, FoundParameter = foundParameter };
                string alias = Aliases.Take(LambdaName(wheres) ?? Initials(d.Entity.Table));
                var target = new Row(d.Entity, alias, null);
                var conds = wheres.SelectMany(s => Sql.Conjuncts(Condition(Apply(s.Lambda(0), s.Env, new RowValue(target)), s.Call.Syntax.GetLocation()))).ToList();
                string table = $"DELETE FROM {TableSql(d.Entity)} {alias}";
                // one condition linking a column to the rows found (x.followee_id = t.id): the column is one of their
                // values, which an index of the deleted rows serves; any other use of them joins them
                var linked = conds.Where(c => Mentions(c, found)).ToList();
                if (D.AnyOfQuery.Length > 0 && linked.Count == 1 && Sql.EqualsColumnOf(linked[0], found, out var other, out var column) && !Mentions(other, found))
                {
                    conds[conds.IndexOf(linked[0])] = Dialect.Format(D.AnyOfQuery, other, $"SELECT {found}.{column} FROM {found}");
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

    // ---------- reads of the table written, as the write left it ----------

    /// <summary>The navigation whose subquery reads the table written as its snapshot has it, to be corrected.</summary>
    ManyValue? _correcting;

    /// <summary>
    /// Count or Any of a navigation into the table the write changes, as the write left it, from the statement's
    /// snapshot (which holds the table as it was): what the snapshot counts, corrected by what the write did to the
    /// rows counted. The snapshot of the rows P holds for is S. After an insert of row r, S and r, so Any is P(r) or
    /// any of S, and Count is S's and P(r): one more. With OrIgnore the row of r's key is there after, r or one there
    /// before (or a concurrent writer's, which the snapshot doesn't hold), so the one more counts only when S holds no
    /// row of that key; P may then read the key only, which that other row has alike. After a delete of the rows D
    /// holds for, S less those: Any is any of S and not D, Count is S's less S's and D's, which is one at most when D
    /// fixes the key.
    /// </summary>
    Value Corrected(ManyValue mv, string name, IInvocationOperation inv, Env env, WriteEffect w)
    {
        var at = inv.Syntax.GetLocation();
        string table = w.Table.Table;
        if (mv.Steps.Any(s => s.Name is "Select" or "Take"))
            throw new Refused(at, $"{name} after Select or Take of {table}, which the write before the Select changes (the write's correction is of rows counted), so it");
        if (w.Inserted is not null && w.Ignored)
        {
            if (w.KeyConflict is null)
                throw new Refused(at, $"{name} of {table} after an ignored insert that doesn't set its whole key ([Key] on each of its columns), so whether it wrote a row isn't known, and it");
            // the correction relies on a row of the key being there after: the insert skips only that
            w.Targeted = true;
        }
        var step = new Step(name, inv, env);
        w.Corrected = true;
        return Nested(() =>
        {
            var outer = _correcting;
            _correcting = mv;
            Select sub;
            Value current;
            try
            {
                sub = Subquery(mv, at, out current, out _);
            }
            finally
            {
                _correcting = outer;
            }
            var child = ((RowValue)current).Row;
            if (step.ArgCount == 1) sub.Where.Add(Condition(Apply(step.Lambda(0), env, current), at));
            var p = sub.Where.SelectMany(Sql.Conjuncts).Distinct().ToList();
            // every row found writes: the correction is of this row's write only, so another's may not be one of
            // the rows counted for it (the write from another row found, t2, holds for them only when t2 is t)
            if (!OnlyItsOwn(mv, step, env, w, child, p, at))
                throw new Refused(at, $"{name} of {table} after a write from rows found that may be several, where the write from another row found may count too " +
                    "(the correction is of each row's own write: count a navigation of the row found that the row written belongs to), so it");
            string Of(IEnumerable<string> where, string? column = null)
            {
                var s = new Select { From = sub.From };
                s.Joins.AddRange(sub.Joins);
                s.Where.AddRange(where.Distinct());
                if (column is not null) s.Columns.Add((column, null));
                return s.Render(D);
            }
            string count = $"({Of(p, "count(*)")})", any = $"EXISTS ({Of(p)})";
            var type = inv.Type!;
            if (w.Inserted is { } r)
            {
                // the inserted row's values for the row counted: with OrIgnore, only its key's
                var seen = w.Ignored ? KeyOnly(r, w.Table) : r;
                string holds = Holds(mv, step, env, seen, w, at);
                // P(r) may be NULL where C# says false (a column against a NULL one): Any is never NULL
                if (name == "Any") return Logic(holds == D.True || holds == D.False ? holds : $"COALESCE({holds}, {D.False})", any, " OR ", type, false);
                string added = holds;
                if (w.Ignored)
                {
                    // none of the key in the snapshot: a row written now, or by a concurrent writer since
                    var key = new Select();
                    var keyRow = FromTable(key, w.Table, child.Alias);
                    foreach (var k in w.Table.KeyColumns)
                        key.Where.Add(Condition(Equality(new SqlValue(Column(keyRow, k, at), k.Property.Type, false), r.Get(k.Property.Name)!, true, type, at), at));
                    added = Logic(holds, $"NOT EXISTS ({key.Render(D)})", " AND ", type, false).Sql;
                }
                return new SqlValue(added == D.False ? count : added == D.True ? $"{count} + 1" : $"{count} + CASE WHEN {added} THEN 1 ELSE 0 END", type, false);
            }
            // a delete: the rows its conditions hold for, of the row counted
            var deleted = w.Deleted!.SelectMany(s => Sql.Conjuncts(Condition(Apply(s.Lambda(0), s.Env, new RowValue(child)), s.Call.Syntax.GetLocation()))).ToList();
            // every row P holds for is deleted: none left
            if (deleted.All(p.Contains)) return new SqlValue(name == "Any" ? D.False : "0", type, false);
            if (name == "Any")
                return new SqlValue($"EXISTS ({Of(p.Append($"NOT COALESCE({string.Join(" AND ", deleted.Select(c => Sql.Wrap(c, " OR ")))}, {D.False})"))})", type, false);
            string gone = FixesKey(deleted, child.Alias!, w.Table) ? $"CASE WHEN EXISTS ({Of(p.Concat(deleted))}) THEN 1 ELSE 0 END" : $"({Of(p.Concat(deleted), "count(*)")})";
            return new SqlValue($"{count} - {gone}", type, false);
        });
    }

    /// <summary>The inserted row's key members only: what a row of its key, there before, has alike.</summary>
    static ObjectValue KeyOnly(ObjectValue r, EntityInfo table)
    {
        var keys = new HashSet<string>(table.KeyColumns.Select(k => k.Property.Name));
        return new ObjectValue(r.Type is INamedTypeSymbol n ? n : (INamedTypeSymbol)r.Type, r.Members.Where(m => keys.Contains(m.Name)).ToList());
    }

    /// <summary>
    /// Whether only the write from this row found (t) can change the rows counted for it: the write from another row
    /// found (t2, its row made or its conditions translated from t2) holds for them only when t2's key is t's. Then
    /// the correction by this row's write alone is the whole of it.
    /// </summary>
    bool OnlyItsOwn(ManyValue mv, Step step, Env env, WriteEffect w, Row child, List<string> counted, Location at)
    {
        var other = new Row(w.FoundEntity, Aliases.Take(w.Found), null, RowMode.Cte);
        var otherEnv = new RowValue(other);
        string? k = w.FoundEntity.Key is { } key ? D.Quote(key.Name) : null, own = w.Found + "." + k;
        if (w.Inserted is not null)
        {
            var made = (ObjectValue)Eval(w.Row!, w.Env!.With(w.FoundParameter, otherEnv));
            string holds = Holds(mv, step, env, w.Ignored ? KeyOnly(made, w.Table) : made, w, at);
            // the same row of the same key from every row found: one row, as the correction has it
            if (w.Ignored && holds == Holds(mv, step, env, KeyOnly(w.Inserted, w.Table), w, at)) return true;
            return holds == D.False || k is not null && Sql.Conjuncts(holds).Any(c => Sql.EqualsColumnOf(c, other.Alias!, out var x, out var col) && col == k && x == own);
        }
        List<string> Deletes(bool fromOther) => w.Deleted!.SelectMany(s => Sql.Conjuncts(Condition(Apply(s.Lambda(0), fromOther ? s.Env.With(w.FoundParameter, otherEnv) : s.Env,
            new RowValue(child)), s.Call.Syntax.GetLocation()))).ToList();
        var others = Deletes(fromOther: true);
        // the same delete from every row found, which reads nothing of it: the rows it deletes are the ones corrected for
        if (others.SequenceEqual(Deletes(fromOther: false))) return true;
        // else one of its conditions ties the counted row's column to t2's key, which the count's ties to t's
        return k is not null && others.Any(c => Sql.EqualsColumnOf(c, other.Alias!, out var x, out var col) && col == k && (counted.Contains($"{x} = {own}") || counted.Contains($"{own} = {x}")));
    }

    /// <summary>Whether the counted rows' conditions hold for the inserted row <paramref name="r"/>, as SQL: the
    /// navigation's key, its Wheres, and the predicate of Count or Any, folded where they are known
    /// (<c>t.id = t.id</c>).</summary>
    string Holds(ManyValue mv, Step step, Env env, ObjectValue r, WriteEffect w, Location at)
    {
        var fk = Inverse(mv, at).ForeignKey;
        var bools = K.Compilation.GetSpecialType(SpecialType.System_Boolean);
        int mark = Params.Mark;
        var aliases = Aliases.Mark();
        try
        {
            var parentKey = new SqlValue(Column(mv.Parent, KeyOf(mv.Parent.Entity, at), at), fk.Property.Type, false);
            string holds = Condition(Equality(r.Get(fk.Property.Name) ?? throw new Refused(at, "x"), parentKey, true, bools, at), at);
            foreach (var s in mv.Steps.Where(s => s.Name == "Where"))
                holds = Logic(holds, Condition(Apply(s.Lambda(0), s.Env, r), at), " AND ", bools, false).Sql;
            if (step.ArgCount == 1) holds = Logic(holds, Condition(Apply(step.Lambda(0), env, r), at), " AND ", bools, false).Sql;
            return holds;
        }
        catch (Refused)
        {
            Params.Reset(mark);
            Aliases.Reset(aliases);
            throw new Refused(at, $"{step.Name} of {w.Table.Table} with a condition on a column the insert before the Select doesn't set" +
                (w.Ignored ? " or that isn't of its key (the row of the key may be another's, there before)" : "") + ", so whether the row written is counted isn't known, and it");
        }
    }

    /// <summary>Whether <paramref name="conditions"/> fix every column of <paramref name="e"/>'s key (<c>x.k = v</c>,
    /// v not of row <paramref name="alias"/>): they hold for one row at most.</summary>
    bool FixesKey(List<string> conditions, string alias, EntityInfo e) =>
        e.KeyColumns.Count > 0 && e.KeyColumns.All(k => conditions.Any(c =>
            Sql.EqualsColumnOf(c, alias, out var other, out var column) && column == D.Quote(k.Name) && !Mentions(other, alias)));
}
