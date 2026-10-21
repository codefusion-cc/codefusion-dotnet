# CodeFusion.Security.Bcrypt

bcrypt for .NET 8, 9 and 10, tested on .NET 11 too. A check allocates no memory at all, and a new hash only its
string, or nothing when it's written as bytes. Up to five hashes run interleaved on one thread, each for a quarter to two fifths of the CPU time of a hash
alone, and `BcryptQueue` gives a server that batching without blocking its thread pool.

Hashes are interchangeable with OpenBSD, Go, PHP, Python's bcrypt and BCrypt.Net-Next, including BCrypt.Net-Next's
enhanced (SHA-2 prehashed) ones.

```bash
dotnet add package CodeFusion.Security.Bcrypt
```

## Three ways to check a password

All three give the same answers and the same hashes. They differ in how many hashes run together and on which
thread. bcrypt spends most of its time waiting for memory: each of its rounds waits for four table lookups from the
round before. Running several hashes interleaved on one thread (each one a "lane") lets the core work on one hash
while another waits. Every hash is still a full bcrypt of its own cost.

### One check: `Bcrypt.Verify`

One password against one stored hash, on the calling thread, which it holds for the whole hash (47 ms at cost 10 on
an Intel Xeon). For scripts, desktop apps, background jobs, or anything that checks a password now and then.

```csharp
using CodeFusion.Security;

string hash = Bcrypt.Hash(password, cost: 12);   // at sign-up: "$2b$12$..."
bool ok = Bcrypt.Verify(password, hash);         // at sign-in: false for a wrong password or a malformed hash, never throws

if (ok && Bcrypt.PasswordNeedsRehash(hash, 12))  // a hash made with a lower cost: upgrade it while you have the password
    hash = Bcrypt.Hash(password, 12);
```

### Several at once: lanes

Give `Verify` spans and it checks them up to five at a time, interleaved on the calling thread. For when you already
hold a batch, such as a migration that checks many stored hashes.

```csharp
string[] passwords = ["alice-pw", "bob-pw", "carol-pw", "dave-pw", "erin-pw"];
string[] hashes = [aliceHash, bobHash, carolHash, daveHash, erinHash];
var results = new bool[5];

Bcrypt.Verify(passwords, hashes, results);   // results[i]: whether passwords[i] matches hashes[i]
```

What that buys at cost 10 on an Intel Xeon:

| | Total | Per hash |
|---|---|---|
| 5 checks one after another | 5 × 47.2 = 236 ms | 47.2 ms |
| 5 checks as 5 lanes | 61 ms | 12.3 ms |

Five times the work in 1.3 times the time. The other side: each of the five answers arrives after 61 ms instead of
47 ms.

### On a server: `BcryptQueue`

Sign-ins reach a server one at a time, on different request threads, so there's no batch to hand over. The queue
makes one:

- It runs bcrypt on its own threads (one per processor by default). `VerifyAsync` queues the check and returns at
  once, so the request thread goes back to the pool while bcrypt runs.
- A worker takes up to five waiting checks and runs them as one interleaved run. Each caller's `await` completes as
  soon as its own lane is done.
- Calling `Bcrypt.Verify` in the endpoint instead would hold a pool thread for each whole hash: a burst of sign-ins
  takes every pool thread and stalls all other requests until the pool grows. And each check would run alone, at
  three times the CPU.

```csharp
builder.Services.AddSingleton(new BcryptQueue(new BcryptQueueOptions
{
    MaxQueued = 1024, // past that, calls fail at once with BcryptQueueFullException (TryVerifyAsync says Busy): answer 503
}));

string dummyHash = Bcrypt.Hash(Guid.NewGuid().ToString(), 12);

app.MapPost("/login", async (Login body, BcryptQueue bcrypt, Users users, CancellationToken aborted) =>
{
    var user = await users.Find(body.Email);
    // Check a dummy hash for an unknown email, so timing doesn't tell which accounts exist.
    bool ok = await bcrypt.VerifyAsync(body.Password, user?.PasswordHash ?? dummyHash, aborted);
    return ok && user is not null ? Results.Ok(Token(user)) : Results.Unauthorized();
});

app.MapPost("/signup", async (Signup body, BcryptQueue bcrypt, Users users) =>
{
    if (!Bcrypt.PasswordFits(body.Password)) return Results.BadRequest("Use at most 72 bytes.");
    string hash = await bcrypt.HashAsync(body.Password, cost: 12);
    await users.Create(body.Email, hash);
    return Results.Ok();
});
```

