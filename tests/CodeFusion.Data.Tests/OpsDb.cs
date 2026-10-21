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

/// <summary>The same table's one partition, as an entity of its own.</summary>
[Table("parted_1")]
public sealed class PartedOne
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

public readonly record struct Trimmed(Utf8 Both, Utf8 Start, Utf8 End);

public readonly record struct Tally(long Id, long Count, bool Has);

/// <summary>The spec's slug rule, in the functions of Utf8Operators: it runs as C# too.</summary>
public static class Slugs
{
    public static Utf8 WithId(Utf8 title, long id) => title.ReplaceMatches("[^A-Za-z0-9]+", "-").ToLowerAscii().Trim('-').Concat("-"u8).Concat(id);
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
    public Table<ChainA> ChainAs => Table<ChainA>();
    public Table<ChainC> ChainCs => Table<ChainC>();
    public Table<PartedOne> PartOnes => Table<PartedOne>();

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

    public Many<long> Sleeps(double seconds) => Sql<long>($"SELECT 1::bigint FROM pg_sleep({seconds})").Many();

    public One<long> Divide(long by) => Sql<long>($"SELECT 1 / {by}").One();

    /// <summary>Columns that aren't a Name's: the reader fails on the id.</summary>
    public One<Name> NotAName() => Sql<Name>($"SELECT 'x'::text, 'y'::text").One();

    public Write<Name> NotANameWritten(long id) => Sql<Name>($"UPDATE people SET age = age WHERE id = {id} RETURNING name, name").Write();

    // ---------- text functions ----------

    public One<Utf8> LowerOf(Utf8 text) => People.Where(p => p.Id == 1).Select(p => text.ToLowerAscii()).One();

    public One<Utf8> UpperOf(Utf8 text) => People.Where(p => p.Id == 1).Select(p => text.ToUpperAscii()).One();

    public One<Trimmed> TrimmedOf(Utf8 text) => People.Where(p => p.Id == 1).Select(p => new Trimmed(text.Trim('-'), text.TrimStart('é'), text.TrimEnd('\''))).One();

    public One<Utf8> ReplacedOf(Utf8 text, Utf8 oldValue, Utf8 newValue) => People.Where(p => p.Id == 1).Select(p => text.Replace(oldValue, newValue)).One();

    public One<Utf8> SlugOf(Utf8 title, long id) => People.Where(p => p.Id == 1).Select(p => Slugs.WithId(title, id)).One();

    public Many<Name> NameTags() =>
        People.OrderBy(p => p.Id).Select(p => new Name(p.Id, p.Name.ToUpperAscii().Concat(":"u8).Concat(p.Age).Replace("N"u8, "n"u8).Concat(p.Nick ?? Utf8.Empty))).Many();

    public Write<Name> AddSlugged(Utf8 title) =>
        Insert(People, n => new Person { Name = Slugs.WithId(title, n.Id), Age = 2, Active = true, Born = SqlFunctions.Now() })
            .ThenSelect(n => Name.Of(n));

    /// <summary>A text function where C# decides: the method itself runs.</summary>
    public Write AddIfLowered(Utf8 name, Utf8 lowered) =>
        Insert(People, new Person { Name = name, Age = 3, Active = true, Born = SqlFunctions.Now() })
            .ThenIf(name.ToLowerAscii() == lowered, Sql($"UPDATE people SET nick = 'lowered' WHERE name = {name}"));

    // ---------- inserts of a list's texts ----------

    /// <summary>Each new name once, in sorted order; names taken already are skipped.</summary>
    public Write AddPeopleNamed(Utf8List names, int age) =>
        Insert(People, names.AsRows().Distinct().OrderBy(n => n).Select(n => new Person { Name = n, Age = age, Active = false, Born = SqlFunctions.Now() })).OrIgnore();

    /// <summary>No Distinct: a name twice in the list is a unique violation.</summary>
    public Write AddPeopleNamedOnce(Utf8List names) =>
        Insert(People, names.AsRows().Where(n => n != ""u8).OrderByDescending(n => n.ToUpperAscii()).Select(n => new Person { Name = n, Nick = n.ToLowerAscii(), Age = 62, Active = false, Born = SqlFunctions.Now() }));

    /// <summary>A team, its new members and the team read back, in one unit.</summary>
    public Write<Name> AddTeamOf(long id, Utf8 name, Utf8List members) =>
        Insert(Teams, new Team { Id = id, Name = name })
            .ThenIf(!members.IsEmpty, t => Insert(People, members.AsRows().Distinct().Select(m => new Person { Name = m, Age = 63, Active = true, Born = SqlFunctions.Now(), TeamId = t.Id })).OrIgnore())
            .ThenSelect(t => new Name(t.Id, t.Name));

