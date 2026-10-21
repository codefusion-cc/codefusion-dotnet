# CodeFusion.Http

An HTTP/1.1 server for .NET 10 that allocates nothing per request, and endpoints written like ASP.NET minimal APIs
that a source generator compiles into the same zero-allocation code. Route values, the query string and JSON bodies
are read over the request's own bytes; replies are written straight into the connection's buffer.

```bash
dotnet add package CodeFusion.Http
```

## Endpoints

```csharp
using CodeFusion;
using CodeFusion.Http;

var builder = HttpApp.CreateBuilder(args);          // HOST and PORT from the environment
builder.AddJwtAuth(HttpAppBuilder.Setting("JWT_SECRET")!); // at least 32 bytes; tokens live 30 days unless set
builder.AddSingleton(new Catalog());
var app = builder.Build();

var api = app.MapGroup("/api");
api.MapGet("/articles/{slug}", static (Utf8 slug, Page page, Viewer viewer, Catalog catalog) =>
        catalog.Find(slug) is { } a ? Reply.Ok(a.View(viewer.Id)) : Reply.NotFound(Errors.ArticleNotFound))
   .AuthOptional();
api.MapPost("/users/login", static async (LoginBody body, Users users, Jwt jwt) =>
{
    long id = await users.Check(body.Email, body.Password);
    return id > 0 ? Reply.Ok(new UserView(body.Email, jwt.Issue(id))) : Reply.Unauthorized(Errors.WrongLogin);
});
app.Run();                                          // until SIGTERM, then the requests in flight finish

[FromQuery] partial struct Page
{
    [DefaultValue(20), Range(1, 100)] public long Limit;
    public long Offset;
    static ErrorReply Invalid => Errors.BadPage;    // a number that isn't valid gets this 422
}

[JsonEnvelope("user")] partial struct LoginBody     // {"user": {"email": ..., "password": ...}}
{
    public Utf8 Email;                              // required: present and not empty
    public Utf8 Password;
}

[JsonEnvelope("user")] readonly record struct UserView(Utf8 Email, Utf8 Token);
```

A handler is a static lambda or a static method. Its parameters are bound by kind: `Utf8`, `long` and `int` are route
values; a `[FromQuery]` struct is the query string; any other struct is the JSON body; `Viewer` is the user id in the
`Authorization: Token <jwt>` header (0 when there is none; an invalid one is a 401); classes are the services
registered on the builder, singletons or one per connection (`IRequestScoped`); `IResponse`, `HttpExchange` and
`CancellationToken` (the client has gone) are the connection's. It returns a `Reply<T>` (`Reply.Ok`, `Reply.Created`,
or any `Reply` such as `Reply.NotFound(error)`), a `Reply`, an `ErrorReply`, or any struct implementing `IReply`; an
`async` lambda returns them through a pooled `Pending<T>` with no attribute needed.

A `Utf8` is a view of bytes: of the request, or of the request's arena when it had to be decoded. It is valid until the
response has been sent, so the analyzer refuses to let a class keep one.

The JWT secret must be at least 32 bytes of UTF-8. A deployment handed a shorter fixed one (the Conduit contest's is
26 bytes) passes `AddJwtAuth(secret, lifetime, allowShortSecret: true)`. Its tokens are still standard HS256, but a
short secret can be guessed offline from any one token, and whoever finds it can sign as any user (SECURITY.md).

`JwtOptions` holds the same settings and the header's scheme, `Token` unless set. The header is the scheme, one space
and the token, once surrounding spaces and tabs are trimmed; the scheme is matched exactly, case included:

```csharp
builder.AddJwtAuth(secret, new JwtOptions { Scheme = "Bearer", AllowShortSecret = true });
```

Tokens can carry string claims beside `sub`. The app declares them once, every token is checked for them, and a
handler reads each one as a `Utf8` view of the token's payload, decoded into the request's arena:

```csharp
var auth = new JwtOptions { Scheme = "Bearer" };
auth.Claims.Add(new JwtClaim("username") { MaxBytes = 64 });  // required unless Required = false
builder.AddJwtAuth(secret, auth);
// ...
app.MapPost("/posts", static (NewPost body, Viewer viewer, Posts posts) =>
        posts.Create(viewer.Id, viewer.Claim("username"u8), body.Body))
   .AuthRequired();
```

A token is invalid when its signature, header, JSON, `exp` or `nbf` fails. When those pass, its payload is still
invalid if `sub` isn't a positive integer string, or a declared claim is missing (when required), isn't a string, isn't
valid text, or is outside `MinBytes` and `MaxBytes` once unescaped; so is one whose `exp` or `nbf` is too large to be a
finite number (`1e400`). If a claim appears twice, the last one counts, and
any bad copy makes the payload invalid. `viewer.Claim(name)` throws for a name that wasn't declared, and for any name
on a `Viewer` made by hand, which declares none; it gives an empty value for an anonymous viewer, and `TryGetClaim`
tells an absent optional claim from an empty one. A `Viewer` used after its request throws, from any thread.
`jwt.Issue(userId, username)` issues a token carrying the claims, and refuses values the verifier would refuse.