Five sign-ins that arrive together cost about 14 ms each through the queue on an Intel Xeon.

- Await each `ValueTask` from `VerifyAsync` or `HashAsync` exactly once. It is backed by a pooled request, which is
  why the queue allocates nothing per call.
- A cancelled token (say, the client went away) ends a waiting call at once, and its hash never runs.

`BcryptQueueOptions`:

| Option | Default | |
|---|---|---|
| `Threads` | one per processor | worker threads; fewer leave CPU for the server's other work |
| `Lanes` | 5 | the most hashes in one run, 1 to 5 |
| `BatchWindow` | 0 | how long a worker that took a hash waits for others to join its run |
| `MaxQueued` | 256 per thread | waiting hashes allowed before calls are refused |
| `Priority` | normal | the workers' thread priority |

With no window, a worker runs whatever is waiting right away. When sign-ins are rare each runs alone, which costs
nothing because the CPU is idle; when they pile up they share runs on their own. A window helps when sign-ins arrive
tens of milliseconds apart and CPU is short: a server with 200 ms spent a third of the CPU per sign-in, at the cost of
up to 200 ms of latency.

### Which one

| Situation | Use |
|---|---|
| A check now and then, or not a server | `Bcrypt.Verify(password, hash)` |
| A batch you already hold | `Bcrypt.Verify(passwords, hashes, results)` |
| A web server's sign-in and sign-up | `BcryptQueue.VerifyAsync` and `HashAsync` |
| A server that reads passwords and keeps hashes as bytes | the byte overloads below |

## Bytes instead of strings

A server reads the password off the wire as bytes and may keep the hash as bytes too. The overloads that take spans
skip both strings, and none of them allocates:

```csharp
bool ok = Bcrypt.Verify(passwordUtf8, storedHash);         // two ReadOnlySpan<byte>
int written = Bcrypt.Hash(passwordUtf8, 12, destination);  // 60 ASCII bytes into a Span<byte>

bool ok = await bcrypt.VerifyAsync(passwordUtf8, storedHash, aborted);
BcryptHash hash = await bcrypt.HashAsync(passwordUtf8, 12); // the 60 bytes, held inline
hash.CopyTo(row.PasswordHash);
```

- The queue copies both spans before the call returns, so their buffers can be reused at once.
- The password's bytes are the key as they are. UTF-8 gives the same hashes as the string overloads, and other bytes
  are taken too, as Go takes them. A NUL counts, as it does in a string; C implementations stop at the first one.
  Over 72 bytes is refused, as with strings. The enhanced hashes take strings only.
- `BcryptHash` reads out with `CopyTo`, `TryFormat` (to bytes or chars) or `AsSpan`; `ToString` makes the string.

`TryVerifyAsync` and `TryHashAsync` report a full queue or a fired token as a status instead of an exception, so
overload costs no exception either:

```csharp
switch (await bcrypt.TryVerifyAsync(passwordUtf8, storedHash, aborted))
{
    case BcryptStatus.Match: return Results.Ok(Token(user));
    case BcryptStatus.Busy: return Results.StatusCode(503);
    case BcryptStatus.Cancelled: return Results.Empty; // the client went away
    default: return Results.Unauthorized(); // NoMatch, or a malformed hash
}

BcryptHashResult made = await bcrypt.TryHashAsync(passwordUtf8, 12);
if (made.Status == BcryptStatus.Hashed) made.CopyTo(row.PasswordHash); // CopyTo throws when there is no hash
```

## Passwords over 72 bytes

