using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CodeFusion.Data.Generator;

/// <summary>What a query method returns.</summary>
enum Shape { One, Many, Page, WriteOf, Write }

/// <summary>What the column after a row's view columns means.</summary>
enum Marker
{
    None,
    /// <summary>A page's key: NULL in the one row of an empty page.</summary>
    PageKey,
    /// <summary>A child's key (SelectMany): NULL in the one row of a source row without children.</summary>
    ChildKey,
    /// <summary>The written row's key (UpdateIf, DeleteIf): NULL when the row was found but its condition didn't hold.</summary>
    Written,
}

/// <summary>One statement of a command, with the C# condition it is sent under (ThenIf).</summary>
sealed record Statement(string Name, string Sql, List<Param> Params, Guard? Condition);

/// <summary>A ThenIf's condition: its C#, evaluated once for all the statements under it, and the ThenIf it is in.</summary>
sealed class Guard(string code, Guard? outer)
{
    public string Code { get; } = code;
    public Guard? Outer { get; } = outer;

    /// <summary>Whether a statement under <paramref name="inner"/> (null: none) runs only when <paramref name="outer"/> holds.</summary>
    public static bool Within(Guard? inner, Guard? outer)
    {
        for (var g = inner; g is not null; g = g.Outer)
            if (g == outer) return true;
        return outer is null;
    }
}

/// <summary>A compiled query method: its statements and how its rows are read.</summary>
sealed class Compiled
{
    public IMethodSymbol Method = null!;
    public Shape Shape;
    public bool IsWrite;
    public ITypeSymbol? Row;
    public ViewInfo? View;
    public ValueKind? Scalar;
    public readonly List<Statement> Statements = new();
    public Marker Marker;
    public int MarkerColumn = -1, CountColumn = -1;
    public bool HasSource;
    public string? ListName, CountName;
    public List<InterceptAt> Calls = new();
}

/// <summary>A call to take over.</summary>
sealed record InterceptAt(string Attribute, string File, int Line);

/// <summary>A chain of operators and where it starts.</summary>
sealed class Chain
{
    public Source Source = null!;
    public readonly List<Step> Steps = new();
}

abstract record Source(Location At);
sealed record TableSource(EntityInfo Entity, Location At) : Source(At);
sealed record SqlSource(ExpressionSyntax Text, Env Env, Location At) : Source(At);
sealed record InsertSource(EntityInfo Entity, IOperation Row, Env Env, Location At) : Source(At);

sealed partial class Translator
{
    Compiled _q = null!;

    /// <summary>Compiles <paramref name="method"/>, whose body is <paramref name="body"/>; null with errors when it can't.</summary>
    public Compiled? Compile(IMethodSymbol method, IOperation body)
    {
        StartMethod(method);
        _inserted.Clear();
        _evaluations.Clear();
        var q = _q = new Compiled { Method = method };
        var ret = (INamedTypeSymbol)method.ReturnType;
        q.Shape = Known.Is(ret, K.One) ? Shape.One : Known.Is(ret, K.Many) ? Shape.Many : Known.Is(ret, K.Page) ? Shape.Page
            : Known.Is(ret, K.WriteOf) ? Shape.WriteOf : Shape.Write;
        q.Row = q.Shape == Shape.Write ? null : ret.TypeArguments[0];
        var list = Known.Attribute(method, K.JsonList);
        q.ListName = list?.ConstructorArguments.FirstOrDefault().Value as string;
        q.CountName = list?.NamedArguments.FirstOrDefault(a => a.Key == "CountName").Value.Value as string;
        var at = method.Locations.FirstOrDefault();
        try
        {
            if (q.Row is not null)
            {
                if (IsView(q.Row))
                {
                    q.View = View(q.Row, at);
                    q.ListName ??= q.View.ListName;
                    q.CountName ??= q.View.CountName;
                }
                else q.Scalar = Kinds.Of(K, q.Row) ?? throw new Refused(at, q.Row.TypeKind == TypeKind.Class
                    ? $"rows of the entity '{q.Row.Name}': entities are never built, so Select a view (or a value) first"
                    : $"rows of '{q.Row.ToDisplayString()}' (a view, or one value)");
            }
            var chain = Parse(body, new Env());
            Root(chain, body.Syntax.GetLocation());
            return q;
        }
        catch (Refused r)
        {
            if (!r.Reported) Errors.Add(Diagnostic.Create(r.Descriptor, r.Where ?? at, r.Args));
            return null;
        }
    }

    int Width => _q.View?.Width ?? 1;

    // ---------- chains ----------

    /// <summary>The chain an expression of a query definition is: its source and its operators.</summary>
    Chain Parse(IOperation op, Env env)
    {
        op = Unwrap(op);
        var at = op.Syntax.GetLocation();
        switch (op)
        {
            case IConversionOperation conv:
                return Parse(conv.Operand, env);
            case IPropertyReferenceOperation pr when Eval(pr, env) is TableValue t:
            {
                var c = new Chain { Source = new TableSource(t.Entity, at) };
                return c;
            }
            case IInvocationOperation inv:
            {
                var m = inv.TargetMethod;
                var args = inv.Arguments.Where(a => a.Parameter is not null).OrderBy(a => a.Parameter!.Ordinal).Select(a => Unwrap(a.Value)).ToList();
                if (IsDefinition(m))
                {
                    if (K.DerivesFromContext(m.ContainingType))
                    {
                        switch (m.Name)
                        {
                            case "Insert":
                            {
                                if (Eval(args[0], env) is not TableValue table) throw new Refused(args[0].Syntax.GetLocation(), "an insert into something other than a table of the context");
                                return new Chain { Source = new InsertSource(table.Entity, args[1], env, at) };
                            }
                            case "Sql":
                            {
                                var text = args[0].Syntax is ArgumentSyntax arg ? arg.Expression : (ExpressionSyntax)args[0].Syntax;
                                return new Chain { Source = new SqlSource(text, env, at) };
                            }
                        }
                        throw new Refused(at, $"'{m.Name}'");
                    }
                    var receiver = inv.Instance ?? (args.Count > 0 ? args[0] : throw new Refused(at, $"'{m.Name}' without a receiver"));
                    var chain = Parse(receiver, env);
                    chain.Steps.Add(new Step(m.Name, inv, env));
                    return chain;
                }
                if (IsQueryMethod(m) && inv.Instance is IInstanceReferenceOperation)
                {
                    // another query method of the context: its definition, with this call's arguments
                    if (!Inlinable(m, out var body, allowGeneric: false))
                        throw new Refused(at, Diagnostics.NotADefinition, m.Name);
                    if (!_inlining.Add(m.OriginalDefinition)) throw new Refused(at, $"'{m.Name}', which calls itself");
                    try
                    {
                        var e = new Env();
                        for (int i = 0; i < args.Count && i < m.Parameters.Length; i++)
                        {
                            var v = Eval(args[i], env);
                            if (v is not (Host or Constant)) throw new Refused(args[i].Syntax.GetLocation(), "an argument to a query method that isn't known when called");
                            e = e.With(m.OriginalDefinition.Parameters[i], v);
                        }
                        return Through(inv.Syntax, () => Parse(body!, e));
                    }
                    finally
                    {
                        _inlining.Remove(m.OriginalDefinition);
                    }
                }
                throw new Refused(at, $"'{Short(op.Syntax.ToString())}' as a query");
            }
            default:
                throw new Refused(at, $"'{Short(op.Syntax.ToString())}' as a query");
        }
    }

    /// <summary>Whether <paramref name="m"/> is a query method of a context: an instance method returning a result.</summary>
    public bool IsQueryMethod(IMethodSymbol m) =>
        !m.IsStatic && K.DerivesFromContext(m.ContainingType) && !IsDefinition(m) && m.MethodKind == MethodKind.Ordinary
        && (Known.Is(m.ReturnType, K.One) || Known.Is(m.ReturnType, K.Many) || Known.Is(m.ReturnType, K.Page) || Known.Is(m.ReturnType, K.WriteOf) || Known.Same(m.ReturnType, K.Write));

