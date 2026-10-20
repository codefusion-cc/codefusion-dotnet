# CodeFusion.Fuzz

Coverage-guided fuzzing of the code that reads bytes from the network, and of what the SQLite provider encodes. Each
target takes bytes and throws when an invariant breaks; the same targets run under libFuzzer and in the test suite.

| Target | What it runs | What must hold |
| --- | --- | --- |
| `http-parser` | `RequestParser.ParseHead`/`ParseBody` as `HttpExchange` drives them, whole, in pieces cut by the input's hash, and a byte at a time; default and tiny limits | no exception; refusals are 400, 413, 414, 431 or 505; every offset inside the bytes parsed so far; pieces give what the whole gives |
| `http-path` | `RequestParser.NormalizePath` | the same as one decoding pass plus RFC 3986 `remove_dot_segments`; no dot segment left; idempotent |
| `http-kestrel` | the same bytes through Kestrel's HTTP/1.1 connection, in process | where both take the request: same method, decoded path, query, body and end byte. Where one refuses, the class is recorded, not failed |
| `json-body` | the sample app's generated body binders (`NewArticle`, `Login`) | no exception; a 4xx or a body that meets the type's rules; invalid JSON never binds; texts are UTF-8 |
| `json-endpoint` | the sample app over loopback: `POST /api/articles`, `/api/users/login`, `/api/slow` | never a 500 or a logged exception; the reply is JSON |
| `pg-row` | `PgRow.Parse` and every getter | only `PgConnectionException`, and `InvalidCastException` from a getter; columns tile the row |
| `pg-error` | `PgConnection.ParseError` | only `PgConnectionException` |
| `pg-startup` | `PgConnection.Open` against a transport that replies with the bytes | only `PgConnectionException` or the server's `PgException`; no more memory than the bytes justify |
| `pg-scram-first`, `pg-scram-final` | `PgScram.ClientFinal`, `VerifyServerFinal` (RFC 7677's vector) | only `PgConnectionException`; only the right signature verifies |
| `pg-settings` | `PgSettings.Parse` | only `FormatException` or `ArgumentException`; what parses keeps its own rules |
| `sqlite-json-array` | a text array as `CodeFusion.Data.Sqlite` binds it (one JSON text), read back with `json_each` | valid UTF-8 comes back element for element, as text; invalid UTF-8 never adds or drops an element |
| `sqlite-saved-row` | a write's rows as the SQLite writer keeps them until its group commits, against the same rows read in place | the same NULLs; wherever a kept row's getter answers, the same answer as in place |

The sample app is `tests/CodeFusion.Http.Tests/Sample/SampleApp.cs`, compiled here by the endpoint generator.

## Running

On every build, `dotnet test` replays `Corpus/<target>/` and a fixed number of mutations of it from a seed fixed per
target. `CODEFUSION_FUZZ_SCALE=20` runs twenty times as many.

For longer runs (everything lands in `artifacts/fuzz/`):

```bash
dotnet run scripts/Fuzz.cs -- build
dotnet run scripts/Fuzz.cs -- local http-kestrel 600
dotnet run scripts/Fuzz.cs -- report http-kestrel artifacts/fuzz/corpus/http-kestrel
```

`build` publishes the project and instruments the libraries with sharpfuzz. `local` is an in-process
coverage-guided loop that runs on any OS, macOS included. On Linux, `dotnet run scripts/Fuzz.cs -- libfuzzer <target> <seconds>` runs
libFuzzer through [libfuzzer-dotnet](https://github.com/Metalnem/libfuzzer-dotnet) (`LIBFUZZER` is its path). CI does
that every night for each target (`.github/workflows/ci.yml`, job `fuzz`).

To replay one input, a libFuzzer crash file for example:

```bash
CODEFUSION_FUZZ=pg-startup artifacts/fuzz/bin/CodeFusion.Fuzz path/to/crash-file
```

## A finding

1. `dotnet run scripts/Fuzz.cs -- report <target> <dir>` minimizes it and names its kind.
2. Write the failing test in the test project of the code that broke, and see it fail.
3. Fix the code, or record why the behavior is right.
4. Add the minimized input to `Corpus/<target>/` so the replay keeps it, and list it in docs/ENGINEERING.md ("Fuzzing", "Findings").

The Kestrel differential fails only when both sides take a request and disagree, or when one ends a request where
the other waits for more: that is the request-smuggling class. A request one side refuses can't be smuggled past
that side, so those classes are recorded instead, printed by the replay test and by `report`.