The generator replaces each `MapGet`/`MapPost`/`MapPut`/`MapDelete` call with an endpoint that binds the parameters
and writes the reply with no reflection and nothing looked up per request. Routes are literal templates; a literal
segment wins over a parameter, methods ignore case, HEAD runs the GET endpoint and sends its head without the body,
and anything no route matches is a 404 with
`builder.Errors.NotFound`'s body. The app's other bodies (401, 422, 500 and the server's own 400, 413, 414, 431) are on
`builder.Errors` too.

A 401 has one body, `Errors.Unauthorized`, unless the app gives a reason its own. Each reason left null answers as
before:

```csharp
builder.Errors.MissingToken = Errors.MissingToken;          // no header, or not "Bearer <token>"
builder.Errors.InvalidToken = Errors.InvalidToken;          // forged, malformed, not HS256, expired
builder.Errors.InvalidTokenPayload = Errors.InvalidPayload; // genuine, but sub or a declared claim breaks its rule
```

`InvalidTokenPayload` falls back to `InvalidToken`, then to `Unauthorized`. A header that is present but carries no
valid token is a 401 on an `AuthOptional` endpoint too; only a request with no header at all is anonymous.

A body's failures can be told apart the same way. `Errors.InvalidBody` (422) answers JSON of another shape (an
array, a missing envelope) and a field its struct names no reply for. Two more replies are null unless set:

```csharp
builder.Errors.MalformedBody = Errors.Malformed; // not JSON: cut short, trailing bytes, a bad escape, past depth 64
builder.Errors.MissingBody = Errors.NoBody;      // no body at all, or Content-Length: 0
```

A null `MissingBody` answers as `MalformedBody` does, and a null `MalformedBody` as before: the struct's own
`static ErrorReply InvalidBody`, then `Errors.InvalidBody`. A reason the app sets wins over a struct's own reply,
which keeps answering the bodies of another shape.

A `long` or `int` route value is ASCII digits that fit its type, within its `[Range]` if it has one. One that isn't
gets `Errors.InvalidRouteValue`, or the endpoint's own reply, and `Errors.NotFound` when neither is set. It is checked
after auth, so a request with no token gets the 401 whatever its route holds:

```csharp
builder.Errors.InvalidRouteValue = Errors.InvalidPostId;  // 400 "invalid post id"
app.MapPost("/posts/{id}/like", static ([Range(1, long.MaxValue)] long id, Viewer viewer, Likes likes) =>
        likes.Add(viewer.Id, id))
   .AuthRequired();
app.MapGet("/users/{id}", static ([Range(1, long.MaxValue)] long id, Users users) => users.Find(id))
   .WithInvalidRouteValue(Errors.InvalidUserId);
```

A body or `[FromQuery]` struct is bound through its public fields and its public properties with a public setter,
and nothing else, so a request can't set a member the struct keeps to itself. A number's `[Range]` holds in the body
as in the query, in each of the attribute's forms, exclusive ends included.

These are build errors (CFH0109 a warning), from the package's analyzers, in every file (one named `*.g.cs` too):

| | |
|---|---|
| CFH0001 | a handler lambda that isn't static (fix: add `static`), or an instance method |
| CFH0002 | a `string` parameter (fix: take a `Utf8`) |
| CFH0003 | a reply the generator can't send, such as `Task<T>` or a type with no JSON form |
| CFH0004 | a `Utf8` (or `RawJson`, `Utf8List`, `Viewer`, `ArenaBytes`) kept by a class: in a field, an auto-property, or a primary-constructor parameter one of its members uses; or by a struct's static field or auto-property. A type that only takes one in, such as `Func<Utf8, bool>` or `IEqualityComparer<Utf8>`, keeps none. A CodeFusion.Data entity's instance members are exempt, since it is never built: a class a data context has a `Table<T>` for, and its base classes, or one marked `[Table]` in an assembly that references CodeFusion.Data and has no data context. A class that derives from one without being an entity is reported for what it inherits |
| CFH0005 | LINQ inside a handler |
| CFH01xx | a Map call the generator can't compile: a route that isn't a literal template, a parameter it can't bind (a `[Range]` on a `Utf8` route value, or one that leaves a `long` or `int` route value nothing), a struct that isn't `partial`, a required member its binder can't set, a handler held as a delegate it can't name (more than 16 parameters, a default value or `params`), a call it can't intercept |
| CFH0109 | an internal field or settable auto-property of a body or `[FromQuery]` struct, which the binder leaves at its default: make it public to bind it, or private or `[JsonIgnore]` to say it isn't. Silence one at the member with `#pragma warning disable CFH0109` or `[SuppressMessage("CodeFusion", "CFH0109")]`, or all with `NoWarn` |

