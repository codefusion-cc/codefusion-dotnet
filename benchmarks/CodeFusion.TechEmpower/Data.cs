using System.ComponentModel.DataAnnotations.Schema;
using CodeFusion;
using CodeFusion.Data;
using CodeFusion.Data.Postgres;

namespace CodeFusion.TechEmpower;

// TFB's tables, as its create-postgres.sql makes them: unquoted names, so world, fortune and randomnumber.

[Table("world")]
public sealed class World
{
    public int Id { get; init; }

    [Column("randomnumber")]
    public int RandomNumber { get; init; }
}

[Table("fortune")]
public sealed class Fortune
{
    public int Id { get; init; }
    public Utf8 Message { get; init; }
}

/// <summary>A World row as TFB's JSON has it: <c>{"id":3217,"randomNumber":2149}</c>.</summary>
public readonly record struct WorldRow(int Id, int RandomNumber);

public readonly record struct FortuneRow(int Id, Utf8 Message);

public sealed partial class TfbDb : DataContext<Postgres>
{
    public Table<World> Worlds => Table<World>();
    public Table<Fortune> Fortunes => Table<Fortune>();

    public One<WorldRow> WorldById(int id) =>
        Worlds.Where(w => w.Id == id).Select(w => new WorldRow(w.Id, w.RandomNumber)).One();

    /// <summary>Every fortune, in no order: the handler adds one and sorts, as TFB's rules say.</summary>
    public Many<FortuneRow> AllFortunes() =>
        Fortunes.Select(f => new FortuneRow(f.Id, f.Message)).Many();

    public Write SetRandomNumber(int id, int randomNumber) =>
        Worlds.Where(w => w.Id == id).Update(w => new World { RandomNumber = randomNumber });
}
