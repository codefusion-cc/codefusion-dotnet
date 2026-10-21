using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CodeFusion.Data.Generator;

sealed partial class Translator
{
    readonly Dictionary<SyntaxTree, SemanticModel> _models = new();
    readonly HashSet<IMethodSymbol> _inlining = new(SymbolEqualityComparer.Default);

    public SemanticModel Model(SyntaxTree tree)
    {
        if (!_models.TryGetValue(tree, out var m)) _models[tree] = m = _compilation.GetSemanticModel(tree);
        return m;
    }

    public bool IsDefinition(ISymbol s) => Known.Attribute(s.OriginalDefinition, K.QueryDefinition) is not null;

    Value Invoke(IInvocationOperation inv, Env env)
    {
        var m = inv.TargetMethod;
        var at = inv.Syntax.GetLocation();
        var args = inv.Arguments.Where(a => a.Parameter is not null).OrderBy(a => a.Parameter!.Ordinal).Select(a => Unwrap(a.Value)).ToList();

        if (Known.Attribute(m, K.SqlTemplate) is { } template)
        {
            // the template may put an argument in a subquery of its own, where a bare column is its table's: the
            // row an UPDATE or DELETE writes is qualified by its name, as in a subquery the translator writes
            var values = Nested(() => args.Select(a => Eval(a, env)).ToList());
            var sql = values.Select((v, i) => Operand(v, args[i].Syntax.GetLocation())).ToArray();
            // its declared type says whether it gives NULL back (coalesce({0}, '') returning Utf8 doesn't), unless it
            // is given a NULL where its parameter takes none, which SQL's functions mostly give back (lower(NULL))
            bool nullable = IsNullable(m.ReturnType) || values.Where((v, i) => i >= m.Parameters.Length || !IsNullable(m.Parameters[i].Type)).Any(MayBeNull);
            return new SqlValue(Dialect.Format((string)template.ConstructorArguments[0].Value!, sql), m.ReturnType, nullable);
        }
        if (Known.Same(m.ContainingType, K.SqlFunctions))
            return m.Name switch
            {
                "Now" => new SqlValue(D.Now, m.ReturnType, false),
                _ => throw new Refused(at, $"SqlFunctions.{m.Name}"),
            };
        if (IsDefinition(m))
        {
            var receiver = inv.Instance is { } i ? Eval(i, env) : args.Count > 0 ? Eval(args[0], env) : null;
            switch (receiver)
            {
                case ManyValue mv:
                    return Many(mv, m.Name, inv, env);
                case TableValue t when m.Name == "Single" && args.Count == 1:
                    return new SingleValue(t.Entity, Lambda(args[0]), env);
                default:
                    throw new Refused(at, $"'{m.Name}' here (inside an expression, a table is used through Single(...), and a navigation through Any, Count, Select, Where, OrderBy, Take and AsJson)");
            }
        }
        // a collection known when called: host.Contains(column) is "column = ANY(parameter)"
        if (m.Name == "Contains" && (inv.Instance is not null ? args.Count == 1 : args.Count == 2))
        {
            var list = inv.Instance is { } li ? Eval(li, env) : Eval(args[0], env);
            var item = Eval(inv.Instance is not null ? args[0] : args[1], env);
            if (list is Host h && item is not Host)
            {
                var p = ParamOf(h, at);
                if (p.Kind != ParamType.TextArray) throw new Refused(at, "Contains on a list that isn't a list of Utf8");
                return new SqlValue(Dialect.Format(D.InArray, Operand(item, at), Params.Add(p)), m.ReturnType, false);
            }
        }
        if (m.IsStatic && Inlinable(m, out var body))
        {
            if (!_inlining.Add(m.OriginalDefinition)) throw new Refused(at, $"'{m.Name}', which calls itself");
            try
            {
                var e = new Env(sites: env.Sites + SiteOf(inv.Syntax));
                for (int i = 0; i < args.Count && i < m.Parameters.Length; i++)
                {
                    var v = Eval(args[i], env);
                    e = e.With(m.OriginalDefinition.Parameters[i], v is Host h ? Argument(h) : v);
                }
                return Eval(body!, e);
            }
            finally
            {
                _inlining.Remove(m.OriginalDefinition);
            }
        }
        // anything else runs in C#, if it depends only on values known when called
        Reachable(m, at);
        var parts = new List<string>();
        var made = new List<Value>();
        foreach (var a in args)
        {
            var v = Eval(a, env);
            made.Add(v);
            if (v is not (Host or Constant)) throw new Refused(at, $"the call '{Short(inv.Syntax.ToString())}', which neither has a SQL form nor depends only on the query's parameters");
            parts.Add(Code(v, at));
        }
        string target;
        if (inv.Instance is { } inst)
        {
            var self = Eval(inst, env);
            if (self is not (Host or Constant)) throw new Refused(at, $"the call '{Short(inv.Syntax.ToString())}', which has no SQL form");
            made.Add(self);
            target = Code(self, at) + "." + Names.Identifier(m.Name);
        }
        else if (m.IsExtensionMethod)
        {
            target = $"{m.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{Names.Identifier(m.Name)}";
        }
        else target = $"{m.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{Names.Identifier(m.Name)}";
        // a call may do anything: its value is the one made where it is written
        return Composite($"{target}({string.Join(", ", parts)})", m.ReturnType, false, null, made.ToArray());
    }

