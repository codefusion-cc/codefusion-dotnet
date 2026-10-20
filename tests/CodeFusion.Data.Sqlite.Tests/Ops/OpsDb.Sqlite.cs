using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Sqlite;

namespace Ops.Data;

/// <summary>SQLite's part of the operator tests' queries: the provider, and JSON made in SQLite's SQL.</summary>
public sealed partial class OpsDb : DataContext<Sqlite>
{
    public One<RawJson> Jsonb() => Sql<RawJson>($"SELECT json_object('a', 1)").One();

    public One<Doc> Docs() => Sql<Doc>($"SELECT 1, json_array(1, 'x'), '[2]'").One();
}
