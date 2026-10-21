using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Postgres;

namespace Ops.Data;

// A small schema with one query per operator the translator knows, for OperatorTests: each query's SQL is checked
// as text, and run against OperatorTests' seed.

public sealed class Team
{
    public long Id { get; init; }
    public Utf8 Name { get; init; }
    public Many<Person> Members { get; init; }
}

public sealed class Person
{
    public long Id { get; init; }
    public Utf8 Name { get; init; }
    public Utf8? Nick { get; init; }
    public int Age { get; init; }
    public double? Score { get; init; }
    public bool Active { get; init; }
    public Timestamp Born { get; init; }
    public long? TeamId { get; init; }
    public Team Team { get; init; } = null!;
    public Many<Pet> Pets { get; init; }

    [JoinTable("person_labels")]
    public Many<Label> Labels { get; init; }
}

public sealed class Pet
{
    public long Id { get; init; }
    public long OwnerId { get; init; }
    public Person Owner { get; init; } = null!;
    public Utf8 Name { get; init; }
    public int Legs { get; init; }
}

public sealed class Label
{
    public int Id { get; init; }
    public Utf8 Name { get; init; }
}

public sealed class PersonLabel
{
    public long PersonId { get; init; }
    public Person Person { get; init; } = null!;
    public int LabelId { get; init; }
    public Label Label { get; init; } = null!;
}

/// <summary>A table whose name needs quotes.</summary>
[Table("Gadgets")]
public sealed class Gadget
{
    public long Id { get; init; }
    public Utf8 Name { get; init; }
}

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

/// <summary>A table of another schema, whose name the search path's schema has too.</summary>
[Table("items", Schema = "cfdata_ops_tenant")]
public sealed class TenantItem
{
    public long Id { get; init; }
    public Utf8 Name { get; init; }
}

public readonly record struct Name(long Id, Utf8 Text)
{
    public static Name Of(Person p) => new(p.Id, p.Name);
}

public readonly record struct Ratio(long Id, double Pets, int Whole);

public readonly record struct PetLists(long Id, RawJson First, RawJson ByName);

public readonly record struct Doc(long Id, RawJson Meta, RawJson Plain);

public static class Checks
{
    /// <summary>A value the generated binder computes when the query is called, which may throw.</summary>
    public static long NotNegative(long x)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        return x;
    }
}

/// <summary>Keys drawn in C#, as a Snowflake or ULID generator would: each call a new one.</summary>
public static class Keys
{
    static long _next = 7000;

    public static long Next()
    {
        long key = System.Threading.Interlocked.Increment(ref _next);
        return key;
    }

    public static long Calls => System.Threading.Interlocked.Read(ref _next) - 7000;

    static long _huge;

    /// <summary>Keys past 2^53, which a double can't hold: 2^53 + 1, 2^53 + 3 and on.</summary>
    public static long Huge()
    {
        long n = System.Threading.Interlocked.Increment(ref _huge);
        return (1L << 53) + 2 * n - 1;
    }

    public static long HugeCalls => System.Threading.Interlocked.Read(ref _huge);

    /// <summary>A new label each call, drawn from the same keys.</summary>
    public static Utf8 Label()
    {
        var b = System.Text.Encoding.UTF8.GetBytes("k" + Next().ToString(System.Globalization.CultureInfo.InvariantCulture));
        return new Utf8(b, 0, b.Length);
    }
}

[JsonList("people", CountName = "total")]
public readonly record struct Card(long Id, Utf8 Name, Utf8? Nick, int Age, double? Score, bool Active, Timestamp Born);

public readonly record struct WithTeam(Utf8 Name, Utf8? Team);

public readonly record struct Owned(Utf8 Pet, Name Owner);

public readonly record struct PetStats(long Id, long Pets, bool HasQuadruped, RawJson Named, RawJson Labels, bool IsAdmin);

public readonly record struct Number(long Id, long Value);

public readonly record struct Flags(long Id, bool Senior, bool HasNick, bool HasScore);