    void Root(Chain chain, Location at)
    {
        var thens = Thens(chain);
        if (thens.Count > 0)
        {
            _q.IsWrite = true;
            Expect(_q.Shape == Shape.Write, at, "Then after a write in a method that returns rows (a unit gives back the last statement's rows: end it with ThenSelect)");
        }
        switch (chain.Source)
        {
            case TableSource t:
            {
                int write = chain.Steps.FindIndex(s => s.Name is "DeleteIf" or "Delete" or "UpdateIf" or "Update");
                if (write >= 0)
                {
                    _q.IsWrite = true;
                    var w = chain.Steps[write];
                    if (w.Name is "DeleteIf" or "Delete") Delete(t, chain.Steps, write, null, root: thens.Count == 0);
                    else Update(t, chain.Steps, write, null, root: thens.Count == 0);
                }
                else
                {
                    Expect(thens.Count == 0, at, "Then after a read");
                    if (chain.Steps.Any(s => s.Name == "Page")) Page(t, chain.Steps);
                    else if (chain.Steps.Any(s => s.Name == "SelectMany")) SelectMany(t, chain.Steps);
                    else Read(t, chain.Steps);
                }
                break;
            }
            case SqlSource s:
                if (thens.Count == 0) SqlRoot(s, chain.Steps);
                else SqlWrite(s, chain.Steps, null, root: false);
                break;
            case InsertSource i:
                _q.IsWrite = true;
                Insert(i, chain.Steps, null, root: thens.Count == 0);
                break;
        }
        Then(thens, null);
    }

    /// <summary>Takes the chain's trailing Write.Then and ThenIf off it: the writes that follow in the same unit.</summary>
    List<Step> Thens(Chain chain)
    {
        int first = chain.Steps.Count;
        while (first > 0 && chain.Steps[first - 1].Name is "Then" or "ThenIf" && Known.Same(chain.Steps[first - 1].Call.TargetMethod.ContainingType, K.Write)) first--;
        var thens = chain.Steps.Skip(first).ToList();
        chain.Steps.RemoveRange(first, chain.Steps.Count - first);
        return thens;
    }

    void Then(List<Step> thens, Guard? condition)
    {
        foreach (var s in thens)
        {
            if (s.Name == "Then") Unit(s.Arg(0), s.Env, null, condition);
            else
            {
                Unit(s.Arg(1), s.Env, null, GuardOf(s, condition));
            }
        }
    }

    /// <summary>A value bound to a statement (under its ThenIf), as the emitter computes it: one local per value, reused
    /// where its statement runs only when that local's did.</summary>
    sealed record Bound(Guard? Under);

    /// <summary>Per impure value of the query: where it runs (<see cref="Bound"/>, a <see cref="Guard"/> it is the
    /// condition of, or "in" another value whose C# holds it).</summary>
    readonly Dictionary<string, List<object>> _evaluations = new();

    /// <summary>A ThenIf's condition, computed once for all its statements.</summary>
    Guard GuardOf(Step s, Guard? outer)
    {
        var at = s.Arg(0).Syntax.GetLocation();
        var v = Eval(s.Arg(0), s.Env);
        var g = new Guard(Code(v, at), outer);
        if (v is Host h) Evaluated(h, g);
        return g;
    }

    /// <summary>
    /// Counts where an impure value runs. C# runs it once, so it may be bound wherever one local serves, but not also
    /// written into another value's C# (an inlined call's argument used as is and in <c>k + 1</c>), nor under two
    /// ThenIfs neither inside the other: there it would run twice, and is refused.
    /// </summary>
    void Evaluated(Host h, object use)
    {
        if (h.Pure) return;
        if (!_evaluations.TryGetValue(h.Key, out var uses)) _evaluations[h.Key] = uses = new();
        bool same = use is Bound b ? uses.Any(u => u is Bound p && Guard.Within(b.Under, p.Under)) : uses.Contains(use);
        if (!same)
        {
            if (uses.Count > 0)
                throw new Refused(h.Where ?? _q.Method.Locations.FirstOrDefault(), $"'{Short(h.Code)}', which runs when the query is called, where it would run twice " +
                    "(written into another value, or under two ThenIfs neither inside the other: pass its value as a parameter of the query method), so it");
            uses.Add(use);
        }
        // its parts run where it runs
        foreach (var part in h.Parts) Evaluated(part, "in" + h.Key);
    }

    Statement Add(string sql, Guard? condition)
    {
        foreach (var p in Params.Items)
            foreach (var h in p.Origins)
                Evaluated(h, new Bound(condition));
        // named once the context's queries are all compiled (QueryGenerator.NameStatements)
        var s = new Statement(_q.Method.Name, sql, Params.Items.ToList(), condition);
        _q.Statements.Add(s);
        return s;
    }

    void Expect(bool ok, Location at, string what)
    {
        if (!ok) throw new Refused(at, what);
    }

    static string? LambdaName(IEnumerable<Step> steps) =>
        steps.SelectMany(s => s.Call.Arguments).Select(a => Unwrap(a.Value)).OfType<IAnonymousFunctionOperation>()
            .Select(f => f.Symbol.Parameters.FirstOrDefault()?.Name).FirstOrDefault(n => n is not null);

    /// <summary>The SQL of each column of <paramref name="v"/> as <paramref name="t"/>: one per view member, nested views flattened.</summary>
    public void Leaves(Value v, ITypeSymbol t, Location at, List<string> into, string? member = null)
    {
        if (!IsView(t))
        {
            // a reader fails on a NULL where the type has none (RawJson's NULL is [] by design)
            if (v is SqlValue { Nullable: true } && Kinds.Of(K, t) is { Nullable: false, Scalar: not Scalar.Raw })
                throw new Refused(at, $"a value that may be NULL here (a nullable column, or one of a row a left join reached) for {(member is null ? "a result" : $"'{member}'")} " +
                    $"of type '{t.ToDisplayString()}', which can't be NULL: make it nullable, or give a default with ??");
            into.Add(Sql.Group(Operand(v, at)));
            return;
        }
        var view = View(t, at);
        if (v is not ObjectValue o) throw new Refused(at, $"a '{t.Name}' that isn't made here: select it with new {t.Name}(...) or a static projection that makes one");
        foreach (var m in view.Members)
        {
            var mv = o.Get(m.Property.Name) ?? throw new Refused(at, $"a '{t.Name}' without '{m.Property.Name}': set every member");
            Leaves(mv, m.Property.Type, at, into, m.Property.Name);
        }
    }

    // ---------- reads ----------

