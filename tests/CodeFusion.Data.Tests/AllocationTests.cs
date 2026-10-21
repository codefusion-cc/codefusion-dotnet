using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Conduit.Data;
using Xunit;

namespace CodeFusion.Data.Tests;

/// <summary>
/// Once warm, a compiled query allocates nothing: written as a reply into a response, or awaited into a request's
/// arena, reads and writes alike. The whole process is counted (GC.GetTotalAllocatedBytes), the provider's loop
/// threads included; the response, its arena and the context are the connection's, reused for every request.
/// </summary>
[Collection(nameof(ConduitCollection))]
public sealed class AllocationTests(ConduitFixture fixture)
{
    static bool Debug() => typeof(DataContext).Assembly.GetCustomAttributes(typeof(DebuggableAttribute), false)
        is [DebuggableAttribute { IsJITOptimizerDisabled: true }];

    static Utf8 U(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return new Utf8(b, 0, b.Length);
    }

    sealed class Connection(ConduitFixture fixture, bool writes)
    {
        public readonly FakeResponse Response = new();
        public readonly ConduitDb Db = writes ? fixture.Writes.CreateContext<ConduitDb>() : fixture.Source.CreateContext<ConduitDb>();

        public void Next()
        {
            Response.Next();
            Db.Bind(Response);
        }
    }

    static readonly Utf8 Slug = U("how-to-train-your-dragon"), Missing = U("no-such-article"), Email = U("bob@example.com"), Dragons = U("dragons"), Title = U("Allocation free: an article");
    static readonly Utf8List KnownTags = Texts.List("training", "dragons", "dragons");
    static readonly Utf8 Dave = U("dave");

    /// <summary>One request of each kind a connection serves, the reads as replies and awaited.</summary>
    static async Task<long> Reads(Connection c, int requests)
    {
        long bad = 0;
        for (int i = 0; i < requests; i++)
        {
            c.Next();
            switch (i % 7)
            {
                case 0:
                    await c.Db.ArticleBySlug(Slug, 1).WriteAsync(c.Response).ConfigureAwait(false);
                    if (c.Response.Status != 200) bad++;
                    break;
                case 1:
                    await c.Db.AllArticles(new Paging(20, 0), 1).WriteAsync(c.Response).ConfigureAwait(false);
                    if (c.Response.Status != 200) bad++;
                    break;
                case 2:
                    await c.Db.CommentsOf(Slug, 1).WriteAsync(c.Response).ConfigureAwait(false);
                    if (c.Response.Status != 200) bad++;
                    break;
                case 3:
                    var user = await c.Db.UserByEmail(Email);
                    if (!user.Found || user.Value.Id != 2) bad++;
                    break;
                case 4:
                    await c.Db.ArticleBySlug(Missing, 1).WriteAsync(c.Response).ConfigureAwait(false);
                    if (c.Response.Status != 404) bad++;
                    break;
                case 5:
                    await c.Db.ArticlesByTag(Dragons, new Paging(10, 0), 0).WriteAsync(c.Response).ConfigureAwait(false);
                    if (c.Response.Status != 200) bad++;
                    break;
                default:
                    var article = await c.Db.ArticleBySlug(Slug, 0);
                    if (!article.Found || article.Value.FavoritesCount != 2) bad++;
                    break;
            }
        }
        return bad;
    }