public readonly record struct Settable
{
    public long Id { get; init; }
    public Utf8 Name { get; init; }
}

public static class SqlText
{
    [SqlTemplate("lower({0})")]
    public static Utf8 Lower(Utf8 text) => throw new NotSupportedException();

    [SqlTemplate("length({0})")]
    public static long Length(Utf8 text) => throw new NotSupportedException();

    /// <summary>A template with a subquery of its own, where a bare column would be its table's.</summary>
    [SqlTemplate("EXISTS (SELECT 1 FROM pets x WHERE x.owner_id = {0})")]
    public static bool HasPets(long person) => throw new NotSupportedException();
}

public sealed partial class OpsDb : DataContext<Postgres>
{
    public Table<Team> Teams => Table<Team>();
    public Table<Person> People => Table<Person>();
    public Table<Pet> Pets => Table<Pet>();
    public Table<Label> Labels => Table<Label>();
    public Table<PersonLabel> PersonLabels => Table<PersonLabel>();
    public Table<Gadget> Gadgets => Table<Gadget>();
    public Table<TenantItem> TenantItems => Table<TenantItem>();
    public Table<Counter> Counters => Table<Counter>();
    public Table<Audited> Audits => Table<Audited>();
    public Table<Ruled> Ruled => Table<Ruled>();
    public Table<Parted> Parts => Table<Parted>();

    // ---------- Where ----------

    public One<Name> ByName(Utf8 name) => People.Where(p => p.Name == name).Select(p => Name.Of(p)).One();

    public Many<Name> Comparisons(int min, long max) =>
        People.Where(p => p.Age >= min && p.Id <= max && (p.Active || !(p.Age < 30)) && p.Name != "ben"u8).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> NickIs(Utf8? nick) => People.Where(p => p.Nick == nick).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> NickIsNot(Utf8? nick) => People.Where(p => p.Nick != nick).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> Nulls() => People.Where(p => p.Nick == null || p.Score is null || !p.Score.HasValue).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> ScoreAbove(double score) => People.Where(p => p.Score > score).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> NotScoreAbove(double score) => People.Where(p => !(p.Score > score)).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> InList(Utf8List names) => People.Where(p => names.Contains(p.Name)).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> InNames(Utf8[] names) => People.Where(p => names.Contains(p.Name)).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> InNamesOf(System.Collections.Generic.IEnumerable<Utf8> names) => People.Where(p => names.Contains(p.Name)).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> OnTeam(Utf8 team) =>
        People.Where(p => p.TeamId == Teams.Single(t => t.Name == team).Id).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> BornBeforeNow() => People.Where(p => p.Born < SqlFunctions.Now()).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> OlderThanPlus(int age, int plus) => People.Where(p => p.Age > age + plus).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> OlderThanHalf(int budget) => People.Where(p => p.Age >= budget / 2.0).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> SameSeniority() => People.Where(p => (p.Age > 30) == p.Active).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> MixedFlags() => People.Where(p => (p.Age >= 30) != (p.Nick == null)).OrderBy(p => p.Id).Select(p => Name.Of(p)).Many();

    public Many<Name> KeywordAliases(int min) => People.Where(order => order.Age >= min).OrderBy(left => left.Id).Select(left => Name.Of(left)).Many();

    public Many<Name> TenantItemNames() => TenantItems.Where(i => i.Id < 100).OrderBy(i => i.Id).Select(i => new Name(i.Id, i.Name)).Many();

    public One<Name> Checked(long id) => People.Where(p => p.Id == Checks.NotNegative(id)).Select(p => Name.Of(p)).One();

    // ---------- Select ----------

    public Many<Card> Cards() => People.OrderBy(p => p.Id).Select(p => new Card(p.Id, p.Name, p.Nick, p.Age, p.Score, p.Active, p.Born)).Many();

    public Many<Name> NickOrName() => People.OrderBy(p => p.Id).Select(p => new Name(p.Id, p.Nick ?? p.Name)).Many();