    public IAnonymousFunctionOperation Lambda(IOperation op) =>
        Unwrap(op) as IAnonymousFunctionOperation ?? throw new Refused(op.Syntax.GetLocation(), "an operator's argument that isn't a lambda written in place");

    /// <summary>Whether <paramref name="m"/> is a source method whose body is one expression, which a definition inlines.</summary>
    bool Inlinable(IMethodSymbol m, out IOperation? body, bool allowGeneric = false)
    {
        body = null;
        if (m.IsGenericMethod && !allowGeneric || m.DeclaringSyntaxReferences.Length != 1) return false;
        if (m.DeclaringSyntaxReferences[0].GetSyntax() is not MethodDeclarationSyntax decl || DefinitionOf(decl) is not { } expr) return false;
        body = Model(decl.SyntaxTree).GetOperation(expr);
        return body is not null;
    }

    /// <summary>A method's definition: its expression body, or the expression of a block holding one return.</summary>
    public static ExpressionSyntax? DefinitionOf(MethodDeclarationSyntax decl) => decl.ExpressionBody?.Expression
        ?? (decl.Body?.Statements.Count == 1 && decl.Body.Statements[0] is ReturnStatementSyntax { Expression: { } r } ? r : null);

    // ---------- many-navigations ----------

    Value Many(ManyValue mv, string name, IInvocationOperation inv, Env env)
    {
        var at = inv.Syntax.GetLocation();
        switch (name)
        {
            case "Where" or "Select" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or "Take":
                return new ManyValue(inv.Type!, mv.Parent, mv.Nav, mv.Target, new List<Step>(mv.Steps) { new(name, inv, env) });
            case "Any" or "Count":
                return Nested(() =>
                {
                    var sub = Subquery(mv, at, out var current, out _);
                    var step = new Step(name, inv, env);
                    if (step.ArgCount == 1)
                    {
                        // SQL would filter before the LIMIT, C# among the rows Take kept
                        if (sub.Limit is not null) throw new Refused(at, $"{name}(predicate), a condition after Take (filter with Where before Take)");
                        var pred = step.Lambda(0);
                        sub.Where.Add(Condition(Apply(pred, env, current), at));
                    }
                    if (name == "Any")
                    {
                        sub.Columns.Clear();
                        sub.OrderBy.Clear();
                        return new SqlValue($"EXISTS ({sub.Render(D)})", inv.Type!, false);
                    }
                    if (sub.Limit is not null)
                    {
                        sub.Columns.Clear();
                        sub.OrderBy.Clear();
                        return new SqlValue($"(SELECT count(*) FROM ({sub.Render(D)}) {Aliases.Take("s")})", inv.Type!, false);
                    }
                    sub.Columns.Clear();
                    sub.OrderBy.Clear();
                    sub.Columns.Add(("count(*)", null));
                    return new SqlValue($"({sub.Render(D)})", inv.Type!, false);
                });
            case "AsJson":
                return Nested(() =>
                {
                    var sub = Subquery(mv, at, out var current, out bool selected);
                    if (!selected || current is not (SqlValue or Host or Constant))
                        throw new Refused(at, "AsJson of rows: select one value of each first (Select(t => t.Name).AsJson())");
                    string value = Operand(current, at);
                    sub.Columns.Clear();
                    sub.Columns.Add((value, null));
                    for (int i = 0; i < sub.OrderBy.Count; i++)
                        if (sub.OrderBy[i].Sql == value) sub.OrderBy[i] = ("1", sub.OrderBy[i].Desc);
                    return new SqlValue(Dialect.Format(D.JsonArray, sub.Render(D)), inv.Type!, false);
                });
            default:
                throw new Refused(at, $"'{name}' on a navigation");
        }
    }

