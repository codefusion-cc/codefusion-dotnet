# Engineering notes

How the repository keeps its packages correct: the build gates every src project compiles under, the runtime checks
the tests rely on, the fuzzing harness and what it found, and the mutation-testing results.

Scripts are C# only: .NET 10 file-based apps in `scripts/`, run with `dotnet run scripts/<Name>.cs`. No Python or
shell scripts; [CLAUDE.md](../CLAUDE.md) has the rule.

## Build gates (src)

Every build of a src project runs four checks, and each finding fails it (TreatWarningsAsErrors). They live in
`src/Directory.Build.props` (which imports the root one), `src/BannedSymbols.txt`, `src/BannedSymbols.HotPath.txt`,
`src/.editorconfig` and each package's `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. Tests and benchmarks keep
the root settings (latest-recommended, no bans). The analyzers are 5.6.0, at or below the Roslyn the SDK's compiler
runs (5.9 in 10.0.402).

### Banned APIs (RS0030)

Microsoft.CodeAnalysis.BannedApiAnalyzers. A list holds one documentation comment ID per line, then `;` and what to
use instead, which the error prints.

| List | Projects | Bans |
|---|---|---|
| `src/BannedSymbols.txt` | every src project, generators included | the runtime's `ManualResetValueTaskSourceCore<T>` (use `InlineValueTaskSourceCore`); `PoolingAsyncValueTaskMethodBuilder` and `<T>` (use `PooledValueTaskMethodBuilder` in a method that runs per request); `System.Console` (report through a hook) |
| `src/BannedSymbols.HotPath.txt` | CodeFusion.Http, CodeFusion.Data.Postgres | `System.Linq.Enumerable`; all eight `Task.Run` overloads |

The hot-path projects are named in `src/Directory.Build.props` (`CodeFusionHotPath`). Core and Data have no hits and
could join with their names. A ban fires on a use, not a declaration: an unused field of a banned type passes.

The hits there were:

- Console. `HttpServer` (accept failing, and working again), `HttpExchange` (a connection that failed, a handler's
  exception with no `OnHandlerError`) and `HttpAppErrors.Log`'s default wrote to stderr. The server's now go to the
  new `HttpServerOptions.OnError` (`Action<string, Exception?>`: a short line and the exception) through
  `HttpServer.Report`, which counts what the hook throws in `CallbackFailures` and drops it, so the hook can't take
  down the accept loop or a connection's cleanup. Null drops them. `OnError`'s default and `HttpAppErrors.Log`'s both
  write through `HttpServerOptions.Stderr`, the same lines as before and the one Console use, with a `#pragma`.
  `ErrorHookTests` cover a handler's exception reaching `OnError`, a hook that throws (counted, and the connection
  answers the next request), a null hook, and the default's exact stderr lines; each went red with its code broken.
  Accept failing is covered by Debug hooks that make an accept, an admission, a start or a backoff throw, and by the
  sample app under `ulimit -n 256` (see "Past the descriptor limit"). Not covered: `Report`'s own catch on the
  connection paths (no test can make a connection loop throw on demand).
- `PoolingAsyncValueTaskMethodBuilder` on `HttpExchange.RunAsync` and `FlushAsync`. They keep it, with a `#pragma`
  and the reason. Switched to `PooledValueTaskMethodBuilder`, `KeepAliveRequestsAllocateNothing(async: true,
  connections: 1)` failed: 0.4 to 1.5 B/request against 0.004 to 0.09 with the runtime's builder (three rounds each,
  alternating, load 5 to 10). The sampled type was `QueueUserWorkItemCallbackDefaultContext<object>`. The runtime's box
  is a thread-pool work item itself, so a continuation registered after the awaited source completed (the handler's
  reply came from another thread first) is queued without allocating; ours is an `Action`, which the thread pool has
  to wrap. Both methods run per connection, so the per-thread cache the ban is about isn't missed per request there.
  The ban's message now says a per-connection loop may keep the runtime's builder with a `#pragma` saying why.
- None for the runtime's source core, LINQ or `Task.Run`.

Left out, because a ban can't be scoped to files: `string.Format` and interpolated strings in hot-path files. The
analyzer reads one list per project, and Http and Data.Postgres build exception messages and startup errors with
interpolation, which is right off the hot path. Interpolation isn't a call either: `$"..."` compiles to
`DefaultInterpolatedStringHandler` or to `string.Concat`, so a ban on the handler would catch every cold use too. Banning
`string.Format` alone, with interpolation allowed, would stop nothing. A rule per file or member needs an analyzer of
our own, for instance one keyed on a `[HotPath]` attribute next to `HandlerAnalyzer`. `Task.Run` had no hits, so it is
banned in the two hot-path projects whole.

### Public API (RS0016, RS0017)

Microsoft.CodeAnalysis.PublicApiAnalyzers, on the four packages (Core, Http, Data, Data.Postgres; the condition is
`IsPackable`). Each has `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` next to its project, generated from
today's API with nullability (`#nullable enable` on the first line, so `string?` against `string!` is a change too).
Nothing is published, so every line is in Unshipped and Shipped holds only that header. A public symbol the files
don't list is RS0016; a listed one that is gone or no longer public is RS0017. So every API change is a diff of
these files in review. A missing file doesn't switch the check off: every public symbol is then an RS0016.

After a deliberate change, update the files from the code:

    dotnet format analyzers src/CodeFusion.Http/CodeFusion.Http.csproj --diagnostics RS0016 RS0017 --severity info

It adds the new symbols and drops the stale ones. Or edit the file: one line per symbol, as the error prints it.

At a release, move each Unshipped file's lines into its Shipped file and leave Unshipped with only
`#nullable enable`. From then on a shipped symbol that goes needs a `*REMOVED*` line in Unshipped
(`*REMOVED*CodeFusion.Utf8.Foo() -> void`), and package validation fails the pack anyway unless the break is
suppressed on purpose.

### Package validation

`EnablePackageValidation` is on for src; `dotnet pack` runs it for each package (`RunPackageValidation` in the pack
log). With no baseline it only checks the package against itself, which with one framework is little. Below it,
commented, is `PackageValidationBaselineVersion`. After the first publish, set it to the published version: pack then
downloads that release and fails on any API it drops or changes, and raise it with each release. Tried on a copy
under central package management: Core 0.1.0 packed to a local feed; `ArenaBytes.Text` made internal and its line
taken out of `PublicAPI.Unshipped.txt`, so only package validation could see it; Core 0.1.1 packed with
`-p:PackageValidationBaselineVersion=0.1.0` failed with

    error CP0002: Member 'CodeFusion.Utf8 CodeFusion.ArenaBytes.Text(int, int)' exists on [Baseline]
    lib/net10.0/CodeFusion.Core.dll but not on lib/net10.0/CodeFusion.Core.dll

and exit code 1. The .nupkg is still written before the check fails, so a release script must stop on pack's exit
code, as CI's steps do. A deliberate break (a major version): `dotnet pack -p:ApiCompatGenerateSuppressionFile=true`
writes `CompatibilitySuppressions.xml`, which then shows in review.

### Every CA rule

src builds with `AnalysisLevel` latest and `AnalysisMode` All. The first build under it had 161 findings in src. Six
rules are off in `src/.editorconfig`, each with its reason there: CA1031 (catch-alls at loop and handler boundaries),
CA1815 (equality on view, result, awaiter and builder structs), CA1028 (byte-wide enums), CA1032 (exception
constructors without the details), CA1308 (lower-casing ASCII keys) and CA2225 (named twins of the implicit
conversions). The rest were fixed, or take a `#pragma` with the reason where they don't fit:

| Rule | Where | Fix |
|---|---|---|
| CA2213 | `HttpApp.DisposeAsync` | stopped its server but never disposed it, so the server's wake event leaked; it disposes it now (nothing outside sees the event, so no test) |
| CA2000 | `PgConnection.Open` | the connection is built inside the `try`, so a constructor that throws can't leak the transport |
| CA2007 | `HttpExchange` | `ConfigureAwait(false)` on its awaits: a connection loop has no context to return to, but a handler could leave one set |
| CA5392 | `PgTransport` | `DefaultDllImportSearchPaths(SafeDirectories)` on the libc and libSystem.Native imports: no library planted next to the app is loaded |
| CA2002 | `BufferPool.Shard` | locks a private `Lock` instead of itself |
| CA1307 | `PgSettings`, `PgStatement`, `PgConnection` | `StringComparison.Ordinal` on `Contains`, `IndexOf` and `Replace` (the same behavior, said) |
| CA1062 | `HttpServer`, `PgSettings.Set`/`Parse`, `PgStatement`, `PgCommand.Then`, `Pending<T>.Awaiter`, `HandlerAnalyzer` | null checks at public entry points (`ErrorHookTests`, `NullArgumentTests`) |

With a `#pragma` and its reason: CA1062 on `RowJson` (the generated readers call it per column with their own
writer), CA1034 (`Utf8List.Enumerator`, `Pending<T>.Awaiter`), CA1819 (`ArenaBytes.Buffer`), CA1040 (`IDataProvider`,
named in a constraint), CA1008 (`PgType`: 0 would ask Postgres to infer the type), CA1054 (`Postgres.FromUrl` takes
Npgsql strings too), CA1724 (the `Postgres` type in its own namespace), CA1002 (`PgSettings.Parameters`), CA1063
(`PgTransport`'s abstract `Dispose`), CA1064 (the generator's internal `Refused`), CA1849 (a completed ValueTask's
`GetResult`), CA2025 (disposing the listener is what ends the accept loop), CA2000 (sockets and transports handed
over) and RS0022 (`DataContext<TProvider>` opens `DataContext` to apps on purpose). None of it grew into a refactor,
so no rule was left out for size.

### Proven red

On a copy of the tree, so nobody's build broke. A banned use in Core and four in Http:

    src/CodeFusion.Core/BanDemo.cs(5,46): error RS0030: The symbol 'Console' is banned in this project: The packages
      don't write to the console: report a failure through a hook (HttpServerOptions.OnError or OnHandlerError, ...)
    src/CodeFusion.Http/BanDemo.cs(12,40): error RS0030: The symbol 'ManualResetValueTaskSourceCore<TResult>' is banned
      in this project: Use CodeFusion's InlineValueTaskSourceCore: ...
    src/CodeFusion.Http/BanDemo.cs(13,48): error RS0030: The symbol 'Enumerable' is banned in this project: LINQ
      allocates an enumerator and a delegate per call: write the loop
    src/CodeFusion.Http/BanDemo.cs(14,37): error RS0030: The symbol 'Task.Run(Action)' is banned in this project: ...
    src/CodeFusion.Http/BanDemo.cs(16,25): error RS0030: The symbol 'PoolingAsyncValueTaskMethodBuilder' is banned in
      this project: Use PooledValueTaskMethodBuilder in a method that runs per request: ...

A public type added to Core and `ArenaBytes.Text` made internal:

    src/CodeFusion.Core/ApiDemo.cs(4,21): error RS0016: Symbol 'CodeFusion.ApiDemo' is not part of the declared public API
    src/CodeFusion.Core/ApiDemo.cs(7,23): error RS0016: Symbol 'static CodeFusion.ApiDemo.Answer() -> int' is not part
      of the declared public API
    src/CodeFusion.Core/PublicAPI.Unshipped.txt(9,1): error RS0017: Symbol 'CodeFusion.ArenaBytes.Text(int offset, int
      length) -> CodeFusion.Utf8' is part of the declared API, but is either not public or could not be found

Both builds exited 1; the copy was deleted afterwards.

### Not done here

- A hot-path rule per file or member for `string.Format` and interpolation, as above.

## Runtime checks

Checks that make a bug fail its test, or a Debug build, where it happens. Release builds run the code they ran
before: every check that costs anything sits behind `[Conditional("DEBUG")]`, so the call and its arguments are gone
from Release IL. The rest are counters on paths that already failed (a caught exception, a full pool).

### Swallowed failures fail the test

| Where | What was dropped | Now |
|---|---|---|
| `HttpExchange.RunCompleted` | a connection loop that threw (logged only) | `HttpServer.FailedConnections` |
| `HttpExchange.Gone`, `HandlerFailed`, `HttpServer.Report` | a callback on `Aborted` that threw, `OnHandlerError` or `OnError` that threw | `HttpServer.CallbackFailures` |
| `PgLoops.Loop` catch-all | anything a group threw became `Broken`, a bug as much as a dropped connection | `PgLoops.UnexpectedFailures` and `FirstUnexpected`, for what `PgLoops.Expected` doesn't explain |
| `PgJob.Complete` catch-all | a continuation that threw, or a second completion the core refused | counted against the loops of the thread (`[ThreadStatic] t_loops`, set once per loop thread) |
| `PgConnection.Dispose` catch-all | anything thrown while saying goodbye | the same, unless `Expected` |
| any thread, any `Task` | an exception that ended a thread; a faulted task nobody awaited | `tests/Shared/RunGuard.cs` |

`PgLoops.Expected`: `PgConnectionException` with no inner exception or an expected one, `PgException` (a refused
login), `SocketException`, `IOException`, `TimeoutException`, `ObjectDisposedException`, and an `AggregateException` of
those (a failed DNS lookup). Anything else (an `InvalidOperationException` from a transport, a `NullReferenceException`
in the reply reader) is a bug, still answered `Broken`, now counted.

The tests fail on them:
- `tests/CodeFusion.Http.Tests/ServerChecks.cs`: `AssertStoppedCleanAsync(server, arena?)` waits for the connections,
  then asserts no loop threw, no callback failure, no connection left, every buffer back (`AssertPoolsBalance`), and in
  Debug builds runs and handler calls started equal ended. `TestServer.DisposeAsync` calls it; `AppCheck` (declared
  after an `HttpApp`, `await using var check = new AppCheck(app)`) does it for the app tests, which checked nothing
  before. `HttpApp.Server` is internal for it.
- `tests/Shared/LoopChecks.cs` (linked into the Postgres and the data layer's tests): `Stop(loops or data sources)`
  completes them, waits for their threads (`PgLoops.WaitStopped`, which skips the calling thread: a command's
  continuation runs on its loop, so a test's `Dispose` often does too), and asserts no unexpected exception, nothing
  queued, and in Debug builds `JobsOut == 0`. `AssertClean` for loops a test shares and doesn't stop (the property
  tests). Every place a test stopped loops calls it now; `CodeFusion.Data.Tests` got `InternalsVisibleTo` for it.
- `tests/Shared/RunGuard.cs`, linked into all five test projects: a `[ModuleInitializer]` adds
  `AppDomain.UnhandledException` (prints the exception that takes the process down; the runtime ends it, the run
  fails) and `TaskScheduler.UnobservedTaskException` (recorded). An xUnit `[assembly: AssemblyFixture]` runs after the
  assembly's last test, finalizes what the GC can reach and throws with every unobserved exception: the run fails
  (exit 2 under `dotnet test`, 1 under the in-process runner). `RunGuardTests` proves each project has it.

### Invariants (Debug builds)

A broken one throws `InvalidOperationException` (“… invariant broken: …”). In HTTP that ends the connection's loop
and counts in `FailedConnections`; in Postgres it isn't `Expected` and counts in `UnexpectedFailures`. Either way the
test that caused it fails, with the message and the stack of the state change.

- `HttpExchange.CheckInvariants`: `0 <= _start <= _end <= _in.Length` (both 0 without a buffer); `_req`'s offsets
  (scanned, line end, head end, body end, raw, method, path, query, Authorization) within `[0, _end - _start]`, body
  end <= raw, and line end <= head end <= body end once the head is parsed (unless the parse failed);
  `0 <= _sendStart <= _sendEnd <= _out.Length`; the body being written after what waits to be sent. Checked after
  every parse, receive, compaction, request end, linger reset, response begin and finish, `Advance`, send step, buffer
  release and close. To make "`_req` describes the request at `_start`" true at each check, `_req.Reset()` moved from
  the top of the loop to right after `_start += _req.Total` (and in `CloseAsync`, before the buffers go): the same one
  reset per request.
- `PgConnection.CheckReplies`, at each message of `ReadReplies`: step in `[0, steps]`; phase 0 (ParseComplete due)
  only at a step that sent a Parse, then 1, then 2; phase 1 past the last step (only ReadyForQuery due); an error if and
  only if a slot is blamed (or `CommitFailed`); every step not yet ended still has its job; a statement parsed in this
  batch is pending (1) until its ParseComplete and parsed (2) after; the receive window inside the buffer.
  `Answer` now takes the count first, so a completion that throws leaves the rest for the loop to fail, none twice.
- `PgJob`: a life, `Pooled -> Building -> (Held ->) Queued -> Completed -> Pooled` (a discard from any but Pooled).
  `To(next)` refuses any other step (“returned twice”, “completed while X (twice, or after its return)”), and
  `CheckInvariants` ties `Submitted`, `Held` and `Generation` to it (pooled: neither flag, nothing left; building:
  neither, the generation it was rented at; held: Held only, sealed; queued and completed: Submitted, sealed). The loop
  side (`Row`, `Ended`, `Save`, `Restart`, `Answer`, `PgWriter.HandRows`) asserts Queued: after its completion a job may
  be someone else's.
- A few “can't happen” checks throw where nothing counts them (a run ended twice, a connection released twice, a
  second completion inside `DropAborted`): the test process ends, and RunGuard prints which exception.

### Pool hygiene (Debug builds)

- `BufferPool` and `PoolCheck`: an array given back is filled with 0xDD (so a reader that kept it sees garbage, not the
  next request), a second return throws (a weak table of returned arrays), and an array of ours that isn't all 0xDD
  when rented again was written after its return: that throws. `BufferPool.Return` now sends other sizes to
  `ArrayPool.Shared` itself and `RentLarger` rents them, so the grown request and response buffers and the arena's
  big chunks get the same checks (`HttpExchange.ReturnIn`/`ReturnOut` and `RequestArena.Return` are gone).
  `BufferPool.Out = Created - Available - Dropped` (+ the larger arrays in Debug) is exact at rest in Release too.
- `PooledBox` (the pooled method builders): a box in the pool throws on `SetResult`, `SetException`, `GetStatus`,
  `OnCompleted`, `GetResult` and `MoveNext` (“used after its return”); a second return throws.
- `PgJob`: `Buf` and `Saved` filled with 0xDD when it goes back and checked when it is rented; `PgLoops.JobsOut` counts
  the jobs out per reader or writer.
- `HttpExchange`: the request's getters throw outside the handler (“the request was read outside its handler”), the
  writer's after `FinishResponse` (“the response was written after it was finished”), and a connection going back to
  (or out of) the pool must hold no buffer or request; if it does, the connection counts as failed.

### Cancellation and disposal races

- `RaceTests` (HTTP, 16 cases): reading the head, reading the body, the handler waiting, sending × client reset, client
  close, server stop, server dispose. Debug builds hold the connection exactly there through `HttpServer.PhaseHook`,
  called by `[Conditional("DEBUG")] Phase(ExchangePhase)` (Release builds make no call), while the test acts; Release
  builds act after a short wait. Two more phases let a test fail or hold a connection exactly there: `Watching` (the
  watch receive about to start; a hook that throws stands in for a receive that fails) and `HandlerEnded`. And
  `HttpServer.WatchHook`, called through `[Conditional("DEBUG")] Peeked(ref bool)`, sees and may replace what the
  watch's peek found, and may hold the watch thread: `CloseTests` makes a gone land after its handler ended with it. Asserts the handler's ValueTask read exactly once (a counting `IValueTaskSource`), the
  answer or close each act calls for, and `ServerChecks` (pools, every run and handler call ended once, nothing thrown).