    public Many<Name> Conditional() => People.OrderBy(p => p.Id).Select(p => new Name(p.Id, p.Active ? p.Name : SqlText.Lower(p.Nick ?? p.Name))).Many();

    public Many<Number> Arithmetic(long add) => People.OrderBy(p => p.Id).Select(p => new Number(p.Id, p.Age * 2 + add - p.Id % 2)).Many();

    public Many<Flags> Conditions() => People.OrderBy(p => p.Id).Select(p => new Flags(p.Id, p.Age >= 30 && p.Active, p.Nick != null, p.Score.HasValue)).Many();

    public Many<Name> WithOverride() => People.OrderBy(p => p.Id).Select(p => Name.Of(p) with { Text = SqlText.Lower(p.Name) }).Many();

    public Many<Settable> Initializer() => People.OrderBy(p => p.Id).Select(p => new Settable { Id = p.Id, Name = p.Name }).Many();

    public Many<Name> AfterSelect(long min) => People.Select(p => Name.Of(p)).Where(n => n.Id >= min).OrderByDescending(n => n.Text).Many();

    public Many<Ratio> Ratios() => People.OrderBy(p => p.Id).Select(p => new Ratio(p.Id, p.Pets.Count(), (int)(p.Score ?? 0))).Many();

    public One<double> HalfAge(long id) => People.Where(p => p.Id == id).Select(p => p.Age / 2.0).One();

    public One<double> AgeAndAHalf(long id) => People.Where(p => p.Id == id).Select(p => p.Age * 1.5).One();

    public One<double> AgeAsDouble(long id) => People.Where(p => p.Id == id).Select(p => (double)p.Age).One();

    // ---------- order and paging ----------

    public Many<Name> Ordered(long skip, long take) =>
        People.OrderBy(p => p.Age).ThenByDescending(p => p.Name).Skip(skip).Take(take).Select(p => Name.Of(p)).Many();

    public Many<Name> Top(long take) => People.OrderByDescending(p => p.Id).Take(take).Select(p => Name.Of(p)).Many();

    public Many<Name> TakeThenSkip(long take, long skip) => People.OrderBy(p => p.Id).Take(take).Skip(skip).Select(p => Name.Of(p)).Many();

    public Many<Name> Windows(long a, long b, long c, long d) => People.OrderBy(p => p.Id).Skip(a).Take(b).Skip(c).Take(d).Select(p => Name.Of(p)).Many();

    public Many<Name> ReorderedByName() => People.OrderBy(p => p.Age).OrderByDescending(p => p.Name).Select(p => Name.Of(p)).Many();

    public Many<Name> ReorderedByAge() => People.OrderBy(p => p.Id).OrderBy(p => p.Age).ThenByDescending(p => p.Name).Select(p => Name.Of(p)).Many();

    public Many<PetLists> PetOrders() => People.OrderBy(p => p.Id).Select(p => new PetLists(p.Id,
        p.Pets.OrderBy(x => x.Name).Take(1).Take(2).Select(x => x.Name).AsJson(),
        p.Pets.OrderBy(x => x.Legs).OrderBy(x => x.Name).Select(x => x.Name).AsJson())).Many();

    // ---------- scalars ----------

    public One<long> CountActive() => People.Where(p => p.Active).Count();

    public One<long> CountFirst(long take) => People.OrderBy(p => p.Id).Take(take).Count();

    public One<bool> AnyOlder(int age) => People.Where(p => p.Age > age).Any();

    public One<RawJson> NamesAsJson() => People.OrderBy(p => p.Name).Select(p => p.Name).AsJson();

    public One<RawJson> NamesOverAgeAsJson(int age) => People.Where(p => p.Age > age).OrderBy(p => p.Name).Select(p => p.Name).AsJson();

    public One<int> AgeOf(long id) => People.Where(p => p.Id == id).Select(p => p.Age).One();

    public One<Utf8> NickOf(long id) => People.Where(p => p.Id == id).Select(p => p.Nick ?? p.Name).One();

    public One<double> ScoreOf(long id) => People.Where(p => p.Id == id).Select(p => p.Score ?? -1).One();

