using System.Linq;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>
/// A provider that declares only what <see cref="IDataProvider{TSelf, TStatement, TCommand}"/> requires, as the SQLite
/// one does: queries that use what it leaves to the interface's defaults still compile. C# calls a static virtual
/// member's default only through a type parameter, so generated code naming the provider type for one it doesn't
/// declare fails to build (CS0117).
/// </summary>
public sealed class MinimalProviderTests
{
    const string Provider = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using CodeFusion;
        using CodeFusion.Data;

        public sealed class MiniStatement(string sql) { public string Sql { get; } = sql; }

        public readonly ref struct MiniCommand : IDataCommand<MiniStatement>
        {
            public void Int64(long value) { }
            public void Int32(int value) { }
            public void Boolean(bool value) { }
            public void Double(double value) { }
            public void Text(ReadOnlySpan<byte> utf8) { }
            public void Text(ReadOnlySpan<char> text) { }
            public void Timestamp(Timestamp value) { }
            public void Null() { }
            public void BeginTextArray() { }
            public void Element(ReadOnlySpan<byte> utf8) { }
            public void EndTextArray() { }
            public void Then(MiniStatement statement) { }
            public PendingCommand Hold() => default;
            public void Discard() { }
        }

        [SqlDialect("mini")]
        public readonly struct Mini : IDataProvider<Mini, MiniStatement, MiniCommand>
        {
            public static MiniStatement Statement(string sql, params ReadOnlySpan<DataType> parameters) => new(sql);
            public static MiniCommand Read(DataContext context, MiniStatement statement) => default;
            public static MiniCommand Write(DataContext context, MiniStatement statement) => default;
            public static ValueTask<DataResult> Run<TReader>(in PendingCommand command, object? state, CancellationToken aborted) where TReader : IRowReader => default;
            public static void Discard(in PendingCommand command) { }
        }

        """;

    [Fact]
    public void QueriesOverAProviderThatDeclaresOnlyWhatItMustCompile()
    {
        string source = Provider + """
            namespace MiniApp.Data
            {
                using System.Linq;
                using System.ComponentModel.DataAnnotations.Schema;
                using CodeFusion;
                using CodeFusion.Data;

                public sealed class Item
                {
                    public long Id { get; init; }
                    public int Count { get; init; }
                }

                public readonly record struct ItemRow(long Id, int Count);

                public sealed partial class MiniDb : DataContext<global::Mini>
                {
                    public Table<Item> Items => Table<Item>();

                    [RunsAlone]
                    public One<ItemRow> ItemAlone(long id) => Items.Where(i => i.Id == id).Select(i => new ItemRow(i.Id, i.Count)).One();

                    public Write SetCount(long id, int count) => Items.Where(i => i.Id == id).Update(i => new Item { Count = count });
                }
            }
            """;
        var result = GeneratorHarness.Run(source);
        Assert.True(result.Generator.IsEmpty && result.Errors.IsEmpty, string.Join("\n", result.Generator.Concat(result.Errors).Take(10)));
        // and the queries were generated: each statement made by the provider, each method intercepted
        Assert.Contains("global::Mini.Statement(", result.Generated);
        Assert.Contains("ItemAlone", result.Generated);
        Assert.Contains("SetCount", result.Generated);
        Assert.DoesNotContain("Mini.Alone(", result.Generated);
        Assert.DoesNotContain("Mini.KeyedUpdate(", result.Generated);
    }
}
