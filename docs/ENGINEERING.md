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
  Not covered: accept failing (it needs the process out of descriptors) and `Report`'s own catch on the accept and
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
  it saw nothing of the group, and its error stands): one more round trip per failure in the middle of a group.
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

## Options for an app's own spec (the feed API)

The $12 contest's feed API couldn't be written with the library: it kept a hand-written auth layer and parsed its
route values itself. Each gap became a public option with today's behavior as its default, not a case for that app.
Each test was red first: on a stub of the API that compiled but didn't do it.

| Gap | Option | Test | Red before |
|---|---|---|---|
| the scheme was `Token ` only | `JwtOptions.Scheme`, and `AddJwtAuth(secret, JwtOptions)` and `Jwt(secret, JwtOptions)` beside the old overloads | `JwtSchemeTests` (exact case, one space, no tab, a Cyrillic look-alike, any RFC 9110 token, invalid schemes refused at startup), `SpecOptionsTests.ABearerAppReadsBearerHeadersOnly`, `TheDefaultAppStillReadsToken` | 17 of 34: `Bearer` refused, `Token` still read |

What it decided:

- The scheme is matched as bytes, case included, then exactly one space, as `Token ` was. RFC 9110 calls schemes
  case-insensitive and RFC 6750 allows several spaces; reading them leniently would change what `Token` apps accept,
  and an exact match leaves nothing for a proxy and the app to read differently. Neither is an option yet.

## Fuzzing (tests/CodeFusion.Fuzz)

Coverage-guided fuzzing of everything that reads network bytes: 11 SharpFuzz targets (the table is in
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

All five were red before their fix. Each minimized input is in `Corpus/` as `regression.*`. Nothing committed in either tree.

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