    public One<Timestamp> BornOf(long id) => People.Where(p => p.Id == id).Select(p => p.Born).One();

    public One<long> NameLength(long id) => People.Where(p => p.Id == id).Select(p => SqlText.Length(p.Name)).One();

    // ---------- navigations ----------

    public Many<Owned> Owners() => Pets.OrderBy(x => x.Id).Select(x => new Owned(x.Name, Name.Of(x.Owner))).Many();

    public Many<WithTeam> Teamed() => People.OrderBy(p => p.Id).Select(p => new WithTeam(p.Name, p.Team.Name)).Many();

    public Many<PetStats> Stats(Utf8 label) => People.OrderBy(p => p.Id).Select(p => new PetStats(
        p.Id,
        p.Pets.Count(),
        p.Pets.Any(x => x.Legs > 2),
        p.Pets.Where(x => x.Legs > 0).OrderBy(x => x.Name).Take(5).Select(x => x.Name).AsJson(),
        p.Labels.OrderBy(l => l.Name).Select(l => l.Name).AsJson(),
        p.Labels.Any(l => l.Name == label))).Many();

    public Many<Name> TeamMembersOver(int age) =>
        Teams.OrderBy(t => t.Id).Select(t => new Name(t.Id, t.Name)).Where(n => n.Id > 0).Many();

    public Many<Number> MemberCounts(int age) => Teams.OrderBy(t => t.Id).Select(t => new Number(t.Id, t.Members.Count(m => m.Age >= age))).Many();

    // ---------- pages and lists of children ----------

    public Page<Card> CardPage(Paging page) =>
        People.Where(p => p.Active).OrderByDescending(p => p.Id).Page(page, countUpTo: 2).Select(p => new Card(p.Id, p.Name, p.Nick, p.Age, p.Score, p.Active, p.Born));

    public Page<Name> LabelPage(Utf8 label, Paging page) =>
        PersonLabels.Where(pl => pl.LabelId == Labels.Single(l => l.Name == label).Id).OrderBy(pl => pl.PersonId)
            .Page(page, countUpTo: 10, PageCount.Probe, maxLimit: 3).Select(pl => Name.Of(pl.Person));

    public Many<Owned> PetsOf(Utf8 name) =>
        People.Where(p => p.Name == name).SelectMany(p => p.Pets.OrderBy(x => x.Name), (p, x) => new Owned(x.Name, Name.Of(p))).Many();

    // ---------- writes ----------

    public Write<Name> AddPerson(Utf8 name, int age) =>
        Insert(People, new Person { Name = name, Age = age, Active = true, Born = SqlFunctions.Now() }).Select(p => Name.Of(p));

    public Write<Owned> AddPet(long id, long owner, Utf8 name) =>
        Insert(Pets, new Pet { Id = id, OwnerId = owner, Name = name, Legs = 4 }).Select(x => new Owned(x.Name, Name.Of(x.Owner)));

    public Write AddLabelOnce(int id, Utf8 name) => Insert(Labels, new Label { Id = id, Name = name }).OrIgnore();

    [RunsAlone]
    public Write AddLabelAlone(int id, Utf8 name) => Insert(Labels, new Label { Id = id, Name = name }).OrIgnore();

    [RunsAlone]
    public One<Name> ByNameAlone(Utf8 name) => People.Where(p => p.Name == name).Select(p => Name.Of(p)).One();

    public Write<Name> AddNicknamed(Utf8 name) =>
        Insert(People, n => new Person { Name = name, Nick = SqlText.Lower(name), Age = 1, Active = false, Born = SqlFunctions.Now(), TeamId = n.Id % 2 + 1 })
            .Then(n => Insert(Pets, Pets.Where(x => x.Id == 1).Select(x => new Pet { Id = x.Id + 1000 + n.Id, OwnerId = n.Id, Name = x.Name, Legs = x.Legs })))
            .ThenIf(name.Length > 3, Sql($"UPDATE people SET score = {name.Length} WHERE name = {name}"))
            .ThenSelect(n => new Name(n.Id, n.Nick ?? n.Name));