    /// <summary>The correlated subquery of a many-navigation with its operators applied; <paramref name="current"/> is
    /// what its rows are after the last Select.</summary>
    public Select Subquery(ManyValue mv, Location at, out Value current, out bool selected)
    {
        _nested++;
        try
        {
            return SubqueryCore(mv, at, out current, out selected);
        }
        finally
        {
            _nested--;
        }
    }

    Select SubqueryCore(ManyValue mv, Location at, out Value current, out bool selected)
    {
        var sub = new Select();
        var parent = mv.Parent;
        var target = mv.Target;
        string parentKey = Column(parent, KeyOf(parent.Entity, at), at);
        string? hint = mv.Steps.Select(s => Unwrap(s.Call.Arguments.LastOrDefault()?.Value ?? s.Call)).OfType<IAnonymousFunctionOperation>()
            .Select(f => f.Symbol.Parameters.FirstOrDefault()?.Name).FirstOrDefault(n => n is not null);
        Row child;
        if (mv.Nav.IsThrough)
        {
            string alias = Aliases.ForTable(mv.Nav.JoinTable!);
            sub.From = D.Quote(mv.Nav.JoinTable!) + " " + alias;
            string thisColumn = mv.Nav.ThisColumn ?? Names.Snake(parent.Entity.Type.Name) + "_id";
            string otherColumn = mv.Nav.OtherColumn ?? Names.Snake(target.Type.Name) + "_id";
            sub.Where.Add($"{alias}.{D.Quote(thisColumn)} = {parentKey}");
            child = new Row(target, null, sub, RowMode.Through) { JoinAlias = alias, JoinColumn = otherColumn };
        }
        else
        {
            child = FromTable(sub, target, hint);
            var inverse = target.Ones.Values.Where(o => Known.Same(o.Target, parent.Entity.Type)
                && (mv.Nav.InverseNavigation is null || o.Property.Name == mv.Nav.InverseNavigation)).ToList();
            if (inverse.Count != 1)
                throw new Refused(at, Diagnostics.BadEntity, parent.Entity.Type.Name, inverse.Count == 0
                    ? $"'{mv.Nav.Property.Name}': {target.Type.Name} has no navigation back to {parent.Entity.Type.Name} (or name its join table with [JoinTable])"
                    : $"'{mv.Nav.Property.Name}': {target.Type.Name} has several navigations to {parent.Entity.Type.Name}; name one with [InverseProperty]");
            sub.Where.Add($"{Column(child, inverse[0].ForeignKey, at)} = {parentKey}");
        }
        current = new RowValue(child);
        selected = false;
        var order = new Ordering(sub);
        foreach (var step in mv.Steps)
        {
            switch (step.Name)
            {
                case "Where":
                    Unlimited(sub, step, "Where");
                    sub.Where.Add(Condition(Apply(step.Lambda(0), step.Env, current), at));
                    break;
                case "Select":
                    current = Apply(step.Lambda(0), step.Env, current);
                    selected = true;
                    break;
                case "OrderBy" or "ThenBy" or "OrderByDescending" or "ThenByDescending":
                    Unlimited(sub, step, step.Name);
                    order.Add(step.Name, Operand(Apply(step.Lambda(0), step.Env, current), at));
                    break;
                case "Take":
                    Take(sub, step);
                    break;
            }
        }
        return sub;
    }