bcrypt reads at most 72 bytes of a password. Most libraries silently ignore the rest, so `"a" * 72 + "anything"`
matches `"a" * 72`. This one refuses instead: `Hash` throws `ArgumentException` and `Verify` returns false. Check
`Bcrypt.PasswordFits(password)` when a password is chosen to tell the user. To accept any length, use the enhanced
hash, which digests the password with SHA-384 first (compatible with BCrypt.Net-Next's `EnhancedHashPassword`):

```csharp
string hash = Bcrypt.EnhancedHash(password, 12);
bool ok = Bcrypt.EnhancedVerify(password, hash);
```

## Speed

Cost 10 on .NET 10, on three Google Cloud machines with 8 vCPUs each. Times are per hash on one thread; sign-ins a
second use the whole machine.

| | Intel Xeon 8581C (`c4-highcpu-8`) | AMD EPYC 9B45 (`c4d-highcpu-8`) | Google Axion, Arm (`c4a-highcpu-8`) |
|---|---|---|---|
| BCrypt.Net-Next 4.2.1 | 60.0 ms | 52.8 ms | 75.0 ms |
| Openwall crypt_blowfish 1.3, C (gcc -O3) | 46.8 ms | 44.4 ms | 57.9 ms |
| **This package**, one at a time | **47.2 ms** | **44.0 ms** | **56.3 ms** |
| **This package**, interleaved | **12.3 ms** (5 lanes) | **11.4 ms** (5 lanes) | **20.7 ms** (4 lanes) |
| Sign-ins a second, BCrypt.Net-Next on 8 threads | 118 | 132 | 112 |
| Sign-ins a second, **`BcryptQueue`** | **325** (4 workers) | **413** (8 workers) | **347** (8 workers) |

- One hash at a time, this package is level with Openwall's C and 17 to 25% faster than BCrypt.Net-Next.
- Interleaved, a hash takes a fifth to just over a quarter of BCrypt.Net-Next's time, and the whole machine does
  2.8 to 3.1 times as many sign-ins.
- .NET 8, 9 and 11 measured within 2% of .NET 10 on every machine.

## Memory

Checking a password allocates nothing on the heap, so sign-ins give the garbage collector no work, however many
there are. Bytes allocated per call, measured over 2,000 calls (the cost doesn't change them):

| | This package | BCrypt.Net-Next 4.2.1 |
|---|---|---|
| `Verify` | **0 B** | 5,232 B |
| `EnhancedVerify` | **0 B** | 5,856 B |
| `BcryptQueue.VerifyAsync` | **0 B** | |
| `Hash` | 144 B: the returned string | 5,416 B |
| `Verify` and `Hash` from bytes | **0 B** | |
| `BcryptQueue.VerifyAsync`, `HashAsync`, `TryVerifyAsync` and `TryHashAsync` from bytes | **0 B** | |

At 1,000 sign-ins a second, BCrypt.Net-Next makes 5 MB of garbage a second; this package makes none.
BCrypt.Net-Next allocates a new 4 KB copy of the Blowfish tables for every hash. This package does the following:

- It keeps that state on the stack of the thread that runs the hash: 4 KB for one hash, 21 KB for five lanes. The
  state is wiped when the hash is done.
- It reads the password into a stack buffer instead of a byte array. An enhanced hash of a password over 85
  characters borrows a buffer from the shared `ArrayPool`; past 16,384 characters it also allocates a small
  `IncrementalHash`.
- The queue reuses its requests. A new queue allocates about 4 KB, and each request object is made once, the first
  time that many calls wait at the same moment. After that it is reused: the pool keeps up to
  `MaxQueued + Threads × Lanes` of them, at most 4,096.
- The tests assert these numbers exactly, so an allocation that creeps in fails the build.

## Benchmark method

Each machine ran the BenchmarkDotNet 0.16 suite in `benchmarks/` (the times above are its means) and the throughput
benchmark, `dotnet run -c Release -f net10.0 --project benchmarks/CodeFusion.Security.Bcrypt.Benchmarks -- throughput 20`.
The throughput benchmark checks for 20 seconds after a 3-second warm-up: BCrypt.Net-Next on one thread per vCPU, and
`BcryptQueue` kept full by its callers, with 4 or 8 workers (the better is shown). Openwall's `crypt_blowfish.c` was
built with gcc -O3 and timed as the median of 25 runs. Every implementation was checked against Go's hash of the same
password first. The machines were spot instances in europe-west1 on 19 October 2026.

## How the loop is built

A Roslyn source generator, [LanesGenerator.cs](https://github.com/codefusion-cc/bcrypt-dotnet/blob/main/src/CodeFusion.Security.Bcrypt.Generators/LanesGenerator.cs), writes
the loop for 1 to 5 lanes while the library compiles. The build also saves the result under `obj/*/generated` to
read. Every lane's two halves stay in local variables, so they live in registers. Each round XORs its key word in
before the table lookups return, which keeps that XOR off the path every round waits on (6 to 10% faster).

The table lookups come in two forms, chosen when the code is compiled for the CPU:

- **x64:** every lookup is `st[constant + index]`. x64 loads `[base + index*4 + constant]` in one instruction, so all
  five lanes share one base register. This form matches clang and gcc.
- **arm64:** arm64 has no load that takes a scaled index and a constant together, so the same source cost four extra
  instructions per lookup, on the path each round waits on. The arm64 form keeps a pointer to each of the 20 tables,
  so a lookup is a shift and a load, as clang compiles it. That made hashes 11 to 22% faster on the M1. On x64 the 20
  pointers don't fit its 16 registers and that form was slower, so x64 keeps the first.

What still separates the arm64 form from clang is the .NET 10 JIT itself: it extracts each byte with two
instructions where clang uses one `ubfx`, and it moves more values through the stack between rounds.

## From BCrypt.Net-Next

| BCrypt.Net-Next | This library |
|---|---|
| `HashPassword(password)`, `HashPassword(password, workFactor)` | `Hash(password)`, `Hash(password, cost)` |
| `HashPassword(password, salt)` | `Hash(password, salt)` |
| `Verify(password, hash)` | `Verify(password, hash)` |
| `EnhancedHashPassword(password, workFactor, hashType)` | `EnhancedHash(password, cost, prehash)` |
| `EnhancedVerify(password, hash, hashType)` | `EnhancedVerify(password, hash, prehash)` |
| `GenerateSalt(workFactor, revision)` | `GenerateSalt(cost, revision)` |
| `PasswordNeedsRehash(hash, cost)` | `PasswordNeedsRehash(hash, cost)` |
| `InterrogateHash(hash)` | `InterrogateHash(hash)`, `TryInterrogateHash(hash, out info)` |
| `ValidateAndReplacePassword(...)` | `ValidateAndReplacePassword(...)`: null instead of an exception when the current password is wrong |
| `HashType.SHA256/SHA384/SHA512` | `BcryptPrehash.Sha256/Sha384/Sha512` |

What differs:

- New hashes are `$2b$` (BCrypt.Net-Next writes `$2a$`). Both libraries check either.
- Passwords over 72 bytes are refused, not cut (see above). An existing hash of such a password, made by a library
  that cut it, won't verify; rehash those users through `EnhancedHash`, or cut to 72 bytes yourself if you must.
- `$2x$` hashes are refused. They come from PHP's sign-extension bug, which BCrypt.Net-Next doesn't reproduce
  either: it checks them as `$2a$`, which is wrong for the passwords the bug affected.
- A malformed hash makes `Verify` return false; BCrypt.Net-Next throws.
- Costs below 4 are refused everywhere; BCrypt.Net-Next accepts 1 to 3 inside a salt string.

## Build

```bash
dotnet test --project tests/CodeFusion.Security.Bcrypt.Tests -c Release
dotnet run -c Release -f net10.0 --project benchmarks/CodeFusion.Security.Bcrypt.Benchmarks -- --filter '*'
```

The tests check the published vectors of OpenBSD, Openwall and pyca/bcrypt, every lane count against
BCrypt.Net-Next, the queue's cancellation, limits and pooling, and that a check allocates exactly 0 bytes.

## License

MIT. Blowfish's initial tables, the hexadecimal digits of pi, are computed at compile time by
[BlowfishTablesGenerator.cs](https://github.com/codefusion-cc/bcrypt-dotnet/blob/main/src/CodeFusion.Security.Bcrypt.Generators/BlowfishTablesGenerator.cs).