    /// <summary>Writes that change nothing the other tests read: an update to the same values, a favorite toggled twice,
    /// new articles of dave's, whose slugs no test reads, tags there already, and carol following dave and not again.
    /// Seven kinds: the runs are multiples of seven, so each toggle ends as it began.</summary>
    static async Task<long> Writes(Connection c, int requests)
    {
        long bad = 0;
        for (int i = 0; i < requests; i++)
        {
            c.Next();
            switch (i % 7)
            {
                case 0:
                    await c.Db.UpdateUser(4, null, null, null, null, null).WriteAsync(c.Response).ConfigureAwait(false);
                    if (c.Response.Status != 200) bad++;
                    break;
                case 1:
                    await c.Db.Favorite(2, Slug).WriteAsync(c.Response).ConfigureAwait(false);
                    if (c.Response.Status != 200) bad++;
                    break;
                case 2:
                    var r = await c.Db.Unfavorite(2, Slug);
                    if (!r.Found) bad++;
                    break;
                case 3:
                    // a new article of dave's, its slug made by the text functions in SQL
                    await c.Db.CreateArticle(4, Title, Title, Title, Utf8List.Empty).WriteAsync(c.Response).ConfigureAwait(false);
                    if (c.Response.Status != 200) bad++;
                    break;
                case 4:
                    // tags there already: the insert of a list's texts skips them all
                    await c.Db.AddTags(KnownTags).WriteAsync(c.Response).ConfigureAwait(false);
                    if (c.Response.Status != 204) bad++;
                    break;
                case 5:
                    // a write from the row found, and the row given back
                    await c.Db.Follow(3, Dave).WriteAsync(c.Response).ConfigureAwait(false);
                    if (c.Response.Status != 200) bad++;
                    break;
                default:
                    var unfollowed = await c.Db.Unfollow(3, Dave);
                    if (!unfollowed.Found || unfollowed.Value.Following) bad++;
                    break;
            }
        }
        return bad;
    }

    [Fact(Timeout = 120_000)]
    public async Task QueriesAllocateNothingOnceWarm()
    {
        TestDb.Require();
        Assert.SkipWhen(Debug(), "Debug builds make every async state machine an object: run in Release");
        const int Callers = 4, ReadsEach = 3500, WritesEach = 280;
        var connections = new Connection[Callers];
        for (int i = 0; i < Callers; i++) connections[i] = new Connection(fixture, writes: i % 2 == 1);

        async Task<long> Run(int reads, int writes)
        {
            var tasks = new Task<long>[Callers];
            for (int i = 0; i < Callers; i++)
            {
                var c = connections[i];
                // off xunit's synchronization context, so continuations run where the loops complete them
                tasks[i] = Task.Run(async () => c.Db.Source == fixture.Writes ? await Writes(c, writes).ConfigureAwait(false) + await Reads(c, reads).ConfigureAwait(false)
                    : await Reads(c, reads).ConfigureAwait(false));
            }
            long failures = 0;
            foreach (var t in tasks) failures += await t.ConfigureAwait(false);
            return failures;
        }

        Assert.Equal(0, await Run(700, 70)); // warm: pools, slots, statements on every connection, JIT
        long Measure(Func<Task<long>> run, out long failures)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            failures = run().GetAwaiter().GetResult();
            return GC.GetTotalAllocatedBytes(precise: true) - before;
        }
        // twice as many queries must not allocate more: what a run allocates is the test's own tasks, not the queries'.
        // Each size is measured twice and the smaller kept: the runtime's own odd few hundred bytes (a timer, the
        // thread pool) land in one run now and then, where a query's allocation would be in both
        long once = Math.Min(Measure(() => Run(ReadsEach, WritesEach), out long failedOnce), Measure(() => Run(ReadsEach, WritesEach), out long failedOnceAgain));
        long twice = Math.Min(Measure(() => Run(2 * ReadsEach, 2 * WritesEach), out long failedTwice), Measure(() => Run(2 * ReadsEach, 2 * WritesEach), out long failedTwiceAgain));
        Assert.Equal(0, failedOnce + failedOnceAgain + failedTwice + failedTwiceAgain);
        int operations = Callers * ReadsEach + Callers / 2 * WritesEach;
        string line = $"{once} bytes for {operations} queries, {twice} bytes for {2 * operations} ({(double)(twice - once) / operations:F3} B per extra query)";
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        // CODEFUSION_TEST_REPORT names a file the measurement is added to, for the notes
        if (Environment.GetEnvironmentVariable("CODEFUSION_TEST_REPORT") is { Length: > 0 } report)
            System.IO.File.AppendAllText(report, $"QueriesAllocateNothingOnceWarm: {line}\n");
        // the callers' Task.Run and async boxes (a few hundred bytes each) are the test's own: under 1 byte per query,
        // and none more for the extra queries (a little slack for the runtime's own timers)
        Assert.True(once < operations, line);
        Assert.True(twice - once < operations / 20, line);
    }
}
