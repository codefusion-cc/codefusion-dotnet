using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Operations;

namespace CodeFusion.Data.Generator;

/// <summary>Stops translating a query: the construct at <see cref="Where"/> can't be compiled.</summary>
// It never leaves the generator, which reports it as a diagnostic, so it needn't be public.
#pragma warning disable CA1064
sealed class Refused : Exception
#pragma warning restore CA1064
{
    /// <summary>"<paramref name="what"/> can't be compiled to SQL".</summary>
    public Refused(Location? where, string what) : this(where, Diagnostics.Unsupported, what) { }

    /// <summary>The error <paramref name="descriptor"/> with its message's <paramref name="args"/>.</summary>
    public Refused(Location? where, DiagnosticDescriptor descriptor, params object[] args) : base(string.Join(": ", args))
    {
        Where = where;
        Descriptor = descriptor;
        Args = args;
    }

    public Location? Where { get; }
    public DiagnosticDescriptor Descriptor { get; }
    public object[] Args { get; }

    /// <summary>Its errors are in the translator's list already.</summary>
    public bool Reported { get; init; }
}

// ---------- values ----------

/// <summary>What a C# expression of a query definition stands for.</summary>
abstract class Value(ITypeSymbol type)
{
    public ITypeSymbol Type { get; } = type;
}

/// <summary>
/// A C# value known when the query is called: a parameter, a member of one, or an expression over them. In SQL it
/// is a statement parameter, or, for a comparison or condition over parameters, <see cref="SqlForm"/>: the same
/// operator in SQL with the operands as parameters, as the hand-written statements have it (<c>$1 &lt;&gt; 0</c>).
/// </summary>
sealed class Host(string code, ITypeSymbol type, Func<string>? sqlForm = null) : Value(type)
{
    [ThreadStatic] static int t_made;

    public string Code { get; } = code;
    public Func<string>? SqlForm { get; } = sqlForm;

    /// <summary>
    /// Whether its C# reads the same value however often it runs, with nothing run on the way: a parameter, a
    /// constant, a readonly field or a property without a setter or a body, and operators over those. Such a value
    /// written twice is one value; any other (a call, a settable member, a user-defined operator) is a value per
    /// place it is written, as C# runs it.
    /// </summary>
    public bool Pure { get; init; }

    /// <summary>Where an impure value is written: the expression, within the inlined calls it was reached through.
    /// Its copies (an implicit conversion) keep it, being the same value.</summary>
    public string? Site { get; set; }

    /// <summary>The values its C# holds, which run when it runs.</summary>
    public IReadOnlyList<Host> Parts { get; init; } = [];

    /// <summary>The order the values were made in, which is the order C# runs them.</summary>
    public int Made { get; init; } = ++t_made;

    /// <summary>Where an impure value is written, for an error about it.</summary>
    public Location? Where { get; set; }

    /// <summary>The value it is: one key, one local computed once.</summary>
    public string Key => Pure ? "=" + Code : "@" + (Site ??= "#" + Made);
}

/// <summary>A constant, written into the SQL.</summary>
sealed class Constant(object? value, ITypeSymbol type) : Value(type)
{
    public object? Constant_ { get; } = value;
}

/// <summary>A SQL expression; <see cref="Nullable"/> when it can be NULL.</summary>
sealed class SqlValue(string sql, ITypeSymbol type, bool nullable, bool nullIsFalse = false) : Value(type)
{
    public string Sql { get; } = sql;
    public bool Nullable { get; } = nullable;

    /// <summary>
    /// A condition that SQL makes NULL where C# says false (a comparison with a NULL). In a WHERE, a join or a CASE
    /// a NULL is as false, so it stays as written, which an index serves; under NOT or as a value it is coalesced.
    /// </summary>
    public bool NullIsFalse { get; } = nullIsFalse;
}

/// <summary>A row of an entity.</summary>
sealed class RowValue(Row row) : Value(row.Entity.Type)
{
    public Row Row { get; } = row;
}

/// <summary>A view or entity being made: its members' values.</summary>
sealed class ObjectValue(INamedTypeSymbol type, List<(string Name, Value Value)> members) : Value(type)
{
    public List<(string Name, Value Value)> Members { get; } = members;

    public Value? Get(string name)
    {
        for (int i = Members.Count - 1; i >= 0; i--)
            if (Members[i].Name == name) return Members[i].Value;
        return null;
    }
}

/// <summary>A many-navigation and the operators applied to it so far.</summary>
sealed class ManyValue(ITypeSymbol type, Row parent, ManyNav nav, EntityInfo target, List<Step> steps) : Value(type)
{
    public Row Parent { get; } = parent;
    public ManyNav Nav { get; } = nav;
    public EntityInfo Target { get; } = target;
    public List<Step> Steps { get; } = steps;
}

/// <summary>A table of the context.</summary>
sealed class TableValue(ITypeSymbol type, EntityInfo entity) : Value(type)
{
    public EntityInfo Entity { get; } = entity;
}

/// <summary><c>Table.Single(predicate)</c>: a row looked up by a scalar subquery each time one of its columns is used.</summary>
sealed class SingleValue(EntityInfo entity, IAnonymousFunctionOperation predicate, Env env) : Value(entity.Type)
{
    public EntityInfo Entity { get; } = entity;
    public IAnonymousFunctionOperation Predicate { get; } = predicate;
    public Env Env { get; } = env;
}

/// <summary>The context itself (<c>this</c>).</summary>
sealed class ContextValue(ITypeSymbol type) : Value(type);

/// <summary>An operator of a chain, with its arguments and where they are evaluated.</summary>
sealed record Step(string Name, IInvocationOperation Call, Env Env)
{
    public IOperation Arg(int i) => Translator.Unwrap(Call.Arguments.Where(a => a.Parameter is not null).OrderBy(a => a.Parameter!.Ordinal).ElementAt(i).Value);
    public int ArgCount => Call.Arguments.Count(a => a.Parameter is not null && a.ArgumentKind != ArgumentKind.DefaultValue);
    public IAnonymousFunctionOperation Lambda(int i) => Arg(i) as IAnonymousFunctionOperation ?? throw new Refused(Arg(i).Syntax.GetLocation(), "an operator's argument that isn't a lambda written in place");
}

/// <summary>The values of lambda parameters and inlined methods' parameters.</summary>
sealed class Env
{
    readonly Dictionary<ISymbol, Value> _values;
    public readonly Env? Parent;

