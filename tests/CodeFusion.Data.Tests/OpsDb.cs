using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Postgres;

namespace Ops.Data;

/// <summary>A table of another schema, whose name the search path's schema has too.</summary>
[Table("items", Schema = "cfdata_ops_tenant")]
public sealed class TenantItem
{
    public long Id { get; init; }
    public Utf8 Name { get; init; }
}

/// <summary>Postgres's part of the operator tests' queries: the provider, a table of another schema, jsonb, and the
/// queries the failure tests make fail.</summary>
public sealed partial class OpsDb : DataContext<Postgres>
{
    public Table<TenantItem> TenantItems => Table<TenantItem>();

    public Many<Name> TenantItemNames() => TenantItems.Where(i => i.Id < 100).OrderBy(i => i.Id).Select(i => new Name(i.Id, i.Name)).Many();

    public Write<Name> AddTenantItem(Utf8 name) => Insert(TenantItems, new TenantItem { Name = name }).ThenSelect(i => new Name(i.Id, i.Name));

    public One<RawJson> Jsonb() => Sql<RawJson>($"SELECT jsonb_build_object('a', 1)").One();

    public One<Doc> Docs() => Sql<Doc>($"SELECT 1::bigint, jsonb_build_array(1, 'x'), '[2]'::json").One();

    // ---------- for the failure tests ----------

    public One<long> Sleep(double seconds) => Sql<long>($"SELECT 1::bigint FROM pg_sleep({seconds})").One();

    public One<long> Divide(long by) => Sql<long>($"SELECT 1 / {by}").One();

    /// <summary>Columns that aren't a Name's: the reader fails on the id.</summary>
    public One<Name> NotAName() => Sql<Name>($"SELECT 'x'::text, 'y'::text").One();

    public Write<Name> NotANameWritten(long id) => Sql<Name>($"UPDATE people SET age = age WHERE id = {id} RETURNING name, name").Write();
}
