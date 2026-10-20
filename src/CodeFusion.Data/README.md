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
strings too, and one where SQL expects a name, as after ORDER BY or FROM, is a build error); a hole calling a
`[SqlTemplate]` method is its SQL, the way to share a fragment.

Entities follow EF's conventions (snake_case names, `AuthorId` with `Author`, `Many<T>` for the other side, and EF's
attributes) and are never built at run time. Views are `record struct`s of `Utf8`, `Utf8?`, `Timestamp`,
`RawJson`, numbers, bools and nested views, written as JSON by CodeFusion.Core's rules.

Calls are taken over in the project that defines the context; a project referencing CodeFusion.Data by project
reference adds the generator as an analyzer and `CodeFusion.Data.Generated` to `InterceptorsNamespaces` (the
package does both itself).