## The server underneath

The endpoints run on `HttpServer`, which can also be used directly with a handler of its own:

```csharp
using System.Net;
using System.Text.Json;
using CodeFusion.Http;

var server = new HttpServer(new HttpServerOptions { EndPoint = new IPEndPoint(IPAddress.Any, 8080) }, static x =>
{
    // One JSON writer per connection, kept in State and pointed at each new response.
    if (x.State is not Utf8JsonWriter w) x.State = w = new Utf8JsonWriter(x);
    else w.Reset(x);

    if (x.Method == RequestMethod.Get && x.Path.SequenceEqual("/hello"u8))
    {
        w.WriteStartObject();
        w.WriteString("hello"u8, "world"u8);
        w.WriteEndObject();
    }
    else x.Status = 404;

    w.Flush();
    return default; // done without waiting: the response goes out now
});

await server.RunAsync(shutdownTimeout: TimeSpan.FromSeconds(10)); // until SIGTERM or Ctrl+C
```

A handler that awaits (a database query, say) is an `async ValueTask` method marked
`[AsyncMethodBuilder(typeof(PooledValueTaskMethodBuilder))]`: its state lives in a pooled box instead of a new
object per call. `x.Aborted` fires when the client leaves, as in Kestrel: it closes the connection (a browser tab
closed, an HttpClient timeout, a proxy whose own client left), resets it, or the connection fails. After a close the
answer is still sent if the handler finishes anyway, since the client may only have closed its sending side; after a
reset it is dropped (`x.IsAborted`). A close behind a pipelined request is seen on Linux and macOS only.

## Zero allocations, measured

`AllocationTests` counts every byte the whole process allocates (`GC.GetTotalAllocatedBytes`) over thousands of
keep-alive requests after warm-up, with sync and async handlers, on 1 and 8 connections: 0 B per request.
`AppAllocationTests` does the same for generated endpoints: route values, the query, bodies with escapes and lists,
tokens issued into the arena, async handlers whose waits end on other threads, replies whose writes wait, 401 and 404,
and the feed sample (`FeedApp`): Bearer tokens with a claim read in the handler, each 401 reason, a malformed body and
no body, route values out of range before and after auth. A malformed body costs no exception either: one reader
binds and checks the body in a single pass. A new connection object (about 1 KB) is made only when the pool of 4,096
is empty.

Against Kestrel answering the same JSON (BenchmarkDotNet short run, load client in the same process, Apple M-series
with 8 cores shared with other work; CPU per request is the whole process's, the client's share included):

| Connections | Kestrel req/s | CodeFusion.Http req/s | Kestrel CPU/req | CodeFusion.Http CPU/req | Kestrel alloc/req | CodeFusion.Http alloc/req |
|---|---|---|---|---|---|---|
| 1 | 17,230 | 18,294 | 87 µs | 81 µs | 138 B | 0 |
| 64 | 120,633 | 125,730 | 44 µs | 38 µs | 138 B | 0 |
| 1,024 | 128,117 | 149,368 | 43 µs | 37 µs | 138 B | 0 |

An idle keep-alive connection holds no buffers. With 2,000 of them open, the server kept about 2.3 KB of GC heap per
connection and Kestrel about 17.6 KB (`HttpIdleBenchmarks`, measured on the contest server before extraction).

## Limits

HTTP/1.1 only, no TLS: put it behind a proxy that terminates TLS when it faces the internet. Request and response
size limits, timeouts, minimum data rates and the connection limit are on `HttpServerOptions`; the defaults are
Kestrel's, apart from a 10-minute keep-alive and a 1 MB body. A body must keep up 240 B/s, and so must a response the
client reads slowly, after a 5 s grace period (`MinRequestBodyDataRate`, `MinResponseDataRate`; null turns either
off). Only a body's own bytes count, not chunk framing, and of a response what the client's TCP acknowledged. Each
byte pays for 1/rate seconds, but no more than `RequestTimeout` (or the grace period, if longer) ahead, so bytes sent
early can't buy a crawl later, and a client faster than the rate between any two of its sends or reads is never cut
off. A new connection's first request must arrive within `RequestTimeout`. A request's buffer doubles as its bytes
come, so a connection holds about what it sent, not what its Content-Length declares. `MaxConnections` is unlimited
unless set. SECURITY.md says what these do and don't bound.