- `PgRaceTests` (18): reader and writer × queued, sent, rows arriving × cancel, stop, reset. A scripted server holds the
  first batch's reply for the test, so each place is exact in Release too, without hooks. Asserts each command's status
  (a cancel counts only while queued; a dropped connection fails what was sent on it, the next group reconnects), the
  rows handed over (a read keeps the rows it got before the drop, a write hands over none it didn't commit), and
  `LoopChecks`.
- `CheckTests` and `PgCheckTests`: each check fires (0xDD, a second return, a write after the return, a box or job used
  after its return, the request or response used after the handler, a counted callback, an unexpected exception
  counted and an expected one not, a continuation that throws counted while the loop goes on).

### Review fixes (HTTP)

Each test was red on the code before its fix (Debug unless said), then green in Debug and Release.

| Finding | Test | Red before |
|---|---|---|
| a body dripped below a rate held the connection (each read restarted the deadline) | `SlowClientTests.ABodyDrippedBelowTheMinimumRateIsCutOff` | still open after 5 s |
| a response read slowly, the same | `AResponseReadBelowTheMinimumRateIsCutOff` | still open after 6 s |
| the linger restarted on each read | `ALingerFedAByteEveryOneAndAHalfSecondsEndsWithinItsCap` | still open after 5 s |
| no connection limit | `PastMaxConnectionsANewConnectionIsClosedAtOnce` | the third connection was served |
| a silent new connection waited the keep-alive timeout | `ANewConnectionThatSendsNothingIsClosedAfterTheRequestTimeout` | still open after 3 s |
| a FIN counted as gone: an async handler's answer was dropped | `CloseTests.AClientThatHalfClosesAfterItsRequestStillGetsAnAsyncHandlersAnswer` | no response |
| a gone found after the handler ended aborted the next request | `AGoneFoundAfterItsHandlerEndedDoesNotAbortTheNextRequest` (hooks) | no second response |
| a watch that failed to start left `RunAsync` under a running handler | `AWatchThatFailsToStartAbortsTheRequestAndStillAwaitsItsHandler` (hooks) | 1 connection loop threw |
| closing with pipelined bytes unread sent a reset | `AClosingResponse…` and `AStopWithAPipelinedRequestUnreadEndsWithAFinNotAReset` | a reset after the response |
| HEAD was a 404 | `SampleAppTests.HeadRunsTheGetEndpointAndSendsOnlyItsHead`, `RouterTests`, `BinderPropertyTests` | the GET's body missing (404) |
| any JWT secret length | `JwtTests.ASecretShorterThan32BytesIsRefused` | no exception |
| the token lifetime was fixed | `TokensLiveThirtyDaysUnlessTheLifetimeIsSet` | a 1 h token valid after 1 h |
| repeated Authorization, first wins | `ParserTests.BadHeadersAre400` rows | 0 (taken), not 400 |
| grown buffers back to `ArrayPool.Shared` uncleared | `PartsTests.ALargerArrayGoesBackToTheSharedPoolWithNothingOfTheRequestInIt` | Release: the bytes still there (Debug's 0xDD fill hid it) |
| a declared Content-Length rented the whole body at the first growth | `SlowClientTests.ConnectionsThatDeclareALargeBodyHoldBuffersSizedByWhatTheySent`, `ABodyUpToTheLimitIsReadWholeInABufferNeverTwiceWhatCame` | 23 MB held by 50 connections that had sent 20 KB each, not 0.8 MB; 2 MB held after 442 KB came |
| a response read well above the minimum rate was cut off (one send call for the rest, under a deadline of the grace period) | `AResponseReadWellAboveTheMinimumRateIsSentWhole`, `WithoutARateAResponseThatKeepsMovingIsNotCutOffByTheRequestTimeout` | 1.4 MB of 4 MB, 0.8 MB of 2 MB arrived |
| then: what the server's socket buffered counted as read, so a client reading nothing kept its connection for that much at the rate (45 minutes at 240 B/s) | `AClientThatReadsNothingIsCutOffOnceItAcknowledgesNothingForTheRequestTimeout`, `TheServersOwnSocketBufferEarnsAClientNoCredit` | still open after 6 s; cut at 1.9 s, not by 1.6 |
| a hard abort closed a handler that blocks its thread with a FIN | `CloseTests.AHardAbortResetsTheConnection` | a FIN |
| chunk framing earned body-rate credit | `ChunkFramingEarnsNoBodyRateCredit` | still open after 5 s |
| a reset after the client sent more (a pipelined request, a FIN) didn't abort the handler | `CloseTests.AResetAfterAPipelinedRequestStillAbortsTheHandler` (the FIN rows went with the FIN change below), `AResetIsNoticedWhenTheSocketWasReadableBeforeTheHandlerStarted` | `Aborted` not canceled within 3 s |
| `ErrorReply` took a 1xx, which the response refuses, so the client got a 500 | `RepliesTests.AStatusOutside200To999IsRefused` (Core) | 100, 101, 103 and 199 accepted |

Not red: `JwtTests.ASignatureSpelledWithOtherUnusedBitsIsRefused`. .NET's `Base64Url` decoder already refuses nonzero
spare bits, so the test only guards it. `TryContentLength` stays apart from `Query.TryParseUint`: its overflow guard
refuses from 9223372036854775800, where `TryParseUint` takes up to `long.MaxValue`
(`AContentLengthUpToTheOverflowGuardIsReadAndPastItIs400` pins it). These guard and weren't red: the declared-2 GB
row of `ConnectionsThatDeclareALargeBodyHoldBuffersSizedByWhatTheySent` (not run on the old code, which would have
rented 2 GB per connection), `AChunkedBodyAboveTheMinimumRateIsReadWholeWhateverItsFraming`, the row of
`AResetAfterAPipelinedRequestStillAbortsTheHandler` whose second request was read with the first, the row of
`AResponseReadBelowTheMinimumRateIsCutOff` that reads nothing, and `AResponseReadAboveTheMinimumRateMayTakeLongerThanTheRequestTimeout`
(the code before the first fix cut it off).

A FIN no longer drops the answer: only a reset or a failed connection does (since the next review, below, it cancels
`Aborted` again). On a close after the last response the server lingers only when the client may still be sending
(pipelined bytes buffered or waiting in the socket); otherwise it sends its FIN and closes at once, as before.

The response rate. A send call completes only once the socket has taken all it was given, and the socket takes in
steps as its buffers drain, not as the client reads: on macOS loopback about 660 KB at once, then 255 KB after the
client had read 60 KB (a client reading 1 KB per 50 ms), and its buffer grows on its own while the client reads
nothing. So what the socket took says little, and what counts is what the client's TCP acknowledged: Linux reports the
bytes acknowledged on the connection (`TCP_INFO`'s `tcpi_bytes_acked`, at offset 120 since 4.1), macOS what the socket
still holds (`SO_NWRITE`), both read raw into a stack buffer. Elsewhere, or when the answer can't be right, the
fallback is what the socket took after it first filled, so the first fill earns only the grace period. Once the socket
is full, `FlushAsync` hands it a slice (64 KB, or with a rate what the rate takes in its grace period, since on macOS a
slice still pending counts as sent) and waits; the heartbeat (`CheckSend`, on its own thread) ends the send when the
client's TCP has acknowledged nothing more for `RequestTimeout`, or when, with a rate, the time spent waiting on it since
the server last waited for its request passes what its acknowledged bytes paid for, after the grace period (each
1/rate seconds, never more than `RequestTimeout` ahead: see the next review, below). A client reading faster than the
rate between any two acknowledgments stays ahead, so the rate never cuts it off. What TCP acknowledges
includes the client's own receive buffer, which takes the first hundreds of KB whether the client reads or not (318 KB
on macOS loopback, 2.4 MB with its buffer left to grow): before it fills, a client that reads nothing looks like one
that reads slowly. Once it has, a client that reads nothing acknowledges nothing, and `RequestTimeout` ends it. The
cut, like a stop's hard abort, closes with a reset (linger 0), so the kernel drops what it still holds for the client.
A plain close reset only when a receive or send was pending (.NET closes abortively when it cancels one). A client
whose window is shut when the reset comes drops it, since its sequence lies past the window, and hears only the
answer to its own next segment, which macOS may rate-limit (`net.inet.tcp.rst_rlc_enable`): a client that reads
nothing for seconds may not hear the abort at all, though the server's side is gone (netstat showed only the client's
half, ESTABLISHED). `AResponseReadBelowTheMinimumRateIsCutOff` asks 1 MB/s of a 20 KB/s client, with a row that
reads nothing.

A reset after the client sent more. The watch is a zero-byte receive, so it says "readable" once: after a pipelined
request or a FIN it has nothing more to tell, and on macOS a peek still returns the waiting bytes after a reset. The
reset leaves its error on the socket, though (ECONNRESET; on Linux EPIPE after a FIN). When the watch finds the
socket readable and the client not reset, the heartbeat reads `SO_ERROR` (raw, so nothing is boxed) while that
handler runs, and behind a request the connection's TCP state for a FIN (CLOSE_WAIT: `TCP_INFO` on Linux,
`TCP_CONNECTION_INFO` on macOS; elsewhere a FIN behind a request isn't seen). The bits of `_handler` above its state
count the handlers started, and `Gone` takes the value its finder read, so a client found gone for one request can't
abort a later one from the heartbeat's thread either.

### Review fixes (HTTP generator and analyzers)

Each test was red on the code before its fix (Release), then green in Debug and Release. Tests in
`CodeFusion.Http.Generators.Tests` unless said; `BinderRuleTests` is in `CodeFusion.Http.Tests` and binds real bytes.

| Finding | Test | Red before |
|---|---|---|
| a nullable service: `T??` for a scoped one, `Singleton<T?>` for a singleton | `EdgeTests.NullableServices` | the generated file didn't compile |
| a handler taken as a Delegate cast to a Func it isn't (a default value, params) | `DelegatesTheEndpointCantName` | no error (InvalidCastException at mapping) |
| more than 16 parameters dropped without a word | `SeventeenParameters` | no error; the call threw at run time |
| a closed generic or generic method's group cast instead of called | `MethodsItCallsByName` | `_handler` with a cast |
| a getter that isn't public written (private, protected, internal, another assembly's) | `OnlyPublicGettersAreWritten` | the generated file didn't compile |
| the CFH0001 fix made a lambda static that calls a local function from outside | `DiagnosticTests.ALambdaCallingALocalFunctionFromOutsideGetsNoFix` | the fix offered, CS8820 after it |
| the fix made the lambda around an anonymous method static | `AnAnonymousMethodIsTheOneMadeStatic` | `static () => api…`, CS8820 |
| CFH0004 on `Func<Utf8, bool>` and `IEqualityComparer<Utf8>` | `ADelegateOrComparerThatOnlyTakesAUtf8HoldsNone` | 12 reported, 6 right |
| CFH0004 on an entity's base, a table of a library's base context, an entities-only assembly | `AnEntitysBaseAndATableFromABaseContext` | 4 reported, 1 right |
| over-posting: internal and private members, private and internal setters bound | `EdgeTests.BindersSetOnlyWhatAPublicCallerCould`, `BinderRuleTests.ABodySetsOnlyPublicMembers`, `AQuerySetsOnlyPublicMembers` | `IsAdmin` true, `Admin` set |
| `[Range(typeof(T), "a", "b")]` and exclusive ends ignored | `QueryRanges`, `QueryNumbersItCantBind` rows, `BinderRuleTests.ATypedRangeBoundsTheQuery` | 0 and 51 taken for 1 to 50 |
| a body's `[Range]` ignored | `BinderRuleTests.BodyNumbersStayInTheirRange`, `BodyMembersItCantBind` rows | all 10 out-of-range bodies taken |
| keyword names of types, namespaces and methods | `KeywordsAsTypeAndMethodNames` | the generator threw (hint name `@`) |
| `KnownTypes.From` per Map call | `TheKnownTypesAreResolvedOncePerCompilation` | two instances |
| an `IReply` implemented explicitly | `AReplyImplementingIReplyExplicitly` | the generated file didn't compile |
| `routes?.MapGet(...)` never intercepted | `EveryFormOfAMapCall`, `BinderRuleTests.AConditionalMapCallRegistersToo` | the `?.` call had no interceptor, so its struct no binder (CS0117) |
| a primary-constructor parameter kept by a member | `APrimaryConstructorParameterAMemberKeeps` | 2 of 5 reported (the struct's and the record's, through fields) |
| a type nested in a generic class | `TypesNestedInAGenericClass` | no CFH0103; the generated file named `TDb` |
| Map calls in a `*.g.cs` or `<auto-generated>` file | `FilesThatLookGeneratedAreCheckedToo` | none reported |

What the fixes decided:

- A handler the generated code can't call by name (a lambda, a private method) is held as the Func of its parameters.
  When it has no such type (more than 16 parameters, a default value or params), CFH0107 says so at the call; a method
  the code can name is called directly whatever its parameters, a closed generic type's or method's included.
- A Map call the generator can't intercept is CFH0108 instead of a throw at startup; `x?.MapGet(...)` and
  `EndpointRouteBuilderExtensions.MapGet(x, ...)` are intercepted.
- Visibility is the compiler's (`IsSymbolAccessibleWithin` the app's assembly), so a type internal to another assembly
  is CFH0106 or CFH0003 too. A JSON member is written when its getter is public, as System.Text.Json does.
- Binders set public fields and public properties with a public setter, and skip the rest, as System.Text.Json
  does (an internal one with warning CFH0109 since the next review, below). `[Range]` in its three forms, exclusive
  ends included, bounds query and body numbers; a form it can't read, or a `[Range]` on something that isn't a
  number, is CFH0105.
- CFH0004 follows variance: a type argument declared `in` only takes values, so a `Func<Utf8, bool>` keeps none, and
  one inside it flips back (`Action<Action<Utf8>>` hands one out). A primary-constructor parameter is kept when a
  member, a lambda or a local function uses it, not when only initializers and the base call do (found with a semantic
  model of the type's own files, RS1030 suppressed). The endpoint generator's own output (`CodeFusion.Http.Generated`)
  is exempt: an endpoint holds its static handler as a Func.
- The analyzer runs on generated-looking files, CFH0004 included. A reply that implements `IReply` is written through
  a constrained generic helper (`Replies.Write<T>`), so an explicit implementation works and a struct isn't boxed.

### Review of the HTTP review fixes

Each test was red on the code before its fix (Debug; the generator's in Release), then green in Debug and Release.
The hooks are Debug-only: `HttpServer.GoneHook` (between a gone's compare-exchange and its cancel) and
`SendCheckHook` (the heartbeat, once it has read a send wait's deadline).

| Finding | Test | Red before |
|---|---|---|
| a gone the heartbeat found could cancel the next connection's `Aborted`: its compare-exchange, then the handler ending, `TryReset` keeping the same source, the LIFO pool handing the object over, then the late `Cancel` | `CloseTests.ALateCancelOfAnAbandonedRequestDoesNotReachTheNextConnection` (hooks) | `second canceled=True` |
| the heartbeat ran `Cancel` inline, so the handler's callbacks and continuations ran on it and stalled every timeout | `ACallbackThatBlocksWhenItsClientResetsDoesNotStallTheHeartbeat` | a silent connection still open after 2 s (a 3 s callback) |
| a FIN didn't cancel `Aborted`, so work a client (or a proxy's client) left ran to its end | `AFinCancelsAbortedAndTheAnswersAreStillSent` (3 rows), `AClientThatHalfClosesAfterItsRequestStillGetsAnAsyncHandlersAnswer`, `ServerTests.AbortedFiresWhenTheClientResetsOrCloses`, `RaceTests` (handler waiting × client closes) | not canceled within 3 s; `canceled=False` |
| every send wait's deadline was `long.MaxValue`, and `CheckSend` read the wait's values one by one: a check of one wait could end a later one | `SlowClientTests.ACheckOfOneSendWaitDoesNotEndALaterOne` (hooks) | 849,151 of 4,194,304 bytes |
| both rates were averages over the whole body or run: bytes sent early bought a crawl later | `BytesSentEarlyDoNotBuyABodyALongCrawl`, `AResponseReadFastAtFirstDoesNotBuyALongCrawl` | still open after 5 s (99,999 bytes bought 100 s); still open after 12 s (8 MB bought 8 s and a half-rate crawl kept it) |
| a rate so small that the body's bytes paid for more milliseconds than a long holds (below about 1e-10 B/s for a 1 MB body) wrapped the deadline negative, which reads as none | `NoRateIsSoLowThatItTurnsTheBodyTimeoutOff` (1e-300, `double.Epsilon`) | still open after 3 s |
| an `internal required` member (or a required one `[JsonIgnore]` or with a non-public setter) was CS9035 in generated code | `EdgeTests.ARequiredMemberABinderWontSetIsRefusedAtTheMapCall` (4 rows) | no CFH diagnostic |
| an internal member that bound before kept its default without a word | `AnInternalMemberABinderLeavesIsAWarningAtTheMember`, `BindersSetOnlyWhatAPublicCallerCould` | no diagnostic |
| CFH0004 missed a struct's static members, what a non-entity inherits from an entity, an entity's static members, and took any `[Table]` class for an entity | `DiagnosticTests.Utf8InAClass`, `AnEntitysBaseAndATableFromABaseContext` | 2 and 4 of their reports missing |
| `AGoneFoundAfterItsHandlerEndedDoesNotAbortTheNextRequest` couldn't fail for the handler count | the same test, rewritten | with the count taken out (`Running` alone), the new test drops the second answer in 0.5 s |

What the fixes decided:

- A FIN cancels `Aborted`, as in Kestrel: behind nginx, or for a browser or an HttpClient that gave up, the handler
  can stop. It is not a reset: the state is `HalfClosed`, not `Abandoned`, so `IsAborted` stays false and the answer
  still goes out if the handler finishes (best effort, to a client that may only have half-closed), and an
  `OperationCanceledException` it throws then isn't reported to `OnHandlerError`. The watch's peek sees a FIN at once;
  one behind a pipelined request only the heartbeat sees, from the connection's state (CLOSE_WAIT), on Linux and macOS.
  A reset after a FIN still abandons the request.
- The source is read before the compare-exchange, and the loop renews it whenever the state it takes back isn't
  plain `Running`, so a late cancel only reaches the request it was found for. `AbortSource` is a
  `CancellationTokenSource` that is its own thread-pool work item: the heartbeat queues the cancel without allocating,
  and a new source is made only for a request a client left. The watch's path still cancels inline, on the thread
  pool's or the loop's thread.
- A send wait's deadline is `long.MaxValue` minus its number on the object, so no two share one, and `CheckSend` reads
  the wait's values between two reads of it (a seqlock: they are set before the deadline and kept until it is
  cleared). `Expire` never acts on one: they lie past any time.
- Each byte pays for 1/rate seconds, in doubles and clamped (and the sum kept in doubles since the next round), but
  never more than `RequestTimeout` (or the grace period, if longer) past the moment it came. So the rate cuts no
  client the request timeout wouldn't keep while it is above the rate between any two of its sends (or
  acknowledgments), and bytes sent early buy at most that much slack. The grace period still counts from the first
  wait.
- A required member a binder can't set is CFH0105 at the Map call. An internal field or settable auto-property is
  CFH0109, a warning at the member, once however many Map calls bind it; a private one, a readonly one or one
  marked `[JsonIgnore]` says it isn't data, quietly. (This round said `#pragma` can't silence a generator's
  diagnostic. Wrong: it couldn't silence this one because it was reported at a location outside the compilation's
  trees, fixed in the next round.)
- CFH0004: a struct's static fields and auto-properties are checked (its instance ones are held, and checked, where
  the struct is). An entity's exemption covers its instance members only. A class that derives from an entity or an
  entity's base without being one is reported at its declaration for each view it inherits (once, at the first such
  class). `[Table]` marks an entity only in an assembly that references CodeFusion.Data and has no data context.
- The test 9 finding was right that the old test couldn't fail for the count, though it did go red with the count
  taken out: its watch hook ran on the loop's own thread when the second request beat `WatchForClose`, and held it
  for 5 s. The rewrite sends the third request once the loop waits for the handler.
- Timing (finding 10): the 400 ms request timeouts are 1.5 s, the body test's client sends ten times its rate, and
  the windowed rate makes a pause shorter than the request timeout harmless once the client is ahead.

### Review fixes (data layer)

Each test was red on the code before its fix (Release unless said), then green in Debug and Release. Tests in
`CodeFusion.Data.Tests`; the operator ones run on `OpsDb` against Postgres.

