using System.Linq;
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

/// <summary>A table whose column is one of SQLite's keywords (and not one of Postgres's reserved words).</summary>
public sealed class Shelf
{
    public long Id { get; init; }
    public int Index { get; init; }
}

public sealed partial class OpsDb
{
    public Table<Shelf> Shelves => Table<Shelf>();

    public One<long> ShelfAt(int index) => Shelves.Where(s => s.Index == index).Select(s => s.Id).One();

    public Write AddShelf(long id, int index) => Insert(Shelves, new Shelf { Id = id, Index = index });
}
