using System;
using System.ComponentModel.DataAnnotations;
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

    /// <summary>The same labels, as the link table's own rows.</summary>
    public Many<PersonLabel> Labelings { get; init; }
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
    /// <summary>With LabelId, the key: [Key] on each.</summary>
    [Key] public long PersonId { get; init; }
    public Person Person { get; init; } = null!;
    [Key] public int LabelId { get; init; }
    public Label Label { get; init; } = null!;
}

/// <summary>A table whose name needs quotes.</summary>
[Table("Gadgets")]
public sealed class Gadget
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

public readonly record struct Trimmed(Utf8 Both, Utf8 Start, Utf8 End);

public readonly record struct Tally(long Id, long Count, bool Has);

/// <summary>The spec's slug rule, in the functions of Utf8Operators: it runs as C# too.</summary>
public static class Slugs
{
    public static Utf8 WithId(Utf8 title, long id) => title.ReplaceMatches("[^A-Za-z0-9]+", "-").ToLowerAscii().Trim('-').Concat("-"u8).Concat(id);
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

    public Write<Name> AddNicknamed(Utf8 name) =>
        Insert(People, n => new Person { Name = name, Nick = SqlText.Lower(name), Age = 1, Active = false, Born = SqlFunctions.Now(), TeamId = n.Id % 2 + 1 })
            .Then(n => Insert(Pets, Pets.Where(x => x.Id == 1).Select(x => new Pet { Id = x.Id + 1000 + n.Id, OwnerId = n.Id, Name = x.Name, Legs = x.Legs })))
            .ThenIf(name.Length > 3, Sql($"UPDATE people SET score = {name.Length} WHERE name = {name}"))
            .ThenSelect(n => new Name(n.Id, n.Nick ?? n.Name));

    public Write<Name> Rename(long id, Utf8 name) => People.Where(p => p.Id == id).Update(p => new Person { Name = name, Age = p.Age + 1 }).Select(p => Name.Of(p));

    public Write<WithTeam> RenameIfActive(long id, Utf8 name) =>
        People.Where(p => p.Id == id).UpdateIf(p => p.Active, p => new Person { Name = name }).Select(p => new WithTeam(p.Name, p.Team.Name));

    public Write Deactivate(int age) => People.Where(p => p.Age > age).Update(p => new Person { Active = false });

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

    /// <summary>An insert without OrIgnore: the row written is the one more, whatever its columns.</summary>
    public Write<Tally> AddPetCounted(Utf8 name, long id) =>
        People.Where(p => p.Name == name).Then(p => Insert(Pets, new Pet { Id = id, OwnerId = p.Id, Name = p.Name, Legs = 3 }))
            .Select(p => new Tally(p.Id, p.Pets.Count(x => x.Legs > 2), p.Pets.Any(x => x.Name == p.Name)));

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