    void Read(TableSource t, List<Step> steps)
    {
        StartStatement();
        var sel = new Select();
        var row = FromTable(sel, t.Entity, LambdaName(steps));
        Value current = new RowValue(row);
        var at = t.At;
        bool selected = false;
        var ordering = new Ordering(sel);
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var sat = s.Call.Syntax.GetLocation();
            switch (s.Name)
            {
                case "Where":
                    Unlimited(sel, s, "Where");
                    sel.Where.Add(Condition(Apply(s.Lambda(0), s.Env, current), sat));
                    break;
                case "Select":
                    current = Apply(s.Lambda(0), s.Env, current);
                    selected = true;
                    break;
                case "OrderBy" or "ThenBy" or "OrderByDescending" or "ThenByDescending":
                    Unlimited(sel, s, s.Name);
                    ordering.Add(s.Name, Operand(Apply(s.Lambda(0), s.Env, current), sat));
                    break;
                case "Take":
                    Take(sel, s);
                    break;
                case "Skip":
                    Skip(sel, s);
                    break;
                case "One" or "Many":
                    Expect(i == steps.Count - 1, sat, $"operators after {s.Name}()");
                    Expect(selected || current is not RowValue, sat, "rows of an entity: entities are never built, so Select a view (or a value) first");
                    // the first row is the result: the server makes and sends no other
                    if (s.Name == "One") sel.Limit = Smaller(sel.Limit, "1");
                    var cols = new List<string>();
                    Leaves(current, _q.Row!, sat, cols);
                    foreach (var c in cols) sel.Columns.Add((c, null));
                    Add(sel.Render(D), null);
                    return;
                case "Count":
                {
                    if (sel.Limit is not null || sel.Offset is not null)
                    {
                        sel.Columns.Clear();
                        Add($"SELECT count(*) FROM ({sel.Render(D)}) {Aliases.Take("s")}", null);
                    }
                    else
                    {
                        sel.OrderBy.Clear();
                        sel.Columns.Add(("count(*)", null));
                        Add(sel.Render(D), null);
                    }
                    return;
                }
                case "Any":
                    sel.OrderBy.Clear();
                    Add($"SELECT EXISTS ({sel.Render(D)})", null);
                    return;
                case "AsJson":
                {
                    Expect(selected && current is not (RowValue or ObjectValue), sat, "AsJson of rows: select one value of each first");
                    string value = Operand(current, sat);
                    string order = sel.OrderBy.Count == 0 ? "" : " ORDER BY " + string.Join(", ", sel.OrderBy.Select(o => o.Desc ? o.Sql + " DESC" : o.Sql));
                    Expect(sel.Limit is null && sel.Offset is null, sat, "AsJson after Take or Skip");
                    sel.OrderBy.Clear();
                    sel.Columns.Add((Dialect.Format(D.JsonAggregate, value, order), null));
                    Add(sel.Render(D), null);
                    return;
                }
                default:
                    throw new Refused(sat, $"'{s.Name}' in a query");
            }
        }
        throw new Refused(at, "a query that doesn't end in One(), Many(), Count(), Any(), AsJson() or Page(...).Select(...)");
    }

    // ---------- pages ----------

    void Page(TableSource t, List<Step> steps)
    {
        int page = steps.FindIndex(s => s.Name == "Page");
        var p = steps[page];
        var at = p.Call.Syntax.GetLocation();
        Expect(page == steps.Count - 2 && steps[page + 1].Name == "Select", at, "a page that doesn't end in .Page(...).Select(...)");
        var orders = steps.Take(page).Where(s => s.Name.StartsWith("Order", StringComparison.Ordinal) || s.Name.StartsWith("ThenBy", StringComparison.Ordinal)).ToList();
        Expect(orders.Count == 1, at, "a page that isn't ordered by exactly one key (the column that identifies its rows)");
        var wheres = steps.Take(page).Where(s => s.Name == "Where").ToList();
        Expect(wheres.Count + orders.Count == page, at, "operators before Page other than Where and one OrderBy");
        bool desc = orders[0].Name == "OrderByDescending";
        string dir = desc ? " DESC" : "";

        // the key: a column of the source that is its own key or a foreign key to the row the page shows
        var keyLambda = orders[0].Lambda(0);
        var keyBody = Body(keyLambda.Body, keyLambda.Syntax.GetLocation());
        keyBody = Unwrap(keyBody is IConversionOperation kc ? kc.Operand : keyBody);
        Expect(keyBody is IPropertyReferenceOperation { Instance: IParameterReferenceOperation } kp && t.Entity.Columns.ContainsKey(kp.Property.Name),
            keyLambda.Syntax.GetLocation(), "a page key that isn't a column of the row (s => s.Id)");
        var keyColumn = t.Entity.Columns[((IPropertyReferenceOperation)keyBody).Property.Name];
        EntityInfo shown;
        string? nav = null;
        if (Known.Same(keyColumn.Property, t.Entity.Key?.Property)) shown = t.Entity;
        else
        {
            var one = t.Entity.Ones.Values.FirstOrDefault(o => Known.Same(o.ForeignKey.Property, keyColumn.Property))
                ?? throw new Refused(keyLambda.Syntax.GetLocation(), "a page key that is neither the row's key nor a foreign key to the row the page shows");
            shown = Entity(one.Target, at);
            nav = one.Property.Name;
        }

        var countUpTo = Eval(p.Arg(1), p.Env) as Constant;
        Expect(countUpTo?.Constant_ is long or int, p.Arg(1).Syntax.GetLocation(), "a countUpTo that isn't a constant");
        long cap = System.Convert.ToInt64(countUpTo!.Constant_, System.Globalization.CultureInfo.InvariantCulture);
        Expect(cap >= 1, p.Arg(1).Syntax.GetLocation(), "a countUpTo below 1");
        // by position: an argument left out is there too, as its default
        bool probe = Eval(p.Arg(2), p.Env) is Constant { Constant_: int mode } && mode == 1;
        Expect(Eval(p.Arg(3), p.Env) is Constant { Constant_: int ml } && ml >= 1, p.Arg(3).Syntax.GetLocation(), "a maxLimit that isn't a positive constant");
        long maxLimit = (int)((Constant)Eval(p.Arg(3), p.Env)).Constant_!;
        var paging = Eval(p.Arg(0), p.Env);
        Expect(paging is Host, p.Arg(0).Syntax.GetLocation(), "a Paging that isn't a parameter of the query method");
        var pagingHost = (Host)paging;
        // a negative limit or offset is an empty page or the first, not Postgres's error
        var limit = Clamped(pagingHost, ".Limit");
        var offset = Clamped(pagingHost, ".Offset");

        StartStatement();
        string cte = Aliases.Take("c"), lateral = Aliases.Take("p"), inner = Aliases.Take("p"), b = Aliases.Take("b");
        string name = keyLambda.Symbol.Parameters[0].Name;

        Select Filtered(out Row row)
        {
            var s = new Select();
            row = FromTable(s, t.Entity, name);
            foreach (var w in wheres) s.Where.Add(Condition(Apply(w.Lambda(0), w.Env, new RowValue(row)), w.Call.Syntax.GetLocation()));
            return s;
        }

        // the count, capped
        string count;
        if (probe)
        {
            var exists = Filtered(out var er);
            exists.OrderBy.Add((Column(er, keyColumn, at), false));
            exists.Limit = "1";
            exists.Offset = Names.Number(cap - 1);
            var all = Filtered(out _);
            all.Columns.Add(("count(*)", null));
            count = $"SELECT CASE WHEN EXISTS ({exists.Render(D)}) THEN {Dialect.Format(D.BigIntLiteral, Names.Number(cap))} ELSE ({all.Render(D)}) END AS n";
        }
        else
        {
            var limited = Filtered(out _);
            limited.Limit = Names.Number(cap);
            count = $"SELECT count(*) AS n FROM ({limited.Render(D)}) {Aliases.Take("s")}";
        }

        // the page's keys, picked before anything is filled in
        var ids = Filtered(out var ir);
        ids.Columns.Add((Column(ir, keyColumn, at), "id"));
        ids.OrderBy.Add((Column(ir, keyColumn, at), desc));
        ids.Limit = Names.Number(maxLimit); // a constant: the generic plan expects the page's real size
        ids.Offset = Operand(offset, at);

        // then the rows of those keys only
        var fill = new Select { From = $"(SELECT id FROM ({ids.Render(D)}) {b} ORDER BY id{dir} {Dialect.Format(D.Limit, Operand(limit, at))}) {inner}" };
        var select = steps[page + 1];
        var proj = select.Lambda(0);
        var shownRow = new Row(shown, Aliases.Take(nav is null ? proj.Symbol.Parameters[0].Name : Initials(shown.Table)), fill);
        string shownKey = Column(shownRow, KeyOf(shown, at), at);
        fill.Joins.Add($"JOIN {TableSql(shown)} {shownRow.Alias} ON {shownKey} = {inner}.id");
        Row bound = nav is null ? shownRow : new Row(t.Entity, null, fill, RowMode.PageSource) { PageKey = keyColumn, PageTarget = shownRow, PageNav = nav };
        var value = Apply(proj, select.Env, new RowValue(bound));
        var cols = new List<string>();
        Leaves(value, _q.Row!, select.Call.Syntax.GetLocation(), cols);
        for (int i = 0; i < cols.Count; i++) fill.Columns.Add((cols[i], "c" + Names.Number(i)));
        fill.Columns.Add((shownKey, "k"));
        fill.OrderBy.Add((shownKey, desc));

        Add($"WITH {cte} AS ({count}) SELECT {lateral}.*, {cte}.n FROM {cte} {(D.Lateral ? "LEFT JOIN LATERAL" : "LEFT JOIN")} ({fill.Render(D)}) {lateral} ON true ORDER BY {lateral}.k{dir}", null);
        _q.Marker = Marker.PageKey;
        _q.MarkerColumn = cols.Count;
        _q.CountColumn = cols.Count + 1;
    }

    static string Initials(string table) => string.Concat(table.Split('_').Where(p => p.Length > 0).Select(p => p[0]));

    // ---------- a row's children ----------

    void SelectMany(TableSource t, List<Step> steps)
    {
        int sm = steps.FindIndex(s => s.Name == "SelectMany");
        var step = steps[sm];
        var at = step.Call.Syntax.GetLocation();
        Expect(sm == steps.Count - 2 && steps[sm + 1].Name == "Many", at, "SelectMany that doesn't end in .Many()");
        Expect(steps.Take(sm).All(s => s.Name == "Where"), at, "operators before SelectMany other than Where");
        StartStatement();
        var outer = new Select();
        var row = FromTable(outer, t.Entity, LambdaName(steps.Take(sm)) ?? step.Lambda(0).Symbol.Parameters[0].Name);
        foreach (var w in steps.Take(sm)) outer.Where.Add(Condition(Apply(w.Lambda(0), w.Env, new RowValue(row)), w.Call.Syntax.GetLocation()));
        var children = Apply(step.Lambda(0), step.Env, new RowValue(row)) as ManyValue
            ?? throw new Refused(step.Arg(0).Syntax.GetLocation(), "SelectMany of something other than a navigation (a => a.Comments...)");
        Expect(children.Steps.All(s => s.Name is "Where" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or "Take"), at,
            "a Select inside SelectMany's children: project them in its second lambda");
        if (!D.Lateral)
        {
            ChildrenByKey(step, row, outer, children, at);
            return;
        }
        var sub = Subquery(children, at, out var child, out _);
        var value = Apply(step.Lambda(1), step.Env, new RowValue(row), child);
        var cols = new List<string>();
        Leaves(value, _q.Row!, at, cols);
        sub.Columns.Clear();
        for (int i = 0; i < cols.Count; i++) sub.Columns.Add((cols[i], "c" + Names.Number(i)));
        var childRow = ((RowValue)child).Row;
        sub.Columns.Add((Column(childRow, KeyOf(childRow.Entity, at), at), "k"));
        string x = Aliases.Take("x");
        // the children's order again outside, on the lateral's columns
        var order = new List<(string, bool)>();
        int extra = 0;
        foreach (var (key, desc) in sub.OrderBy)
        {
            int found = sub.Columns.FindIndex(c => c.Sql == key);
            string column;
            if (found >= 0) column = sub.Columns[found].Alias!;
            else
            {
                column = "o" + Names.Number(extra++);
                sub.Columns.Add((key, column));
            }
            order.Add(($"{x}.{column}", desc));
        }
        outer.Joins.Add($"LEFT JOIN LATERAL ({sub.Render(D)}) {x} ON true");
        for (int i = 0; i <= cols.Count; i++) outer.Columns.Add(($"{x}.{(i < cols.Count ? "c" + Names.Number(i) : "k")}", null));
        outer.OrderBy.AddRange(order);
        Add(outer.Render(D), null);
        _q.Marker = Marker.ChildKey;
        _q.MarkerColumn = cols.Count;
        _q.HasSource = true;
    }

    /// <summary>
    /// A row's children where a subquery in FROM can't refer to the row before it (SQLite has no LATERAL): the
    /// children are joined by their keys being in the row's top n, a correlated subquery an ON may hold, which an index
    /// on the foreign key serves per row. The inner joins of the children's Where filter inside it; their navigations
    /// in the projection join outside, LEFT, so a row without children is still one row of NULLs.
    /// </summary>
    void ChildrenByKey(Step step, Row row, Select outer, ManyValue children, Location at)
    {
        var keys = Subquery(children, at, out var inner, out _);
        var innerRow = (inner as RowValue)?.Row ?? throw new Refused(at, "SelectMany of something other than a navigation's rows");
        var e = innerRow.Entity;
        var key = KeyOf(e, at);
        keys.Columns.Clear();
        keys.Columns.Add((Column(innerRow, key, at), null));
        string alias = Aliases.Take(step.Lambda(1).Symbol.Parameters[1].Name);
        outer.Joins.Add($"LEFT JOIN {TableSql(e)} {alias} ON {alias}.{D.Quote(key.Name)} IN ({keys.Render(D)})");
        var child = new Row(e, alias, outer) { Optional = true };
        var value = Apply(step.Lambda(1), step.Env, new RowValue(row), new RowValue(child));
        var cols = new List<string>();
        Leaves(value, _q.Row!, at, cols);
        foreach (var c in cols) outer.Columns.Add((c, null));
        outer.Columns.Add((Column(child, key, at), null));
        // the children's order again outside, on the joined rows
        foreach (var s in children.Steps)
        {
            if (s.Name is not ("OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending")) continue;
            var by = Apply(s.Lambda(0), s.Env, new RowValue(child));
            outer.OrderBy.Add((Operand(by, s.Call.Syntax.GetLocation()), s.Name.EndsWith("Descending", StringComparison.Ordinal)));
        }
        Add(outer.Render(D), null);
        _q.Marker = Marker.ChildKey;
        _q.MarkerColumn = cols.Count;
        _q.HasSource = true;
    }

    // ---------- deletes and updates ----------

    void Delete(TableSource t, List<Step> steps, int write, Guard? condition, bool root)
    {
        var w = steps[write];
        var at = w.Call.Syntax.GetLocation();
        var wheres = steps.Take(write).ToList();
        Expect(wheres.All(s => s.Name == "Where"), at, "operators before a delete other than Where");
        var rest = steps.Skip(write + 1).ToList();
        bool select = rest.Count > 0;
        if (select) Expect(rest[0].Name == "Select" && (rest.Count == 1 || rest.Count == 2 && rest[1].Name == "One"), at, "a delete that doesn't end in .Select(...).One()");
        if (root) Expect(select == (_q.Shape != Shape.Write), at, "a delete whose Select doesn't match the method's result");
        StartStatement();
        var e = t.Entity;
        var key = KeyOf(e, at);
        if (w.Name == "Delete")
        {
            var target = Target(e);
            var b = new StringBuilder($"DELETE FROM {TableSql(e)}");
            var conds = wheres.Select(s => Condition(Apply(s.Lambda(0), s.Env, new RowValue(target)), s.Call.Syntax.GetLocation())).ToList();
            if (conds.Count > 0) b.Append(" WHERE ").Append(string.Join(" AND ", conds.Select(c => Sql.Wrap(c, " OR "))));
            Add(select ? ReturningOrCte(b.ToString(), rest[0], e, root) : b.ToString(), condition);
            return;
        }
        if (!D.WritableCtes)
        {
            ConditionalApart(e, wheres, w, select ? rest[0] : null, null, condition, root, at);
            return;
        }
        // DeleteIf: find the rows, locked, delete each whose condition holds, and give back the rows as found, each
        // with its key again if it was deleted (NULL: found, but refused). The rows deleted come first, by key: the
        // reply (One) is the first deleted, and Refused only when every row found was
        string found = Aliases.Take("t"), deleted = Aliases.Take("d");
        var find = Find(e, wheres, w, out var findRow);
        var asFound = new Row(e, found, null, RowMode.Cte);
        asFound.Need(key.Name);
        string cond = Condition(Apply(w.Lambda(0), w.Env, new RowValue(asFound)), at);
        var cols = new List<string>();
        if (select) Leaves(Apply(rest[0].Lambda(0), rest[0].Env, new RowValue(asFound)), root ? _q.Row! : rest[0].Lambda(0).Symbol.ReturnType, at, cols);
        foreach (var c in asFound.Needed) find.Columns.Add((Column(findRow, e.Columns.Values.First(x => x.Name == c), at), null));
        string k = D.Quote(key.Name);
        string delete = $"DELETE FROM {TableSql(e)} WHERE {k} IN (SELECT {found}.{k} FROM {found} WHERE {cond})";
        if (!select)
        {
            Add($"WITH {found} AS ({find.Render(D)}) {delete}", condition);
            return;
        }
        Add($"WITH {found} AS ({find.Render(D)}), {deleted} AS ({delete} RETURNING {k}) SELECT {string.Join(", ", cols)}, {deleted}.{k} " +
            $"FROM {found} LEFT JOIN {deleted} ON {deleted}.{k} = {found}.{k} ORDER BY {deleted}.{k} IS NULL, {found}.{k}", condition);
        if (root)
        {
            _q.Marker = Marker.Written;
            _q.MarkerColumn = cols.Count;
        }
    }

    /// <summary>The row an UPDATE or DELETE writes: unaliased, as the hand-written statements have it, and by its
    /// table's name in a correlated subquery, which no alias of the statement takes.</summary>
    Row Target(EntityInfo e)
    {
        Aliases.Reserve(e.Table);
        return new Row(e, null, null) { Name = D.Quote(e.Table) };
    }

    /// <summary>
    /// The rows a conditional write finds, for its first CTE: locked (the target's rows only, not those its joins
    /// reach), so the condition is checked on each row as a concurrent write left it, not as the statement's
    /// snapshot saw it.
    /// </summary>
    Select Find(EntityInfo e, List<Step> wheres, Step write, out Row findRow)
    {
        var find = new Select();
        findRow = FromTable(find, e, LambdaName(wheres) ?? write.Lambda(0).Symbol.Parameters[0].Name);
        foreach (var s in wheres) find.Where.Add(Condition(Apply(s.Lambda(0), s.Env, new RowValue(findRow)), s.Call.Syntax.GetLocation()));
        if (D.LockRows.Length > 0) find.Lock = Dialect.Format(D.LockRows, findRow.Alias!);
        return find;
    }

    void Update(TableSource t, List<Step> steps, int write, Guard? condition, bool root)
    {
        var w = steps[write];
        var at = w.Call.Syntax.GetLocation();
        var wheres = steps.Take(write).ToList();
        Expect(wheres.All(s => s.Name == "Where"), at, "operators before an update other than Where");
        var rest = steps.Skip(write + 1).ToList();
        bool select = rest.Count > 0;
        if (select) Expect(rest.Count == 1 && rest[0].Name == "Select", at, "an update that doesn't end in .Select(...)");
        if (root) Expect(select == (_q.Shape != Shape.Write), at, "an update whose Select doesn't match the method's result");
        StartStatement();
        var e = t.Entity;
        var key = KeyOf(e, at);
        var target = Target(e);
        bool conditional = w.Name == "UpdateIf";
        string set = Set(w.Lambda(conditional ? 1 : 0), w.Env, target, at);
        if (!conditional)
        {
            var b = new StringBuilder($"UPDATE {TableSql(e)} SET {set}");
            var conds = wheres.Select(s => Condition(Apply(s.Lambda(0), s.Env, new RowValue(target)), s.Call.Syntax.GetLocation())).ToList();
            if (conds.Count > 0) b.Append(" WHERE ").Append(string.Join(" AND ", conds.Select(c => Sql.Wrap(c, " OR "))));
            if (!select)
            {
                Add(b.ToString(), condition);
                return;
            }
            Add(ReturningOrCte(b.ToString(), rest[0], e, root), condition);
            return;
        }
        if (!D.WritableCtes)
        {
            ConditionalApart(e, wheres, w, select ? rest[0] : null, set, condition, root, at);
            return;
        }
        // UpdateIf: find the rows, locked, update each whose condition holds; each row comes back as updated, or
        // with NULL columns when it was found but refused. The rows updated come first, by key: the reply
        // (Write<T>) is the first updated, and Refused only when every row found was
        string found = Aliases.Take("t");
        var find = Find(e, wheres, w, out var findRow);
        var asFound = new Row(e, null, null, RowMode.Cte) { Name = found };
        asFound.Need(key.Name);
        string cond = Condition(Apply(w.Lambda(0), w.Env, new RowValue(asFound)), at);
        foreach (var c in asFound.Needed) find.Columns.Add((Column(findRow, e.Columns.Values.First(x => x.Name == c), at), null));
        string k = D.Quote(key.Name);
        string update = $"UPDATE {TableSql(e)} SET {set} WHERE {k} IN (SELECT {k} FROM {found} WHERE {cond})";
        if (!select)
        {
            Add($"WITH {found} AS ({find.Render(D)}) {update}", condition);
            return;
        }
        var proj = rest[0].Lambda(0);
        string updated = Aliases.Take(proj.Symbol.Parameters[0].Name);
        var x = new Select { From = updated };
        var updatedRow = new Row(e, updated, x, RowMode.Cte);
        updatedRow.Need(key.Name);
        var cols = new List<string>();
        Leaves(Apply(proj, rest[0].Env, new RowValue(updatedRow)), root ? _q.Row! : proj.Symbol.ReturnType, at, cols);
        for (int i = 0; i < cols.Count; i++) x.Columns.Add((cols[i], "c" + Names.Number(i)));
        x.Columns.Add((Column(updatedRow, key, at), "k"));
        // each row found with its own updated row
        x.Where.Add($"{Column(updatedRow, key, at)} = {found}.{k}");
        string lateral = Aliases.Take("x");
        Add($"WITH {found} AS ({find.Render(D)}), {updated} AS ({update} RETURNING {string.Join(", ", updatedRow.Needed.Select(D.Quote))}) " +
            $"SELECT {lateral}.* FROM {found} LEFT JOIN LATERAL ({x.Render(D)}) {lateral} ON true ORDER BY {lateral}.k IS NULL, {found}.{k}", condition);
        if (root)
        {
            _q.Marker = Marker.Written;
            _q.MarkerColumn = cols.Count;
        }
    }

    /// <summary>
    /// UpdateIf and DeleteIf where a CTE can't write (SQLite): the write of the rows found whose condition holds,
    /// returning them (navigations as lookups by the written row's foreign keys) with their key; then, when the method
    /// reads rows, the rows found and refused with a NULL key, as Postgres's one statement gives them back, written rows
    /// first. An update's refused rows are NULL throughout; a delete's are the rows as found. The provider hands the
    /// reader every row of both statements, in order. No row lock: the writer's transaction is the only one, so the
    /// condition sees the rows as the write does.
    /// </summary>
    void ConditionalApart(EntityInfo e, List<Step> wheres, Step w, Step? select, string? set, Guard? condition, bool root, Location at)
    {
        var key = KeyOf(e, at);
        string k = D.Quote(key.Name);
        var find = Find(e, wheres, w, out var findRow);
        find.Where.Add(Condition(Apply(w.Lambda(0), w.Env, new RowValue(findRow)), at));
        find.Columns.Add((Column(findRow, key, at), null));
        string write = (set is null ? $"DELETE FROM {TableSql(e)}" : $"UPDATE {TableSql(e)} SET {set}") + $" WHERE {k} IN ({find.Render(D)})";
        if (select is null)
        {
            Add(write, condition);
            return;
        }
        var type = root ? _q.Row! : select.Lambda(0).Symbol.ReturnType;
        var target = Target(e);
        var cols = new List<string>();
        Leaves(Apply(select.Lambda(0), select.Env, new RowValue(target)), type, at, cols);
        int shown = cols.Count;
        cols.Add(Column(target, key, at));
        Add(write + Returning(cols), condition);

        StartStatement();
        var refused = Find(e, wheres, w, out var refusedRow);
        string cond = Condition(Apply(w.Lambda(0), w.Env, new RowValue(refusedRow)), at);
        refused.Where.Add($"NOT COALESCE({Sql.Group(cond)}, {D.False})");
        if (set is null)
        {
            var asFound = new List<string>();
            Leaves(Apply(select.Lambda(0), select.Env, new RowValue(refusedRow)), type, at, asFound);
            foreach (var c in asFound) refused.Columns.Add((c, null));
        }
        else
            for (int i = 0; i < shown; i++) refused.Columns.Add(("NULL", null));
        refused.Columns.Add(("NULL", null));
        refused.OrderBy.Add((Column(refusedRow, key, at), false));
        Add(refused.Render(D), condition);
        if (root)
        {
            _q.Marker = Marker.Written;
            _q.MarkerColumn = shown;
        }
    }

    /// <summary>The SET list of <c>a => new Article { Title = ..., ... }</c>, the row's columns unqualified.</summary>
    string Set(IAnonymousFunctionOperation set, Env env, Row target, Location at)
    {
        if (Apply(set, env, new RowValue(target)) is not ObjectValue o || !Known.Same(o.Type, target.Entity.Type))
            throw new Refused(set.Syntax.GetLocation(), $"a set that isn't 'x => new {target.Entity.Type.Name} {{ Column = value, ... }}'");
        Expect(o.Members.Count > 0, at, "an update that sets nothing");
        return string.Join(", ", o.Members.Select(m =>
        {
            var col = target.Entity.Columns.TryGetValue(m.Name, out var c) ? c : throw new Refused(at, $"setting '{m.Name}', which isn't a column");
            return $"{D.Quote(col.Name)} = {Operand(m.Value, at)}";
        }));
    }

    /// <summary>RETURNING the projection when it needs the written row's own columns only.</summary>
    string Returning(Step select, Row target)
    {
        var at = select.Call.Syntax.GetLocation();
        var cols = new List<string>();
        Leaves(Apply(select.Lambda(0), select.Env, new RowValue(target)), _q.Row!, at, cols);
        return Returning(cols);
    }

    string Returning(List<string> cols) =>
        D.ReturningAfter ? " RETURNING " + string.Join(", ", cols) : throw new Refused(null, $"RETURNING in {D.Name} (write it with Sql)");

    /// <summary>
    /// <paramref name="write"/> with the projection as RETURNING when it uses only the written row's columns, else as
    /// a CTE whose rows the projection selects from, joins and subqueries and all.
    /// </summary>
    string ReturningOrCte(string write, Step select, EntityInfo e, bool root)
    {
        var at = select.Call.Syntax.GetLocation();
        var proj = select.Lambda(0);
        var type = root ? _q.Row! : proj.Symbol.ReturnType;
        int mark = Params.Mark;
        var aliases = Aliases.Mark();
        try
        {
            var own = Target(e);
            var cols = new List<string>();
            Leaves(Apply(proj, select.Env, new RowValue(own)), type, at, cols);
            return write + Returning(cols);
        }
        catch (Refused)
        {
            Params.Reset(mark);
            Aliases.Reset(aliases);
        }
        string name = Aliases.Take("w");
        string alias = Aliases.Take(proj.Symbol.Parameters[0].Name);
        var outer = new Select { From = $"{name} {alias}" };
        var row = new Row(e, alias, outer, RowMode.Cte);
        var leaves = new List<string>();
        Leaves(Apply(proj, select.Env, new RowValue(row)), type, at, leaves);
        foreach (var c in leaves) outer.Columns.Add((c, null));
        outer.Ctes.Add($"{name} AS ({write} RETURNING {string.Join(", ", row.Needed.Select(D.Quote))})");
        return outer.Render(D);
    }

    // ---------- inserts and units of writes ----------

    void Insert(InsertSource source, List<Step> steps, Guard? condition, bool root)
    {
        var at = source.At;
        var e = source.Entity;
        bool ignore = false;
        int i = 0;
        for (; i < steps.Count && steps[i].Name == "OrIgnore"; i++) ignore = true;
        // the insert's own statement, with RETURNING when Select follows it at once
        Step? returning = i < steps.Count && steps[i].Name == "Select" ? steps[i] : null;
        if (returning is not null) Expect(i == steps.Count - 1, returning.Call.Syntax.GetLocation(), "operators after an insert's Select (use ThenSelect after Then)");
        StartStatement();
        // a key drawn from the table's sequence moves its current value, which an earlier insert's key was
        if (!SetsKey(source)) Moves(e);
        string insert = InsertSql(source, ignore, out var keySet, out var noKey);
        if (returning is not null)
        {
            Add(ReturningOrCte(insert, returning, e, root), condition);
            return;
        }
        Add(insert, condition);
        var insertedRow = new Row(e, null, null, RowMode.Inserted)
        {
            KeyValue = keySet,
            NoKey = noKey ?? (ignore && keySet is null ? "an insert with OrIgnore, which may write no row" : null),
        };
        _inserted.Add(insertedRow);
        for (; i < steps.Count; i++)
        {
            var s = steps[i];
            var sat = s.Call.Syntax.GetLocation();
            switch (s.Name)
            {
                case "Then":
                    Unit(s.Arg(0), s.Env, insertedRow, condition);
                    break;
                case "ThenIf":
                {
                    Unit(s.Arg(1), s.Env, insertedRow, GuardOf(s, condition));
                    break;
                }
                case "ThenSelect":
                {
                    Expect(i == steps.Count - 1, sat, "operators after ThenSelect");
                    if (root) Expect(_q.Shape == Shape.WriteOf, sat, "ThenSelect in a method that doesn't return Write<T>");
                    StartStatement();
                    var sel = new Select();
                    var proj = s.Lambda(0);
                    var row = FromTable(sel, e, proj.Symbol.Parameters[0].Name);
                    var key = KeyOf(e, sat);
                    // a drawn key in a scalar subquery: evaluated once, so the key's index is used
                    string inserted = Column(insertedRow, key, sat);
                    sel.Where.Add($"{Column(row, key, sat)} = {(insertedRow.KeyValue is null ? $"(SELECT {inserted})" : inserted)}");
                    var cols = new List<string>();
                    Leaves(Apply(proj, s.Env, new RowValue(row)), root ? _q.Row! : proj.Symbol.ReturnType, sat, cols);
                    foreach (var c in cols) sel.Columns.Add((c, null));
                    Add(sel.Render(D), condition);
                    break;
                }
                default:
                    throw new Refused(sat, $"'{s.Name}' after an insert");
            }
        }
        if (root && _q.Shape != Shape.Write && steps.LastOrDefault()?.Name != "ThenSelect")
            throw new Refused(at, "an insert that gives back no row in a method that returns one (end it with Select or ThenSelect)");
    }

    /// <summary>The rows the method's inserts wrote so far, whose keys later statements may use.</summary>
    readonly List<Row> _inserted = new();

    /// <summary>An insert into <paramref name="e"/>'s table draws a key from its sequence: the earlier inserts' keys
    /// into it, read as its current value, are gone.</summary>
    void Moves(EntityInfo e)
    {
        foreach (var r in _inserted)
            if (r.Entity.Table == e.Table && r.Entity.Schema == e.Schema) r.Moved = true;
    }

    /// <summary>Whether an insert's row sets its key itself, so it draws none from the sequence.</summary>
    bool SetsKey(InsertSource source)
    {
        var row = source.Row is IAnonymousFunctionOperation f ? Body(f.Body, f.Syntax.GetLocation()) : source.Row;
        if (Unwrap(row) is not IObjectCreationOperation { Initializer: { } init } || source.Entity.Key is not { } key) return false;
        return init.Initializers.Any(i => i is ISimpleAssignmentOperation { Target: IPropertyReferenceOperation p } && p.Property.Name == key.Property.Name);
    }

    /// <summary>The INSERT itself; <paramref name="keySet"/> is the key its row sets, a value known when called, and
    /// <paramref name="noKey"/> why later statements can't use its key, if they can't.</summary>
    string InsertSql(InsertSource source, bool ignore, out Value? keySet, out string? noKey)
    {
        var at = source.At;
        var e = source.Entity;
        keySet = null;
        noKey = null;
        string head = $"INSERT INTO {TableSql(e)}";
        string tail = ignore ? D.InsertOrIgnore switch
        {
            0 => " ON CONFLICT DO NOTHING",
            1 => "",
            _ => throw new Refused(at, $"OrIgnore in {D.Name}"),
        } : "";
        if (ignore && D.InsertOrIgnore == 1) head = $"INSERT OR IGNORE INTO {TableSql(e)}";
        var row = source.Row;
        if (row is IAnonymousFunctionOperation lambda)
        {
            // the row's values may use its own new key, drawn first
            string alias = Aliases.Take(lambda.Symbol.Parameters[0].Name);
            var key = KeyOf(e, at);
            var self = new Row(e, alias, null, RowMode.NewKey) { KeySql = alias + "." + D.Quote(key.Name) };
            var o = Apply(lambda, source.Env, new RowValue(self)) as ObjectValue
                ?? throw new Refused(lambda.Syntax.GetLocation(), $"an insert that isn't 'n => new {e.Type.Name} {{ ... }}'");
            var (cols, vals) = Values(o, e, at);
            KeySet(o, e, ref keySet, ref noKey);
            if (!self.KeyUsed) return $"{head} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)}){tail}";
            Expect(D.NextKey.Length > 0, at, $"an insert whose values use its new key in {D.Name}");
            Expect(o.Get(key.Property.Name) is null, at, "an insert that sets its own key and uses the new one");
            return $"{head} ({D.Quote(key.Name)}, {string.Join(", ", cols)}) SELECT {self.KeySql}, {string.Join(", ", vals)} " +
                $"FROM (SELECT {Sequence(D.NextKey, e, key)} AS {D.Quote(key.Name)}) {alias}{tail}";
        }
        if (row is IObjectCreationOperation)
        {
            var o = (ObjectValue)Eval(row, source.Env);
            var (cols, vals) = Values(o, e, at);
            KeySet(o, e, ref keySet, ref noKey);
            return $"{head} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)}){tail}";
        }
        // INSERT ... SELECT from a query whose Select makes the entity
        noKey = "an insert of a query's rows, which may be none or several";
        var chain = Parse(row, source.Env);
        if (chain.Source is not TableSource src || chain.Steps.Count == 0 || chain.Steps[chain.Steps.Count - 1].Name != "Select"
            || chain.Steps.Take(chain.Steps.Count - 1).Any(s => s.Name != "Where"))
            throw new Refused(row.Syntax.GetLocation(), $"an insert from something other than Table.Where(...).Select(x => new {e.Type.Name} {{ ... }})");
        var sel = new Select();
        var srcRow = FromTable(sel, src.Entity, LambdaName(chain.Steps));
        foreach (var w in chain.Steps.Take(chain.Steps.Count - 1)) sel.Where.Add(Condition(Apply(w.Lambda(0), w.Env, new RowValue(srcRow)), w.Call.Syntax.GetLocation()));
        var last = chain.Steps[chain.Steps.Count - 1];
        var made = Apply(last.Lambda(0), last.Env, new RowValue(srcRow)) as ObjectValue
            ?? throw new Refused(last.Call.Syntax.GetLocation(), $"an insert's Select that doesn't make a {e.Type.Name}");
        var (columns, values) = Values(made, e, at);
        foreach (var v in values) sel.Columns.Add((v, null));
        // SQLite reads the ON of ON CONFLICT as a join's after a FROM without WHERE
        if (ignore && D.InsertOrIgnore == 0 && D.InsertSelectNeedsWhere && sel.Where.Count == 0) sel.Where.Add(D.True);
        return $"{head} ({string.Join(", ", columns)}) {sel.Render(D)}{tail}";
    }

    /// <summary>The key a row sets: a value known when called is the inserted row's key for later statements; one
    /// SQL computes, they can't name again.</summary>
    static void KeySet(ObjectValue o, EntityInfo e, ref Value? keySet, ref string? noKey)
    {
        if (e.Key is not { } key || o.Get(key.Property.Name) is not { } v) return;
        if (v is Host or Constant) keySet = v;
        else noKey = "an insert whose key SQL computes";
    }

    (List<string> Columns, List<string> Values) Values(ObjectValue o, EntityInfo e, Location at)
    {
        Expect(Known.Same(o.Type, e.Type), at, $"an insert of a '{o.Type.Name}' into {e.Table}");
        var cols = new List<string>();
        var vals = new List<string>();
        foreach (var (name, value) in o.Members)
        {
            if (!e.Columns.TryGetValue(name, out var col)) throw new Refused(at, $"setting '{name}', which isn't a column");
            if (cols.Contains(D.Quote(col.Name))) continue;
            cols.Add(D.Quote(col.Name));
            vals.Add(Operand(value, at));
        }
        Expect(cols.Count > 0, at, "an insert that sets no column");
        return (cols, vals);
    }

    /// <summary>A write of a unit (Then's argument): its statements, after the ones so far.</summary>
    void Unit(IOperation write, Env env, Row? inserted, Guard? condition)
    {
        if (write is IAnonymousFunctionOperation lambda)
        {
            if (inserted is null) throw new Refused(lambda.Syntax.GetLocation(), "a lambda here (only an insert's Then has a row to give it)");
            env = env.With(lambda.Symbol.Parameters[0], new RowValue(inserted));
            write = Body(lambda.Body, lambda.Syntax.GetLocation());
        }
        var chain = Parse(write, env);
        var thens = Thens(chain);
        switch (chain.Source)
        {
            case InsertSource i:
                Insert(i, chain.Steps, condition, root: false);
                break;
            case SqlSource s:
                SqlWrite(s, chain.Steps, condition, root: false);
                break;
            case TableSource t:
            {
                int at = chain.Steps.FindIndex(x => x.Name is "DeleteIf" or "Delete" or "UpdateIf" or "Update");
                if (at < 0) throw new Refused(write.Syntax.GetLocation(), "a read as a write of the unit");
                if (chain.Steps[at].Name is "DeleteIf" or "Delete") Delete(t, chain.Steps, at, condition, root: false);
                else Update(t, chain.Steps, at, condition, root: false);
                break;
            }
        }
        Then(thens, condition);
    }

    // ---------- SQL ----------

    static readonly Regex Writes = new(@"\b(INSERT|UPDATE|DELETE|MERGE|TRUNCATE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    void SqlRoot(SqlSource source, List<Step> steps)
    {
        if (steps.Count > 0 && steps[0].Name is "One" or "Many" or "Page" or "Write")
        {
            var s = steps[0];
            if (steps.Count > 1) throw new Refused(steps[1].Call.Syntax.GetLocation(), $"operators after {s.Name}()");
            StartStatement();
            string sql = SqlText(source);
            if (s.Name != "Write" && Writes.Match(sql) is { Success: true } m)
                throw new Refused(s.Call.Syntax.GetLocation(), Diagnostics.WriteAsRead, m.Value.ToUpperInvariant());
            _q.IsWrite = s.Name == "Write";
            Add(sql, null);
            if (s.Name == "Page")
            {
                _q.Marker = Marker.PageKey;
                _q.MarkerColumn = Width;
                _q.CountColumn = Width + 1;
            }
            return;
        }
        _q.IsWrite = true;
        SqlWrite(source, steps, null, root: true);
    }

    /// <summary>A write in SQL that gives back nothing.</summary>
    void SqlWrite(SqlSource source, List<Step> steps, Guard? condition, bool root)
    {
        if (steps.Count > 0) throw new Refused(steps[0].Call.Syntax.GetLocation(), $"'{steps[0].Name}' after Sql(...)");
        if (root) Expect(_q.Shape == Shape.Write, source.At, "Sql(...) without a row type in a method that returns rows (use Sql<T>(...))");
        StartStatement();
        string sql = SqlText(source);
        // SQL that inserts into a table an earlier insert of the unit wrote moves its sequence too
        foreach (var r in _inserted)
            if (InsertsInto(sql, r.Entity)) r.Moved = true;
        Add(sql, condition);
    }

    static readonly Regex RowsInto = new(@"\b(?:(?:INSERT|MERGE)\s+INTO|COPY)\s+(?:ONLY\s+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="sql"/> writes rows into <paramref name="e"/>'s table (INSERT INTO, MERGE INTO or COPY
    /// it), which may draw keys from its sequence. A quoted name is the name; a bare one is matched whatever its case,
    /// and with any schema or none, so a statement that may be into the table counts.
    /// </summary>
    bool InsertsInto(string sql, EntityInfo e)
    {
        foreach (Match m in RowsInto.Matches(sql))
        {
            var name = QualifiedName(sql, m.Index + m.Length);
            if (name.Count == 0 || !SameName(name[name.Count - 1], e.Table)) continue;
            if (name.Count == 1 || e.Schema is null || SameName(name[name.Count - 2], e.Schema)) return true;
        }
        return false;
    }

    static bool SameName((string Text, bool Quoted) name, string table) =>
        string.Equals(name.Text, table, name.Quoted ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>The parts of the dotted name at <paramref name="at"/>, each unquoted, and whether it was quoted.</summary>
    List<(string Text, bool Quoted)> QualifiedName(string sql, int at)
    {
        char open = D.Quotes[0], close = D.Quotes.Length > 1 ? D.Quotes[1] : D.Quotes[0];
        var parts = new List<(string, bool)>();
        while (true)
        {
            while (at < sql.Length && char.IsWhiteSpace(sql[at])) at++;
            if (at < sql.Length && sql[at] == open)
            {
                var b = new StringBuilder();
                for (at++; at < sql.Length; at++)
                {
                    if (sql[at] != close) b.Append(sql[at]);
                    else if (at + 1 < sql.Length && sql[at + 1] == close) b.Append(sql[++at]);
                    else break;
                }
                at++;
                parts.Add((b.ToString(), true));
            }
            else
            {
                int start = at;
                while (at < sql.Length && (char.IsLetterOrDigit(sql[at]) || sql[at] is '_' or '$')) at++;
                if (at == start) return parts;
                parts.Add((sql.Substring(start, at - start), false));
            }
            while (at < sql.Length && char.IsWhiteSpace(sql[at])) at++;
            if (at >= sql.Length || sql[at] != '.') return parts;
            at++;
        }
    }

    /// <summary>
    /// The interpolated SQL with each hole a parameter, a constant string too (its text is a value, never SQL, and
    /// one where SQL expects a name is refused); a constant number or bool is a literal. A call of a [SqlTemplate] method is SQL, its template with its
    /// arguments: a shared fragment. Interpolated strings joined with + are one text.
    /// </summary>
    string SqlText(SqlSource source)
    {
        var model = Model(source.Text.SyntaxTree);
        var b = new StringBuilder();
        void Part(ExpressionSyntax e)
        {
            switch (e)
            {
                case ParenthesizedExpressionSyntax p:
                    Part(p.Expression);
                    return;
                case BinaryExpressionSyntax { RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddExpression } add:
                    Part(add.Left);
                    Part(add.Right);
                    return;
                case LiteralExpressionSyntax { RawKind: (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression } lit:
                    b.Append(lit.Token.ValueText);
                    return;
                case InterpolatedStringExpressionSyntax text:
                    for (int i = 0; i < text.Contents.Count; i++)
                    {
                        var part = text.Contents[i];
                        if (part is InterpolatedStringTextSyntax t)
                        {
                            b.Append(t.TextToken.ValueText);
                            continue;
                        }
                        var hole = (InterpolationSyntax)part;
                        var hat = hole.GetLocation();
                        if (hole.AlignmentClause is not null || hole.FormatClause is not null)
                            throw new Refused(hat, Diagnostics.BadHole, hole.Expression.ToString());
                        var op = model.GetOperation(hole.Expression) ?? throw new Refused(hat, Diagnostics.BadHole, hole.Expression.ToString());
                        Value v;
                        try
                        {
                            v = Eval(op, source.Env);
                        }
                        catch (Refused)
                        {
                            throw new Refused(hat, Diagnostics.BadHole, hole.Expression.ToString());
                        }
                        if (v is Constant { Constant_: string value })
                        {
                            // code written when a constant was spliced in as SQL: a parameter there is wrong SQL, or
                            // (ORDER BY $1) right SQL that does nothing
                            string after = i + 1 < text.Contents.Count && text.Contents[i + 1] is InterpolatedStringTextSyntax next ? next.TextToken.ValueText : "";
                            if (NamePosition(b.ToString(), after)) throw new Refused(hat, Diagnostics.NameHole, hole.Expression.ToString());
                            b.Append(Param(new Host(Names.Literal(value), op.Type!) { Pure = true }, hat));
                        }
                        else if (v is Constant c) b.Append(Literal(c, hat));
                        else if (v is Host h) b.Append(Param(h, hat));
                        else if (v is SqlValue fragment) b.Append(fragment.Sql);
                        else throw new Refused(hat, Diagnostics.BadHole, hole.Expression.ToString());
                    }
                    return;
                default:
                    throw new Refused(e.GetLocation(), "Sql of something other than an interpolated string written in place ($\"...\")");
            }
        }
        Part(source.Text);
        return b.ToString();
    }

    static readonly Regex NameBefore = new(@"(?:\b(?:ORDER\s+BY|GROUP\s+BY|(?<!\bDISTINCT\s+)FROM|JOIN|INTO|UPDATE|TABLE)|\.)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex ListStart = new(@"\b(?:SELECT(?:\s+DISTINCT)?|RETURNING|ORDER\s+BY|GROUP\s+BY)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex ClauseEnd = new(@"\b(?:SELECT|RETURNING|ORDER\s+BY|GROUP\s+BY|FROM|WHERE|SET|VALUES|ON|USING|HAVING|LIMIT|OFFSET|JOIN|WITH|AND|OR|NOT|CASE|WHEN|THEN|ELSE)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex IntoTable = new(@"\bINTO\s+[^\s(]+\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether a hole after <paramref name="before"/> and before <paramref name="after"/> stands where SQL expects a
    /// name, not a value: after ORDER BY, GROUP BY, FROM, JOIN, INTO, UPDATE or a qualifier's dot, before a dot, or as an
    /// item of a SELECT, RETURNING, ORDER BY or GROUP BY list or of an INSERT's column list. The common places only:
    /// this reads no more SQL than the clause the hole is in.
    /// </summary>
    static bool NamePosition(string before, string after)
    {
        if (after.TrimStart().StartsWith(".", StringComparison.Ordinal) || NameBefore.IsMatch(before) || ListStart.IsMatch(before)) return true;
        string t = before.TrimEnd();
        if (t.Length == 0) return false;
        if (t[t.Length - 1] == '(') return IntoTable.IsMatch(t.Substring(0, t.Length - 1));
        if (t[t.Length - 1] != ',') return false;
        // the list the item is in: the parenthesis it is in, or the clause
        int depth = 0;
        for (int i = t.Length - 2; i >= 0; i--)
        {
            char c = t[i];
            if (c == ')') depth++;
            else if (c == '(' && depth-- == 0) return IntoTable.IsMatch(t.Substring(0, i));
            else if (depth == 0 && char.IsLetter(c) && (i + 1 == t.Length || !char.IsLetterOrDigit(t[i + 1])) && ClauseEnd.Match(t.Substring(0, i + 1)) is { Success: true } m)
                return ListStart.IsMatch(m.Value);
        }
        return false;
    }
}