    public Write<Name> Rename(long id, Utf8 name) => People.Where(p => p.Id == id).Update(p => new Person { Name = name, Age = p.Age + 1 }).Select(p => Name.Of(p));

    public Write<WithTeam> RenameIfActive(long id, Utf8 name) =>
        People.Where(p => p.Id == id).UpdateIf(p => p.Active, p => new Person { Name = name }).Select(p => new WithTeam(p.Name, p.Team.Name));

    public Write Deactivate(int age) => People.Where(p => p.Age > age).Update(p => new Person { Active = false });

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

    /// <summary>Fails with a serialization failure (40001) the first time once_seq is used after its restart.</summary>
    public Write SetCountFailingOnce(long id, int n) => Sql($"UPDATE counters SET n = {n} WHERE id = {id} AND fail_once()");

    // not one row by its key with parameters only: never merged
    public Write MoveCounter(long id, long to) => Counters.Where(c => c.Id == id).Update(c => new Counter { Id = to });

    public Write<Name> SetAgeReturning(long id, int age) => People.Where(p => p.Id == id).Update(p => new Person { Age = age }).Select(p => Name.Of(p));

    public Write AgeOneYear(long id) => People.Where(p => p.Id == id).Update(p => new Person { Age = p.Age + 1 });

    public Write SetAgeOfName(Utf8 name, int age) => People.Where(p => p.Name == name).Update(p => new Person { Age = age });

    /// <summary>Keeps the writer busy, so the writes queued meanwhile go as the next group together.</summary>
    public Write Pause() => Sql($"SELECT pg_sleep(0.15)");

    public One<long> DeletePetIf(long id, int legs) => Pets.Where(x => x.Id == id).DeleteIf(x => x.Legs == legs).Select(x => x.OwnerId).One();

    public Write DeletePets(long owner) => Pets.Where(x => x.OwnerId == owner).Delete();

    public Write<Name> AddKeyed(Utf8 name) =>
        Insert(People, new Person { Id = Keys.Next(), Name = name, Age = 5, Active = true, Born = SqlFunctions.Now() })
            .Then(p => Insert(Pets, new Pet { Id = Keys.Next(), OwnerId = p.Id, Name = name, Legs = 3 }))
            .ThenSelect(p => new Name(p.Id, p.Name));

    public Write<Name> AddLabelled() =>
        Insert(People, new Person { Name = Keys.Label(), Nick = Keys.Label(), Age = 6, Active = true, Born = SqlFunctions.Now() })
            .ThenSelect(p => new Name(p.Id, p.Name));

    // a key a double can't hold, compared as a double first and then written as the pet's key
    public Write TouchBelow(long n, Utf8 name) => People.Where(p => p.Name == name && p.Age + 0.5 < n).Update(p => new Person { Active = true });
    public Write AddHuge(long n, Utf8 name) => Insert(People, new Person { Name = name, Age = 9, Active = false, Born = SqlFunctions.Now() })
        .Then(TouchBelow(n, name))
        .Then(p => Insert(Pets, new Pet { Id = n, OwnerId = p.Id, Name = name, Legs = 4 }));
    public Write AddHugeOf(Utf8 name) => AddHuge(Keys.Huge(), name);

    // an argument the body doesn't use, and one it uses only under a ThenIf
    public Write Audited(long audit, Utf8 name) => Insert(People, new Person { Name = name, Age = 7, Active = true, Born = SqlFunctions.Now() });
    public Write AuditedOf(Utf8 name) => Audited(Keys.Next(), name);
    public Write MaybePet(bool pet, long k, Utf8 name) => Insert(People, new Person { Name = name, Age = 8, Active = true, Born = SqlFunctions.Now() })
        .ThenIf(pet, p => Insert(Pets, new Pet { Id = k, OwnerId = p.Id, Name = name, Legs = 1 }));
    public Write MaybePetOf(bool pet, Utf8 name) => MaybePet(pet, Keys.Next(), name);