    // ---------- writes from the rows a query found ----------

    /// <summary>A pet for the person of this name, and the person given back; a pet key taken already is skipped.</summary>
    public Write<Name> GivePet(Utf8 name, long id, Utf8 pet) =>
        People.Where(p => p.Name == name).Then(p => Insert(Pets, new Pet { Id = id, OwnerId = p.Id, Name = pet, Legs = 4 }).OrIgnore()).Select(p => Name.Of(p));

    /// <summary>Without OrIgnore a taken key is a conflict, and nothing is written; the Select joins the row's team.</summary>
    public Write<WithTeam> GivePetStrict(Utf8 name, long id) =>
        People.Where(p => p.Name == name).Then(p => Insert(Pets, new Pet { Id = id, OwnerId = p.Id, Name = p.Name, Legs = 3 })).Select(p => new WithTeam(p.Name, p.Team.Name));

    public Write<Number> TakePets(Utf8 name, int legs) =>
        People.Where(p => p.Name == name).Then(p => Pets.Where(x => x.OwnerId == p.Id && x.Legs == legs).Delete()).Select(p => new Number(p.Id, p.Age));

    /// <summary>A pet for each person of an age, and the one of the smallest key given back.</summary>
    public Write<Name> GivePetsOfAge(int age) =>
        People.Where(p => p.Age == age).Then(p => Insert(Pets, new Pet { Id = p.Id + 60000, OwnerId = p.Id, Name = p.Name, Legs = 1 }).OrIgnore()).Select(p => Name.Of(p));

    /// <summary>The pets with more legs or of the person found: none when no one is found.</summary>
    public Write TakeLegsOrOwn(Utf8 name, int legs) => People.Where(p => p.Name == name).Then(p => Pets.Where(x => x.Legs > legs || x.OwnerId == p.Id).Delete());

    /// <summary>Each person of this age a pet keyed by their own id; nothing given back.</summary>
    public Write PetsForAge(int age) =>
        People.Where(p => p.Age == age).Then(p => Insert(Pets, new Pet { Id = p.Id + 50000, OwnerId = p.Id, Name = p.Name, Legs = 1 }).OrIgnore());

    // ---------- reads of the table written, as the write left it ----------

    /// <summary>A label for the person, and their labels counted after it, in one statement: the snapshot's count
    /// corrected by the label written (or there already).</summary>
    public Write<Tally> GiveLabel(Utf8 name, int label) =>
        People.Where(p => p.Name == name).Then(p => Insert(PersonLabels, new PersonLabel { PersonId = p.Id, LabelId = label }).OrIgnore())
            .Select(p => new Tally(p.Id, p.Labelings.Count(), p.Labelings.Any(l => l.LabelId == label)));

    /// <summary>A count of another label than the one written: it counts the row written only when the two are one.</summary>
    public Write<Tally> GiveLabelCountOther(Utf8 name, int label, int other) =>
        People.Where(p => p.Name == name).Then(p => Insert(PersonLabels, new PersonLabel { PersonId = p.Id, LabelId = label }).OrIgnore())
            .Select(p => new Tally(p.Id, p.Labelings.Count(l => l.LabelId == other), p.Labelings.Where(l => l.LabelId == other).Any()));

    public Write<Tally> TakeLabel(Utf8 name, int label) =>
        People.Where(p => p.Name == name).Then(p => PersonLabels.Where(l => l.PersonId == p.Id && l.LabelId == label).Delete())
            .Select(p => new Tally(p.Id, p.Labelings.Count(), p.Labelings.Any(l => l.LabelId == label)));

    /// <summary>A delete of the labels above a number, which no key fixes: the rows gone are counted.</summary>
    public Write<Tally> TakeLabelsAbove(Utf8 name, int above, int probe) =>
        People.Where(p => p.Name == name).Then(p => PersonLabels.Where(l => l.PersonId == p.Id && l.LabelId > above).Delete())
            .Select(p => new Tally(p.Id, p.Labelings.Count(), p.Labelings.Any(l => l.LabelId == probe)));

    /// <summary>An Any whose condition reads a nullable column, after a plain insert: false, not NULL, when it is NULL.</summary>
    public Write<Tally> AddPetLikeNick(Utf8 name, long id, Utf8 pet) =>
        People.Where(p => p.Name == name).Then(p => Insert(Pets, new Pet { Id = id, OwnerId = p.Id, Name = pet, Legs = 4 }))
            .Select(p => new Tally(p.Id, p.Pets.Count(), p.Pets.Any(x => x.Name == p.Nick)));