    public Env(Env? parent = null)
    {
        Parent = parent;
        _values = new Dictionary<ISymbol, Value>(SymbolEqualityComparer.Default);
    }

    public Env With(ISymbol symbol, Value value)
    {
        var e = new Env(this);
        e._values[symbol] = value;
        return e;
    }

    public Value? Find(ISymbol symbol)
    {
        for (var e = this; e is not null; e = e.Parent)
            if (e._values.TryGetValue(symbol, out var v)) return v;
        return null;
    }
}

/// <summary>How a row's columns are reached.</summary>
enum RowMode
{
    /// <summary>A table in a FROM: columns as <c>alias.column</c>, navigations joined into its select.</summary>
    Table,
    /// <summary>A target reached through a join table: its key is the join table's column, anything else a scalar subquery.</summary>
    Through,
    /// <summary>A CTE (a write's rows): columns used are collected for the CTE's select list or RETURNING.</summary>
    Cte,
    /// <summary>The row an earlier insert of the unit wrote: only its key, as the dialect's last inserted key.</summary>
    Inserted,
    /// <summary>The row an insert is writing, whose key is drawn first: only its key.</summary>
    NewKey,
    /// <summary>A page's source row: only its key and the navigation the key is the foreign key of.</summary>
    PageSource,
}

/// <summary>An entity row and where its joins go.</summary>
sealed class Row(EntityInfo entity, string? alias, Select? owner, RowMode mode = RowMode.Table)
{
    public EntityInfo Entity { get; } = entity;
    public string? Alias { get; } = alias;
    public Select? Owner { get; } = owner;
    public RowMode Mode { get; } = mode;
    public readonly Dictionary<string, Row> Joins = new();
    /// <summary>Through: the join table's alias and its column holding this row's key.</summary>
    public string? JoinAlias, JoinColumn;
    /// <summary>Cte: the columns used, in order of first use.</summary>
    public readonly List<string> Needed = new();
    /// <summary>Table and Cte without an alias (the target of an UPDATE or DELETE, a CTE's row): the name a correlated
    /// subquery qualifies its columns with, since a bare column there is the subquery's own table's.</summary>
    public string? Name;
    /// <summary>NewKey: the SQL of the key drawn for the row, and whether a value used it.</summary>
    public string? KeySql;
    public bool KeyUsed;
    /// <summary>Inserted: the key the insert set itself, a value known when called; else why the row has no key a
    /// later statement can use, or whether a later insert into its table moved the sequence it was drawn from.</summary>
    public Value? KeyValue;
    public string? NoKey;
    public bool Moved;
    /// <summary>A row an inner join reached: its key is the foreign key it was joined on, which the hand-written
    /// statements correlate on (so Postgres evaluates subqueries at the same plan node).</summary>
    public string? KeyViaForeignKey;
    /// <summary>Reached through a left join (here or before): every column may be NULL.</summary>
    public bool Optional;
    /// <summary>Through: the scalar subquery written for each column, so a column used twice is the same SQL.</summary>
    public readonly Dictionary<string, string> ColumnSql = new();
    /// <summary>PageSource: the key column, the row it identifies, and the navigation to it (null when the key is the row's own).</summary>
    public ColumnInfo? PageKey;
    public Row? PageTarget;
    public string? PageNav;

    public void Need(string column)
    {
        if (!Needed.Contains(column)) Needed.Add(column);
    }
}

/// <summary>
/// Translates the C# of a query definition (its IOperation tree) into SQL values. One instance per query method;
/// <see cref="Params"/> and <see cref="Aliases"/> are per statement and set by the statement builder.
/// </summary>
sealed partial class Translator
{
    public readonly Known K;
    public readonly Dialect D;
    readonly Compilation _compilation;
    readonly Dictionary<ITypeSymbol, string> _tables;
    readonly Dictionary<ITypeSymbol, EntityInfo> _entities = new(SymbolEqualityComparer.Default);
    readonly Dictionary<ITypeSymbol, ViewInfo?> _views = new(SymbolEqualityComparer.Default);
    readonly HashSet<ISymbol> _queryParameters = new(SymbolEqualityComparer.Default);
    public readonly List<Diagnostic> Errors = new();
    public ParamList Params = null!;
    public Aliases Aliases = null!;
    int _depth;

    public Translator(Known k, Dialect d, Dictionary<ITypeSymbol, string> tables)
    {
        K = k;
        D = d;
        _compilation = k.Compilation;
        _tables = tables;
    }

    public IMethodSymbol Method = null!;
    public ITypeSymbol ContextType = null!;

    public void StartMethod(IMethodSymbol method)
    {
        Method = method;
        _queryParameters.Clear();
        foreach (var p in method.Parameters) _queryParameters.Add(p);
    }

    public void StartStatement()
    {
        Params = new ParamList(D);
        Aliases = new Aliases();
    }

    // ---------- the model ----------

