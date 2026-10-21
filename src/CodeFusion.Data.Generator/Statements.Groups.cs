using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace CodeFusion.Data.Generator;

sealed partial class Translator
{
    /// <summary>
    /// The rows of a page of each source row's children (<c>Follows.Where(...).SelectMany(f =&gt; f.Followee.Articles,
    /// (f, a) =&gt; a)</c>): a lateral subquery per source row. For the page's own rows (<paramref name="cap"/>) it takes
    /// each group's first rows in the page's order, as many as the page can reach (its offset and its rows): the page's
    /// rows are among them, each being one of its group's first that many. So an index of the children by group and
    /// key serves each group, and no group is read past the page. A Take of the children is each group's own cap.
    /// <code>
    /// SELECT x.id FROM follows f CROSS JOIN LATERAL (SELECT a.id FROM articles a WHERE a.author_id = f.followee_id
    ///   ORDER BY a.id DESC LIMIT $2) x WHERE f.follower_id = $1
    /// </code>
    /// </summary>
    Select Groups(TableSource t, List<Step> wheres, Step sm, List<Step> childWheres, ColumnInfo keyColumn, bool desc, Host? cap, out string key)
    {
        var at = sm.Call.Syntax.GetLocation();
        var children = sm.Lambda(0);
        var s = new Select();
        var source = FromTable(s, t.Entity, LambdaName(wheres) ?? children.Symbol.Parameters[0].Name);
        foreach (var w in wheres) s.Where.Add(Condition(Apply(w.Lambda(0), w.Env, new RowValue(source)), w.Call.Syntax.GetLocation()));
        var mv = Apply(children, sm.Env, new RowValue(source)) as ManyValue
            ?? throw new Refused(sm.Arg(0).Syntax.GetLocation(), "SelectMany of something other than a navigation (f => f.Followee.Articles)");
        Expect(mv.Steps.All(x => x.Name is "Where" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or "Take"), at,
            "a Select inside SelectMany's children: the page's Select makes the view");
        var sub = Subquery(mv, at, out var current, out _);
        var child = ((RowValue)current).Row;
        if (Apply(sm.Lambda(1), sm.Env, new RowValue(source), current) is not RowValue { Row: var made } || made != child)
            throw new Refused(sm.Arg(1).Syntax.GetLocation(), "a SelectMany whose result isn't the child itself ((f, a) => a): the page's Select makes the view");
        string childKey = Column(child, keyColumn, at);
        bool taken = sub.Limit is not null;
        if (taken)
        {
            // each group's first n are the page's only in the page's order
            Expect(sub.OrderBy.Count == 1 && sub.OrderBy[0].Sql == childKey && sub.OrderBy[0].Desc == desc, at,
                "a Take of each row's children in an order other than the page's (it would take other rows than the page's first ones)");
            Expect(childWheres.Count == 0, childWheres.FirstOrDefault()?.Call.Syntax.GetLocation() ?? at, "Where after SelectMany of children taken with Take (SQL would filter before the Take: put it inside, before Take)");
        }
        Nested(() =>
        {
            foreach (var w in childWheres) sub.Where.Add(Condition(Apply(w.Lambda(0), w.Env, current), w.Call.Syntax.GetLocation()));
            return 0;
        });
        sub.Columns.Clear();
        sub.OrderBy.Clear();
        sub.Columns.Add((childKey, "id"));
        if (cap is not null)
        {
            sub.OrderBy.Add((childKey, desc));
            sub.Limit = Smaller(sub.Limit, Param(cap, at));
        }
        else if (taken) sub.OrderBy.Add((childKey, desc));
        string x = Aliases.Take("x");
        s.Joins.Add($"CROSS JOIN LATERAL ({sub.Render(D)}) {x}");
        key = x + ".id";
        // the source row's navigations the children were reached through for their key only (f.Followee's id is
        // f.followee_id) need no join: the navigation is required, so the joined row is there
        Prune(s, source);
        return s;
    }

    /// <summary>The joins of <paramref name="row"/>'s navigations into <paramref name="s"/> that nothing else in it reads,
    /// taken out: a required navigation's row is there, and the join is on its key, so it neither drops nor adds a row.</summary>
    void Prune(Select s, Row row)
    {
        foreach (var joined in row.Joins.Values)
        {
            Prune(s, joined);
            if (joined.JoinSql is not { } join || joined.Owner != s || !joined.Required) continue;
            int i = s.Joins.IndexOf(join);
            if (i < 0) continue;
            s.Joins.RemoveAt(i);
            if (Mentions(s.Render(D), joined.Alias!)) s.Joins.Insert(i, join);
        }
    }

    /// <summary>How many of each group's first rows a page reaches: its offset and its rows (at most
    /// <paramref name="maxLimit"/>), long.MaxValue past what a long holds, computed in C# where the query is called.</summary>
    Host PageEnd(Host offset, Host limit, long maxLimit)
    {
        string rows = $"global::System.Math.Min({limit.Code}, {Names.Number(maxLimit)}L)";
        var end = Composite($"({offset.Code} > global::System.Int64.MaxValue - {rows} ? global::System.Int64.MaxValue : {offset.Code} + {rows})",
            K.Compilation.GetSpecialType(SpecialType.System_Int64), true, null, offset, limit);
        if (!end.Pure) end.Site = offset.Site + "end;";
        return end;
    }
}