| Finding | Test | Red before |
|---|---|---|
| a navigation in an UPDATE or DELETE compared the child's own key | `OperatorTests.ANavigationInAnUpdateOrDeleteIsOfTheRowWritten` | the person with a pet deleted |
| numeric conversions never reached SQL | `NumericConversionsAreSqlsToo` | 25 / 2.0 read as 1.68e-44 |
| `61 / 2.0` written as C#'s `61 / 2` | same test, `OlderThanHalf` | ann and dee kept |
| a nested insert into the same table took the outer row's key | `GeneratorTests.AnInsertedKeyThatIsntTheRowsIsRefused` | no error (B's key used) |
| a virtual query called through a base reference ran the base SQL | `AVirtualQueryMethodIsRefused` | no error |
| `[Table(Schema = ...)]` ignored | `OperatorTests.ATableOfAnotherSchema`, `GeneratorTests.ASequenceOfAQuotedOrQualifiedTableIsNamedAsSql` | the search path's rows |
| keyword lambda names as bare aliases; keywords that can't be bare columns unquoted | `KeywordTests` (pg_get_keywords' R and T, every statement kind prepared), `OperatorTests.ALambdaNamedAfterAKeyword` | 326 of 606 statements didn't prepare |
| Take then Skip, OrderBy twice, Take twice in a navigation | `SkipAndTakeComposeAsInCSharp`, `ALaterOrderByOrdersFirstAndKeepsTheEarlierOrderForTies` | 2,3,4 for 2,3 |
| Where, OrderBy, Any or Count with a predicate after Take or Skip | `GeneratorTests.AnOperatorAfterTakeOrSkipThatSqlWouldApplyBeforeItIsRefused` | no error |
| the table unquoted in `pg_get_serial_sequence` | `OperatorTests.AnInsertedKeyOfATableNeedingQuotes` | 500 |
| `currval` after OrIgnore | `GeneratorTests.AnInsertedKeyThatIsntTheRowsIsRefused`, `OperatorTests.AnIgnoredInsertsRowIsTheOneItsKeyHolds` | 404 for the row its key holds |
| `==` and `!=` of conditions unparenthesized | `ConditionsComparedWithEachOther` | 500 (syntax) |
| a base context's `Table<T>` properties unseen | `GeneratorTests.TablesOfABaseContextAreItsTables` | `persons` for `folks` |
| UpdateIf and DeleteIf over several rows; no row lock | `AConditionalWriteOfSeveralRowsWritesEachWhoseConditionHolds`, `AConditionalDeleteSeesTheRowAsAConcurrentWriteLeftIt` | 500; a pet deleted after a concurrent write made it two-legged |
| an awaited Write reset the state of a reply in progress | `ReplyStateTests` | the same state object |
| a moved misuse reported where it was | `GeneratorTests.AMisuseMovedInAnEditIsReportedWhereItIsNow` | the generator crashed (an old tree's location) |
| values bound after the job was rented | `OperatorTests.AValueThatThrowsWhenCalledRentsNothing` | Debug: 1 job never back |
| `ref` parameters passed to the binder without `ref` | `GeneratorTests.RefAndInParametersAreBound` | CS1620 |
| a nullable SQL value into a member that isn't | `ANullableValueIntoAMemberThatIsntIsRefused` | no error (a 500 at run time) |
| DeleteIf never refused | `OperatorTests.ARefusedDeleteIsForbidden` | 200 with the row |
| jsonb read with its version byte | `JsonbIsValidJsonAsAReplyAndAwaited` | a 0x01 before the JSON |
| `(int)enumParameter`, `(int)longParameter` checked | `GeneratorTests.ACastOfAParameterIsBoundAsItsType` | CS1503 |
| One read every row | `OneAsksForOneRow` | no LIMIT |
| overloads and a method named like a statement | `OverloadedQueryMethodsAndAMethodNamedLikeAStatementCompile` | CS0102 |
| a generic context | `AGenericContextIsRefused` | CS0246 |
| a view with a private setter | `AViewWhoseSettersTheReaderCantReachIsRefused` | CS0272 |
| a constant string hole spliced as SQL | `OperatorTests.AConstantStringHoleIsAParameterToo` | `name = ann` |
| a negative Take or Skip | `ANegativeTakeOrSkipIsNoneOrAll` | 500 |

Not red: Take then Count (`CountAnyAndAsJson` keeps it: a count of the limited subquery).

What the fixes decided:

- A row an UPDATE or DELETE writes stays unaliased, as the hand-written statements have it; inside a subquery its
  columns are qualified with the table's name (a CTE's row with the CTE's), which no alias of the statement takes.
- Conversions between integers and floating point are SQL casts (`CAST(trunc(x) AS integer)` cuts toward zero as C#
  does); floating-point constants are typed (`CAST(2.5 AS double precision)`); C# numbers keep their suffix. A cast
  of a parameter is written out, checked or unchecked as it was written.
- Refused at build time, with what to write instead: a virtual, abstract or override query method (a call is
  compiled for the method it names); a generic context with queries; Where, OrderBy, Any(predicate) or
  Count(predicate) after Take or Skip; a later statement using the key of an insert with OrIgnore that draws its key,
  of an insert of a query's rows, or of an insert after another insert into its table (or SQL inserting into it); a
  value SQL may make NULL in a view member that isn't nullable (`.Value` says it isn't, as in C#).
- An inserted row's key is the value the insert set when it set one, else the sequence's current value.
- Take and Skip compose as LINQ's (`LIMIT GREATEST($1 - $2, 0) OFFSET $2`), a negative count is 0, a later OrderBy
  orders first (LINQ's sort is stable), and One adds `LIMIT 1`.
- UpdateIf and DeleteIf lock what they find (`FOR UPDATE OF` the target, `SqlDialectAttribute.LockRows`), write each
  row whose condition holds (`IN`), and give back each row with its own; a refused DeleteIf is `Refused` and
  `OrForbidden`'s reply, as UpdateIf's was. `PlanTests` compares these with the contest's statements plus the same
  changes (`Fixed`), and the One queries with `LIMIT 1` added: the same plans under them.
- A constant string hole is a parameter; a hole calling a `[SqlTemplate]` method is its SQL, how the Conduit context
  now shares its fragments.
- `IDataRow.GetJson` gives a JSON column's text, the provider's framing left out (`PgRow` drops jsonb's version
  byte, which no JSON text starts with).
- SQL names are unique per context: overloads add their parameter types (`ById_Int64`), and a statement whose
  `_2` is a method's name takes another underscore (`Touch__2`).
- The generator still reruns on every compilation (`CompilationProvider`): a query's SQL depends on entity types in
  any file, which a syntax node's transform wouldn't see change.

### Review fixes (Postgres client)

Each test was red on the code before its fix (Release unless said; a fix made before its test was taken out again to
see it red), then green in Debug and Release. Tests in `CodeFusion.Data.Postgres.Tests`.
The SASLprep tables in `PgSaslPrep.cs` are written by `dotnet run scripts/SaslPrepTables.cs` from RFC 3454 and Unicode 16.0.0 (downloaded to `artifacts/saslprep/`); change the script, not the tables.

| Finding | Test | Red before |
|---|---|---|
| ReadyForQuery's status ignored: BEGIN left the group uncommitted, reported Ok | `PgProtocolTests.AWriteGroupThatEndsInATransactionIsBrokenAndItsConnectionReplaced`, `PgWriterTests.AWriteThatBeginsATransactionIsBrokenAndNothingOfItCommits` | Ok |
| COMMIT or ROLLBACK inside a group: committed writes run again, rolled-back ones Ok | `AWriteWhoseStatementControlsTheTransactionBreaksItsGroup`, `PgWriterTests.AWriteThatEndsOrSplitsTheGroupsTransactionBreaksTheGroupInsteadOfReplayingOrLosingWrites` | Ok after ROLLBACK; 23505 for a write that committed |
| the failing write kept its error from the attempt that rolled back | `PgWriterTests.AWriteThatFailedOnlyBecauseOfWhatTheRolledBackAttemptWroteRunsAgainAndCommits` | 23505 though id 1 was free |
| a connection Postgres ended while idle failed the next group | `AConnectionTheServerEndedWhileItIdledIsReplacedBeforeTheNextGroupGoesOut`, `PgReaderTests.AConnectionPostgresEndedWhileIdleIsReplacedAndTheNextReadSucceeds`, `PgWriterTests.AWriteAfterPostgresEndedTheIdleConnectionCommitsOnANewOne` | Broken (57P05) |
| no wait between failed connects | `AFailedConnectIsTriedAgainOnlyAfterAWaitThatGrowsAndCommandsMeanwhileFailAtOnce` | 256 connects in 600 ms |
| ±infinity written as a finite date or an overflow; the last 30 years read wrapped | `InfinityGoesOutAsPostgresInfinityAndAnInstantNoOffsetCanReachIsOnePastIt`, `PgReaderTests.InfinityIsWrittenAsInfinityAndAnInstantPastPostgresRangeIsRefusedByIt`, `TheYearsPastWhatALongOfMicrosecondsSince1970HoldsAreRefusedInsteadOfWrapping` | OverflowException; `294247-01-10` for +infinity; no InvalidCastException |
| a refused text[] element still counted | `PgReaderTests.AnElementRefusedMidArrayNeverSendsAMiscountedArray` | 08P01 insufficient data |
| a held command run from two threads ran twice | `PgJobReuseGuardTests.AHeldCommandRunOrDiscardedFromTwoThreadsAtOnceRunsOnceOrNever` | the command never completed |
| a builder exception kept the pooled job | `ACommandWhoseBuildThrowsGivesItsJobBackAndTakesNoMore` (8 ways to throw) | the builder took more |
| SCRAM with 1 iteration | `PgAuthTests.ScramBelow4096IterationsIsRefusedUnlessWeakAuthIsAllowed` | Ok |
| trust while a password is set | `AServerThatLetsAPasswordUserInWithoutAskingForItIsRefusedUnlessWeakAuthIsAllowed` | Ok |
| no SASLprep | `PgSaslPrepTests.ARoleWhosePasswordPostgresPreparedLogsInWithTheSamePassword` (a role per password) | 28P01 for NBSP, soft hyphen, U+2168, combining marks, ligatures |
| TLS keys other than sslmode ignored, and the PG* variables | `PgSettingsTests.AnyKeyThatAsksForTlsIsRefusedRatherThanIgnored`, `PgAuthTests.AnEnvironmentVariableThatAsksForTlsMakesConnectingFailInsteadOfGoingOnInTheClear` | parsed; logged in |
| quoted values split at `;` | `AValueIsQuotedAndEscapedAsAdoNetAndNpgsqlRead`, `AMalformedQuoteIsRefused`, two properties against Npgsql's own parser | `"pa` for `"pa;ss"` |
| replies taken in during a large send grew without limit | `RepliesTakenInWhileABatchGoesOutAreCapped` | 64 MB taken in, nothing thrown |
| NUL, user, database, client_encoding in the parameter list; another client_encoding reported | `AStartupParameterThatWouldChangeWhoOrHowTheClientTalksIsRefusedBeforeItIsSent`, `AClientEncodingChangedMidSessionBreaksTheConnection`, `PgReaderTests.AStatementThatChangesTheClientEncodingEndsItsConnectionAndTheNextReadIsUtf8Again` | sent; `ä€` failed in LATIN1 |
| a getter read any type of its size | `PgReaderTests.AGetterReadsOnlyTheTypesItCanReadExactlyAndRefusesTheRest` | 1069547520 for 1.5::float4, 5E-324 for 1::int8 |

Not red: SECURITY.md's "password wiped" (B9) is wording; it now says what the client wipes and what it can't
(`PgSettings.Password` is a string). The cleartext and md5 paths wipe their copies now too; no test sees a private
buffer. `options=-c client_encoding=...` can't change the encoding: the startup packet's own `client_encoding`
wins, so only ParameterStatus is checked.

What the fixes decided:

- A write group's statements may not control the transaction: a CommandComplete of BEGIN, START TRANSACTION, COMMIT,
  ROLLBACK, SAVEPOINT, RELEASE or a prepared transaction breaks the group (`Broken`: what committed is unknown), and so
  does any ReadyForQuery that isn't idle, for reads too. The connection is replaced.
- A failing write runs again alone once the writes before it committed, unless it was the first of its batch (then
  it saw nothing of the group, and its error stands): one more round trip per failure in the middle of a group. A
  failed merged run of keyed updates is the exception: the writes from there go once more one by one in one round
  trip (see "Merged keyed updates" below).
- Idle means the loop slept in the queue: the next group then first takes in what waits on the socket (one
  non-blocking receive), and a FATAL, a close or a reset there opens a new connection before anything is sent.
  Under load the loops don't sleep, so no group pays for it.
- A failed connect makes the loop wait `ReconnectDelay` (100 ms) before the next try, twice as long after each failure
  up to `MaxReconnectDelay` (2 s), a quarter either way at random; meanwhile its groups fail at once.
- Infinity is `long.MaxValue` and `long.MinValue` both ways; an instant within the 1970-to-2000 offset of
  long.MinValue goes out as one past -infinity, which Postgres refuses as out of range like any instant before
  4714 BC. A value after 294247-01-10, which a long of microseconds since 1970 can't hold, throws on reading.
- A builder call that throws gives the command back; the builder is done (it was "left as it was" before, and so
  lost when the exception left the caller).
- One weak-auth switch (`AllowWeakPasswordAuth`) for cleartext, md5, SCRAM below 4,096 iterations and trust with a
  password set.
- SASLprep runs as pg_saslprep does, which checks the mapped password before it normalizes: RFC 3454's tables
  (Unicode 3.2) for mapping, prohibited, unassigned and bidi characters; NFKC through ICU, and only for a password with
  a character NFKC can change (a table from Unicode 16.0). With InvariantGlobalization .NET normalizes nothing, so
  such a password fails to connect with a message saying so, rather than with a wrong proof. The Postgres tests now
  run with ICU (`InvariantGlobalization` false), so the property tests check that table against ICU's NFKC.
- Connection strings are parsed by .NET's own `DbConnectionStringBuilder`, Npgsql's parser too.
- A TLS environment variable applies, as in libpq, only where the connection string didn't set the mode, and only to
  TCP connections (not a test's transport).
- Every statement is described when it is parsed (a Describe after the Parse, answered in the same batch), so the
  connection knows its columns' types. `GetInt64` reads int2, int4 and int8; `GetDouble` float4, float8, int2 and int4;
  `GetBoolean` bool; the timestamps timestamptz and timestamp; `GetJson` json, jsonb (version 1 only), text, varchar
  and bytea. `GetBytes` reads any type. A DataRow whose width isn't its RowDescription's breaks the connection.

### Review of the data layer and Postgres review fixes

Each test was red on the code before its fix (Release unless said), then green in Debug and Release. The generator
and operator tests are in `CodeFusion.Data.Tests`, the rest in `CodeFusion.Data.Postgres.Tests`.

| Finding | Test | Red before |
|---|---|---|
| a key the insert set from a call (`Id = Ids.Next()`) computed again for each later statement using it | `GeneratorTests.AKeyTheInsertSetsFromACallIsComputedOnce`, `OperatorTests.AKeyDrawnInCSharpIsDrawnOnceAndUsedByEachLaterStatement` | 7 calls in the interceptors for 4 written; 500 (the pet's owner a key no row has) |
| a ThenIf's condition computed once per statement under it | `AKeyTheInsertSetsFromACallIsComputedOnce` (`Guarded`) | (found on the way, fixed with the keys) |
| two calls written alike in one statement (`Id = Ids.Next(), OwnerId = Ids.Next()`) bound as one value | `TwoCallsWrittenAlikeInOneStatementAreTwoValues`, `OperatorTests.TwoCallsWrittenAlikeInOneInsertAreTwoValues`, `AValueOfACallUsedInsideAnotherIsRefused` | 2 calls in the interceptors for 3; 1 label for 2; no error |
| a `[SqlTemplate]` argument of the row an UPDATE or DELETE writes left bare, so the template's own subquery took it | `GeneratorTests.ATemplatesArgumentOfTheRowWrittenIsQualifiedByItsTable`, `OperatorTests.ATemplatesSubqueryInAnUpdateOrDeleteSeesTheRowWritten` | `x.owner_id = id`; nobody deleted (pet 1 is person 1's, so `x.owner_id = x.id` held) |
| a template returning a type without NULL refused for a nullable argument | `ATemplateReturningATypeWithoutNullIsNeverNullUnlessGivenANullItDoesntTake` | 3 errors for 1 |
| a class with only tables (generic, or not partial) refused as a context | `AGenericContextWithoutQueriesIsABaseForOneWithThem` | 2 CFDATA007 |
| a query method implementing an interface's method ran its definition through the interface | `AQueryMethodImplementingAnInterfaceIsRefused` | no error |
| One or Write&lt;T&gt; of a conditional write over several rows answered from whichever row came first | `OperatorTests.AConditionalWriteOfSeveralRowsGivesBackTheFirstWrittenOrRefusedWhenNoneWas` | 3813 for 3812 |
| SQL inserting into the table spelled `"people"`, `public . "people"`, `MERGE INTO People` or `COPY people` unseen | `AnInsertedKeyThatIsntTheRowsIsRefused` (4 more methods, and a quoted `"People"` that is another table) | 5 errors for 9 |
| a constant string hole where SQL expects a name bound as a parameter (`ORDER BY $1` sorts by nothing) | `AConstantStringWhereSqlExpectsANameIsRefused` (10 places, 4 value places that pass) | no error |
| an internal setter or constructor of another assembly's view taken as reachable; its errors reported in that assembly's source | `AViewOfAnotherAssemblyWhoseSettersAreInternalToItIsRefused` (by compilation reference, as an IDE has it) | no error; with only the constructor check undone, 2 of 3; the generator crashed on a diagnostic in the other assembly's source before the location fix |
| a lazy `IEnumerable<Utf8>` that threw while bound kept its job | `OperatorTests.AListThatThrowsWhileItIsReadGivesItsCommandBack` | Debug: 1 job never back |
| DEALLOCATE (one or ALL) or DISCARD ALL left the connection binding statements the server forgot | `PgReaderTests.AStatementThatDropsTheConnectionsPreparedStatementsEndsItAndTheNextReadParsesAgain` (3 rows), `PgWriterTests.AWriteThatDropsTheConnectionsStatementsIsBrokenAndTheNextWriteParsesAgain` | 26000 `prepared statement "s4" does not exist` on the next read; Ok |
| without recv (Windows), a reply landing between `Available` and `Poll` taken for a close | `PgTransportTests.AReplyThatCameAfterTheFirstLookIsTakenInNotTakenForAClose` (a scripted socket) | PgConnectionException |
| the backoff test's `reads > 10 * connects` and 50 ms sleeps assumed the scheduler's timing | `PgProtocolTests.AFailedConnectIsTriedAgainOnlyAfterAWaitThatGrowsAndCommandsMeanwhileFailAtOnce`, `AConnectionTheServerEndedWhileItIdledIsReplacedBeforeTheNextGroupGoesOut`, `NoticesAndParameterStatusWhileIdleKeepTheConnection` | with a 16 ms timer (Windows'): 33 reads, 5 connects |
| the idle test's second half ended a session the timeout had ended already | `PgReaderTests.AConnectionPostgresEndedWhileIdleIsReplacedAndTheNextReadSucceeds` | with its terminate taken out, the old half passed and the new one fails |

Not red: SECURITY.md's SCRAM wipe (the stored key and the proof stay on the stack) is a reading of the code, as no
test sees a stack buffer; they are wiped now and SECURITY.md says what is and isn't.

What the fixes decided:

- Every value the statements bind is computed once, before the command is rented, and each later use binds that
  local: an inserted row's key set by a call is that call's value in the follow-up select and a child's insert, and a
  ThenIf's condition is computed once for all the statements under it (a nested one only when the outer holds). A
  value computed under a ThenIf is reused only by statements under it or a ThenIf inside it. A pure value bound as
  another type is computed again (`BothOf`: an inlined method's parameter bound as a long and as an int); an impure
  one is bound in one type only (see the second review below: an inlined call's argument is a local of its own type).
- A value is one value by its C# text only when that text is pure: a parameter (`in` and `ref readonly` too), a
  constant, a readonly field, a class's or struct's property without a setter (or with init) and without a body
  that is neither an interface's, abstract, virtual nor an override, Nullable's Value and HasValue, a string's or an
  array's Length, and operators that aren't user-defined over those. Anything else (a call, a settable property, a
  field that isn't readonly, a property of another assembly, a `ref` parameter) is a value per place it is written: two `Ids.Next()`
  in one insert are two parameters and two calls (`TwoCallsWrittenAlikeInOneStatementAreTwoValues`,
  `TwoCallsWrittenAlikeInOneInsertAreTwoValues`; before, one call bound twice). Such a value is known by where it is
  written, within the inlined calls it was reached through, so a lambda translated twice is still one value and an
  inlined method's argument used twice is one call (`KeyedOnce`). A statement's new values are computed in the order
  the translator made them, which is C#'s (with the order reversed, the name took the nick's label), an inlined
  call's arguments with them, at the call.
- An impure value that would run twice is refused (CFDATA001): written into another value's C# as well as bound
  (a `ref Paging`'s limit and offset), or bound inside a ThenIf and again outside it. An inlined call's argument is
  no longer one of these: it is a local computed at the call (second review, finding 8).
- A template's argument of the row an UPDATE or DELETE writes is qualified by its table's name (a CTE's row by the
  CTE's), as in the subqueries the translator writes. A template whose subquery takes an alias the statement uses
  (`t`, or the target table unaliased) still shadows it.
- A template's declared return type decides whether it can be NULL, unless an argument SQL may make NULL goes to a
  parameter declared without NULL (`lower(m.Team.Name)` over a left join gives NULL back): taking `Utf8?` says it
  handles NULL. Unsound only for a template declaring a nullable parameter and a non-nullable result that passes NULL
  through.
- A class declaring no query methods gets nothing generated and nothing asked of it: no partial, no provider, any
  generics. A query method that implements an interface's method, implicitly or explicitly, is refused (CFDATA007),
  and so is one a base declares when a subclass adds the interface (reported on the subclass).
- UpdateIf and DeleteIf give the rows written first, then the refused ones, each by key: One and Write&lt;T&gt; answer
  the written row with the smallest key, and Refused only when none was written (`ORDER BY ... IS NULL, key`, one
  sort of the rows found; PlanTests compares with the contest's statements plus the same ORDER BY).
- A raw SQL write moves an earlier insert's sequence when it inserts, merges or copies into the table: a quoted name
  is matched as written, a bare one in any case, with any schema or none.
- A constant string hole only where a parameter is a syntax error is CFDATA010, which names `[SqlTemplate]`: right
  after FROM (a table's: not IS DISTINCT FROM, nor a function's as in `substring(x FROM y)` or `extract(epoch FROM
  y)`, told by a SELECT, DELETE, UPDATE or COPY earlier in its parentheses), JOIN, INTO, UPDATE or TABLE, beside a
  qualifier's dot, in an INSERT's column list, and in ORDER BY or GROUP BY only as a whole item (followed by `,`,
  `)`, ASC, DESC, NULLS, LIMIT, OFFSET or the end: `ORDER BY $1` sorts by nothing, `ORDER BY $1 = name` is a value).
  An item of a SELECT or RETURNING list is a value. Keywords in comments and string literals are skipped. Only the
  clause the hole is in is read, not the SQL as a whole.
- A view's setters and constructor must be reachable from the app's assembly (`IsSymbolAccessibleWithin`), and a
  view's errors are reported where it is used when its source is another assembly's.
- `IDataCommand.Discard` gives a command back unrun; the binder calls it when a list throws while it is read (nothing
  when the builder threw, which gave it back already). Lists aren't copied first, so a binding allocates nothing more.
- DEALLOCATE ALL, DISCARD ALL and a DEALLOCATE of one of the client's own statements end the connection as the
  transaction-control statements do: their group is Broken (a write's outcome unknown) and the next connection
  parses afresh. Clearing the cache mid-batch would leave the later statements of the batch bound by names the
  server forgot. One DEALLOCATE's tag doesn't name the statement: the SQL the client sent does, so the app's own
  `DEALLOCATE myplan` leaves the connection be, and SQL it can't read so counts as the client's. A DEALLOCATE that a
  function or a `DO` block runs (`EXECUTE 'DEALLOCATE ALL'`) completes as `SELECT` or `DO` and isn't seen: the next
  Bind of a dropped statement fails with 26000.
- Without recv, a socket readable with nothing to read is looked at once more before it counts as closed.
- The Postgres tests wait for a loop to sleep (`PgLoops.Sleeping`, for tests) instead of sleeping 50 ms, and the
  backoff test checks each wait from when its reads started and ended, which holds on any machine: four connects,
  each at least three quarters of 40 ms doubled each time after the one before, and more reads than connects.

### Second review of the data layer fixes

Each test was red on the code before its fix (Release), then green in Debug and Release. A test that pins behavior
the code already had (no fix) was proven red by breaking that code on purpose, as noted.

| Finding | Test | Red before |
|---|---|---|
| a query taking `in Paging` (or `ref readonly`) refused: an `in` parameter counted as impure, so its limit and offset "ran twice" | `GeneratorTests.InAndRefReadonlyParametersReadTheSameEachTime` | 2 CFDATA001 |
| CFDATA010 refused values: a SELECT or RETURNING item, `substring(x FROM $1)`, `extract(epoch FROM $1)`, `ORDER BY $1 = name`, a keyword in a comment or a string literal before the hole | `AConstantStringWhereSqlExpectsANameIsRefused` (12 refused, 11 value places, IS [NOT] DISTINCT FROM among them) | 8 holes refused that are values |
| an interface's, abstract, virtual or overriding property taken as pure: `seq.Next` written twice bound once | `APropertyAnOverrideOrAnImplementationMayComputeIsAValuePerPlace` | `VALUES ($1, $1, $2)` |
| an impure value first bound as a double, then cast back to a long: 2^53 + 1 stored as 2^53; and one statement binding a value as both made one parameter of the first type | `AValueBoundAsADoubleAndAsALongKeepsEveryDigitOfTheLong`, `OperatorTests.AKeyPast2To53ComparedAsADoubleIsWrittenWithEveryDigit` | `(long)__v1_0` in the interceptor; 9007199254740992 for 9007199254740993 |
| an overload's statements documented by its type suffix ("ById, statement Int64") | `AnOverloadsStatementsAreDocumentedByTheirNumber` | "Touch, statement Int64" |
| DEALLOCATE of the app's own statement broke the connection and its group | `PgReaderTests.ADeallocateOfTheAppsOwnPreparedStatementKeepsTheConnection` (4 spellings), `PgWriterTests.AWriteThatDeallocatesTheAppsOwnStatementKeepsItsGroup`, `PgPropertyTests.ADeallocate*` | Broken |
| a query method of a base implementing an interface a subclass adds wasn't refused | `AQueryMethodOfABaseImplementingAnInterfaceTheSubclassAddsIsRefused` | no error |
| an inlined method's impure argument ran only where the body bound it: never when unused, only when a ThenIf held | `AnInlinedMethodsArgumentIsComputedAtTheCallLikeEveryArgumentInCSharp`, `OperatorTests.AnInlinedMethodsArgumentRunsOnceWhetherItsBodyUsesItOrNot` | 1 call for 3; `Keys.Calls` grew by 0 |
| a value bound under an inner ThenIf, then under its outer one, refused, with a message saying "neither inside the other" | `AValueUnderAnInnerThenIfAndThenUnderItsOuterOneIsComputedOnce` | CFDATA001 |
| the idle test's second half raced the 100 ms `idle_session_timeout` of its new connection | `PgReaderTests.AConnectionPostgresEndedWhileIdleIsReplacedAndTheNextReadSucceeds` | through a proxy delaying the server's bytes 60 ms and a 70 ms stall before the SET: Broken, FATAL 57P05, 3 runs of 3 (the new shape passed the same 3) |
| the throwing-list test could fail only in Debug | `OperatorTests.AListThatThrowsWhileItIsReadGivesItsCommandBack` (the job given back is the next one rented, its generation 2 on) | with the binder's `Discard()` taken out: red in Release |
| raw-SQL insert detection: no case for ONLY, `COPY (SELECT …) TO`, or another schema | `AnInsertedKeyThatIsntTheRowsIsRefused` (MERGE INTO ONLY, COPY out), `AnSqlInsertMovesTheKeyOfARowOfItsSchemaOnly` | each red with the code broken: ONLY dropped from the pattern, COPY's query read as its FROM's table, the schema compare skipped |
| found on the way: a call in a query method inlined twice made once for both when it was in a later statement of its body (a Then's argument, an Update's Where): two pets given one key | `ACallInAMethodInlinedTwiceIsMadeOncePerInlining` | 1 call for 2 (red on the code before this round too) |

What the fixes decided:

- An `in` or `ref readonly` parameter is pure: the code that reads it can't assign it. A `ref` one stays impure.
- A property is pure only when it is a non-virtual, non-abstract, non-override auto-property of a class or struct
  declared in source (with no setter, or init).
- An inlined method's impure argument (a query method's, or a static method's with an expression body) is computed
  where the call is, under the call's ThenIf, in the order C# makes it, whether the body uses it or not: a local in
  the argument's own type (`var`), which the body reads. So the body may use it under a ThenIf, after an inner one,
  and inside other values (`k + 1`, which was refused before) and still run it once; each use converts from its own
  type, so a long bound as a double too keeps every digit. Any other impure value is bound in one type only; bound
  as two, it would be refused (CFDATA001) rather than rounded, though no query found reaches that.
- One statement's parameters are one per value and wire type: the same value bound as a double and as a long is two
  parameters (before, one parameter of the first type served both: a long bound as a double was rounded, and a
  column read as the other type threw).
- The QuerySql doc comment of a statement names its method and its number, as before the last round.
- The inlined calls a value was reached through travel with the body's `Env`, not a stack the translator leaves when
  the call's own translation ends: a Then's argument and an operator's lambda are translated later, and kept the
  same site for every inlining of the method.

### Last HTTP review, and gaps the Conduit port found

Each test was red on the code before its fix (Release), then green in Debug and Release.

| Finding | Test | Red before |
|---|---|---|
| `Paid` stored each sum back as a long, so a receive worth less than 1 ms earned nothing: at a configured rate over about 1000 B/s, a client sending small segments faster than the rate was cut | `SlowClientTests.BytesWorthLessThanAMillisecondEachStillAddUp` | 1000 one-byte receives at 0.5 ms each paid 0, not 500 |
| CFH0109 (and every generator diagnostic) came with a location outside the compilation's trees, which `#pragma` and `[SuppressMessage]` can't reach | `EdgeTests.APragmaAtTheMemberSilencesItsWarning`; the build of `BinderRuleTests` with a `#pragma` and a `[SuppressMessage]` in place of the project's `NoWarn` | `SourceTree` null, not suppressed |
| a secret under 32 bytes had no way in, though a deployment may be handed one (the contest's is 26) | `JwtTests.AShortSecretIsTakenOnlyWhenAllowed` (1, 26, 31), `EveryKeyLengthSignsAsTheStandardHmac` (1 to 200 bytes against `HMACSHA256.HashData`), `NoSecretIsTakenEmptyEvenWhenShortOnesAreAllowed` | `ArgumentException` with the opt-in |
| `AddDatabase` took only a `PostgresDatabase`, so an app that built its `PgDataSource` (its own loops or failure log) couldn't register it | `RegistrationTests.AddDatabaseTakesASourceTheAppBuiltItself` | no overload (with one that registered nothing: no singleton) |
| a lock timeout (55P03) or a statement timeout (57014) never reached `OnFailure`: only Broken, Failed and ReaderFailed did, in both the reply's path and the awaited row's | `FailureTests.ALockHeldTooLongIsBusyReportedAndTheWriteDoesNotHappen`, `AStatementPastItsTimeoutIsBusyAndReported` | nothing reported |

Refuted: an aggregate of no rows came out as `null` (`{"tags":null}`). `array_agg` is NULL on zero rows, but the
correlated form is `ARRAY(...)`, never NULL, and `RawJson` reads a NULL as `[]` in the reply (`RowValues.RawValue`)
and in the awaited row (`RowValues.Raw`), as it has from the first commit. `OperatorTests.AsJsonOfNoRowsIsAnEmptyList`
passed before any change and now pins it; with `RawValue` writing null it goes red. Only a `RawJson?` member reads
NULL as null, as its type says.

What the fixes decided:

- `Paid` takes and returns doubles; `_bodyPaid` and `_sendPaid` are doubles, cast only where a deadline is made.
- The generator reports its diagnostics from an output of their own, combined with the compilation, at
  `Location.Create(tree, span)` of the tree with the member's path. The sources stay an output of their own, so an
  edit that changes no endpoint still writes no source again. `CodeFusion.Http.Tests` dropped CFH0109 from its
  `NoWarn`: `BinderRuleTests` silences `Signup.IsAdmin` with `#pragma` and `Search.Admin` with `[SuppressMessage]`
  (taken out, the build fails with both).
- `allowShortSecret` is a parameter of a third `Jwt` constructor and `AddJwtAuth` overload, never a default, and not
  a minimum length: an app opts in by name, and an empty secret stays refused. The signature check is unchanged
  (`FixedTimeEquals`). SECURITY.md says the risk.
- `AddDatabase(source)` registers the source, a context per connection on it and its stop; the other overload opens
  its database and calls it. Stopping a source twice is harmless.
- `DataSource.Reports` is the one list of statuses `OnFailure` hears of, read by both paths: the timeouts are the
  database's own trouble (contention, a slow plan) even when a client's load caused them, and an app logging only
  its 500s missed its 503s. A request whose client left still reports nothing.
- Timing: `AResponseReadFastAtFirstDoesNotBuyALongCrawl` has a 3 s request timeout (its fast part may pause 3 s), its
  cut lands at about 7 s, and the bound is 11 s (before the fix the crawl kept it past 14 s).
  `AResetAfterAFinStillAbandonsTheRequest` polls `IsAborted` for up to 5 s instead of waiting 300 ms.
  `ACallbackThatBlocksWhenItsClientResetsDoesNotStallTheHeartbeat` moved to `BlockingCloseTests` in the `quiet`
  collection, which runs alone, and waits for its 3 s callback to end before the next test there.
- CI runs the test projects in Debug as well, as matrix entries beside Release on both architectures, so the Debug
  invariant and pool checks run on Linux and the wall time stays one run's. Debug skips the pack.

### LINQ shapes the Conduit port wrote as SQL

The Conduit context had five queries in `Sql(...)` because the translator had no form for them. Each shape below
has execution tests on Postgres (both a C-collation database and an ICU one, as CI's), generator tests of its SQL and
of the variants it refuses (CFDATA001, with what to write instead), a `PlanTests` row against the contest's
statement, and an `AllocationTests` row. Each test was red first: built against the code without the shape (the
query was refused, or its SQL or rows differed), or, for the properties, with the shape's code broken on purpose.

**Text functions** (`Utf8Operators`, `TextFunctionTests`, `ShapeGeneratorTests.TextFunctions*`). A C# method that SQL
can't match exactly isn't mapped: no SQL `lower` lowers as `ToLowerInvariant` does (a collation lowers by its locale:
ICU gives `ς` for a final Σ and `i̇` for İ, the C collation lowers A to Z only), so the function is `ToLowerAscii`,
`translate(x, 'A…Z', 'a…z')`, alike in every collation and in SQLite, and keeping the text's collation for an
order by it. Each function also runs as C#, so the property tests compare the database's result with the method's for random text of ASCII, letters only a locale lowers, ß, ǅ, astral
characters and combining marks.

| Test | Red with |
|---|---|
| `ForAnyTextEachFunctionsSqlGivesTheMethodsResult` (ICU database) | `lower({0})` for `LowerAscii`: `𐐨é` for `𐐀É` |
| `APatternTakenIsReadAlikeByDotNetAndSql` (400 patterns from a grammar wider than the accepted one) | the `[^...]` class without `+` accepted: `/[^ ]/` on Ä, two combining marks, two 𐐀 and a mark: SQL 6 dashes, .NET 8 |
| `APatternRefusedIsOneTheyReadDifferently` | (pins why each refusal is needed: `[^a]`, `[^a]+[^b]+`, `.` on an astral character, `a$` before a newline) |
| `TextFunctionsAreTheirSql`, `ATextFunctionSqlWouldReadOtherwiseIsRefused` (15 variants) | the mapping taken out: CFDATA001 on every query using one |
| `TheSlugRuleGivesWhatTheSpecsSlugifyGives` | (the spec's SLUGIFY run by the database beside ours, five titles) |

What it decided:

- A function over values known when called is still SQL (`translate($1, ...)`): the call never runs, so it
  allocates nothing. Where C# decides (a ThenIf's condition) the method itself runs, a nullable's `.Value` as written.
- `ReplaceMatches` takes the patterns .NET and Postgres's AREs read alike: literals, escaped punctuation, `[...]`
  classes of ASCII characters and ranges, once or with `+`, and at most one `[^...]` class, with `+` (.NET reads a
  character outside the BMP as two halves; one greedy class takes both, a second one or one without `+` can split
  it). `\d`, `\w` and `.` are refused: in Postgres they follow the collation (an ICU database's `\d` matches `٣`). The
  replacement has no `$` (.NET's substitutions) and no `\` (SQL's back references).
- `Trim` by a constant character, any but a surrogate (UTF-8 is self-synchronizing, so trimming whole sequences of
  bytes is trimming the character). `Replace` with an empty old value replaces nothing, as SQL's does.
- Each is a dialect template (`SqlDialectAttribute.LowerAscii`, `Trim`, `RegexReplace` and the rest); an empty
  `RegexReplace` refuses `ReplaceMatches`, which SQLite will need.
- The Conduit context's slug rule is LINQ now (`Slugs.WithId`); its insert has the contest's plan (`PlanTests`
  `CreateArticle`), and `AllocationTests` creates articles with it.

**An insert of a list's texts** (`Utf8ListOperators.AsRows`, `Query.Distinct`, `ListInsertTests`,
`ShapeGeneratorTests.AListsTexts*`). `Insert(Tags, names.AsRows().Distinct().OrderBy(n => n).Select(n => new Tag
{ Name = n })).OrIgnore()` is `INSERT INTO tags (name) SELECT DISTINCT n.v FROM unnest($1) n(v) ORDER BY n.v ON
CONFLICT DO NOTHING`. Red before: every query using `AsRows` refused (`'names' as a query`).

| Test | What it pins |
|---|---|
| `EachNewTextOnceInSortedOrderAndTakenOnesSkipped` | a name twice inserted once, a taken one skipped (its row unchanged), keys in the names' order, an empty list, 3,000 `ž` and an emoji |
| `WithoutDistinctATextTwiceIsAConflictAndNothingIsWritten` | 409 and no row; Where and an order by an expression |
| `InAUnitTheTextsUseTheInsertedRow` | a ThenIf's insert using the inserted row's key; no members, no statement |
| `ConcurrentInsertsOfTheSameNewTextsAllSucceedAndInsertEachOnce` | 8 callers on two writer connections, half with the list reversed, 20 rounds: all Ok, each name once |
| `AnInsertOfAListSqlWouldReadOtherwiseIsRefused`, `AListsRowsOnTheirOwnAndDistinctInAReadAreRefused` | the variants below |

What it decided:

- LINQ's Distinct is of the texts, SQL's of the columns the Select makes: the same only when a column is the text
  itself, so a Select that sets none is refused (`Name = n.ToLowerAscii()` could make two rows one), and so is an
  order by anything but the text (SQL orders distinct rows by what they hold).
- Distinct is taken only there; in a read it is refused (`Distinct in a read`), as are a list's rows on their own.
- The rows are a set-returning function in FROM (`SqlDialectAttribute.ListRows`, `unnest({0}) {1}(v)`), where Where
  can filter them; the contest's statement has it in the select list. The plans differ in that node only (a
  Function Scan for a ProjectSet over a Result, the same one pass over the array): `PlanTests` compares
  `CreateArticle_2` with the contest's statement so rewritten.
- The Conduit context's tag insert is this LINQ, inside `CreateArticle`'s unit; `AllocationTests` inserts tags that
  are there already.

**A write from the rows a query found** (`Query.Then`, `FoundQuery`, `FoundWriteTests`,
`ShapeGeneratorTests.AWriteFromTheRowsFound*`). `Users.Where(...).Then(u => <insert or delete>).Select(...)` is one
statement: the rows found in a CTE, the write as a CTE that writes (the statement itself without a Select), and the
Select over the CTE. Red before: every such query refused (`'Then' in a query`).

| Test | What it pins |
|---|---|
| `AnInsertFromTheRowFoundAndTheRowGivenBack` | the row back as a reply and awaited; again, skipped by OrIgnore, row still back; no row found: 404 and nothing written |
| `AConflictWritesNothingAndTheSelectJoinsAsAnyOther` | without OrIgnore a taken key is 409 and nothing; the Select's join (a left one for a nullable key) |
| `ADeleteOfAnotherTablesRowsJoinedToTheRowFound` | only the matching rows deleted; nothing left, row still back; no row, 404 |
| `EachRowFoundIsWrittenFrom` | two rows found, two written; none found, none |
| `ConcurrentWritesFromTheSameRowAllSucceedAndWriteOnce` | 8 callers on two writer connections, 10 rounds: all found, one row each |
| `AWriteFromTheRowsFoundSqlWouldReadOtherwiseIsRefused` (10 variants), `AWriteFromTheRowsFoundIsOneStatement`, `ADialectWithoutWhatAShapeNeedsRefusesIt` | the refusals below |

What it decided:

- The write is one per row found: an insert of `new T { ... }` is `INSERT ... SELECT ... FROM t`; a delete whose one
  condition on the rows found is `x.col = t.col` makes the column one of their values (`x.col = ANY(ARRAY(SELECT
  t.col FROM t))`, an index condition), and any other use of them joins them (`DELETE ... USING t`). The contest's
  `= (SELECT id FROM t)` fails on a second row; for Unfollow the plan is better than the contest's (an index
  condition on the whole primary key instead of the followee index and a filter), which `PlanTests` shows with the
  contest's statement so rewritten. Follow has the contest's plan as it is.
- Refused: operators before Then other than Where; an update, a conditional write, SQL, an insert of a query's rows
  or a unit as the write; a write to the table the rows are found in (UpdateIf and DeleteIf write those); Then in a
  unit; a dialect without writable CTEs (`SqlDialectAttribute.WritableCtes`, false for SQLite).
- The statement reads one snapshot, from before the write, so the Select reads the table written only by Count and
  Any (below); a join, a navigation's subquery or a `Single` into it are refused, as they would show the table as it
  was. Templates are SQL the generator doesn't read and aren't checked.
- Of several rows found, each is written from, and the reply is the one of the smallest key (`ORDER BY` the key
  `LIMIT 1`; the writable CTE still runs to its end). An entity without a key can't take a Select after Then.
- The Conduit context's Follow and Unfollow are this LINQ.

**A count corrected within the snapshot** (`Translator.Corrected`, `CorrectedReadTests`). The Select after Then may
read the table its write changes through `Count` and `Any` (with or without a predicate, after Where) of a
navigation: the snapshot's answer, corrected by what the write did to the rows counted, so
`Articles.Where(...).Then(a => Insert(Favorites, ...).OrIgnore()).Select(a => ArticleView.Of(a, viewer))` gives the
count with the favorite and `favorited` true, in one statement. Red before: such a Select refused (`a read of
person_labels in the Select after Then`).

| Write | Any of the rows P holds for | Count |
|---|---|---|
| insert of r | P(r) or any of the snapshot's | the snapshot's, and one if P(r) |
| insert of r, OrIgnore (P reads r's key only) | P(r) or any of the snapshot's | the snapshot's, and one if P(r) and the snapshot holds no row of r's key |
| delete of the rows D holds for | any of the snapshot's where D doesn't hold | the snapshot's less those D holds for (one at most when D fixes the key: an EXISTS) |

| Test | What it pins |
|---|---|
| `ACountAfterAnInsertCountsTheRowOnceWhetherItWasThereOrNot` | a new row counted, one there before counted once, a third |
| `ACountOfOtherRowsCountsTheRowWrittenOnlyWhenItIsOne` | `Count(l => l.LabelId == other)` and `Where(...).Any()`: one more only when the label written is the one counted |
| `ACountAfterADeleteLeavesTheRowsDeletedOut` | the row deleted not counted, gone already, another |
| `ADeleteNoKeyFixesCountsTheRowsGone` | `label_id > n` deletes several: counted and tested with NOT COALESCE(D) |
| `AnInsertWithoutOrIgnoreIsTheOneMore` | a predicate on a column only a plain insert sets; a conflict is 409 |
| `ConcurrentWritesOfTheSameRowAllCountItOnce` | 8 callers on two writer connections, 10 rounds, insert then delete: every reply counts the row once (or not at all), though a snapshot may miss the write that did it (counting the rows the write changed would give the others the count without it) |
| `ShapeGeneratorTests`: Take before Count, a predicate on a column not of the key after OrIgnore, OrIgnore without the whole key, an insert that doesn't set the navigation's key | refused |

What it decided:

- P(r) is translated with the inserted row's values for the row's columns, and folded where it is known: the same
  column or parameter on both sides of `=` (never NULL) is true (`t.id = t.id`, `$2 = $2`), and true or false on one
  side of AND or OR decides it. So Follow's `following` is `$2 <> 0` (the LINQ's `viewer != 0 && ...`, where the
  contest writes `true`), Unfollow's `false`, Favorite's count `count + CASE WHEN NOT EXISTS (...) THEN 1 ELSE 0 END`
  (the contest's `(NOT EXISTS (...))::int`), Unfavorite's `count - CASE WHEN EXISTS (...)`.
- With OrIgnore the row of r's key is there after the statement, r or another written before (a concurrent writer's
  too: ON CONFLICT waits for it and skips), so P may read only r's key, which that row has alike; anything else is
  refused. The key is `[Key]` on each of the entity's key columns (several make one key; `EntityInfo.KeyColumns`), and
  when a correction relies on the row of the key being there after, the insert's conflict target is that key (`ON
  CONFLICT (user_id, article_id) DO NOTHING`), so a conflict on another unique key is an error, not a skip the Select
  would count as the row; otherwise OrIgnore skips on any unique key, as everywhere. Without OrIgnore r is new, and P
  may read any column it sets.
- A delete's D is translated on the counted row: when every condition of D is one of P's, none is left (`false`, 0).
- Only Count and Any of a navigation are corrected: Take before them, AsJson or a Select of the written table's rows,
  a join or Single into it, and a navigation through it as a join table stay refused.
- The Conduit context's Favorite and Unfavorite are this LINQ (`[Key]` on Follow's and Favorite's two columns), Follow
  and Unfollow give `ProfileView.Of(u, viewer)`. Their plans (`PlanTests.TheCompiledQueryReadsNoMoreThanTheHandWrittenOne`)
  read no index the contest's doesn't and fewer rows: the article from the CTE, where the contest's reads it again by
  its key, and Unfavorite's delete by the whole primary key.

**The newest rows per group** (`Translator.Groups`, `GroupPageTests`, `ShapeGeneratorTests.APageOfEachRowsChildren*`).
`Follows.Where(f => f.FollowerId == viewer).SelectMany(f => f.Followee.Articles, (f, a) => a).OrderByDescending(a =>
a.Id).Page(page, countUpTo: 1000)` is the page over a source row's children, each group from a lateral join. Red
before: such a page refused (`operators before Page other than Where and one OrderBy`).

| Test | What it pins |
|---|---|
| `EachGroupsNewestAsFarAsThePageReaches` | two groups of 5 and 3 rows and an empty one: pages at offsets 0, 2, 6 and past the end; the count capped at 5; maxLimit; an offset next to long.MaxValue; negative limit and offset; no source rows |
| `ATakeOfTheChildrenIsEachGroupsOwnCap` | each group's newest one only, counted so (`LIMIT 1`, and `LEAST(1, $2)` for the page) |
| `AWhereOnTheChildrenAndAnAscendingOrder` | a Where after SelectMany inside each group, ascending keys |
| `APageOfEachRowsChildrenSqlWouldReadOtherwiseIsRefused` (6 variants), `ADialectWithoutWhatAShapeNeedsRefusesIt` | the refusals below |

What it decided:

- The page's rows are among each group's first offset + rows in the page's order (each is one of its group's first
  that many), so each group's lateral takes that many (`PageEnd`: offset plus at most maxLimit rows, long.MaxValue
  past what a long holds, computed in C# where the query is called), and the page is taken of their union as any
  page is: ids first, then the rows of those ids. The count is of every child, capped, with no order.
- The navigation the children are reached through for its key only (`f.Followee`, whose key is `f.followee_id`) is
  taken out of the lateral's source (`Prune`) only where the model declares it required (`[Required]` on it or its
  key): then the row it leads to is there and the join on its key neither drops nor adds a row. Without it the join
  stays, so a link row whose parent is missing (no foreign key) or hidden (row security on the parent) is dropped as
  LINQ's inner join drops it. Don't declare `[Required]` where row security may hide the parent.
- Refused: a result other than the child itself (the page's Select makes the view), an order by anything but the
  child's key, a Take of the children in another order than the page's (it would take other rows than the page's
  first), a Where after SelectMany of children taken with Take, a Select inside the children, operators before
  SelectMany other than Where, and a dialect without LATERAL (`SqlDialectAttribute.Lateral`, false for SQLite).
- The Conduit context's feed is this LINQ, with `following` true as the feed found the author through the follow
  (`ArticleListView.InFeed`). Its plan is the contest's (`PlanTests` `Feed`), the count's join and the semi-join of
  the contest's planned alike; `AllocationTests` reads it.

**Review of the shapes.** A review of the five commits found these; each test was red on the code before its fix.

| Finding | Test | Red before |
|---|---|---|
| a Count or Any after a write from several rows found was corrected by the current row's write only (a favorite of each of an author's articles by the author counted once in `a.Author.Favorites.Count()`) | `ShapeGeneratorTests.AWriteFromTheRowsFoundSqlWouldReadOtherwiseIsRefused` (an insert keyed by a constant owner, a delete keyed by a name) | no error |
| a delete's link to the rows found inside an OR (`x.Legs > n \|\| x.OwnerId == p.Id`) taken as the link, so no row found still deleted | `FoundWriteTests.NoRowFoundDeletesNothingEvenWhereTheLinkIsOneOfTwoConditions`, `AWriteFromTheRowsFoundIsOneStatement` (`TakeEither`) | the pet deleted |
| every ignored insert in Then named its key as the conflict target, so a conflict on another unique key was an error there and a skip everywhere else | `FoundWriteTests` (GivePet, PetsForAge: `ON CONFLICT DO NOTHING`) | `ON CONFLICT (id)` |

What the fixes decided:

- A correction is taken only where the write from another row found can't be one of the rows counted: made from a
  second row found (t2), the inserted row's conditions hold only when t2's key is t's, or the delete's conditions tie
  the counted row's column to t2's key where the count's tie it to t's. Every Conduit read is so (the favorites of
  the article favorited, the followers of the user followed); anything else is refused, with what to count instead.
- `Sql.EqualsColumnOf` takes `x = t.c` only with one term on each side.
- The conflict target is the key only when a correction relies on a row of the key being there after; otherwise
  OrIgnore skips on any unique key, as it does everywhere.
- Not changed, and so noted: a delete's correction reads D on the snapshot's row while the DELETE rechecks it on the
  newest version, so a concurrent update of a column D reads can leave the count matching neither. The Conduit
  deletes read key columns only, which is exact unless a concurrent update changes a key column.

**Second review of the shapes.** Each test was red on the code before its fix (or with the fix broken on purpose,
where the test came with it), then green on the C and the ICU database.

| Finding | Test | Red before |
|---|---|---|
| a corrected Any after a plain insert was NULL where its condition read a NULL column; the reader failed after the write committed | `CorrectedReadTests.AnAnyAgainstANullColumnIsFalseAsInCSharp` (with and without `!`) | the row not read |
| a delete's cascade into a table the Select reads, a trigger or rule on the table written, row security, a column storing another value than the one written: the read as before the write | `AReadADeleteMayCascadeIntoIsRefused` (a navigation leads to the table deleted from), `AReadADeleteMayHaveCascadedIntoFailsTheStatementAndNothingIsWritten` (a join table's foreign key the model can't see), `ATriggerRowSecurityOrARoundingColumnOnTheTableWrittenFailsTheStatement` | no error; the statement ran |
| of several rows found, the reply was whichever came first | `FoundWriteTests.OfSeveralRowsFoundEachIsWrittenFromAndTheOneOfTheSmallestKeyIsTheReply` | no ORDER BY |
| `Sql.Conjuncts` split a condition inside CASE ... END, giving SQL Postgres refuses | `ConditionsSplitOnlyAtTheirTopLevel` (CASE, nested CASE, quotes, `weekend` and `ends_at`), `AWriteFromTheRowsFoundIsOneStatement` (`TakeCase`) | `CASE WHEN a AND (b THEN ...)` |
| a NUL in Trim's character or ReplaceMatches' replacement broke the generated class when its statements were made | `ATextFunctionSqlWouldReadOtherwiseIsRefused` (2 rows) | no error |
| the group page's source navigation taken out of the join on a foreign key the database may not have | `GroupPageTests.WithoutForeignKeysARowWhoseParentIsMissingIsLeftOutAsTheJoinLeavesIt`, `ANavigationsJoinIsLeftOutOnlyWhereTheModelDeclaresItRequired` | `nemo/2`: the ghost link counted |
| ReplaceMatches as C# backtracked: 2.8 s on 24,000 characters | `AsCSharpAPatternRunsInLinearTimeOnALongText` | 2.2 s for the bound of 1 s |
| `lower(x COLLATE "C")` ordered an `OrderBy(n => n.ToUpperAscii())` bytewise | `ListInsertTests.AnOrderByARaisedTextIsInTheTextsOwnCollation` (ICU database) | `_x` before `B` |
| a delete in Then wrote `ARRAY(SELECT ...)` into any dialect's InArray; an empty ListRows wrote `FROM ` | `ADialectWithoutAnArrayOfAQueryJoinsTheRowsFound`, `ADialectWithoutWhatAShapeNeedsRefusesIt` | the ANY form whatever the dialect; 3 errors for 4 |
| the pattern property drew 6 classes and 2 escapes, bound as a parameter | `APatternTakenIsReadAlikeByDotNetAndSql` (every class punctuation and escape, and the pattern as the generator's SQL literal) | found `\_` (.NET refuses it) and `\'Z'` (.NET reads a back reference), both refused now |
| the concurrency test passed whether or not the writers overlapped | `AWriteTheSnapshotMissedIsCountedOnce`: another transaction holds the row, the statement waits for it (seen in pg_stat_activity), then it commits | with the correction taken out: 1 for 2 |

What the fixes decided:

- A corrected Any is `COALESCE(P(r), false) OR EXISTS (...)`: C#'s comparison with a null is false. Count needs
  nothing, as its CASE takes NULL as no.
- Only the table the write names is corrected. After a delete, a read of a table whose rows lead to it by a
  navigation is refused at build time; for the rest the statement checks the catalog first
  (the generator's `Dialect.ThenGuardOfPostgres`, its one copy, which `SqlDialectAttribute.ThenGuard` replaces for
  another dialect: a scalar subquery the final SELECT's WHERE compares with 0, as
  perf/data-batched-updates' merged updates check their table). A recursive CTE walks from the table written and its
  partitions (pg_inherits); a delete follows each foreign key that cascades, sets NULL or sets a default (found
  through pg_depend's index, as pg_constraint has none on the referenced table), and from a table whose rows it sets
  to NULL or a default, the keys acting on their update. A rule or non-internal trigger on any table walked fails the
  statement (a trigger may write anywhere), and so does a table reached that the Select reads or that a table it reads
  holds, a partition at any depth or an inheriting table: each table walked is matched with its ancestors,
  through pg_inherits by inhrelid (the found table always counts as read, the table written only where a read of it is corrected: a key of a
  comment thread into itself reaches nothing an article's Select reads). The table written, or a partition of it, held
  by the row found's table fails it too: two entities of one partition tree are one table to the database. Each table
  carries relhassubclass, so one without children costs no lookup of its children, and each inserted column's types come
  as a regtype[] constant the plan folds once. For a corrected read, so do row security on it or a
  column the insert sets with a type modifier, a type other than the one its value is bound as except a wider one of
  the same kind (int into bigint and float into double precision hold the value; long into integer narrows, and int
  into double precision crosses to floats, which past 2^53 don't hold every integer), or a collation other than the
  default (a nondeterministic one compares the snapshot's rows otherwise than the correction compares the row
  written). Any of them casts `'CodeFusion: the Select after Then can't read <table> as the write left it ...'` to an
  integer, which fails the statement (22P02) before its write commits. The check runs for every Then with a Select,
  whatever it reads: the row given back is the one found, which a cascade or a trigger may have changed or deleted,
  so its table is checked as consistently as the others. It costs a few index lookups for the table written, the
  tables read and their partitions, and as many again for each table a delete can reach: it grows with how far the delete cascades,
  not with the catalog's size ([then-guard-cost](benchmarks/2026-10-21-then-guard-cost.md) measures it). PlanTests
  compare the tables read with the contest's statements so checked (another index of a table read as often passes:
  favorites' two indexes cost Unfavorite's delete the same, and CI's planner took the other), and
  `TheCheckReadsTheCatalogByItsIndexes` pins that an index condition bounds each catalog read (with sequential scans
  off: the planner scans a small catalog table whole, as that costs less). The triggers are looked up by a scalar
  subquery with LIMIT 1, which the planner can't turn into a hash of the whole of pg_trigger.
- A correction is taken also where every row found makes the same write: the same delete, which reads nothing of
  the row found, or an ignored insert of the same key.
- The reply of a Then with a Select is the row found of the smallest key.
- Conditions split at AND only outside parentheses and CASE ... END, scanning `Sql.Code`, which blanks string
  literals (E'' and dollar-quoted too), quoted names and comments. `Sql.TopLevel` (whether `Wrap` adds parentheses) and
  `Sql.Atomic` scan the same code, so a quote in a comment hides no operator; a split also needs the operator as
  written, so `a/**/AND/**/b` is one condition rather than two pieces ending inside a comment.
- `ToLowerAscii` and `ToUpperAscii` are `translate()` of A to Z, which keeps the text's collation. `regexp_replace`
  fails on text of a nondeterministic collation, and `replace` does before Postgres 18 (documented on the settings).
- `Concat(long)` writes its number through `SqlDialectAttribute.IntegerText` (`CAST({0} AS text)`); `AnyOfQuery`
  (`{0} = ANY(ARRAY({1}))`, empty: join the rows found) and an empty `ListRows` (refused) are the dialect's to say.
- Not done as asked, and why: a page ordered by one column of a key of several was to be refused. That column is a
  foreign key in every such page here (`FavoritedBy` pages favorites by `ArticleId`, Ops' `LabelPage` by `PersonId`),
  and those link tables had no `[Key]` before this branch, so the page was a page of the row the key leads to then as
  now; refusing it would break both. `APageByAColumnOfAKeyOfSeveralIsAPageOfTheRowItsForeignKeyLeadsTo` pins that, and
  a key column that is no foreign key stays refused. What changed is only for an entity declaring several `[Key]`s:
  the last one used to be taken as its key, and is not any more; every use needing one key refuses such an entity.

**Third review of the shapes.** Fixes only, each test red on the code before its fix (or with the fix broken on
purpose), then green on the C and the ICU database.

| Finding | Test | Red before |
|---|---|---|
| the guard followed a delete's cascades one table deep, and checked rules and triggers on the table written only | `CorrectedReadTests.ACascadeTwoTablesAwayFromTheDeleteFailsTheStatement` (chain_a to chain_b to chain_c, none of it in the model; a trigger and a rule on chain_b) | the count of chain_c as before the delete |
| a corrected read of a column in a nondeterministic or other non-default collation | `ACorrectedReadOfAColumnInAnotherCollationFailsTheStatement` (ICU `ci`, and `"C"`) | the pet written |
| a column of another type than its value is bound as passed the fixed list of types | `ACorrectedReadOfAColumnOfAnotherTypeThanTheValueBoundFailsTheStatement` (int into float8, long into int4, long into numeric); `AWiderColumnOfTheKindBoundHoldsTheValueAsBound` (int into int4 and int8, float into float4 and float8 pass) | the pet written; with the exact match first asked for, int into int8 and float into float4 failed |
| a NUL in a Utf8 or string constant, in Replace or Concat, or in the text of Sql broke the generated class | `ShapeGeneratorTests.ANulInAnyTextTheStatementWritesIsRefused` (6 rows) | no error for 4 |
| conditions split inside E'' strings, dollar quotes and comments | `ConditionsSplitOnlyAtTheirTopLevel` (5 rows more) | 4 split wrong |
| two copies of the guard, nothing tying them | `TheThenGuardIsOneTextTheGeneratorKeeps` (the attribute holds none, the text pinned) | the attribute's copy |
| PlanTests dropped every pg_* scan | `PlanTests.TheCheckReadsTheCatalogByItsIndexes`; the favorite comparisons with the check on both sides | pg_trigger read whole (a hashed EXISTS) |
| with NonBacktracking taken out, the backtracking test failed by 2.2 s against 1 s only, which a faster machine passes | `AsCSharpAPatternRunsInLinearTimeOnALongText` on 48,000 characters | 7 s |
| the found table was checked only when the guard ran for another reason | `TheRowFoundIsCheckedWhetherOrNotTheSelectReadsAnotherTable` (a cascade into the row found, a trigger on the table an insert writes) | the statement ran |

**Fourth review of the shapes.** Fixes only, each test red on the code before its fix unless it says otherwise, then
green on the C and the ICU database.

| Finding | Test | Red before |
|---|---|---|
| N3's exact type refused a column that holds the value as bound | `AWiderColumnOfTheKindBoundHoldsTheValueAsBound` (int into int4 and int8, float into float4 and float8) | int into int8 failed the statement; float into float4 too, with a float taken as double precision |
| the walk started at the table written, not its partitions | `TheCheckFollowsEveryKeyTheDeleteActsOn`: a key into a partition, a trigger on one partition | the statement ran |
| a row set to NULL was walked along delete actions | the same theory: SET NULL then an ON UPDATE CASCADE into chain_c; SET NULL with chain_c's ON DELETE CASCADE only | the first ran; the second failed the statement |
| a key of the table written into itself failed every Then that deleted from it | the same theory: a self-cascade on chain_a, and a cycle back to it, each reaching nothing read | both failed the statement |
| the recursion's edges were untested | the same theory: a cycle and a self-reference partway, each reaching chain_c | green before (coverage) |
| the cost was "a few lookups" | `docs/benchmarks/2026-10-21-then-guard-cost.md`; the paragraph above | unmeasured |
| the index pin passed a whole index read in order | `TheCatalogPinTellsAWholeIndexReadApart`; `TheCheckReadsTheCatalogByItsIndexes` requires an Index Cond | the old pattern accepts pg_class's oid index read whole |
| TopLevel and Atomic kept a quote-toggle scanner, and a split could cut inside a comment | `ACommentOrALiteralNeverHidesNorMakesAnOperator` (`a/**/AND/**/b`, a `--` comment with an apostrophe before an OR, `E'a\' b'`) | `a/**` and `**/b` |

**Fifth review of the shapes.** Fixes only, each test red on the code before its fix, then green on the C and the
ICU database.

| Finding | Test | Red before |
|---|---|---|
| a table reached under a mid-level partition, or a table inheriting one, read by the Select passed | `TheCheckFollowsEveryKeyTheDeleteActsOn`: a key on a leaf of chain_c, itself a partition of chain_all; a key on a table inheriting chain_c (a key on chain_all, the topmost, as a control) | both ran the statement |
| the found table and the table written as one partition tree, two entities, passed | `ARowFoundIsCheckedThroughAnotherEntityOfItsPartitionTree` (found in parted, deleted through parted_1, and the other way round) | both ran and deleted the row given back |
| each inserted column's types were a name cast per execution | `TheThenGuardIsOneTextTheGeneratorKeeps` and the SQL pins (`'{int4,int8}'::regtype[]` per column); the cost in [then-guard-cost](benchmarks/2026-10-21-then-guard-cost.md) | the pins |

All five of the Conduit context's `Sql(...)` queries are LINQ now; the context has none left. The app's own copy
(`ConduitDb.cs` in the contest submission) needs the same entities: `[Key]` on Follow's and Favorite's two columns,
`[Required]` on `Follow.Followee`, and `User.Articles`.

### Release cost

Release keeps: a few fields (a bool per connection and per pooled box; a byte, an int and a reference per pooled job;
the server's counters), `_req.Reset()` in a new place (same count), a thread-static write per loop thread, a list of
loop threads, and counters on paths that already failed. Allocation tests: 0 B as before.

### Found on the way

- `PgJob.Timestamptz` wrote the parameter's length before its overflow check threw: a caller that caught the
  `OverflowException` and went on sent a malformed Bind (“ts takes 1 parameters” on the next value). Checked first now;
  the test was red first.
- A command that a build mistake left unfinished (a lone surrogate, a timestamp out of range, one parameter too many)
  keeps its pooled job: `PgCommand` is a ref struct and nothing can give the job back for it. The input-driven cases
  make a request allocate a new job later. The Debug job count found it; the tests give such jobs back (`Hold()`). Follow-up: let a builder that throws discard its job, or say so in the docs.
- Loops started with `Start(0)` and completed never run what was queued: no loop takes it.

### Proven red (each broken on purpose in a copy of the tree, seen failing, the copy thrown away)

| Broken | Build | Caught by |
|---|---|---|
| linger block without `_req.Reset()` | Debug | the invariant in `RunAsync` right after the linger reset (“request lines 57/18/57, body to 62, raw 67 with 0 bytes read”), not a later copy: 3 `LingerTests` and the 413 tests |
| `_sendStart += n + 1` in `FlushAsync` | Debug | the invariant in `FlushAsync` (“to send [289, 288)”) |
| `ReleaseBuffers` keeps `_in` after returning it | Debug | “a pooled buffer was returned twice” (2068 times), and the connection going back holding a buffer |
| `ReleaseBuffers` drops `_out` without returning it | Release | `ServerChecks`: “buffers out after the stop: 0 request, 1 response” (37 `ServerTests`) |
| `RequestArena.Release` drops its kept chunk | Release | `AppCheck`: “1 arena” out (`SampleAppTests`, `AppTests`) |
| a pooled box returned twice | Debug | “a pooled ValueTask box was returned twice” (`AsyncTests`, `SampleAppTests`) |
| the handler's ValueTask read again after its await | Release | `RaceTests`: read 2 times, not 1 (the 4 handler-waiting cases); the compiler's CA2012 refuses it first, so the copy silenced it |
| `Gone()` doesn't cancel `Aborted` | Release | `RaceTests`: the handler never hears the client left (2 cases) |
| `Gone()` doesn't count a callback that threw | Release | `CheckTests.AnAbortedCallbackThatThrowsIsCountedNotDroppedUnseen` |
| `Receive` throws `InvalidOperationException` instead of `PgConnectionException` when Postgres closes | Release | `LoopChecks`: “exception(s) in the loops that no failing connection explains”, in tests that only asserted Broken |
| `Answer` completes each read twice | Release | `LoopChecks`: “the operation completed twice” (the core refused it, `Complete` counted it) |
| `Dispose` throws while saying goodbye | Release | `LoopChecks` (57 `PgProtocolTests`) |
| `phase = 1` after every step | Debug | “PgConnection invariant broken: statement 2 at step 1 is 1, not 2”, before the protocol check would have |
| `Return` leaves `Submitted` set | Debug | “a Postgres job Pooled with Submitted=True” |
| the writer hands rows over after completing the write | Debug | “a Postgres job was used while Pooled, not Queued” |
| `GetResult` doesn't give the job back | Debug | `LoopChecks`: “job(s) never back in the pool” |
| `DropAborted` ignores the token | Release | `PgRaceTests`: queued + cancel ends Ok, not Canceled (reader and writer) |
| the guard's self-test leaves its dropped task recorded | Release | the run fails after the last test: “1 task exception(s) were never observed” |
| a test throws on a thread of its own | Release | the process ends; “unhandled exception, the test run fails: … a bug on a thread of its own”; exit 2 |
| `Timestamptz` as it was | both | `ATimestampPastPostgresRangeThrowsInsteadOfWrappingAndLeavesTheCommandAsItWas` (“ts takes 1 parameters”), before the fix (since replaced: infinity is written as infinity) |

Tests: `Tests/RunGuard.cs`, `Tests/Server/{ServerChecks,RaceTests,CheckTests}.cs`,
`Tests/Pg/{LoopChecks,PgRaceTests,PgCheckTests}.cs`, `TestServer.DisposeAsync` and every loop stop checked; commands a
build mistake left unfinished give their job back through `Run` (there is no `Hold`). 829 tests pass in Debug (19
Release-only allocation tests skip) and in Release (7 Debug-only checks skip); the linger reset removed is red at the
invariant there too (the smuggling-shape 400 tests).

### Numbers (`--job short`, three rounds, alternating order)

“Before” is the tree with exactly this change taken out (its hunks reverse-applied, other changes in the same files
kept); “after” is the tree with it. Built side by side in scratch copies, run one class at a time,
round 1 before then after, round 2 after then before, round 3 before then after. The Mac ran at load 10 to 12 from
other work, so a single run is noisy (Kestrel and Npgsql, which this change doesn't touch, moved up to 18%);
the best of each side is the fair figure. Means in µs; allocations per operation as BenchmarkDotNet counts them
(the zero-allocation claims are the allocation tests', which pass).

| Benchmark | before | after | best of each | median of each | CPU/req (best) | allocated |
|---|---|---|---:|---:|---|---|
| HttpServer Kestrel 1 | 64.0 / 68.3 / 55.9 | 62.4 / 64.1 / 56.3 | +0.7% | -2.5% | 83.0 → 83.2 | 138 B → 138 B |
| HttpServer Kestrel 64 | 8.8 / 9.1 / 8.1 | 8.7 / 11.4 / 8.2 | +0.8% | -1.7% | 44.6 → 41.3 | 138 B → 138 B |
| HttpServer Kestrel 1024 | 7.6 / 8.0 / 7.3 | 7.8 / 7.7 / 7.3 | -0.1% | +1.4% | 42.5 → 41.8 | 138 B → 138 B |
| HttpServer Own 1 | 85.5 / 53.4 / 52.7 | 68.1 / 68.1 / 54.6 | +3.6% | +27.4% | 77.1 → 78.1 | - → - |
| HttpServer Own 64 | 8.3 / 8.5 / 7.9 | 8.3 / 8.2 / 7.8 | -1.2% | -1.6% | 37.9 → 36.3 | - → - |
| HttpServer Own 1024 | 7.2 / 6.9 / 6.8 | 7.2 / 7.8 / 6.9 | +0.9% | +4.0% | 38.9 → 34.0 | - → - |
| MinimalApi HandWritten Article 1 | 61.1 / 70.7 / 53.7 | 98.7 / 56.9 / 53.4 | -0.6% | -6.9% | 78.4 → 78.4 | - → - |
| MinimalApi HandWritten Article 64 | 8.3 / 10.3 / 8.0 | 8.6 / 8.9 / 7.8 | -1.8% | +4.1% | 37.9 → 40.2 | - → - |
| MinimalApi HandWritten Login 1 | 59.8 / 65.1 / 53.6 | 80.6 / 54.9 / 53.3 | -0.5% | -8.1% | 77.6 → 76.8 | - → - |
| MinimalApi HandWritten Login 64 | 9.0 / 8.7 / 7.9 | 8.0 / 8.7 / 7.9 | -0.1% | -8.2% | 39.5 → 41.2 | - → - |
| MinimalApi HandWritten Wait 1 | 62.0 / 60.3 / 54.4 | 107.8 / 69.0 / 54.1 | -0.5% | +14.4% | 78.9 → 77.7 | - → - |
| MinimalApi HandWritten Wait 64 | 8.2 / 8.8 / 8.2 | 8.2 / 9.9 / 8.3 | +0.2% | +0.5% | 42.9 → 42.2 | - → - |
| MinimalApi Generated Article 1 | 61.8 / 62.6 / 59.0 | 67.1 / 64.2 / 54.1 | -8.2% | +3.9% | 80.5 → 79.4 | - → - |
| MinimalApi Generated Article 64 | 8.1 / 8.4 / 7.9 | 8.0 / 8.3 / 8.0 | +0.5% | -1.0% | 41.2 → 39.5 | - → - |
| MinimalApi Generated Login 1 | 63.3 / 61.2 / 53.5 | 61.7 / 59.6 / 53.6 | +0.2% | -2.5% | 77.6 → 77.7 | - → - |
| MinimalApi Generated Login 64 | 8.5 / 8.0 / 7.9 | 8.3 / 8.8 / 8.0 | +1.2% | +4.3% | 41.7 → 39.7 | - → - |
| MinimalApi Generated Wait 1 | 64.8 / 55.1 / 54.5 | 92.3 / 63.6 / 54.5 | +0.1% | +15.5% | 79.0 → 78.9 | - → - |
| MinimalApi Generated Wait 64 | 8.6 / 9.0 / 8.2 | 8.9 / 8.6 / 8.2 | +0.8% | +0.2% | 44.4 → 40.6 | - → - |
| Pg ArticleBySlugOne Pg | 95.2 / 137.0 / 103.5 | 99.5 / 134.2 / 89.0 | -6.6% | -3.9% |  | 341 B → 342 B |
| Pg ListPage20 Pg | 457.9 / 399.8 / 416.6 | 565.5 / 502.6 / 391.6 | -2.1% | +20.6% |  | 344 B → 344 B |
| Pg ReadGroup32 Pg | 167.4 / 170.5 / 178.3 | 166.4 / 160.8 / 174.3 | -3.9% | -2.4% |  | 191 B → 191 B |
| Pg WriteGroup1 Pg | 126.9 / 118.9 / 142.1 | 123.3 / 112.0 / 122.5 | -5.7% | -3.4% |  | 352 B → 350 B |
| Pg WriteGroup64 Pg | 56.4 / 56.2 / 57.5 | 55.5 / 51.5 / 58.2 | -8.2% | -1.6% |  | 196 B → 196 B |
| Pg Concurrent64 Pg | 52.0 / 50.5 / 62.0 | 51.0 / 50.8 / 51.4 | +0.6% | -1.9% |  | 179 B → 179 B |
| Pg ArticleBySlugOne Npgsql | 194.0 / 135.2 / 146.7 | 137.5 / 133.9 / 138.5 | -0.9% | -6.3% |  | 1983 B → 1979 B |
| Pg ListPage20 Npgsql | 533.5 / 465.2 / 487.9 | 470.7 / 460.3 / 467.2 | -1.0% | -4.3% |  | 2175 B → 2173 B |
| Pg ReadGroup32 Npgsql | 123.1 / 113.2 / 106.1 | 109.6 / 105.6 / 106.2 | -0.5% | -6.2% |  | 1659 B → 1659 B |
| Pg WriteGroup1 Npgsql | 265.6 / 154.9 / 166.2 | 152.5 / 154.6 / 159.2 | -1.5% | -7.0% |  | 2203 B → 2208 B |
| Pg WriteGroup64 Npgsql | 56.8 / 53.0 / 49.0 | 66.2 / 54.5 / 52.5 | +7.1% | +2.9% |  | 1112 B → 1112 B |
| Pg Concurrent64 Npgsql | 58.3 / 48.9 / 52.3 | 58.5 / 57.8 / 73.3 | +18.1% | +11.8% |  | 1532 B → 1515 B |
| Data ArticleBySlugOne Linq | 88.9 / 87.4 | 98.9 / 89.3 | +2.3% | +6.8% |  | 184 B → 182 B |
| Data ListPage20 Linq | 437.8 / 389.7 | 455.2 / 394.6 | +1.2% | +2.7% |  | 184 B → 184 B |
| Data CommentsOfArticle Linq | 80.8 / 76.2 | 194.3 / 79.7 | +4.6% | +74.5% |  | 184 B → 127 B |
| Data WriteGroup1 Linq | 64.2 / 53.7 | 127.5 / 55.8 | +3.9% | +55.5% |  | 183 B → 181 B |
| Data Concurrent64 Linq | 51.2 / 49.7 | 56.5 / 47.1 | -5.2% | +2.8% |  | 19 B → 19 B |
| Data ArticleBySlugOne Handwritten | 96.9 / 90.5 | 95.6 / 89.1 | -1.6% | -1.5% |  | 184 B → 183 B |
| Data ListPage20 Handwritten | 398.8 / 395.7 | 428.7 / 393.1 | -0.6% | +3.4% |  | 184 B → 184 B |
| Data CommentsOfArticle Handwritten | 88.5 / 75.5 | 113.6 / 76.3 | +1.1% | +15.8% |  | 184 B → 182 B |
| Data WriteGroup1 Handwritten | 59.9 / 53.6 | 85.0 / 53.6 | +0.0% | +22.2% |  | 183 B → 179 B |
| Data Concurrent64 Handwritten | 56.1 / 49.8 | 57.9 / 47.9 | -3.6% | +0.0% |  | 19 B → 22 B |

No case is slower by more than its noise: best of three, everything is within ±8% and nearly all within ±4%, in both
directions; the per-request CPU of the HTTP server is the same or lower; allocations are unchanged. Release IL of the
hot paths is the same as before: each check is a `[Conditional("DEBUG")]` call, removed with its arguments.

### Green

0 warnings in Debug and Release for the whole tree. Core 107, Http.Generators 99, Data.Postgres 229, Data 163,
Http 505: all pass in Release (the 7 Debug-only check tests skip) and in Debug (the allocation tests skip), against a
Postgres 18 with CODEFUSION_TEST_DB and the four CODEFUSION_TEST_PG_* logins set. The allocation tests pass in Release.

### Pipelined reads, shared bursts and RunsAlone (perf/pg-pipelining)

What was decided:
- **Two groups in flight per read connection** (`PgReader.MaxInFlight`). The loop sends what has queued while Postgres
  still answers the group before, so a backend never waits a round trip. More than two gained nothing measured.
- **A group that binds a statement whose Parse is still unanswered waits** for the batches before it. Postgres skips
  everything after an error up to the Sync, that Parse included, so sending at once would bind a statement that
  doesn't exist (`PgConnection.BindsPendingParse`).
- **A failing read's skipped reads go out again** as a new batch behind the rest, in their group. They are back in
  flight before the send, so a failed send ends them Broken with the rest.
- **A broken connection fails every command in flight**, and the next group reconnects.
- **A burst is shared out among the loops asleep**: a loop that finds others asleep takes its share (at least
  `PgQueue.ShareAtLeast`, 4) and wakes the next.
- **A batch sent while another is answered takes replies in** whenever the socket is full: those replies can fill our
  buffers while Postgres waits for us to read them. On Linux this uses MSG_DONTWAIT, which costs no system call.
- **`RunsAlone` statements run on connections of their own**: `PgSettings.AloneConnections`, 2 by default, made at the
  first such command. Each runs one command in its own transaction, and the next goes to a free one.
- **The job pool is bounded**:
  - by bytes: 32 MB for the shared pool, each job counted whole (about 512 B of its own and its buffers). Past that
    a returning job grown past 4 KB goes (a job grown for one large command keeps its buffers through every small one
    after it; shrinking them instead would allocate on a return), and a smaller one takes the place of the least
    recently used jobs, from the bottom of a ring;
  - by count: what the started loops can hold (their queues and groups, given back when they complete), at most
    16,384;
  - a buffer past 64 KB is never kept;
  - a thread's cache holds up to 32 jobs whose buffers are 4 KB or less, and goes back from its finalizer;
  - each loop that starts makes the jobs its groups and its thread's cache can hold (the first loop also a cache's
    worth for each core's callers), so the shared pool is never empty while other threads' caches hold jobs. Without
    it, `AllocationTests.QueriesAllocateNothingOnceWarm` failed about one run in three in the full suite: the shared
    pool held 0 jobs at the end, and 10 to 16 were made in the measured runs.

The review's findings, each test red on the code before its fix:

| Finding | Test | Red before |
|---|---|---|
| a rerun whose send failed: its reads never answered, the group lost; after two the loop spun | `PgPipelineTests.ARerunWhoseSendFailsEndsItsReadsBrokenAndTheLoopGoesOn` | timed out (31 reads unanswered) |
| a small batch sent while another was answered could block on full buffers | `PgPipelineTests.ABatchSentWhileAnotherIsAnsweredTakesRepliesInWhileTheSocketIsFull` | 0 of 5 sends took replies in |
| one alone loop: a slow report held up every alone command | `PgAloneTests.ASlowCommandThatRunsAloneHoldsUpNoOtherThatRunsAlone`, `TheConnectionsForCommandsThatRunAloneAreAsManyAsTheSettingsSay` | waited; 1 connection for 3 |
| the pool kept 64 KB buffers for 16,384 jobs | `PgAllocationTests.APoolFilledByLargeCommandsKeepsAtMostItsByteBudget` | 479 MB kept after a burst of 4,096 |
| (re-review N3) buffers up to 4 KB counted against nothing | `PgAllocationTests.APoolFilledBySmallButNotTinyCommandsStaysBounded` (the live heap after a compacting GC; GC.GetTotalMemory over-reads with regions) | 66 MB kept for 16,384 commands of 3.9 KB |
| the pool's room never given back | `PgAllocationTests.ThePoolHasRoomForWhatTheLoopsCanHoldAndGivesItBackWhenTheyStop` | room still held after the loops stopped |
| the reuse guard never saw a reused job (the thread caches) | `PgJobReuseGuardTests` (asserts the job is the same) | another job |
| the burst test passed without sharing | `PgPipelineTests.ABurstWhileOtherLoopsSleepIsSharedOutAmongThem` (queued under the queue's lock) | 1 group of 32 with sharing off |
| `Provider.Alone` called on a provider that doesn't declare it: CS0117 | `MinimalProviderTests.QueriesOverAProviderThatDeclaresOnlyWhatItMustCompile` | CS0117 |

`PgAllocationTests.ABurstAllocatesAlmostNothingOnceWarmWithTheThreadCachesOn` measures the production setting (under
256 KB for 10,240 reads: at most the caches' worth of jobs made), next to the test with the caches off (under 4 KB).
`PgAloneTests` opens both alone connections before its timed part.

The broken-connection test raced xunit's synchronization context: its reads now go into the queue behind a slow
read, with no await between. That race can't be shown red on demand.

### Merged keyed updates (perf/data-batched-updates)

A group commit's run of updates of one row by its key, every value a parameter (`Where(x => x.Id == id).Update(x => new
T { A = a })`), goes as one statement: `UPDATE t AS t0 SET a = v.p1 FROM unnest($1, $2) AS v(p1, p2) WHERE t0.id = v.p2`,
each parameter an array of the run's values, built from the writes' own Bind bytes. What was decided, so that every
outcome is what running the writes one by one gives:
- **A run holds each key once.** It ends before a key comes again, so every write updates its row once and every
  write is checked. Keeping only the last write per key (DISTINCT ON) hid an earlier write its CHECK refuses: the
  random-order property test found it. Only int and bigint keys are merged, whose equal values have equal bytes.
- **Never merged:**
  - an update that sets its own key: which rows the later writes find depends on it;
  - one that returns its row (`Write<T>`);
  - one that runs alone;
  - one over a provider that doesn't declare `KeyedUpdate`.
  All of these are decided by the generator at build time.
- **The table is checked first, by a statement of its own** run before the merged update in the same transaction,
  so a refusal skips the update whatever the plans. A table with a trigger or a rule, partitions or inheritance
  children (whose own triggers and indexes the check wouldn't see), or a unique or exclusion index on a column the
  update sets fails the check with "CodeFusion: no merged updates on <table>". (A trigger would fire once per run,
  not once per write; a unique index judges the rows against each other at once, not one write after another.)
  Postgres refuses a rule in a WITH itself (0A000), which is treated the same way. The check reads the catalog
  (index lookups) on every merged run, so a trigger added later is seen at once.
- **A refused statement goes unmerged for a while** (`PgWriter.UnmergedFor`, five minutes), then is tried merged
  again, so a trigger dropped later doesn't cost merging until a restart. So does one whose merged run failed with no
  write at fault in the fallback (a permission on the catalog, a statement_timeout only the merged statement runs
  into). `PgCounters.Unmerged` counts each time.
- **A failed merged run goes once more unmerged, in one transaction per 64 writes** (more savepoints would overflow
  the backend's cache of subtransactions, which every other session's snapshot pays for), each write in a savepoint of
  its own: `ROLLBACK TO SAVEPOINT cf` (which undoes the write before only if it failed: after a write that ran, the
  savepoint is set again), the write, `RELEASE SAVEPOINT cf`, `SAVEPOINT cf`, and a Sync, so an error skips nothing of
  the writes after it. Then `ROLLBACK TO SAVEPOINT cf` (the last write, if it failed) and `COMMIT`. Each write gets
  its own outcome and they commit together: the writes in one round trip, the commit in a second, where one
  transaction per write cost a commit each. A failed commit (a deferred constraint) committed nothing: each write runs
  again alone. A merged run that failed with no write at fault stops merging for a while (a statement timeout
  included: the bigger statement's own cost), unless another transaction failed it (a deadlock, a serialization
  failure, a lock timeout): then it just runs again. A write
  binding a statement whose Parse an earlier write of the round carries waits for that write's replies, so a failed
  Parse there goes again with it. A row locked elsewhere costs one more lock timeout, not one per write. A deadlock or serialization
  failure there rolls the transaction back: the writes before it run as a group, it runs alone, then the rest, so the
  order holds. Every Parse a write needs goes before its first Bind, so a write's own failure can't skip a Parse that
  a later write in the round binds.

Each test was red on the code before its fix:

| Finding | Test | Red before |
|---|---|---|
| a run of 128 with a locked key last: 128 lock timeouts | `KeyedUpdateTests.ALockedKeyInAMergedRunCostsOneMoreLockTimeoutNotOnePerWrite` | 19 merged, several reruns |
| an overwritten write's error lost (DISTINCT ON) | `RandomGroupsEndAsApplyingTheWritesInOrder` (5 seeds), `TheLastWriteToAKeyWins` | a refused write reported Ok |
| an update of its own key merged | `AnUpdateOfItsOwnKeyIsNeverMergedAndLaterWritesFindTheMovedRow` | the row ended at 716, not 717 |
| row and statement triggers fired once per run | `ATableWithTriggersSeesEveryWriteAsItsOwnStatementInOrder` | one statement entry for two writes |
| a unique column merged: the outcome depended on the order Postgres updated the rows | `AColumnUnderAUniqueIndexIsNeverMergedSoOrderDecidesWhoGetsTheValue` | [Ok, Ok] for a write in-order refuses |
| a refused table tried merged again every group | the trigger, rule and unique tests' second groups | merged again |
| `KeyedUpdate` called on a provider that doesn't declare it: CS0117 | `MinimalProviderTests.QueriesOverAProviderThatDeclaresOnlyWhatItMustCompile` | CS0117 |
| a merged statement's pending Parse not seen | `PgMergedParseTests.AGroupBindingAMergedStatementWhoseParseIsUnansweredWaitsForIt` | false |
| (re-review N1) a write's failure skipped a Parse a later write in the fallback binds | `AWriteAfterAFailedMultiStatementWriteInTheFallbackStillRuns` (a fresh connection) | 26000 for the valid write |
| (N2) one commit per write after a merged failure | `AFaultyWriteInAMergedRunCostsAFewCommitsNotOnePerWrite` (`PgCounters.Commits`: 2) | 220 commits on the server for 100 writes (before the counter) |
| (N4) a write retried after 40001 came after later writes | `AWriteRetriedAfterASerializationFailureStillComesBeforeTheWritesQueuedAfterIt` | the row ended 5, not 9 |
| (N5) a partitioned table merged | `APartitionedTableIsNeverMerged` | [Ok, Ok] for a write in-order refuses |
| (check H1) a failed COMMIT in the fallback read as success: every write Ok, nothing committed | `ACommitThatFailsInTheFallbackCommitsNothingAndEachWriteGetsItsOwnOutcome` (a deferred unique key) | the second insert Ok, no row committed |
| (check L1) a deadlock or lock wait in a merged run stopped merging | `AMergedRunThatAnotherTransactionFailedRunsAgainAndStaysMerged` (a CHECK failing once with 40001) | `Unmerged` 1 |
| (second check) a merged statement past its statement_timeout kept merging | `AMergedStatementPastItsTimeoutStopsMerging` (a CHECK that sleeps once) | `Unmerged` 0 |
| (check L2) a failed Parse skipped a later write's Parse in the round | `AWriteWhoseStatementFailsToParseInTheFallbackLeavesTheNextWriteItsParse` | 26000 |
| (check L3) one savepoint round for 100 writes | `AFaultyWriteInAMergedRunCostsAFewCommitsNotOnePerWrite` (rounds of 64: 3 commits) | 2 commits |
| (N7) unmerged until a restart, and invisible | `AStatementThatStoppedMergingIsTriedMergedAgainLater`, the trigger test's `Unmerged` | 0 merged after the trigger went; 0 counted |

Not red: `RunsAloneTests.AKeyedUpdateMarkedRunsAloneRunsAloneAndIsNeverMerged`. An alone loop runs one command at a
time, so nothing could merge there before either; `MinimalProviderTests.AKeyedUpdateThatRunsAloneIsNeverMadeAKeyedUpdateStatement`
checks the generator leaves it unmerged. Nor the guard as a statement of its own (N6): the WITH form's outer query
ran first in every Postgres tried, so no test tells them apart. Also not
red on demand: CHECK and NOT NULL blame, two faulty writes, the FOR UPDATE lock and the missing key. Their tests
pass on both the old fallback and the new one; each fails when the fallback blames the run's first write.

## Options for an app's own spec (the feed API)

The $12 contest's feed API couldn't be written with the library: it kept a hand-written auth layer and parsed its
route values itself. Each gap became a public option with today's behavior as its default, not a case for that app.
Each test was red first: on a stub of the API that compiled but didn't do it.

| Gap | Option | Test | Red before |
|---|---|---|---|
| the scheme was `Token ` only | `JwtOptions.Scheme`, and `AddJwtAuth(secret, JwtOptions)` and `Jwt(secret, JwtOptions)` beside the old overloads | `JwtSchemeTests` (exact case, one space, no tab, a Cyrillic look-alike, any RFC 9110 token, invalid schemes refused at startup), `SpecOptionsTests.ABearerAppReadsBearerHeadersOnly`, `TheDefaultAppStillReadsToken` | 17 of 34: `Bearer` refused, `Token` still read |
| no claim but `sub` reached a handler | `JwtClaim` (`Required`, `MinBytes`, `MaxBytes`) on `JwtOptions.Claims`; `Viewer.Claim`, `Viewer.TryGetClaim`; `Jwt.Issue(userId, params ReadOnlySpan<Utf8> claims)`, `Issue(userId, now, claims, dest)`, `MaxIssuedBytesWith` | `JwtClaimTests` (escapes, surrogate pairs and lone surrogates, raw invalid UTF-8, null, wrong types, lengths at 0, 1, max and max+1 once unescaped, a 2,900-byte claim, repeats, escaped names of 64 bytes, every bad `sub`, forged and expired tokens leaving the arena untouched, issue and read back, a property test of 3,000 payloads against a `JsonDocument` reading of the rules), `SpecOptionsTests.AHandlerReadsTheTokensClaimAsItWasIssued`, `AnOptionalClaimThatIsAbsentReadsAsNone` | 56 of 71 on a stub that read no claim |
| one 401 body for every reason | `HttpAppErrors.MissingToken`, `InvalidToken`, `InvalidTokenPayload`, null by default | `SpecOptionsTests.EachRefusalHasItsOwnBody` (16 headers against a required and an optional endpoint: none, empty, spaces, another scheme, its case, the scheme alone, two spaces, not a JWT, forged, expired, bad sub, no claim, a claim of the wrong type, expired with a bad payload, valid), `UnsetReasonsAnswerWithUnauthorizedAsBefore`, `AnUnsetPayloadReasonAnswersAsAnInvalidToken`, `TheDefault401IsTheSameBytesForEveryReason` | 16 of 39 on properties that dispatch didn't read |
| a body that isn't JSON and no body were the 422 of a body of another shape | `HttpAppErrors.MalformedBody`, `MissingBody`; `BodySource.MalformedBody`, `MissingBody`, `IsEmpty` and a constructor that takes them | `SpecOptionsTests.EachBodyFailureHasItsOwnReply`, `UnsetBodyReasonsAnswerWithInvalidBodyAsBefore`, `NoBodyIsMalformedUnlessTheAppSaysOtherwise` (27 bodies each: none, empty, a space, a BOM alone, cut short in five ways, trailing bytes, a bad escape, single quotes, a trailing comma, depth 65 and 63, every non-object root, missing and bad fields, raw invalid UTF-8 in a string), `AStructsOwnInvalidBodyGivesWayToTheAppsReasons`; the generator's snapshots | 34 of 134, among them `[1,`, which today's binder answered as another shape without reading on |
| a route value that didn't bind was the app's 404, and nothing bounded one | `HttpAppErrors.InvalidRouteValue` (null: `NotFound`), `EndpointBuilder.WithInvalidRouteValue`; `[Range]` on a `long` or `int` route parameter; `RequestContext.RejectRouteValue` for the generated code | `SpecOptionsTests.ARouteValueThatDoesntBindGetsTheAppsReply` (23 paths: 0, -1, `+1`, `1.5`, `1e3`, `0x10`, spaces escaped either side, one past a long, Arabic-Indic and fullwidth digits, `%31`, leading zeros, int bounds and one past), `UnsetARouteValueThatDoesntBindIsANotFoundAsBefore`, `AuthIsCheckedBeforeTheRouteValue`, `AnEndpointCanNameItsOwnReply`, `AnEndpointsReplyIsCheckedWhenItIsSet`; `SnapshotTests.RouteValues`, `DiagnosticTests.RouteValueRangesItCantUse` and `RouteValueRangesItUses` | 23 of 170 on a stub; 7 of 11 diagnostic cases with the analysis taken out |
| the feed API itself | `tests/CodeFusion.Http.Tests/Sample/FeedApp.cs`: the spec on these options alone, with an in-memory store | `FeedAppTests` (the spec's error table row by row: 15 auth failures on two endpoints, auth before the id, every body failure, trimming and the 500-character limit, likes); 10 `AppAllocationTests` cases | 1,176 B per request for a malformed body: a `JsonException` each |

What it decided:

- The scheme is matched as bytes, case included, then exactly one space, as `Token ` was. RFC 9110 calls schemes
  case-insensitive and RFC 6750 allows several spaces; reading them leniently would change what `Token` apps accept,
  and an exact match leaves nothing for a proxy and the app to read differently. Neither is an option yet.
- Claims are declared for the whole app at runtime, not per endpoint: a token's payload is valid or not wherever it
  is read. A handler reads them by name (`viewer.Claim("username"u8)`, a scan of at most 16 names), and an undeclared
  name throws so that a typo can't read as an empty claim. A claim can't be a view of the request buffer, which holds
  base64url: the payload is decoded into the arena, twice its size, so that an escaped value is unescaped at its own
  offset in the second half, as `JsonBody` does. Without declared claims the payload stays on the stack as before,
  and the arena isn't touched.
- The verifier is one path, `Jwt.Check`, which reports why a token failed (`TokenCheck`). `Verify` and the
  dispatcher both use it. A bad `sub` or claim no longer stops the read early: the rest is still read, so an expired
  or malformed token is reported as invalid whatever its claims hold.
- `JsonBody.TryTextEquals` unescaped into a fixed 96-byte buffer, enough for the names it had (at most 16 bytes). A
  declared name may be 64 bytes, so it now takes what the raw name needs, at most six bytes for each byte compared
  (`ALongDeclaredNameMatchesThroughEscapes`).
- The reasons are nullable properties next to `Unauthorized`, not a new reply type: an app that set only
  `Unauthorized` (Conduit's `{"errors":{"body":[...]}}`) keeps it for every reason. A header without the scheme is
  the missing reason, as the feed spec says, even on an optional endpoint, where it stays a 401 as before.
- (Superseded by the single pass below: nothing throws now, and malformed means `JsonCursor.Malformed`.) A body was
  malformed when the reader threw: a syntax error, an end too early, anything after the root but whitespace, depth
  past 64. A root that isn't an object is read to its end, so `[1,` is malformed and `[]` is another shape. That read is bounded by the body limit, and no binder stops early on a well-formed body anyway.
  Raw invalid UTF-8 inside a string isn't a syntax error to `Utf8JsonReader`, so it stays a bad field, as before.
  Missing means no bytes at all; whitespace is malformed. Neither new reply is a struct member: a struct's own
  `InvalidBody` already answers for its shape, and a reason the app names wins over it.
- A route value's bounds are a `[Range]` on the handler's parameter, read by the generator as it reads them on query
  fields (its ends rounded inward, an exclusive end moved in by one), and compiled into the parse's own `if`: no
  `[Range]` emits the same check as before. A `[Range]` on a `Utf8` route value, one the generator can't read, and
  one that leaves no value of digits are CFH0103. The reply is the endpoint's, then the app's, then `NotFound`, one
  `??` chain in `RejectRouteValue`. Auth runs in the dispatcher before the endpoint binds anything, as before.
- A malformed body cost a `JsonException` (1,176 B per request in `AppAllocationTests`), as it had since the binders
  were written; it mattered once a malformed body had its own reply. For one commit the binder checked every body's
  syntax first, without throwing (`JsonSyntax`, about 150 lines, one pass, since folded into `JsonCursor`). The check is never stricter than the
  reader: `JsonSyntaxTests` (60 cases, 25,000 generated inputs, depth 63 to 1,000) and the `json-syntax` fuzz target
  hold them to the same verdict. Broken on purpose (leading zeros taken, the depth limit one off), the property test
  failed.
- That read a well-formed body twice: binding got 65 to 75% slower
  ([2026-10-21-body-syntax-check.md](benchmarks/2026-10-21-body-syntax-check.md)). Binding first again gave that
  back but made a malformed body throw (1,192 B, about 4 µs).
- Now one reader does both, in one pass: `JsonCursor`, that state machine read a token at a time, with
  `Utf8JsonReader`'s tokens (type, start, value bytes, escape flag, `TryGetInt64`, `TryGetInt32`, `TryGetDouble`,
  `Skip`). It reports a syntax error as `Malformed` instead of throwing. `JsonBody` reads through it, the generated
  binders have no `try` or `catch`, and `ReadToEnd()` says whether the body was JSON before any field's reply. A
  malformed body costs no exception and no allocation, and stops where it breaks (23 ns at its second byte). On the
  three bodies measured (57 B to 2 KB), valid and bad-field bodies bind within noise to 35% faster than before the
  branch (third iteration of the benchmark file).
  - Held to the reader token by token: `JsonSyntaxTests` (60 cases, 25,000 generated inputs with numbers at and past
    the Int32, Int64 and Double limits, 5,000 arbitrary byte arrays, depth 63 to 1,000) and the `json-syntax` fuzz
    target, replayed at 20 times its mutations.
  - The differential found two of the cursor's own mistakes before anything ran on it: `Utf8JsonReader.TryGetDouble`
    returns an infinity for `1e400` rather than false (the binder refuses it with `IsFinite`, as before), and a
    closing token kept the previous string's escape flag.
  - `SpecOptionsTests.NoBodyThrowsWhileItIsBound` counts first-chance exceptions while all 42 bodies of the table bind
    (20 of them threw before). `EdgeTests.ABodyIsBoundAndCheckedInOnePassWithoutExceptions` pins the generated code.
  - `AppAllocationTests` has the malformed feed case back under 0.5 B, with no allowance.
- The JWT verifier keeps `Utf8JsonReader` and its `catch`: it parses only a payload the secret signed, so a flood of
  forged tokens never reaches a parser.
- An Opus review of the diff found nothing critical or high. Fixed, each test red on the code before:
  `Jwt.Issue` asked the arena for six times a value's length before checking the value (a 1 MB claim took about
  8 MB from the pool, then a 500), and its size sum could overflow: the claims are checked first now, sized by their
  real escapes, and refused once the payload passes `MaxTokenBytes`, so the payload fits on the stack and the
  `ArrayPool` is gone (`AValueIssueRefusesTakesNothingFromTheArena`, `IssuingChecksTheClaimsAsReadingWould`). A
  `Viewer` kept past its request read the connection's next claims beside its own `Id`; each request now has a
  generation, and a stale `Viewer` throws on `Claim` (`AViewerKeptPastItsRequestCantReadTheNextRequestsClaims`). The
  lifetime overloads named `options` in their exception (`ABadLifetimeIsNamedAsTheCallerWroteIt`). Two summaries I
  had left as placeholders got their text.
- Changes an existing app can notice, all deliberate: a `[Range]` on a `long` or `int` route parameter was ignored
  and is now enforced (an out-of-range value gets `NotFound` unless the app sets a reply), and one on a `Utf8` route
  value is CFH0103; a signed token with bytes after its header or payload object is refused; an anonymous `Viewer` is
  no longer `default(Viewer)`; `jwt.Issue(id, default)`, which bound to the `Span<byte>` overload, is now ambiguous.
  `Issue` writes every declared claim when it is given values, so it can't leave out one optional claim and keep
  another; that needs a way to tell an absent `Utf8` from an empty one.
- `FeedApp` is the proof that the spec needs nothing app-specific: it reads no header and parses no id itself. What
  it still does by hand is the spec's own: trimming the post and counting its characters. `Utf8.Trim()` and
  `Utf8.Slice(start, length)` came for it: views of the same bytes, bounds-checked against the text and not its
  buffer, so the trimmed post is no longer copied into the arena (`Utf8Tests`: edges, out-of-range starts and lengths
  including `int.MaxValue`, JSON whitespace only, a property test against `Span.Trim`, no allocation; 21 of 48 red
  on a stub).

### Second security review (d1a1f85)

Twelve findings: 0 critical, 0 high, 1 medium, 6 low, 5 info. `JsonCursor` held: no divergence from `Utf8JsonReader`
was found, and no invalid UTF-8 reaches a handler or a response. Each fix's test was red on the code before it,
except where the row says the test is a guard.

| # | Finding | Fix | Test | Red before |
|---|---|---|---|---|
| 1 | A `Viewer` read from another thread checked the generation once, then read the claim's place: it could read the next request's place, or a torn one | a seqlock: the generation is read (`Volatile.Read`) before and after the place, with a full fence between; `HandlerDone` bumps it first with `Interlocked.Increment` | `SpecOptionsTests.AViewerReadFromAnotherThreadThrowsOrReadsItsOwnRequest`: 16 threads read a kept `Viewer` for 3 s while one connection alternates alice and bob | 1,891 and 55,215 bad reads in two runs (absent, bob's place, `ArgumentOutOfRangeException`); 0 in three runs after |
| 5 | A `Viewer` made by hand read every claim as absent, so a typo passed its unit test | every name throws on a `Viewer` the dispatcher didn't make | `AViewerMadeByHandHasNoClaimsSoEveryNameThrows` | no exception |
| 12 | The claim ranges started at 0, "present at 0, length 0" | they start at -1 | the claim tests, as a guard | (hardening) |
| 2 | `Issue` held only the payload to the token's limit before asking the arena: a 2,950 to 4,010-byte value took 4 to 5 KB, then was refused | the exact token, numbers at their real length, is checked before anything is written or allocated | `ATokenPastItsLimitIsRefusedBeforeTheArenaIsAsked` (a 3,200-byte value at `MaxBytes` 4096), `ATokenIsRefusedExactlyWhenItWouldPassTheLimit` (2,900 to 3,100 bytes against the formula, a guard) | 1 allocation |
| 9 | `Issue` wrote `"sub":"0"` and `"sub":"-5"`, which every request then refused | a user ≤ 0 throws `ArgumentOutOfRangeException`, before the arena | `AUserTheVerifierWouldRefuseIsntIssued` (0, -5, `long.MinValue`) | issued |
| 3 | A later envelope reset each field but not a nested field's value and error, so the first wrapper's reason answered for the last (older than the spec options) | the reset clears `n{i}` and `e{i}` too | `ALaterWrapperReplacesAnEarlierOnesNestedErrorsToo` (three orders) | 1 of 3: `bad age` for a last wrapper with no author |
| 4 | On a body cut short in an array, `ReadTextArray` returned an array whose count ran past its texts; no handler saw it, since `ReadToEnd` decides first | `Invalid` when the cursor is malformed | `AnArrayInAMalformedBodyIsInvalid` (four cuts) | 4 of 4 |
| 8 | A signed `exp` of `1e400` reads as an infinity, so the token never expired (older than the spec options) | an `exp` or `nbf` that isn't finite makes the payload invalid; a past one, `-1e400` among them, stays expired | `ATimeThatIsntAFiniteNumberIsntAUsablePayload` (seven cases, `Verify` too) | 3 of 7: valid |
| 10 | `Jwt.Viewer` and `JsonSyntax.IsValid` were called only by tests, and the auth property test covered `Jwt.Viewer`, not the dispatcher's path | both deleted; the dispatcher's rule is `HttpApp.Refuses`, which `Dispatch`, `BinderPropertyTests` (through `JwtSchemeTests.Dispatched`: `Authenticate` and `Refuses`) and the hand-written benchmark app all call; the cursor check is test code | `ViewersMatchTheContestAuth`, `ArbitraryHeadersAgreeToo` | with the rule broken on purpose (a header without the scheme taken as anonymous), 2 red; the old test stayed green on that |
| 11 | `[Range(typeof(ulong), ...)]` was CFH0103 | `UInt64` among the types the generator reads | `RouteValueRangesItUses` (`typeof(ulong)`, `typeof(byte)`) | CFH0103 |
| 7 | `TryTextEquals` said its text was ASCII of at most 16 bytes; a paragraph said a body is malformed when the reader throws; `JsonSyntax` was described as the binders' check | rewritten or marked superseded | (comments) | |
| 6 | The benchmark's conclusions went past what two small bodies, best of two rounds, could support | `BodyBindingBenchmarks` adds a 2 KB order (numbers, a bool, nested objects, skipped keys, a long escaped note) and an early malformation, checks each body binds as its kind says, and runs three rounds for a median; the docs now claim what that measures | third iteration in `docs/benchmarks/2026-10-21-body-syntax-check.md` | (claims) |

## Fuzzing (tests/CodeFusion.Fuzz)

Coverage-guided fuzzing of everything that reads network bytes: 13 SharpFuzz targets (the table is in
`tests/CodeFusion.Fuzz/README.md`). Each throws when an invariant breaks, and the same `Run` serves three engines:

- `dotnet test`: `Corpus/<target>/` (551 seeds taken from the existing tests' inputs, plus the regressions below) and a
  fixed number of mutations from a seed fixed per target, about 50,000 inputs in 1.2 to 2 s. In the CI test loop.
- `dotnet run scripts/Fuzz.cs -- local`: publishes, instruments the libraries with sharpfuzz, and runs an in-process coverage loop on
  SharpFuzz's edge map. Runs on macOS, where libFuzzer can't.
- `dotnet run scripts/Fuzz.cs -- libfuzzer`: libFuzzer through libfuzzer-dotnet, built in CI from a pinned, checksummed source file. CI
  job `fuzz`, nightly and on dispatch, one matrix job per target, corpus kept between nights in the Actions cache.

The differential target runs the bytes through Kestrel's whole HTTP/1.1 connection in process (an in-memory
transport), not only `HttpParser`: where a request ends is decided in Kestrel's message bodies. A probe on Kestrel's
input tells "waits for more" from "refused". Both taking a request and disagreeing on method, decoded path, query,
body or end byte fails, as does one ending a request where the other waits. One side refusing is recorded.

Outside the project: `InternalsVisibleTo CodeFusion.Fuzz` in the Http and Data.Postgres csproj files (the parsers are
internal), the project in `CodeFusion.slnx`, and `.github/workflows/ci.yml` (the replay in the test loop, the `fuzz`
job, the test job skipped on the nightly schedule). SharpFuzz's version is in the project file, not
Directory.Packages.props: nothing else uses it.

### Findings

| # | Where | Minimal input | What happened | Status |
| --- | --- | --- | --- | --- |
| 1 | `RequestParser.ParseHead` | a request line or header line whose bare LF lands past its limit | 400 (bare LF) when the line came whole, 414 or 431 when the limit's worth came first | Fixed: the limit is checked before the line's syntax. `ParserTests.ALineOverrunningItsLimitIsRefusedForItsLengthHoweverItArrives` |
| 2 | `RequestParser` chunked body | `1 0\r\n`, `1 x\r\n`, `1\t\r\n` as a chunk-size line | taken as size 1: any SP or HT after the digits opened an extension. A reader that skips the space reads 0x10. Kestrel refuses (seen as an "only Kestrel refuses" class) | Fixed: after the size, whitespace only before `;` (RFC 9112 BWS). `BadChunksAre400` rows, `WhitespaceMayComeBeforeAnExtension` |
| 3 | `PgConnection.Fill` | `R` + length 0x3FFFFFFF (5 bytes) | allocated 1 GB before any of the message came; without TLS anyone on the path can send it | Fixed: the buffer doubles as the message's bytes fill it, up to its length. `PgProtocolTests.AnAnnouncedGigabyteIsNotAllocatedBeforeItArrives`, `AMessageLargerThanTheBufferGrowsItAsItArrives` |
| 4 | `PgConnection.CheckParameter` | ParameterStatus `integer_datetimes\0` (a name, no value) | `ArgumentOutOfRangeException` from the value's slice, at startup and mid-query | Fixed: refused as malformed. `AParameterStatusWithoutAValueIsMalformed`, rows in `BadStartups` and `Malformed` |
| 5 | `PgSettings.Parse` | `Port=9999999993` | `OverflowException` where callers expect `FormatException` | Fixed. `MalformedSettingsAreRefused` rows |
| 6 | `Jwt` payload and header (`jwt-claims`, on its first replay) | `{"sub":"2","user\u006eame":"","exp":1700000600}+`, and a 0xBF byte after the object | taken: the reader stopped at the root object's end and never read what followed, so a signed payload or header with trailing bytes verified. Only the secret's holder could make one, and every token issued here ends at its `}` | Fixed: whatever follows must be whitespace, as `JsonDocument` requires. `JwtClaimTests.AnythingAfterThePayloadOrHeaderMakesTheTokenInvalid`, `ARawByteAfterThePayloadIsInvalid` |

All six were red before their fix. Each minimized input is in `Corpus/` as `regression.*`. Nothing committed in either tree.

Seen, not changed then: `PgRow.GetUnixMicroseconds` added the 1970-to-2000 offset unchecked, so a value in Postgres's
last 30 years wrapped silently. Fixed with the Postgres client's review fixes.

### The Kestrel differential: one side refuses (recorded, not failures)

No case where both take a request and disagree, after 4.0 million inputs. Refusing can't be smuggled past the side
that refuses, so these stay as they are:

- Only Kestrel refuses: a Host value it can't parse (ours doesn't use Host's value); bytes 0x80 and up in header or
  trailer values (RFC 9110 obs-text; in a trailer Kestrel throws InvalidOperationException); HTTP/1.0 POST or PUT
  without Content-Length (411; RFC 9112 6.3 says the body is empty); whitespace before a chunk extension's `;`.
- Only ours refuses: bare LF or CR; obs-fold; whitespace before the colon; header-name bytes outside tchar (Kestrel
  takes `/` and `\`); Content-Length with Transfer-Encoding (Kestrel uses chunked); a coding other than one
  `chunked`; chunked in HTTP/1.0; `+3` as a length; absolute and asterisk forms; target bytes outside visible ASCII
  (Kestrel takes TAB, CR and DEL); NUL or invalid UTF-8 in the decoded path (Kestrel keeps the raw bytes); an empty
  line before the request line; a bare LF ending the trailers.
- One refuses while the other still waits for more: the same rules met before the request is complete.

### Runs

Local coverage loop on instrumented libraries, 3 minutes per target, two at a time (Apple M-series):

| Target | Executions | Per second | Edges | Corpus |
| --- | --- | --- | --- | --- |
| http-parser | 18.4 M | 102,000 | 479 | 426 |
| http-path | 18.8 M | 105,000 | 77 | 133 |
| http-kestrel | 4.0 M | 22,000 | 452 | 391 |
| json-body | 7.3 M | 41,000 | 438 | 477 |
| json-endpoint | 0.74 M | 4,000 | 3,784 | 1,612 |
| pg-row | 7.0 M | 39,000 | 133 | 65 |
| pg-error | 19.8 M | 110,000 | 74 | 88 |
| pg-startup | 7.4 M | 41,000 | 348 | 200 |
| pg-scram-first | 3.6 M | 20,000 | 58 | 20 |
| pg-scram-final | 22.9 M | 127,000 | 56 | 13 |
| pg-settings | 19.9 M | 111,000 | 140 | 231 |

Finding 4 came from this run. The other four came from the first replay and from the differential's classes.

### Proven red (on a copy of the tree)

Each break on its own, then its target on the seeds (the lone surrogate one needed 30 s of mutation):

- Content-Length body one byte short: http-parser (offset out of range), http-kestrel.
- `%2F` decoded into a slash: http-path (differs from RFC 3986), http-kestrel (the path differs).
- A chunked request ending before its last LF: http-kestrel (the end differs).
- A lone low-surrogate escape let through: json-body (ArgumentOutOfRangeException in TryUnescape), json-endpoint
  (the binder threw).
- DataRow trailing bytes ignored: pg-row. An ErrorResponse field without its NUL: pg-error.
- Any SCRAM server signature accepted: pg-scram-final. The iteration count unchecked: pg-scram-first.
- Settings without a user: pg-settings.

### Not done here

- libFuzzer didn't run: macOS can't, and Docker is off the table. `dotnet run scripts/Fuzz.cs -- build` and `local` ran here. The
  `libfuzzer` step and the `fuzz` job have only been through actionlint and shellcheck until the workflow runs on
  GitHub.
- `PgConnection.ReadReplies` is fuzzed only through `ParseError` and `PgRow.Parse`, not as a whole. A target that
  feeds it a job group and replies would cover the reply state machine.
## Mutation testing (Stryker.NET)

Stryker.NET 5.0.0 runs on this stack (xunit.v3 on Microsoft.Testing.Platform, .NET 10) with `--test-runner mtp`.
Each package's test folder has a `stryker-config.json` (Core, Http, Data.Postgres). They skip string mutations,
because a changed exception message is no test gap. Run one from its folder (the Postgres one needs the
CODEFUSION_TEST_* variables):

    dotnet tool install dotnet-stryker --version 5.0.0 --tool-path <dir>
    <dir>/dotnet-stryker -O <out>

Core: 73% at first, 98% after the gaps it found were closed. Those gaps were Timestamp's equal-value comparisons, the
Utf8 and Utf8List hash codes, RecordBytes' overflow check, a record that runs past an unchecked list's end, and
everything in Replies.cs. In InlineValueTaskSourceCore they were the version check in OnCompleted, completing twice,
and the late continuation on a nearly full stack. A run takes about 3 minutes with 2 workers. What survives can't be
observed from one thread, or is equivalent: the spin while Signal finishes, `preferLocal` on the thread-pool fallback,
the double-completion check (the sentinel throws the same exception), and a null-buffer branch that
`new ReadOnlySpan(null, 0, 0)` makes equivalent.

HTTP parser (RequestParser, ResponseHead, HttpDate, RequestMethod): 90.6% at first (37 minutes), 99.2% after the gaps
were closed. The gaps were limit edges (an unfinished head refused once it can't fit, chunk-size lines, trailers), a
bare LF line, an empty method or target, the Content-Length overflow guard, chunked with `Content-Length: 0`, an empty
size line, trailer control bytes, an empty path segment, the Date line and a status without a reason phrase. Three
survive and are equivalent: two trailer `>=` checks that return 431 a byte sooner, and an unreachable `w == 0`.

Postgres wire code (PgConnection, PgRow, PgAuth, PgSettings, PgException, PgJob): 69.4% at first (42 minutes), about
86% after (102 of 744 left). The gaps were real:

- `PgSettings.Parse` had no tests: URLs, aliases, sslmode, port range, missing user, Set and Clone.
- No int4, bool, float8, timestamptz or NULL parameter was ever checked on the wire, nor a parameter far larger
  than the buffer. `RandomParametersOfEveryTypeAndSizeDecodeAsTheyWereBound` now round-trips 150 seeded commands.
- Startup: short BackendKeyData, malformed ParameterStatus, float timestamps (at startup and mid-session), out-of-order
  SASL, no SCRAM-SHA-256, no password sent on a mid-SCRAM cleartext request or with a NUL.
- Out-of-place messages after an error or after the last statement, ErrorResponse fields (severity fallbacks, an
  empty message, a short code, interned known codes), row getters on NULL and wrong sizes, DateTime's range edges.
- A stale builder or held command once its job is reused (`PgJobReuseGuardTests`, alone: the job pool is shared).
- Connections the client drops are disposed.

What survives is mostly not observable from outside: buffer and pool sizes (Receive doubles a full buffer, so Fill's
grow-to-size is a shortcut), zeroing secrets, `Debug.Assert`, checks whose outcome is the same either way, the 1 GB
command guard, and the static strict UTF-8 encoder (Stryker can't switch a static initializer per test). Multi-statement
commands and the data-command interface's misuse checks are covered by CodeFusion.Data's tests, which this run didn't
include (`--test-project` can add them).

Targeted reruns: `mutate` takes character spans, `**/File.cs{start..end}`, so a rerun of only the lines that had
survivors takes minutes instead of the whole file's run.

## The event-loop transport (HttpTransport.EventLoop)

### Why

On one pinned CPU with 256 keep-alive connections the Sockets transport made 2 `recvfrom` and 1 `sendto` per
response. The first receive is the zero-byte one that waits for the next request: .NET's `ReceiveAsync` always tries
the call at once before it registers for readiness, and right after a response the client hasn't answered yet, so
that try finds nothing. Its readiness completion then costs no call (the runtime skips the syscall for a zero-byte
read), and the real `Receive` reads the request. No option of `System.Net.Sockets` skips the first try: any receive
started while its queue is ready makes the call. So one receive per request needs readiness from somewhere else.

### How

`HttpServerOptions.Transport = HttpTransport.EventLoop` starts `EventLoops` threads (one per core by default), each
with its own epoll (Linux) or kqueue (macOS) set. A connection's socket is added once, for reading (and for writing
once a send has had to wait) and edge-triggered (`EPOLLIN | EPOLLRDHUP | EPOLLET`, `EVFILT_READ` with `EV_CLEAR`), with its slot and a connection id
as the event's data, so a late event of a closed socket can't reach the connection object's next socket. The
connection's wait for a request becomes `Arm()`: no call, only a flag. The loop's edge completes the wait on the
loop's thread, and the request is read, handled and answered there until the handler first waits. Everything else in
`HttpExchange` (the parse, deadlines, the close watch, the linger, the send-rate checks) is the same code for both
transports; a send that waits for room waits on the loop (see the review fixes, 7).

Edges are not repeated, so the exchange keeps one that comes while no wait is armed (`_edge`), and whoever turns
`_armed` from 1 to 0 completes the wait, exactly once. Three ways an edge would be lost, each with its test:

- A receive that fills the buffer may leave bytes the kernel won't announce again: it sets the edge itself
  (`ARequestLargerThanTheBufferIsReadWhole`).
- The client's last bytes and its FIN or reset can come in one edge; the short read takes the bytes and doesn't see
  the end. An edge with `EPOLLRDHUP`, `EPOLLHUP`, `EPOLLERR` or `EV_EOF` stays set until a receive sees the end
  (`BytesAndTheCloseInOneEdgeStillCloseTheConnection`, which holds the loop with another connection's handler so both
  arrive in one edge). The stress test found this one: 30 to 60 of 1,200 connections that sent half a head and reset
  stayed open.
- Closing a socket takes it out of the set without an event. The connection's own close takes back its armed wait
  (`AnAsyncHandlersLastAnswerEndsTheConnection`); a timeout or the stop's abort, which close it from the heartbeat or
  the stop, queue a made-up edge to the thread pool (`AnIdleConnectionPastItsKeepAliveTimeoutIsClosed`,
  `AHeadThatStallsIsClosedAfterTheRequestTimeout`, `AStopClosesIdleConnectionsAtOnce`).

The loops end with the server's stop, after its connections; `ServerChecks` now fails a test whose server left one
running. Descriptors are `SafeFileHandle`s, so an `Add` racing the end gets `ObjectDisposedException` (the connection
closes quietly) rather than a reused descriptor; a refused `epoll_ctl` is an `IOException`, reported as a failed
connection. `struct epoll_event` is 12 bytes on x86 and x86-64 (packed) and 16 elsewhere; CI runs the suite on both.

A handler that blocks its thread holds up every connection on its loop. The default became EventLoop on Linux once
the 8-core TechEmpower run measured it (plaintext +20%, json +42%, db and fortunes +9% over Sockets); Sockets stays the
default elsewhere, and the choice for handlers that block or apps that put the p99 under a mixed load first.

### Direct calls, batches and listeners (later on the same branch)

- **Direct calls:** a loop's connections call `recv` and `send` themselves (`DirectSocket`: LibraryImport with
  SuppressGCTransition, since neither can block on a non-blocking socket). The handle is held for each call, as Socket
  holds it, so a socket the heartbeat closed is an ObjectDisposedException, never a call on a reused descriptor number.
  EAGAIN maps to WouldBlock and EINTR is retried. Peeks stay on Socket. One call copies at most 64 KB, so a GC never
  waits long on a call without its transition, and a receive that took 64 KB counts as one that filled the buffer.
- **Batches:** a loop handles every connection one wake-up found ready, runs the batch's actions
  (`HttpServerOptions.OnEventLoopBatch`, then the app's `IServiceRegistry.OnEventLoopBatch` registrations, in order),
  sends every held answer, then lets each connection go on. A connection that goes on into a pipelined request holds
  its answer again, and the actions run again: up to 8 rounds per wake-up, after which the next wait doesn't block.
  - The hold is awaited in `RunAsync`, whose box lives as long as the connection. Held inside `FlushAsync`, it
    allocated 17 to 20 B/request with 8 connections: the runtime's pooled boxes run out when several are held at once.
  - A handler the hook completes (a commit) runs its rest inside the hook, and its answer joins the batch.
  - The stop's wake ends the loop only after its batch is sent.
  - `CodeFusion.EventLoopThread.Current` (Core) is the loop's index on its thread and -1 elsewhere.
- **Listeners:** on Linux each loop has its own listening socket (SO_REUSEPORT), level-triggered in its epoll set.
  - It accepts up to 64 connections per wake-up with `accept4` and starts each on its own thread.
  - Out of descriptors or memory, or on any exception from an accept, an admission or a start, it stops watching the
    listener (one report per run of failures), and the heartbeat watches it again. The pause is one state changed by
    compare-and-swap, set after the listener is off. If turning it off fails, the listener is taken out of the epoll
    set (which frees memory rather than needing it) and the heartbeat adds it back: left on, the level-triggered
    listener would wake the loop at once, again and again.
  - The kernel spreads connections by a hash of their addresses, not in turn: a few long-lived connections may share a
    loop. `ListenerPerLoop = false` keeps one listener and gives connections to the loops in turn.
  - A loop that fails hands its listener to the runtime's accepts, so its queued connections aren't reset; with every
    loop failed, connections wait on their sockets as Sockets' do. The connections it had are closed with a FIN, as a
    timeout closes them: a reset (it used `Close(0)`) reached a connection it had just accepted before its client's
    own connect had finished, and failed that connect (Linux CI, arm64 and x64).
  - The runtime's `ReuseAddress` sets SO_REUSEPORT on Unix anyway (checked on macOS), so port sharing by a process of
    the same user is no different from Sockets'.
  - macOS keeps one listener and the runtime's accepts.

### Past the descriptor limit (fix/accept-past-resource-limits)

The $12 entry died past 65,535 descriptors: out of them, the runtime spun, said "Out of memory." and aborted
(docs/benchmarks/2026-10-21-default-connection-limit-past-fd-limit.md). On macOS the runtime's accept threw something
other than a `SocketException` at about 10k sockets, and accepting ended without a word.

- `MaxConnections` defaults to the soft RLIMIT_NOFILE less 1024, or less half of a limit under 2048 (the runtime
  alone holds about 50 descriptors on macOS). The server raises the soft limit to the hard one first, on macOS to at
  most OPEN_MAX (10,240), as CoreCLR's PAL does at start: NativeAOT doesn't, and under systemd's soft 1024 an AOT app
  would get 512 connections. 64-bit Linux and macOS only.
- The runtime's accept (Sockets, macOS's loops, a failed loop's listener) never ends while the server runs. Any
  exception is reported once per run of failures (`http accept failing`, then `http accept works again`) and the
  next accept waits 10 ms, doubling up to 1 s; if that wait's timer can't be made, the thread sleeps instead.
- Admitting or starting a connection that throws closes it and gives its object back. A loop's `Wrap` closes the
  descriptor if even its handle can't be made, and a run whose builder throws before it begins ends as a failed
  connection.

Each with its test first, seen failing before the fix (`EventLoopGuaranteeTests` unless named):

| Failure | Test |
|---|---|
| An accept that throws ended accepting | `AnAcceptThatThrowsIsReportedAndAcceptingGoesOn`, both transports |
| An admission that throws left the socket open | `AConnectionWhoseAdmissionThrowsIsClosedAndAcceptingGoesOn` |
| A start that throws (posting to a loop, queuing on the pool) ended accepting and kept the connection | `AConnectionWhoseStartThrowsIsClosedAndAcceptingGoesOn` |
| A backoff that throws ended accepting | `ABackoffThatThrowsDoesNotEndAccepting` |
| A listener that couldn't be turned off woke its loop at every wait | `OnLinuxAListenerThatCantBeTurnedOffIsTakenOutUntilTheHeartbeat` (Linux, CI only) |
| Past the descriptor limit the server hung at EMFILE | `DescriptorLimitTests.PastTheDescriptorLimitNewConnectionsAreClosedAndTheServerGoesOn`, the sample app under `ulimit -n 256` with 2 loops |
| NativeAOT kept a low soft limit | `DescriptorLimitTests.TheSoftLimitIsRaisedToTheHardOne`, `DescriptorLimitRaiseTests` (lowers the test process's own soft limit, alone) |

Not tested: out of memory inside `Wrap`'s handle and a run's builder, which no hook reaches; the Linux loop's tests
run only in CI.

### Review fixes (review of f00a731)

Each fix had its test first, seen failing on the code before it (`EventLoopGuaranteeTests` unless named).

| Finding | Fix | Test |
|---|---|---|
| 1 (high): a made-up "closed" edge (a timeout's Kick, a stale event) landing on the pooled object's next connection made every wait complete at once: a spin with no end | The edge word counts edges in its upper bits; a receive that finds nothing clears a hang-up seen before it began if no edge came since (after a FIN or an error recv never says EAGAIN). A Kick carries the connection id it was queued for. | `ALateClosedEdgeOnAReusedConnectionCostsOneReadNotASpin`: the late edge returns, costs at most 4 reads, and the connection and a new one still answer |
| 2 (high): the stop joined each loop with no limit, so a handler blocking a loop held the stop | Loops signal `Exited`; the stop waits for them at most a second, as it waits for a handler that never completes, and leaves a held loop's background thread to end with the handler | `AStopEndsWithinItsHardAbortWhileAHandlerHoldsTheLoop` |
| 3 (medium): a client keeping every read full held its loop | 16 reads per connection per wake-up on the loop's thread, then its armed wait goes to the loop's next round (after a wait that doesn't block), behind the others | `AClientThatPipelinesWithoutPauseDoesNotHoldItsLoop`: a neighbour answered within 500 ms while one client floods |
| 4 (medium): CI never ran the Debug-only tests on epoll | Left to the coordinator, who adds a Linux Debug pass in PR #1 | |
| 5 (medium): the cancel of `Aborted`, and what the handler runs after it, ran on the loop's thread | `Gone` is queued to the thread pool for a loop's connection, as the heartbeat's is | `ACancelCallbackThatBlocksDoesNotHoldTheLoop`: a callback sleeping 3 s, a neighbour answered within 1 s |
| 6 (low): the id check before `Readable` leaves a window for a stale event | Harmless after 1: a stale edge costs one read | |
| 7 (low): a send that waited registered the socket with the runtime's epoll for good | A send waits for room on its loop: EPOLLOUT added edge-triggered to the socket's registration (it fires only when a full socket gets room), or a one-shot EVFILT_WRITE. A Kick, and the slow-send cut, wake it. | `ASendThatWaitsWaitsOnTheLoop`: 8 MB answers, no runtime send waits on the loop, at least one on Sockets |
| 8 (low): a loop whose wait failed kept getting connections, closed without a word | The failed loop closes its listener (the kernel sends new connections to the others) and its connections; new ones go to the loops still running, or to Sockets' waits with none left | `ALoopThatFailsTakesNoNewConnections`, with a Debug hook that fails one wait |
| 9 (low): `BytesAndTheCloseInOneEdgeStillCloseTheConnection` failed if `/block` ran on a pool thread | It holds only the loop's thread and sends `/block` again until it runs there | |
| 10 (low): the docs didn't say what the loop gives up | SECURITY.md, `Aborted`, the package README | |
| 11 (low): copied helpers, a server without the suite's transport, allocation rows run twice in CI | `TestTransport.Use` and `UseWithHook` over `EventLoop.Supported`; `SlowClientTests` applies the transport; CI's second run leaves out the allocation classes | |

### Second review fixes (review of 3a8d89d)

| Finding | Fix | Test |
|---|---|---|
| 1 (medium): a handler started while answers were sent, after the batch's actions, could wait for a commit that never came | Everything on a loop's thread is in a batch; the actions run again while sending started handlers, up to 8 rounds, then the next wait doesn't block | `PipelinedWritesThatOnlyTheHookCompletesAreAllAnswered` (red with one round) |
| 2 (medium): a heartbeat's resume between "paused" and "listener off" left it off | One accept state changed by compare-and-swap, published after the listener is off | `OnLinuxALoopOutOfDescriptorsPausesAndTheHeartbeatResumesIt`, with a hook that resumes mid-pause (Linux, CI only) |
| 3 (medium): the budget counted reads, and the send phase ran a connection's backlog before its neighbours' answers went | 16 handler calls per connection per wake-up as well (past it the connection parks at a request boundary until the next round); the send phase sends every held answer before any connection goes on | `ANeighbourOfAFloodOfSlowRequestsWaitsAtMostOneShare`: 1 ms handlers flooded, a neighbour within 200 ms (1,120 ms without the budget) |
| 4 (low): MaxConnections could be passed by loops admitting at once | The limit is checked where the count goes up, under the lock | `OnLinuxMaxConnectionsHoldsAcrossLoops` (Linux, CI only) |
| 5 (low): the out-of-descriptors branch could throw past the loop, and reported the errno as a SocketError | The listener's branch is guarded like a connection's; the pause catches a listener the stop closed; the report is an IOException with the errno | `OnLinuxAStopStartedByTheAcceptFailureReportEndsClean`, `OnLinuxAnAcceptErrorOfOneConnectionSkipsOnlyIt` (Linux, CI only) |
| 6 (low): sends without a GC transition had no size bound | One direct call copies at most 64 KB; a receive that took 64 KB counts as full | `ConnectionsOnTheLoopsKeepWorkingThroughCollections`; the 300 KB body row (not red on macOS: its buffers never leave 64 KB queued) |
| 7 (low): hash spreading, resets on a failed loop, no real fallback | `ListenerPerLoop` (on by default) keeps one listener and turns when off; a failed loop hands its listener to the runtime's accepts; with every loop failed, connections fall back to Sockets' waits | `WithOneListenerConnectionsGoToTheLoopsInTurn`, `ALoopThatFailsTakesNoNewConnections(listenerPerLoop)`, `WhenEveryLoopFailsConnectionsFallBackToSockets` |
| 8 (low): test gaps | The Linux accept tests above; the stop test's bound 4 s; EventLoop allocation rows for a body, an error, a 404 and a 401 | |
| 9 (low): stale docs | This section, the package README, SECURITY.md (the exact bound, and port sharing for both transports) | |
| (coordinator) a connection's first request ran on the pool thread that started it, outside any batch | A new connection starts with no edge (epoll's ADD and kqueue's EV_ADD report bytes already waiting), and one the runtime accepted is started on its loop's thread (a queue per loop, and its wake) | `AConnectionsFirstRequestIsHandledInABatch`, for both listener settings (red before) |

### Buffers per loop (perf/http-loop-pools)

The C4 profile of json (1.17M req/s) had `System.Threading.Lock` at about 2% of the app's time: `BufferPool`'s locked
shards, rented and returned around every request.
- Each pool the server's connections use (request, response, and the app's arena chunks) now keeps a stack per event
  loop, touched only by that loop's thread, so no lock.
- A rent or return on a loop's thread goes there first. Any other thread (a continuation off the loop, Sockets) uses
  the shards, as does what a full stack can't take. A loop's stack never sees another thread.
- Each stack holds capacity / (2 x loops) arrays (at least one, while the capacity has one per loop), taken out of the
  shards' share: `MaxPooledBuffers` stays the whole bound. With fewer arrays than loops there are no stacks.
- A loop that ends (its wait failed, or the stop) gives its stacks back to the shards on its own thread.
- Tests (`LoopPoolTests`), each red with its part broken:
  - rents and returns go through the loop's stack;
  - a buffer given back off its loop goes to the shards;
  - each loop keeps exactly its share (red with the stacks off);
  - the pools keep no more than `MaxPooledBuffers` in all (red with the shards at the full capacity);
  - a failed loop's stacks go back to the shards (red without the drain);
  - the arena's chunks use it too.
- `ServerChecks`' pool balance counts the loop stacks; the allocation tests are unchanged.

### Tests

`EventLoopTests`, `EventLoopBatchTests`, `EventLoopGuaranteeTests` and `LoopPoolTests`, on kqueue here and epoll in CI. `AllocationTests` and `AppAllocationTests` have EventLoop rows
(0.004 to 0.05 B/request, as Sockets). `TestTransport` makes every server the tests start use the transport that
`CODEFUSION_HTTP_TRANSPORT` names, and CI runs `CodeFusion.Http.Tests` a second time with `EventLoop`. Debug builds count
every receive call (`HttpServer.SocketReceives`): `EachKeepAliveRequestCostsThisManyReceives` asserts 1 per request on
the loop and 2 on Sockets.

Proven red (broken in the tree, seen failing, restored): no edge after a full read (the 64 KB body hangs); the close
flag ignored (both close-in-one-edge cases); no made-up edge after a timeout or abort (the three timeout and stop
tests); no disarm on close (the async handler's last answer leaves the connection open).

Green on this Mac (SDK 10.0.402), 0 warnings for the whole tree in Debug and Release. `CodeFusion.Http.Tests`: Debug
516 pass and 26 skip, Release 532 pass and 10 skip, each with Sockets and with `CODEFUSION_HTTP_TRANSPORT=EventLoop`.

### Numbers

The $12 challenge entry (csharp-codefusion-XeonFX) with this library, in the cf-bench VM (server pinned to one arm64
CPU with 2 GB, load generator on three others, 256 keep-alive connections, no think time; health, post and feed for
15 s each, the mix for 30 s). "Before" is the parent commit's library, "after" the same entry with `Transport =
HttpTransport.EventLoop`. Syscalls from `strace -c -f` over 5 s, per `sendto` (one per response).

| | before (Sockets) | after (EventLoop) | change |
|---|---:|---:|---:|
| GET /health | 110,272 req/s | 133,529 | +21% |
| GET /posts/:id | 74,377 | 84,842 | +14% |
| GET /feed | 34,272 | 37,570 | +10% |
| mix | 33,710 | 35,370 | +5% |
| recvfrom per response (health, mix) | 2.000, 2.000 | 1.000, 1.000 | |
| sendto per response | 1.000 | 1.000 | |
| epoll_pwait per response (health) | 0.001 | 0.004 | |

The challenge's 42 checks pass on both. The same library with Sockets did 115 and 118k on health against 112 and 116k
before, so that path didn't move. In the same session the epoll entry (#74) did 181k on health: per request it spends
about 5.1 µs in the kernel, this server 6.0 µs in the kernel and 1 µs in its own code and libc, spread thin
(`RunAsync` 1.4%, `ParseHead` 0.8%, `FlushAsync` 0.6%, the runtime's send wrapper 0.6%). Both spend about 20% in
`__wake_up_sync_key`, the wakeup of the client, and both now make one receive and one send per request. #74 reads
every ready connection before it sends any answer; whether batching the sends that way explains the kernel's extra
0.9 µs is not measured.
