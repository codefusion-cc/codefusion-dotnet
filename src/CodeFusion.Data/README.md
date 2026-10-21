# CodeFusion.Data

A data layer for .NET 10 shaped like a DbContext, whose LINQ queries are compiled to SQL at build time. A query
method's body is its definition: the package's source generator writes its SQL once, into the build, and takes
over every call to it, so at run time a query is a prepared statement, binary parameters, and each column copied
from the database's reply straight into the response's JSON. Nothing is allocated per query. Use it with a
provider package such as CodeFusion.Data.Postgres.

```csharp
public sealed partial class ConduitDb : DataContext<Postgres>
{
    public Table<Article> Articles => Table<Article>();

    public One<ArticleView> ArticleBySlug(Utf8 slug, long viewer) =>
        Articles.Where(a => a.Slug == slug).Select(a => ArticleView.Of(a, viewer)).One();

    public Page<ArticleListView> AllArticles(Paging page, long viewer) =>
        Articles.OrderByDescending(a => a.Id).Page(page, countUpTo: 1000).Select(a => ArticleListView.Of(a, viewer));
}

// in a handler: a reply, written as the rows arrive
return db.ArticleBySlug(slug, viewer.Id).OrNotFound(Errors.ArticleNotFound);

// or awaited: the row is copied into the request's arena
var user = await db.UserByEmail(email);
if (user.Failed) return user.Error!;
```

The SQL of every query is a constant (`ConduitDb.QuerySql.ArticleBySlug`). LINQ the translator can't compile is a
build error that points at `Sql<T>($"...")`, whose interpolation holes become parameters at build time (constant
strings too, and one where a parameter can't stand, as a table after FROM or a whole ORDER BY item, is a build
error); a hole calling a `[SqlTemplate]` method is its SQL, the way to share a fragment.

Text is changed by the functions of `Utf8Operators`, each SQL in a query and the same C# anywhere else:
`ToLowerAscii` and `ToUpperAscii` (A to Z only, `translate(x, 'A…Z', 'a…z')`, alike in every collation), `Trim`,
`TrimStart` and `TrimEnd` by a constant character, `Replace`, `Concat` of text or a number, and `ReplaceMatches`
(`regexp_replace`) with a constant pattern of the kind .NET and SQL read alike: literals and ASCII `[...]` classes,
each once or with `+`, and one `[^...]` class with `+`. The spec's slug rule:

```csharp
public static Utf8 WithId(Utf8 title, long id) =>
    title.ReplaceMatches("[^A-Za-z0-9]+", "-").ToLowerAscii().Trim('-').Concat("-"u8).Concat(id);
```

A list known when called is the rows of an insert through `AsRows()`, with Where, Distinct and one OrderBy before the
Select: `Insert(Tags, names.AsRows().Distinct().OrderBy(n => n).Select(n => new Tag { Name = n })).OrIgnore()` is
`INSERT INTO tags (name) SELECT DISTINCT n.v FROM unnest($1) n(v) ORDER BY n.v ON CONFLICT DO NOTHING`, alone or in
an insert's unit (`ThenIf`).

`Then` after a query's Where writes from each row found, in one statement, so one round trip, and the write and its
reply commit or fail together. Its Select gives back the row found of the smallest key (`ORDER BY` the key `LIMIT 1`;
every row found is written from); a missing row is a 404 and writes nothing:

```csharp
public Write<ProfileView> Follow(long viewer, Utf8 username) =>
    Users.Where(u => u.Username == username)
        .Then(u => Insert(Follows, new Follow { FollowerId = viewer, FolloweeId = u.Id }).OrIgnore())
        .Select(u => ProfileView.Of(u, viewer));
// WITH t AS (SELECT u.id, u.username, u.bio, u.image FROM users u WHERE u.username = $1),
//   w AS (INSERT INTO follows (follower_id, followee_id) SELECT $2, t.id FROM t
//     ON CONFLICT (follower_id, followee_id) DO NOTHING)
// SELECT t.username, t.bio, t.image, ($2 <> 0) FROM t WHERE <check of follows> ORDER BY t.id LIMIT 1
```

The write is an insert of `new T { ... }` or a delete (`Follows.Where(f => f.FolloweeId == u.Id).Delete()`, the
followee one of the ids found). The statement reads the snapshot it started with, from before the write, so the
Select reads the table written only through `Count` and `Any` of a navigation, which the generator corrects by the
write: `ProfileView.Of(u, viewer)` after the follow is followed, and an article's `Favorites.Count()` after a favorite
is the snapshot's count and one if the snapshot held no favorite of that key (written now, or by a concurrent request
the snapshot missed). After an insert with `OrIgnore` that needs the table's key, `[Key]` on each of its columns.
After a delete, the Select may not read a table whose rows lead to the table deleted from (a cascade could have
changed it). What the model can't see the statement checks first, in the catalog, whatever the Select reads
(the row found is read too): a rule or trigger on the table written or on any table the delete cascades into, key
after key, a foreign key by which the delete reaches a table read, and, for a corrected read, row security or a column
that may store another value than the one written, or compare it in a collation other than the default. Any of them
fails the statement with a message starting `CodeFusion: the Select after Then can't read`, and nothing is written.

A page of each row's children is `SelectMany` of a navigation, the child itself as its result, before one OrderBy of
the child's key and the page. Each source row's children come from a lateral join that takes, in the page's order,
only as many as the page reaches (its offset and its rows), so an index of the children by parent serves each group:

```csharp
public Page<ArticleListView> Feed(long viewer, Paging page) =>
    Follows.Where(f => f.FollowerId == viewer)
        .SelectMany(f => f.Followee.Articles, (f, a) => a)
        .OrderByDescending(a => a.Id)
        .Page(page, countUpTo: 1000)
        .Select(a => ArticleListView.InFeed(a, viewer));
// ... FROM follows f2 CROSS JOIN LATERAL (SELECT a2.id AS id FROM articles a2 WHERE a2.author_id = f2.followee_id
//   ORDER BY a2.id DESC LIMIT $2) x2 WHERE f2.follower_id = $1 ORDER BY x2.id DESC LIMIT 100 OFFSET $3 ...
```

A `Take` of the children (`f.Followee.Articles.OrderByDescending(a => a.Id).Take(3)`) is each group's own cap, and
the count counts only those.

Entities follow EF's conventions (snake_case names, `AuthorId` with `Author`, `Many<T>` for the other side, and EF's
attributes) and are never built at run time. Views are `record struct`s of `Utf8`, `Utf8?`, `Timestamp`,
`RawJson`, numbers, bools and nested views, written as JSON by CodeFusion.Core's rules.

Calls are taken over in the project that defines the context; a project referencing CodeFusion.Data by project
reference adds the generator as an analyzer and `CodeFusion.Data.Generated` to `InterceptorsNamespaces` (the
package does both itself).
