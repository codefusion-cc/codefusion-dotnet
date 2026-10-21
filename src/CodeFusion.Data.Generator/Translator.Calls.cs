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
        if (Known.Same(m.ContainingType, K.Utf8Operators)) return TextFunction(m, inv, args, env);
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

    /// <summary>
    /// A text function of Utf8Operators: its SQL, by the dialect's template. Over values known when called it is also
    /// C#, the method itself, for where C# decides (a ThenIf's condition); in SQL it is still the function over the
    /// parameters, so the call never runs and nothing is allocated.
    /// </summary>
    Value TextFunction(IMethodSymbol m, IInvocationOperation inv, List<IOperation> args, Env env)
    {
        var at = inv.Syntax.GetLocation();
        var values = args.Select(a => Eval(a, env)).ToList();
        Location Arg(int i) => args[i].Syntax.GetLocation();
        string Literal(int i, string what) => values[i] is Constant { Constant_: string s } ? s
            : throw new Refused(Arg(i), $"{m.Name}'s {what} that isn't a constant string (it is checked when the query is compiled)");
        string template;
        // the operands' SQL, and the arguments written as SQL literals
        var operands = new List<int> { 0 };
        var literals = new List<string>();
        switch (m.Name)
        {
            case "ToLowerAscii":
                template = D.LowerAscii;
                break;
            case "ToUpperAscii":
                template = D.UpperAscii;
                break;
            case "Trim" or "TrimStart" or "TrimEnd":
            {
                template = m.Name == "Trim" ? D.Trim : m.Name == "TrimStart" ? D.TrimStart : D.TrimEnd;
                if (values[1] is not Constant { Constant_: char c })
                    throw new Refused(Arg(1), $"{m.Name}'s character that isn't a constant");
                if (char.IsSurrogate(c)) throw new Refused(Arg(1), $"{m.Name} by half a character (a surrogate)");
                literals.Add(Sql.String(c.ToString(), Arg(1)));
                break;
            }
            case "Replace":
                template = D.Replace;
                operands.AddRange([1, 2]);
                break;
            case "ReplaceMatches":
            {
                template = D.RegexReplace;
                string pattern = Literal(1, "pattern"), replacement = Literal(2, "replacement");
                if (Patterns.Problem(pattern) is { } problem)
                    throw new Refused(Arg(1), $"ReplaceMatches with {problem}, which .NET and SQL may read differently, so the pattern");
                if (Patterns.ReplacementProblem(replacement) is { } bad)
                    throw new Refused(Arg(2), $"ReplaceMatches with {bad}, which .NET and SQL read differently, so the replacement");
                literals.Add(Sql.String(pattern, Arg(1)));
                literals.Add(Sql.String(replacement, Arg(2)));
                break;
            }
            case "Concat":
                template = D.Concat;
                operands.Add(1);
                break;
            default:
                throw new Refused(at, $"Utf8Operators.{m.Name}");
        }
        if (template.Length == 0) throw new Refused(at, $"{m.Name} in {D.Name}");
        string Render()
        {
            var sql = operands.Select(i => Operand(values[i], Arg(i))).ToList();
            // a number as text by the dialect's own cast: + would add it where + is the concatenation
            if (m.Name == "Concat")
                return Dialect.Format(template, Chained(sql[0]), m.Parameters[1].Type.SpecialType == SpecialType.System_Int64 ? Dialect.Format(D.IntegerText, sql[1]) : Sql.Group(sql[1]));
            return Dialect.Format(template, sql.Concat(literals).ToArray());
        }
        if (values[0] is Host && operands.All(i => values[i] is Host or Constant))
        {
            // the method itself in C#: a nullable value's .Value as written (Property gives the value itself)
            var code = values.Select((v, i) => v is Host h && IsNullable(h.Type) && !IsNullable(m.Parameters[i].Type) ? $"({h.Code}).Value" : Code(v, Arg(i)));
            return Composite($"global::CodeFusion.Data.Utf8Operators.{m.Name}({string.Join(", ", code)})", m.ReturnType, true, Render, values.ToArray());
        }
        return new SqlValue(Render(), m.ReturnType, operands.Any(i => MayBeNull(values[i])));
    }

    /// <summary>The left side of a concatenation: as it is when it is one term or a chain of concatenations
    /// (<c>a || b || c</c> is read from the left), else in parentheses.</summary>
    string Chained(string sql)
    {
        if (Sql.Atomic(sql)) return sql;
        string op = Dialect.Format(D.Concat, "", "");
        int depth = 0;
        bool quoted = false;
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (c == '\'') quoted = !quoted;
            else if (quoted) continue;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && c == ' ')
            {
                if (string.CompareOrdinal(sql, i, op, 0, op.Length) != 0) return "(" + sql + ")";
                i += op.Length - 1;
            }
        }
        return sql;
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
            case "Any" or "Count" when _written is { } w && !mv.Nav.IsThrough && SameTable(mv.Target, w.Table):
                return Corrected(mv, name, inv, env, w);
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
        // the table the write before a Select changes is read only by the correction of a Count or Any of it
        if (!ReferenceEquals(mv, _correcting))
        {
            Unwritten(target, at);
            if (mv.Nav.IsThrough) Unwritten(mv.Nav.JoinTable!, null, at);
        }
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
            var inverse = Inverse(mv, at);
            sub.Where.Add($"{Column(child, inverse.ForeignKey, at)} = {parentKey}");
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

    /// <summary>The one-navigation of a many-navigation's rows back to its parent, whose foreign key correlates them.</summary>
    OneNav Inverse(ManyValue mv, Location at)
    {
        var parent = mv.Parent.Entity;
        var inverse = mv.Target.Ones.Values.Where(o => Known.Same(o.Target, parent.Type)
            && (mv.Nav.InverseNavigation is null || o.Property.Name == mv.Nav.InverseNavigation)).ToList();
        if (inverse.Count != 1)
            throw new Refused(at, Diagnostics.BadEntity, parent.Type.Name, inverse.Count == 0
                ? $"'{mv.Nav.Property.Name}': {mv.Target.Type.Name} has no navigation back to {parent.Type.Name} (or name its join table with [JoinTable])"
                : $"'{mv.Nav.Property.Name}': {mv.Target.Type.Name} has several navigations to {parent.Type.Name}; name one with [InverseProperty]");
        return inverse[0];
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