    public Write<Name> AddGadget(Utf8 name) => Insert(Gadgets, new Gadget { Name = name }).ThenSelect(g => new Name(g.Id, g.Name));

    public Write<Name> AddTenantItem(Utf8 name) => Insert(TenantItems, new TenantItem { Name = name }).ThenSelect(i => new Name(i.Id, i.Name));

    /// <summary>The label with this key, inserted or there already.</summary>
    public Write<Name> LabelOnce(int id, Utf8 name) => Insert(Labels, new Label { Id = id, Name = name }).OrIgnore().ThenSelect(l => new Name(l.Id, l.Name));

    public Write DeactivateTeam(long team) => People.Where(p => p.TeamId == team).UpdateIf(p => p.Active, p => new Person { Active = false });

    public One<long> DeleteAPetIf(long owner, int legs) => Pets.Where(x => x.OwnerId == owner).DeleteIf(x => x.Legs == legs).Select(x => x.Id).One();

    public Write<Name> NickTeamIfActive(long team, Utf8 nick) =>
        People.Where(p => p.TeamId == team).UpdateIf(p => p.Active, p => new Person { Nick = nick }).Select(p => Name.Of(p));

    public Write DeletePetsIf(long owner, int legs) => Pets.Where(x => x.OwnerId == owner).DeleteIf(x => x.Legs == legs);

    public Write DeletePetless(int age) => People.Where(p => p.Age == age && !p.Pets.Any()).Delete();

    public Write CountPetsIntoScore(int age) => People.Where(p => p.Age == age).Update(p => new Person { Score = p.Pets.Count() });

    public Write<Number> TouchWithPets(long id) => People.Where(p => p.Id == id).Update(p => new Person { Active = true }).Select(p => new Number(p.Id, p.Pets.Count()));

    public Write DeletePetlessByTemplate(int age) => People.Where(p => p.Age == age && !SqlText.HasPets(p.Id)).Delete();

    public Write ActiveIfPetOwner(int age) => People.Where(p => p.Age == age).Update(p => new Person { Active = SqlText.HasPets(p.Id) });

    public One<long> DeleteIfPetless(long id) => People.Where(p => p.Id == id).DeleteIf(p => !p.Pets.Any()).Select(p => p.Id).One();

    // ---------- SQL ----------

    public Many<Name> Raw(int min, Utf8 name) =>
        Sql<Name>($"SELECT id, name FROM people WHERE age >= {min} AND name <> {name} AND age < {100} ORDER BY " + $"{OrderById()}").Many();

    [SqlTemplate("id")]
    static long OrderById() => throw new NotSupportedException();

    public Write<Name> RawWrite(long id) => Sql<Name>($"UPDATE people SET age = age WHERE id = {id} RETURNING id, name").Write();

    const string Ann = "ann";

    public One<long> IdOfAnn() => Sql<long>($"SELECT id FROM people WHERE name = {Ann}").One();

    public One<RawJson> Jsonb() => Sql<RawJson>($"SELECT jsonb_build_object('a', 1)").One();

    public One<Doc> Docs() => Sql<Doc>($"SELECT 1::bigint, jsonb_build_array(1, 'x'), '[2]'::json").One();

    // ---------- for the failure tests ----------

    public One<long> Sleep(double seconds) => Sql<long>($"SELECT 1::bigint FROM pg_sleep({seconds})").One();

    public One<long> Divide(long by) => Sql<long>($"SELECT 1 / {by}").One();

    /// <summary>Columns that aren't a Name's: the reader fails on the id.</summary>
    public One<Name> NotAName() => Sql<Name>($"SELECT 'x'::text, 'y'::text").One();

    public Write<Name> NotANameWritten(long id) => Sql<Name>($"UPDATE people SET age = age WHERE id = {id} RETURNING name, name").Write();

    public Write<Name> Touch(long id) => People.Where(p => p.Id == id).Update(p => new Person { Age = p.Age }).Select(p => Name.Of(p));
}
