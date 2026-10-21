using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Postgres;

namespace Ops.Data;

/// <summary>A count that can't go below 0 (a CHECK), with a note that can't be null.</summary>
[Table("counters")]
public sealed class Counter
{
    public long Id { get; init; }
    public int N { get; init; }
    public Utf8? Note { get; init; }
}

/// <summary>A table with a row trigger and a statement trigger, each logging to audit_log.</summary>
[Table("audited")]
public sealed class Audited
{
    public long Id { get; init; }
    public int N { get; init; }
}

/// <summary>A table with a rule that logs each update to audit_log.</summary>
[Table("ruled")]
public sealed class Ruled
{
    public long Id { get; init; }
    public int N { get; init; }
}

/// <summary>A partitioned table whose one partition has a unique index of its own.</summary>
[Table("parted")]
public sealed class Parted
{
    public long Id { get; init; }
    public Utf8 Name { get; init; }
}

/// <summary>A table whose CHECK fails with a serialization failure the first time once_seq is used.</summary>
[Table("flaky")]
public sealed class Flaky
{
    public long Id { get; init; }
    public int N { get; init; }
}

/// <summary>A table whose CHECK sleeps the first time sleep_seq is used after its restart.</summary>
[Table("sleepy")]
public sealed class Sleepy
{
    public long Id { get; init; }
    public int N { get; init; }
}

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
    public Table<Counter> Counters => Table<Counter>();
    public Table<Audited> Audits => Table<Audited>();
    public Table<Ruled> Ruled => Table<Ruled>();
    public Table<Parted> Parts => Table<Parted>();
    public Table<Flaky> Flakies => Table<Flaky>();
    public Table<Sleepy> Sleepies => Table<Sleepy>();

    public Many<Name> TenantItemNames() => TenantItems.Where(i => i.Id < 100).OrderBy(i => i.Id).Select(i => new Name(i.Id, i.Name)).Many();

    public Write<Name> AddTenantItem(Utf8 name) => Insert(TenantItems, new TenantItem { Name = name }).ThenSelect(i => new Name(i.Id, i.Name));

    [RunsAlone]
    public Write AddLabelAlone(int id, Utf8 name) => Insert(Labels, new Label { Id = id, Name = name }).OrIgnore();

    [RunsAlone]
    public One<Name> ByNameAlone(Utf8 name) => People.Where(p => p.Name == name).Select(p => Name.Of(p)).One();

    // updates of one row by its key, every value a parameter: the writer may merge a run of each into one statement
    public Write SetAge(long id, int age) => People.Where(p => p.Id == id).Update(p => new Person { Age = age });

    public Write SetName(long id, Utf8 name) => People.Where(p => p.Id == id).Update(p => new Person { Name = name });

    [RunsAlone]
    public Write SetAgeAlone(long id, int age) => People.Where(p => p.Id == id).Update(p => new Person { Age = age });

    public Write SetCard(long id, Utf8? nick, double? score, bool active, Timestamp born) =>
        People.Where(p => p.Id == id).Update(p => new Person { Nick = nick, Score = score, Active = active, Born = born });

    public Write SetCount(long id, int n) => Counters.Where(c => c.Id == id).Update(c => new Counter { N = n });

    public Write SetNote(long id, Utf8? note) => Counters.Where(c => c.Id == id).Update(c => new Counter { Note = note });

    public Write SetAudited(long id, int n) => Audits.Where(a => a.Id == id).Update(a => new Audited { N = n });

    public Write SetRuled(long id, int n) => Ruled.Where(r => r.Id == id).Update(r => new Ruled { N = n });

    public Write SetPartedName(long id, Utf8 name) => Parts.Where(x => x.Id == id).Update(x => new Parted { Name = name });

    public Write SetFlaky(long id, int n) => Flakies.Where(f => f.Id == id).Update(f => new Flaky { N = n });

    public Write SetSleepy(long id, int n) => Sleepies.Where(f => f.Id == id).Update(f => new Sleepy { N = n });

    /// <summary>A key whose uniqueness is checked at commit (a deferred constraint).</summary>
    public Write InsertDeferred(long id, int k) => Sql($"INSERT INTO deferred_keys (id, k) VALUES ({id}, {k})");

    /// <summary>Fails with a serialization failure (40001) the first time once_seq is used after its restart.</summary>
    public Write SetCountFailingOnce(long id, int n) => Sql($"UPDATE counters SET n = {n} WHERE id = {id} AND fail_once()");

    // not one row by its key with parameters only: never merged
    public Write MoveCounter(long id, long to) => Counters.Where(c => c.Id == id).Update(c => new Counter { Id = to });

    public Write<Name> SetAgeReturning(long id, int age) => People.Where(p => p.Id == id).Update(p => new Person { Age = age }).Select(p => Name.Of(p));

    public Write AgeOneYear(long id) => People.Where(p => p.Id == id).Update(p => new Person { Age = p.Age + 1 });

    public Write SetAgeOfName(Utf8 name, int age) => People.Where(p => p.Name == name).Update(p => new Person { Age = age });

    /// <summary>Keeps the writer busy, so the writes queued meanwhile go as the next group together.</summary>
    public Write Pause() => Sql($"SELECT pg_sleep(0.15)");

    public One<RawJson> Jsonb() => Sql<RawJson>($"SELECT jsonb_build_object('a', 1)").One();

    public One<Doc> Docs() => Sql<Doc>($"SELECT 1::bigint, jsonb_build_array(1, 'x'), '[2]'::json").One();

    // ---------- for the failure tests ----------

    public One<long> Sleep(double seconds) => Sql<long>($"SELECT 1::bigint FROM pg_sleep({seconds})").One();

    public One<long> Divide(long by) => Sql<long>($"SELECT 1 / {by}").One();

    /// <summary>Columns that aren't a Name's: the reader fails on the id.</summary>
    public One<Name> NotAName() => Sql<Name>($"SELECT 'x'::text, 'y'::text").One();

    public Write<Name> NotANameWritten(long id) => Sql<Name>($"UPDATE people SET age = age WHERE id = {id} RETURNING name, name").Write();
}