    // ---------- order, Take and Skip ----------

    /// <summary>
    /// A select's ORDER BY as LINQ's operators build it: OrderBy is a stable sort, so a later one orders first and
    /// the keys before it break its ties; ThenBy adds a key to the last OrderBy's.
    /// </summary>
    sealed class Ordering(Select select)
    {
        int _last;

        public void Add(string op, string key)
        {
            bool desc = op.EndsWith("Descending", StringComparison.Ordinal);
            if (op.StartsWith("OrderBy", StringComparison.Ordinal))
            {
                select.OrderBy.Insert(0, (key, desc));
                _last = 1;
            }
            else select.OrderBy.Insert(_last++, (key, desc));
        }
    }

    /// <summary>Refuses <paramref name="op"/> after Take or Skip: SQL applies it before the LIMIT and OFFSET, C# to the
    /// rows they kept.</summary>
    void Unlimited(Select select, Step step, string op)
    {
        string? after = select.Limit is not null ? "Take" : select.Offset is not null ? "Skip" : null;
        if (after is not null)
            throw new Refused(step.Call.Syntax.GetLocation(), $"{op} after {after} (SQL would apply it first: put it before {after}, or write the query with Sql)");
    }

    /// <summary>
    /// Take(n) after the operators so far: at most n more rows, a negative n none (as LINQ's), and after an earlier
    /// Take the smaller.
    /// </summary>
    void Take(Select select, Step step) => select.Limit = Smaller(select.Limit, Count(step));

    /// <summary>Skip(n): n rows more skipped, a negative n none, and an earlier Take's rows n fewer.</summary>
    void Skip(Select select, Step step)
    {
        string n = Count(step);
        select.Offset = select.Offset is null ? n : Sum(select.Offset, n);
        if (select.Limit is not null) select.Limit = Less(select.Limit, n);
    }

    /// <summary>The count of a Take or Skip, at least 0: a constant, or a parameter clamped in C# as Page's are.</summary>
    string Count(Step step)
    {
        var at = step.Arg(0).Syntax.GetLocation();
        return Eval(step.Arg(0), step.Env) switch
        {
            Constant { Constant_: { } c } => Names.Number(Math.Max(0L, System.Convert.ToInt64(c, CultureInfo.InvariantCulture))),
            Host h => Param(Clamped(h, ""), at),
            _ => throw new Refused(at, $"a count of {step.Name} that isn't known when the query is called"),
        };
    }

    static bool Number(string sql, out long n) => long.TryParse(sql, NumberStyles.None, CultureInfo.InvariantCulture, out n);

    string Smaller(string? a, string b) =>
        a is null ? b : Number(a, out long x) && Number(b, out long y) ? Names.Number(Math.Min(x, y)) : Dialect.Format(D.Least, a, b);

    string Sum(string a, string b) =>
        Number(a, out long x) && Number(b, out long y) ? Names.Number(x > long.MaxValue - y ? long.MaxValue : x + y) : $"{Sql.Group(a)} + {Sql.Group(b)}";

    string Less(string a, string b) =>
        Number(a, out long x) && Number(b, out long y) ? Names.Number(Math.Max(0L, x - y)) : Dialect.Format(D.Greatest, $"{Sql.Group(a)} - {Sql.Group(b)}", "0");
}