    public EntityInfo Entity(ITypeSymbol type, Location? at)
    {
        if (_entities.TryGetValue(type, out var e)) return e;
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class } named)
            throw new Refused(at, $"'{type.ToDisplayString()}' as an entity (entities are classes)");
        var attribute = Known.Attribute(named, K.TableAttribute);
        string table = attribute?.ConstructorArguments.FirstOrDefault().Value as string
            ?? (_tables.TryGetValue(named, out var t) ? t : Names.Snake(named.Name) + "s");
        string? schema = attribute?.NamedArguments.FirstOrDefault(a => a.Key == "Schema").Value.Value as string;
        var problems = new List<string>();
        e = EntityInfo.Build(K, named, table, string.IsNullOrEmpty(schema) ? null : schema, problems);
        if (problems.Count > 0) throw new Refused(at ?? named.Locations.FirstOrDefault(), Diagnostics.BadEntity, named.Name, string.Join("; ", problems));
        _entities[type] = e;
        return e;
    }

    /// <summary>An entity's table as SQL: quoted where it needs it, and qualified by its schema.</summary>
    public string TableSql(EntityInfo e) => e.Schema is null ? D.Quote(e.Table) : D.Quote(e.Schema) + "." + D.Quote(e.Table);

    /// <summary>
    /// A template of the table's key sequence (<see cref="Dialect.LastInsertedKey"/>, <see cref="Dialect.NextKey"/>),
    /// whose arguments are in a string literal: the table as SQL, which Postgres parses (case folded unless quoted),
    /// and the column as it is.
    /// </summary>
    public string Sequence(string template, EntityInfo e, ColumnInfo key) =>
        Dialect.Format(template, TableSql(e).Replace("'", "''"), key.Name.Replace("'", "''"));

    public ViewInfo View(ITypeSymbol type, Location? at)
    {
        if (!_views.TryGetValue(type, out var v))
        {
            var errors = new List<Diagnostic>();
            v = type is INamedTypeSymbol n ? ViewInfo.Of(K, n, errors, at ?? Location.None) : null;
            Errors.AddRange(errors);
            _views[type] = v;
        }
        return v ?? throw new Refused(at, $"'{type.ToDisplayString()}' as a view") { Reported = true };
    }

    public bool IsView(ITypeSymbol t) => Kinds.Of(K, t) is { IsScalar: false };

    // ---------- evaluating C# ----------

    public static IOperation Unwrap(IOperation op)
    {
        while (true)
        {
            switch (op)
            {
                case IDelegateCreationOperation d: op = d.Target; continue;
                case IParenthesizedOperation p: op = p.Operand; continue;
                case IArgumentOperation a: op = a.Value; continue;
                default: return op;
            }
        }
    }

    /// <summary>What <paramref name="op"/> stands for, with <paramref name="env"/>'s parameters.</summary>
    public Value Eval(IOperation op, Env env)
    {
        if (++_depth > 200) throw new Refused(op.Syntax.GetLocation(), "an expression nested this deep");
        try
        {
            var v = EvalCore(op, env);
            // an impure value is the one written here, reached through these inlined calls: evaluating the same
            // expression again (a lambda applied twice) is the same value, another written alike is another
            if (v is Host { Pure: false, Site: null } h)
            {
                h.Site = string.Concat(_callSites) + SiteOf(op.Syntax);
                h.Where = op.Syntax.GetLocation();
            }
            return v;
        }
        finally
        {
            _depth--;
        }
    }

    Value EvalCore(IOperation op, Env env)
    {
        op = Unwrap(op);
        var at = op.Syntax.GetLocation();
        if (op.ConstantValue.HasValue && op is not IInterpolatedStringOperation)
            return new Constant(op.ConstantValue.Value, op.Type ?? _compilation.ObjectType);
        switch (op)
        {
            case IParameterReferenceOperation p:
                if (env.Find(p.Parameter) is { } bound) return bound;
                if (_queryParameters.Contains(p.Parameter)) return new Host(Names.Identifier(p.Parameter.Name), p.Parameter.Type) { Pure = p.Parameter.RefKind == RefKind.None };
                throw new Refused(at, $"the parameter '{p.Parameter.Name}' here");
            case ILocalReferenceOperation l:
                throw new Refused(at, $"the local '{l.Local.Name}' (a definition can use only the query method's parameters)");
            case IInstanceReferenceOperation:
                return new ContextValue(op.Type!);
            case IUtf8StringOperation u8:
                return new SqlValue(Sql.String(u8.Value), K.Utf8, false);
            case IDefaultValueOperation d:
                return new Constant(null, d.Type ?? _compilation.ObjectType);
            case IConversionOperation c:
                return Convert(c, env);
            case IPropertyReferenceOperation pr:
                return Property(pr, env);
            case IFieldReferenceOperation f:
                return Field(f, env);
            case IBinaryOperation b:
                return Binary(b, env);
            case IUnaryOperation u:
                return Unary(u, env);
            case ICoalesceOperation co:
                return Coalesce(co, env);
            case IConditionalOperation cond when cond.WhenFalse is not null:
                return Conditional(cond, env);
            case IIsPatternOperation pat:
                return IsPattern(pat, env);
            case IObjectCreationOperation oc:
                return Create(oc, env);
            case IWithOperation w:
                return With(w, env);
            case IInvocationOperation inv:
                return Invoke(inv, env);
            default:
                throw new Refused(at, Describe(op));
        }
    }

    static string Describe(IOperation op) => op.Kind switch
    {
        OperationKind.AnonymousFunction => "A lambda here",
        _ => $"'{Short(op.Syntax.ToString())}' ({op.Kind})",
    };

    static string Short(string s) => s.Length <= 60 ? s : s.Substring(0, 57) + "...";

    Value Convert(IConversionOperation c, Env env)
    {
        var v = Eval(c.Operand, env);
        var to = c.Type!;
        // user-defined conversions (Utf8 to a span, Insert to Write) keep the value; numeric ones change its type
        switch (v)
        {
            case Host h:
                if (c.Conversion.IsUserDefined || c.Conversion.IsIdentity) return h;
                // an implicit one C# makes again where the binder passes the value on; any other is written out, in
                // the checked or unchecked context it was written in (an enum to its number, a long cut to an int)
                if (!c.Conversion.IsImplicit)
                    return Composite($"{(c.IsChecked ? "checked" : "unchecked")}(({to.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}){h.Code})", to, true, null, h);
                return new Host(h.Code, c.Conversion.IsNullable ? h.Type : to) { Pure = h.Pure, Site = h.Site, Parts = h.Parts, Made = h.Made };
            case SqlValue s:
                // lifting to T? doesn't make the value NULL: only what it was says so
                return new SqlValue(c.Conversion.IsNumeric || c.Conversion.IsNullable ? NumericCast(s.Sql, c.Operand.Type ?? s.Type, to) : s.Sql, c.Conversion.IsUserDefined ? s.Type : to, s.Nullable, s.NullIsFalse);
            case Constant k:
                return new Constant(k.Constant_, to);
            default:
                return v;
        }
    }

    /// <summary>
    /// A numeric conversion of SQL: between integers and floating point it changes the value's SQL type as it does
    /// C#'s (an int divided as a double, a double cut toward zero as C# casts it, where SQL would round); between two
    /// integers or two floating-point types the value is the same, and a reader takes either width.
    /// </summary>
    string NumericCast(string sql, ITypeSymbol from, ITypeSymbol to)
    {
        var (f, t) = (Underlying(from).SpecialType, Underlying(to).SpecialType);
        bool fromFloat = f is SpecialType.System_Double or SpecialType.System_Single, toFloat = t is SpecialType.System_Double or SpecialType.System_Single;
        if (fromFloat == toFloat || SqlNumber(f) is null || SqlNumber(t) is not { } type) return sql;
        return Dialect.Format(toFloat ? D.ToFloat : D.ToInteger, sql, type);
    }

    static ITypeSymbol Underlying(ITypeSymbol t) => t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n ? n.TypeArguments[0] : t;

    static string? SqlNumber(SpecialType t) => t switch
    {
        SpecialType.System_Double => "double precision",
        SpecialType.System_Single => "real",
        SpecialType.System_Int64 or SpecialType.System_UInt32 => "bigint",
        SpecialType.System_Int32 or SpecialType.System_UInt16 => "integer",
        SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_SByte => "smallint",
        _ => null,
    };

    /// <summary>The inlined calls (a query method's, or a source method's one-expression body) the translation is in.</summary>
    readonly List<string> _callSites = new();

    static string SiteOf(SyntaxNode s) => $"{s.SyntaxTree.FilePath}@{s.Span.Start}+{s.Span.Length};";

    /// <summary>Translates <paramref name="body"/> as reached through the call at <paramref name="call"/>.</summary>
    public T Through<T>(SyntaxNode call, Func<T> body)
    {
        _callSites.Add(SiteOf(call));
        try
        {
            return body();
        }
        finally
        {
            _callSites.RemoveAt(_callSites.Count - 1);
        }
    }

    /// <summary>C# over <paramref name="parts"/>: pure when it adds nothing that runs (<paramref name="pure"/>) and its
    /// parts are pure.</summary>
    static Host Composite(string code, ITypeSymbol type, bool pure, Func<string>? sqlForm, params Value[] parts)
    {
        var hosts = parts.OfType<Host>().ToArray();
        return new Host(code, type, sqlForm) { Pure = pure && hosts.All(h => h.Pure), Parts = hosts };
    }

    /// <summary>A count or a Paging's <paramref name="member"/> (".Limit") at least 0, as a long: a page or a Take of a
    /// negative count is empty, not Postgres's error.</summary>
    Host Clamped(Host h, string member)
    {
        var c = Composite($"global::System.Math.Max(0L, {h.Code}{member})", K.Compilation.GetSpecialType(SpecialType.System_Int64), true, null, h);
        if (!c.Pure) c.Site = h.Site + "max" + member + ";";
        return c;
    }

    /// <summary>
    /// Whether reading <paramref name="p"/> runs nothing and reads the same each time: a property without a setter
    /// (or with init) and without a body (an auto-property, a record's positional one), or Nullable's Value and
    /// HasValue, a string's or an array's Length. A property of another assembly otherwise can't be seen to be one.
    /// </summary>
    static bool PureProperty(IPropertySymbol p)
    {
        if (p.SetMethod is { IsInitOnly: false }) return false;
        var type = p.ContainingType.OriginalDefinition;
        if (type.SpecialType == SpecialType.System_Nullable_T) return p.Name is "Value" or "HasValue";
        if (type.SpecialType is SpecialType.System_String or SpecialType.System_Array) return p.Name == "Length";
        var decl = p.OriginalDefinition.DeclaringSyntaxReferences;
        if (decl.Length == 0) return false;
        return decl.All(r => r.GetSyntax() switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.ParameterSyntax => true,
            Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax { ExpressionBody: null, AccessorList: { } list } =>
                list.Accessors.All(a => a.Body is null && a.ExpressionBody is null),
            _ => false,
        });
    }

    public static bool IsNullable(ITypeSymbol t) => t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }
        || (t.IsReferenceType && t.NullableAnnotation == NullableAnnotation.Annotated);

    Value Field(IFieldReferenceOperation f, Env env)
    {
        var at = f.Syntax.GetLocation();
        if (f.Field.IsStatic) return new Host($"{Reachable(f.Field, at).ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{Names.Identifier(f.Field.Name)}", f.Field.Type) { Pure = f.Field.IsReadOnly || f.Field.IsConst };
        var inst = Eval(f.Instance!, env);
        if (inst is Host h) return Composite($"{h.Code}.{Names.Identifier(Reachable(f.Field, at).Name)}", f.Field.Type, f.Field.IsReadOnly, null, h);
        throw new Refused(at, $"the field '{f.Field.Name}' (entities and views use properties)");
    }

    Value Property(IPropertyReferenceOperation pr, Env env)
    {
        var at = pr.Syntax.GetLocation();
        var prop = pr.Property;
        if (prop.IsStatic)
            return new Host($"{Reachable(prop, at).ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{Names.Identifier(prop.Name)}", prop.Type) { Pure = PureProperty(prop) };
        var inst = Eval(pr.Instance!, env);
        switch (inst)
        {
            case Host h when prop.Name == "Value" && h.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }:
                // the same value in SQL, NULL included: binding .Value would throw for null
                return h;
            case Host h:
                return Composite($"{h.Code}.{Names.Identifier(Reachable(prop, at).Name)}", prop.Type, PureProperty(prop), null, h);
            case ContextValue when Known.Is(prop.Type, K.Table):
                return new TableValue(prop.Type, Entity(((INamedTypeSymbol)prop.Type).TypeArguments[0], at));
            case RowValue r:
                return Member(r.Row, prop, at);
            case SingleValue s:
                return SingleColumn(s, prop, at);
            case ObjectValue o:
                return o.Get(prop.Name) ?? throw new Refused(at, $"the member '{prop.Name}', which this object doesn't set");
            case SqlValue sv when prop.Name == "Value" && IsNullable(sv.Type):
                return new SqlValue(sv.Sql, prop.Type, false);
            case SqlValue sv when prop.Name == "HasValue" && IsNullable(sv.Type):
                return new SqlValue(Sql.Group(sv.Sql) + " IS NOT NULL", prop.Type, false);
            case SqlValue sv when prop.Name == "IsEmpty" && Known.Same(sv.Type, K.Utf8):
                return new SqlValue(Sql.Group(sv.Sql) + " = ''", prop.Type, sv.Nullable);
            default:
                throw new Refused(at, $"'{Short(pr.Syntax.ToString())}'");
        }
    }

    /// <summary>A property of an entity row: a column, a one-navigation, or a many-navigation.</summary>
    public Value Member(Row row, IPropertySymbol prop, Location at)
    {
        var e = row.Entity;
        if (e.Columns.TryGetValue(prop.Name, out var col)) return new SqlValue(Column(row, col, at), prop.Type, col.Kind.Nullable || IsNullable(prop.Type) || row.Optional);
        if (e.Ones.TryGetValue(prop.Name, out var one)) return new RowValue(Navigate(row, one, at));
        if (e.Manys.TryGetValue(prop.Name, out var many)) return new ManyValue(prop.Type, row, many, Entity(many.Target, at), new List<Step>());
        throw new Refused(at, $"'{e.Type.Name}.{prop.Name}', which isn't mapped");
    }

    /// <summary>A column of a row, as SQL.</summary>
    public string Column(Row row, ColumnInfo col, Location? at)
    {
        switch (row.Mode)
        {
            case RowMode.Table:
                if (row.KeyViaForeignKey is not null && Known.Same(col.Property, row.Entity.Key?.Property)) return row.KeyViaForeignKey;
                return Qualified(row, col);
            case RowMode.Cte:
                row.Need(col.Name);
                return Qualified(row, col);
            case RowMode.Through:
                if (Known.Same(col.Property, row.Entity.Key?.Property)) return row.JoinAlias + "." + D.Quote(row.JoinColumn!);
                if (row.ColumnSql.TryGetValue(col.Name, out var written)) return written;
                // a lookup by the target's primary key per row, not a join over the whole target table
                string t = Aliases.ForTable(row.Entity.Table);
                return row.ColumnSql[col.Name] =
                    $"(SELECT {t}.{D.Quote(col.Name)} FROM {TableSql(row.Entity)} {t} WHERE {t}.{D.Quote(KeyOf(row.Entity, at).Name)} = {row.JoinAlias}.{D.Quote(row.JoinColumn!)})";
            case RowMode.Inserted:
                if (!Known.Same(col.Property, row.Entity.Key?.Property))
                    throw new Refused(at, $"'{col.Property.Name}' of the inserted row: a later statement can use only its key");
                // the key the insert set, or the one it drew from the table's sequence while that is still its current value
                if (row.KeyValue is Host h) return Param(h, at);
                if (row.KeyValue is { } set) return Operand(set, at);
                if (row.NoKey is { } why) throw new Refused(at, $"the key of {why}: set the key in the insert, or write the unit with Sql");
                if (row.Moved)
                    throw new Refused(at, $"the key of an insert into {row.Entity.Table} after another insert into {row.Entity.Table} (it is the sequence's current value, " +
                        "which the second insert moved): use it before the second insert, or write the unit with Sql");
                return Sequence(D.LastInsertedKey, row.Entity, col);
            case RowMode.NewKey:
                if (Known.Same(col.Property, row.Entity.Key?.Property))
                {
                    row.KeyUsed = true;
                    return row.KeySql!;
                }
                throw new Refused(at, $"'{col.Property.Name}' of the row being inserted: its values can use only its new key");
            case RowMode.PageSource:
                if (Known.Same(col.Property, row.PageKey!.Property))
                    return Column(row.PageTarget!, KeyOf(row.PageTarget!.Entity, at), at);
                throw new Refused(at, $"'{col.Property.Name}' in a page's Select: it can use only the row the page's key identifies");
            default:
                throw new InvalidOperationException();
        }
    }

    /// <summary>Subqueries being written: inside one, a row without an alias is written by its name.</summary>
    int _nested;

    /// <summary>A column of a Table or Cte row: <c>alias.column</c>, or bare for a row without an alias, but inside a
    /// subquery, where a bare name is the subquery's own table's first, by the row's name.</summary>
    string Qualified(Row row, ColumnInfo col)
    {
        if (row.Alias is not null) return row.Alias + "." + D.Quote(col.Name);
        return _nested > 0 && row.Name is { } name ? name + "." + D.Quote(col.Name) : D.Quote(col.Name);
    }

    /// <summary>Runs <paramref name="write"/>, which writes a subquery: the outer rows' columns it uses are qualified.</summary>
    T Nested<T>(Func<T> write)
    {
        _nested++;
        try
        {
            return write();
        }
        finally
        {
            _nested--;
        }
    }

    /// <summary>
    /// <paramref name="s"/>, which generated code evaluates where the query is called: it must be reachable from
    /// another class of the assembly, so not private or protected (a private one compiles in the definition, but not
    /// in the interceptor).
    /// </summary>
    public static T Reachable<T>(T s, Location? at) where T : ISymbol
    {
        for (ISymbol? x = s; x is not null and not INamespaceSymbol; x = x.ContainingType)
            if (x.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
                throw new Refused(at, $"'{s.Name}', which is {x.DeclaredAccessibility.ToString().ToLowerInvariant()} (the generated code that evaluates it when the query is called can't reach it: make it internal)");
        return s;
    }

    public ColumnInfo KeyOf(EntityInfo e, Location? at) =>
        e.Key ?? throw new Refused(at, Diagnostics.BadEntity, e.Type.Name, "it has no key (an Id property or [Key]) for this query");

    /// <summary>The row a one-navigation leads to, joined into the row's select once.</summary>
    Row Navigate(Row row, OneNav nav, Location at)
    {
        if (row.Joins.TryGetValue(nav.Property.Name, out var joined)) return joined;
        if (row.Mode == RowMode.PageSource)
        {
            if (row.PageNav == nav.Property.Name) return row.PageTarget!;
            throw new Refused(at, $"'{nav.Property.Name}' in a page's Select: it can use only the row the page's key identifies");
        }
        if (row.Mode is RowMode.Inserted or RowMode.NewKey or RowMode.Through)
            throw new Refused(at, $"the navigation '{nav.Property.Name}' from this row");
        var target = Entity(nav.Target, at);
        var owner = row.Owner ?? throw new Refused(at, $"the navigation '{nav.Property.Name}' here (it would need a join)");
        string fk = Column(row, nav.ForeignKey, at);
        string alias = Aliases.ForTable(target.Table);
        // a nullable key, or a row a left join reached, may have no target: the join keeps the row either way
        bool optional = nav.ForeignKey.Kind.Nullable || row.Optional;
        owner.Joins.Add($"{(optional ? "LEFT JOIN" : "JOIN")} {TableSql(target)} {alias} ON {alias}.{D.Quote(KeyOf(target, at).Name)} = {fk}");
        // equal under an inner join; a left join's NULL row would have a NULL key where the foreign key isn't
        var r = new Row(target, alias, owner) { KeyViaForeignKey = optional ? null : fk, Optional = optional };
        row.Joins[nav.Property.Name] = r;
        return r;
    }

    /// <summary>A column of <c>Table.Single(predicate)</c>: its own scalar subquery.</summary>
    Value SingleColumn(SingleValue s, IPropertySymbol prop, Location at)
    {
        if (!s.Entity.Columns.TryGetValue(prop.Name, out var col)) throw new Refused(at, $"'{prop.Name}' of Single(...): use one of its columns");
        return Nested(() =>
        {
            var sub = new Select();
            var row = FromTable(sub, s.Entity, s.Predicate.Symbol.Parameters[0].Name);
            sub.Where.Add(Condition(Apply(s.Predicate, s.Env, new RowValue(row)), s.Predicate.Syntax.GetLocation()));
            sub.Columns.Add((Column(row, col, at), null));
            // C#'s Single has a value (it would throw without a row), so the comparison is a plain one, which an index serves
            return new SqlValue("(" + sub.Render(D) + ")", prop.Type, col.Kind.Nullable);
        });
    }

    /// <summary>A table in <paramref name="select"/>'s FROM, aliased after <paramref name="name"/>.</summary>
    public Row FromTable(Select select, EntityInfo e, string? name)
    {
        string alias = name is null ? Aliases.ForTable(e.Table) : Aliases.Take(name);
        select.From = TableSql(e) + " " + alias;
        return new Row(e, alias, select);
    }

    /// <summary>Calls a lambda with <paramref name="args"/> for its parameters: the value of its body.</summary>
    public Value Apply(IAnonymousFunctionOperation f, Env env, params Value[] args)
    {
        var e = env;
        for (int i = 0; i < f.Symbol.Parameters.Length && i < args.Length; i++) e = e.With(f.Symbol.Parameters[i], args[i]);
        return Eval(Body(f.Body, f.Syntax.GetLocation()), e);
    }

    /// <summary>The one returned expression of a lambda's or method's body.</summary>
    public static IOperation Body(IBlockOperation block, Location at)
    {
        var ops = block.Operations.Where(o => o is not ILabeledOperation).ToList();
        if (ops.Count == 1 && ops[0] is IReturnOperation { ReturnedValue: { } r }) return r;
        if (ops.Count == 1 && ops[0] is IExpressionStatementOperation es) return es.Operation;
        throw new Refused(at, "A body with statements");
    }

    // ---------- operators ----------

    Value Binary(IBinaryOperation b, Env env)
    {
        var at = b.Syntax.GetLocation();
        var l = Eval(b.LeftOperand, env);
        var r = Eval(b.RightOperand, env);
        var kind = b.OperatorKind;
        bool arithmetic = kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply or BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder;
        if (l is Host or Constant && r is Host or Constant && (l is Host || r is Host))
        {
            // over values known when called: C# when C# decides (ThenIf), and in SQL arithmetic is C#'s while a
            // comparison or condition stays SQL over the parameters, as the hand-written statements have it
            string code = $"({Code(l, at)} {CsToken(kind)} {Code(r, at)})";
            return Composite(code, b.Type!, b.OperatorMethod is null, arithmetic ? null : () => SqlBinary(b, l, r, at).Sql, l, r);
        }
        return SqlBinary(b, l, r, at);
    }

    SqlValue SqlBinary(IBinaryOperation b, Value l, Value r, Location at)
    {
        var kind = b.OperatorKind;
        var type = b.Type!;
        bool arithmetic = kind is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply or BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder;
        switch (kind)
        {
            case BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.And when IsBool(b.LeftOperand.Type):
                return Logic(Condition(l, at), Condition(r, at), " AND ", type, NullIsFalse(l) || NullIsFalse(r));
            case BinaryOperatorKind.ConditionalOr or BinaryOperatorKind.Or when IsBool(b.LeftOperand.Type):
                return Logic(Condition(l, at), Condition(r, at), " OR ", type, NullIsFalse(l) || NullIsFalse(r));
            case BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals:
                return Equality(l, r, kind == BinaryOperatorKind.Equals, type, at);
            case BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual:
            {
                // C#'s lifted comparisons are false with a null; SQL's are NULL, which is false where it is a condition
                return new SqlValue($"{Sql.Group(Operand(l, at))} {Token(kind)} {Sql.Group(Operand(r, at))}", type, false, MayBeNull(l) || MayBeNull(r));
            }
            case BinaryOperatorKind.Add when IsText(l.Type) || IsText(r.Type):
                return new SqlValue(Dialect.Format(D.Concat, Operand(l, at), Operand(r, at)), type, MayBeNull(l) || MayBeNull(r));
            default:
                if (arithmetic)
                    return new SqlValue($"{Sql.Group(Operand(l, at))} {Token(kind)} {Sql.Group(Operand(r, at))}", type, MayBeNull(l) || MayBeNull(r));
                throw new Refused(at, $"the operator '{Token(kind)}'");
        }
    }

    static string CsToken(BinaryOperatorKind k) => k switch
    {
        BinaryOperatorKind.ConditionalAnd => "&&",
        BinaryOperatorKind.ConditionalOr => "||",
        BinaryOperatorKind.And => "&",
        BinaryOperatorKind.Or => "|",
        BinaryOperatorKind.ExclusiveOr => "^",
        BinaryOperatorKind.Equals => "==",
        BinaryOperatorKind.NotEquals => "!=",
        _ => Token(k),
    };

    bool IsText(ITypeSymbol? t) => t is not null && (Known.Same(t, K.Utf8) || t.SpecialType == SpecialType.System_String);

    static bool IsBool(ITypeSymbol? t) => t?.SpecialType == SpecialType.System_Boolean;

    static string Token(BinaryOperatorKind k) => k switch
    {
        BinaryOperatorKind.Add => "+",
        BinaryOperatorKind.Subtract => "-",
        BinaryOperatorKind.Multiply => "*",
        BinaryOperatorKind.Divide => "/",
        BinaryOperatorKind.Remainder => "%",
        BinaryOperatorKind.LessThan => "<",
        BinaryOperatorKind.LessThanOrEqual => "<=",
        BinaryOperatorKind.GreaterThan => ">",
        BinaryOperatorKind.GreaterThanOrEqual => ">=",
        BinaryOperatorKind.Equals => "=",
        BinaryOperatorKind.NotEquals => "<>",
        _ => k.ToString(),
    };

    SqlValue Logic(string l, string r, string op, ITypeSymbol type, bool nullIsFalse)
    {
        // a AND b AND c stays flat; a mix gets parentheses
        string other = op == " AND " ? " OR " : " AND ";
        return new SqlValue(Sql.Wrap(l, other) + op + Sql.Wrap(r, other), type, false, nullIsFalse);
    }

    static bool NullIsFalse(Value v) => v is SqlValue { NullIsFalse: true };

    SqlValue Equality(Value l, Value r, bool equal, ITypeSymbol type, Location at)
    {
        bool lNull = l is Constant { Constant_: null }, rNull = r is Constant { Constant_: null };
        if (lNull && rNull) return new SqlValue(equal ? D.True : D.False, type, false);
        if (lNull || rNull)
        {
            var v = lNull ? r : l;
            if (v is Host h) return new SqlValue($"{Param(h, at)} IS {(equal ? "" : "NOT ")}NULL", type, false);
            return new SqlValue($"{Sql.Group(Operand(v, at))} IS {(equal ? "" : "NOT ")}NULL", type, false);
        }
        // an operand that is itself a comparison or a condition needs its parentheses: SQL's comparisons don't chain
        string a = Sql.Group(Operand(l, at)), b = Sql.Group(Operand(r, at));
        bool ln = MayBeNull(l), rn = MayBeNull(r);
        // C#'s == holds for two nulls and is false against one, where SQL's = is NULL: against one that can't be
        // NULL, = is false there as a condition (and an index serves it); two that can need the null-safe form
        if (equal && ln != rn) return new SqlValue($"{a} = {b}", type, false, nullIsFalse: true);
        if (ln || rn)
        {
            string same = Dialect.Format(D.NullSafeEqual, a, b);
            return new SqlValue(equal ? same : "NOT (" + same + ")", type, false);
        }
        return new SqlValue($"{a} {(equal ? "=" : "<>")} {b}", type, false);
    }

    bool MayBeNull(Value v) => v switch
    {
        SqlValue s => s.Nullable,
        Host h => IsNullable(h.Type),
        Constant { Constant_: null } => true,
        _ => false,
    };

    Value Unary(IUnaryOperation u, Env env)
    {
        var at = u.Syntax.GetLocation();
        var v = Eval(u.Operand, env);
        switch (u.OperatorKind)
        {
            case UnaryOperatorKind.Not when v is Host h:
                return Composite($"(!{h.Code})", u.Type!, u.OperatorMethod is null, () => "NOT " + Sql.Group(Operand(h, at)), h);
            case UnaryOperatorKind.Not:
                return new SqlValue("NOT " + Sql.Group(Operand(v, at)), u.Type!, false);
            case UnaryOperatorKind.Minus when v is Host h:
                return Composite("(-" + h.Code + ")", u.Type!, u.OperatorMethod is null, null, h);
            case UnaryOperatorKind.Minus:
                return new SqlValue("-" + Sql.Group(Operand(v, at)), u.Type!, MayBeNull(v));
            case UnaryOperatorKind.Plus:
                return v;
            default:
                throw new Refused(at, $"the operator {u.OperatorKind}");
        }
    }

    Value Coalesce(ICoalesceOperation c, Env env)
    {
        var at = c.Syntax.GetLocation();
        var l = Eval(c.Value, env);
        var r = Eval(c.WhenNull, env);
        if (l is Host && r is Host or Constant) return Composite($"({Code(l, at)} ?? {Code(r, at)})", c.Type!, true, null, l, r);
        return new SqlValue($"COALESCE({Operand(l, at)}, {Operand(r, at)})", c.Type!, MayBeNull(r));
    }

    Value Conditional(IConditionalOperation c, Env env)
    {
        var at = c.Syntax.GetLocation();
        var condition = Eval(c.Condition, env);
        var t = Eval(c.WhenTrue, env);
        var f = Eval(c.WhenFalse!, env);
        if (condition is Host or Constant && t is Host or Constant && f is Host or Constant)
            return Composite($"({Code(condition, at)} ? {Code(t, at)} : {Code(f, at)})", c.Type!, true, null, condition, t, f);
        string cond = Condition(condition, at);
        if (IsView(c.Type!)) throw new Refused(at, "a condition choosing between two views (choose each member instead)");
        return new SqlValue($"CASE WHEN {cond} THEN {Operand(t, at)} ELSE {Operand(f, at)} END", c.Type!, MayBeNull(t) || MayBeNull(f));
    }

    Value IsPattern(IIsPatternOperation p, Env env)
    {
        var at = p.Syntax.GetLocation();
        var v = Eval(p.Value, env);
        switch (p.Pattern)
        {
            case IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } }:
                return new SqlValue(Sql.Group(Operand(v, at)) + " IS NULL", p.Type!, false);
            case INegatedPatternOperation { Pattern: IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } } }:
                return new SqlValue(Sql.Group(Operand(v, at)) + " IS NOT NULL", p.Type!, false);
            default:
                throw new Refused(at, $"the pattern '{Short(p.Pattern.Syntax.ToString())}'");
        }
    }

    Value Create(IObjectCreationOperation oc, Env env)
    {
        var at = oc.Syntax.GetLocation();
        var type = (INamedTypeSymbol)oc.Type!;
        var members = new List<(string, Value)>();
        if (oc.Constructor is { } ctor)
        {
            var props = type.GetMembers().OfType<IPropertySymbol>().ToList();
            foreach (var arg in oc.Arguments)
            {
                if (arg.Parameter is null || arg.ArgumentKind == ArgumentKind.DefaultValue && type.IsValueType) continue;
                var prop = props.FirstOrDefault(p => string.Equals(p.Name, arg.Parameter.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new Refused(arg.Syntax.GetLocation(), $"the constructor parameter '{arg.Parameter.Name}', which names no property");
                members.Add((prop.Name, Eval(arg.Value, env)));
            }
        }
        if (oc.Initializer is { } init)
            foreach (var i in init.Initializers)
            {
                if (i is not ISimpleAssignmentOperation { Target: IPropertyReferenceOperation target } assign)
                    throw new Refused(i.Syntax.GetLocation(), "an initializer that isn't 'Property = value'");
                members.Add((target.Property.Name, Eval(assign.Value, env)));
            }
        return new ObjectValue(type, members);
    }

    Value With(IWithOperation w, Env env)
    {
        var at = w.Syntax.GetLocation();
        if (Eval(w.Operand, env) is not ObjectValue o) throw new Refused(at, "'with' on something other than a view being made");
        var members = new List<(string, Value)>(o.Members);
        foreach (var i in w.Initializer.Initializers)
        {
            if (i is not ISimpleAssignmentOperation { Target: IPropertyReferenceOperation target } assign)
                throw new Refused(i.Syntax.GetLocation(), "an initializer that isn't 'Property = value'");
            members.Add((target.Property.Name, Eval(assign.Value, env)));
        }
        return new ObjectValue(o.Type is INamedTypeSymbol n ? n : (INamedTypeSymbol)w.Type!, members);
    }

    // ---------- values as SQL ----------

    /// <summary>A scalar value as SQL: a column or expression, a constant, or a parameter.</summary>
    public string Operand(Value v, Location? at) => v switch
    {
        SqlValue { NullIsFalse: true } s => $"COALESCE({s.Sql}, {D.False})",
        SqlValue s => s.Sql,
        Host { SqlForm: { } form } => form(),
        Host h => Param(h, at),
        Constant c => Literal(c, at),
        _ => throw new Refused(at, $"a {Name(v)} where a single value is needed"),
    };

    /// <summary>A value as a condition: a bool parameter becomes one too.</summary>
    public string Condition(Value v, Location? at) => v switch
    {
        Constant { Constant_: bool b } => b ? D.True : D.False,
        SqlValue s => s.Sql,
        _ => Operand(v, at),
    };

    static string Name(Value v) => v switch
    {
        RowValue => "row",
        ObjectValue => "view",
        ManyValue => "list",
        TableValue => "table",
        _ => "value",
    };

    string Literal(Constant c, Location? at) => c.Constant_ switch
    {
        null => "NULL",
        bool b => b ? D.True : D.False,
        string s => Sql.String(s),
        char ch => Sql.String(ch.ToString()),
        // typed: 2.5 alone is numeric in SQL, and 2 an integer
        double d => Dialect.Format(D.ToFloat, FloatLiteral(d.ToString("R", CultureInfo.InvariantCulture)), "double precision"),
        float f => Dialect.Format(D.ToFloat, FloatLiteral(f.ToString("R", CultureInfo.InvariantCulture)), "real"),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new Refused(at, $"the constant {c.Constant_}"),
    };

    /// <summary>A floating-point number as SQL text: digits as they are, NaN and the infinities as the strings SQL casts.</summary>
    static string FloatLiteral(string text) => char.IsDigit(text[text.Length - 1]) ? text : Sql.String(text);

    /// <summary>A C# value as a statement parameter.</summary>
    public string Param(Host h, Location? at) => Params.Add(ParamOf(h, at));

    public Param ParamOf(Host h, Location? at)
    {
        var t = h.Type;
        bool nullable = false;
        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } n)
        {
            t = n.TypeArguments[0];
            nullable = true;
        }
        ParamType? kind = t.SpecialType switch
        {
            SpecialType.System_Int64 => ParamType.Int64,
            SpecialType.System_Int32 or SpecialType.System_Int16 or SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_UInt16 => ParamType.Int32,
            SpecialType.System_UInt32 => ParamType.Int64,
            SpecialType.System_Boolean => ParamType.Boolean,
            SpecialType.System_Double or SpecialType.System_Single => ParamType.Double,
            SpecialType.System_String => ParamType.String,
            _ => null,
        };
        if (kind is null)
        {
            if (Known.Same(t, K.Utf8)) kind = ParamType.Text;
            else if (Known.Same(t, K.Timestamp)) kind = ParamType.Timestamp;
            else if (t is INamedTypeSymbol { Name: "ReadOnlySpan" or "Span" } span && span.TypeArguments.Length == 1 && span.TypeArguments[0].SpecialType == SpecialType.System_Byte) kind = ParamType.Span;
            else if (ElementOf(t) is { } element && Known.Same(element, K.Utf8)) kind = ParamType.TextArray;
        }
        if (kind is null) throw new Refused(at, $"a parameter of type '{h.Type.ToDisplayString()}' (use Utf8, a number, a bool, Timestamp, or a list of Utf8)");
        var p = new Param(h.Code, h.Type, kind.Value, nullable || (t.IsReferenceType && t.NullableAnnotation == NullableAnnotation.Annotated), h.Key, h.Made);
        p.Origins.Add(h);
        return p;
    }

    /// <summary>The element type a foreach over <paramref name="t"/> gives, if any.</summary>
    public static ITypeSymbol? ElementOf(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol a) return a.ElementType;
        if (t is INamedTypeSymbol { Name: "ReadOnlySpan" or "Span", TypeArguments.Length: 1 } s) return s.TypeArguments[0];
        var get = t.GetMembers("GetEnumerator").OfType<IMethodSymbol>().FirstOrDefault(m => m.Parameters.Length == 0 && !m.IsStatic);
        if (get?.ReturnType.GetMembers("Current").OfType<IPropertySymbol>().FirstOrDefault() is { } current) return current.Type;
        foreach (var i in t.AllInterfaces)
            if (i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) return i.TypeArguments[0];
        return null;
    }

    /// <summary>A value as C#, where the generated code evaluates it.</summary>
    public string Code(Value v, Location? at) => v switch
    {
        Host h => h.Code,
        Constant c => c.Constant_ switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            string s => Names.Literal(s),
            // each number with its type: C#'s 61 / 2.0 isn't 61 / 2
            long l => Names.Number(l) + "L",
            ulong ul => ul.ToString(CultureInfo.InvariantCulture) + "UL",
            uint u => u.ToString(CultureInfo.InvariantCulture) + "U",
            double d => double.IsNaN(d) ? "global::System.Double.NaN" : double.IsInfinity(d) ? (d > 0 ? "global::System.Double.PositiveInfinity" : "global::System.Double.NegativeInfinity")
                : d.ToString("R", CultureInfo.InvariantCulture) + "D",
            float f => float.IsNaN(f) ? "global::System.Single.NaN" : float.IsInfinity(f) ? (f > 0 ? "global::System.Single.PositiveInfinity" : "global::System.Single.NegativeInfinity")
                : f.ToString("R", CultureInfo.InvariantCulture) + "F",
            decimal m => m.ToString(CultureInfo.InvariantCulture) + "M",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => throw new Refused(at, "this constant in C#"),
        },
        _ => throw new Refused(at, "a value that depends on the database where C# decides (a condition of ThenIf, a hole of Sql)"),
    };
}