    public Write<Tally> AddPetNotLikeNick(Utf8 name, long id, Utf8 pet) =>
        People.Where(p => p.Name == name).Then(p => Insert(Pets, new Pet { Id = id, OwnerId = p.Id, Name = pet, Legs = 4 }))
            .Select(p => new Tally(p.Id, p.Pets.Count(), !p.Pets.Any(x => x.Name == p.Nick)));

    /// <summary>A delete whose Select reads, through a join table, rows the delete may cascade into: the model can't see
    /// the join table's foreign key, so the statement checks it and fails.</summary>
    public Write<Tally> DropMember(long team, Utf8 name) =>
        Teams.Where(t => t.Id == team).Then(t => People.Where(p => p.Name == name).Delete())
            .Select(t => new Tally(t.Id, t.Members.Count(), t.Members.Any(m => m.Labels.Any())));

    /// <summary>A delete two cascades away from the table the Select reads: chain_a to chain_b to chain_c.</summary>
    public Write<Number> DropChain(long team, Utf8 name) =>
        Teams.Where(t => t.Id == team).Then(t => ChainAs.Where(a => a.Name == name).Delete()).Select(t => new Number(t.Id, t.Chains.Count()));

    /// <summary>The same delete from a chain_c row found, which it may cascade into, giving back that row's own columns.</summary>
    public Write<Number> DropChainOf(long id, Utf8 name) =>
        ChainCs.Where(c => c.Id == id).Then(c => ChainAs.Where(a => a.Name == name).Delete()).Select(c => new Number(c.Id, c.TeamId));

    /// <summary>A row found in the partitioned table, deleted through its partition, mapped as another entity.</summary>
    public Write<Number> DropPartThroughItsPartition(long id) =>
        Parts.Where(p => p.Id == id).Then(p => PartOnes.Where(x => x.Id == p.Id).Delete()).Select(p => new Number(p.Id, p.Id));

    /// <summary>A row found in the partition, deleted through the partitioned table.</summary>
    public Write<Number> DropPartThroughItsParent(long id) =>
        PartOnes.Where(p => p.Id == id).Then(p => Parts.Where(x => x.Id == p.Id).Delete()).Select(p => new Number(p.Id, p.Id));

    /// <summary>An insert without OrIgnore: the row written is the one more, whatever its columns.</summary>
    public Write<Tally> AddPetCounted(Utf8 name, long id) =>
        People.Where(p => p.Name == name).Then(p => Insert(Pets, new Pet { Id = id, OwnerId = p.Id, Name = p.Name, Legs = 3 }))
            .Select(p => new Tally(p.Id, p.Pets.Count(x => x.Legs > 2), p.Pets.Any(x => x.Name == p.Name)));

    /// <summary>The same insert with a float set, the pets of a weight counted.</summary>
    public Write<Tally> AddPetWeighed(Utf8 name, long id) =>
        People.Where(p => p.Name == name).Then(p => Insert(Pets, new Pet { Id = id, OwnerId = p.Id, Name = p.Name, Legs = 3, Weight = 2.5f }))
            .Select(p => new Tally(p.Id, p.Pets.Count(x => x.Weight > 2f), p.Pets.Any(x => x.Name == p.Name)));

    // ---------- pages of each row's children ----------

    /// <summary>The pets of the people with a label, newest first, as the feed has the articles of the authors followed:
    /// a person's pets reached through the link row's one-navigation.</summary>
    public Page<Owned> PetsOfLabel(int label, Paging page) =>
        PersonLabels.Where(pl => pl.LabelId == label).SelectMany(pl => pl.Person.Pets, (pl, x) => x).OrderByDescending(x => x.Id)
            .Page(page, countUpTo: 5, maxLimit: 3).Select(x => new Owned(x.Name, Name.Of(x.Owner)));

    /// <summary>Each labelled person's newest pet only, and the count probed.</summary>
    public Page<Owned> NewestPetOfLabel(int label, Paging page) =>
        PersonLabels.Where(pl => pl.LabelId == label).SelectMany(pl => pl.Person.Pets.OrderByDescending(x => x.Id).Take(1), (pl, x) => x).OrderByDescending(x => x.Id)
            .Page(page, countUpTo: 5, PageCount.Probe).Select(x => new Owned(x.Name, Name.Of(x.Owner)));

    /// <summary>A team's members of an age, oldest key first: a Where on the children, ascending.</summary>
    public Page<Name> MembersOf(long team, int age, Paging page) =>
        Teams.Where(t => t.Id == team).SelectMany(t => t.Members, (t, m) => m).Where(m => m.Age >= age).OrderBy(m => m.Id)
            .Page(page, countUpTo: 5).Select(m => Name.Of(m));
}
