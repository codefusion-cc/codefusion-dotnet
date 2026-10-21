# What the body syntax check costs a well-formed body, and what binding first costs a malformed one

- **Question:** feat/http-spec-options made the generated body binders run `JsonSyntax` over every body before
  reading it, so a malformed body is answered without a `JsonException`. That reads a well-formed body twice. The fix
  binds first and runs the check only where binding failed without the reader throwing (a root that isn't an object).
  How much did the check cost, and does the fix give a well-formed body back what it cost before the branch?
- **Code:** before = 5eb3a83 (the branch's base); after = 1f80d20 (check first); fix = 1f80d20 plus the binder-first
  emitter change (committed right after this run). Each side built from its own copy of the tree (`git archive`), with
  the same `benchmarks/CodeFusion.Benchmarks/BodyBindingBenchmarks.cs`, which uses only what every side has.
- **Machine:** Apple M1 Pro, 8 cores, macOS 26.7.1, .NET SDK 10.0.402, runtime 10.0.13 Arm64, BenchmarkDotNet
  0.15.8. Shared with other sessions' builds: load average 12.2 at the start, 5.8 at the end.
- **Method:** `dotnet CodeFusion.Benchmarks.dll --job short --filter "*BodyBindingBenchmarks*"` (ShortRun, server GC,
  the repository's `BenchConfig`). Two rounds, the order reversed: before, after, fix; then fix, after, before. The
  figure is the best mean of the two rounds; both rounds are in brackets. Each benchmark binds one body from a request
  buffer and resets the arena: Login (`{"user":{...}}`, 2 texts, 57 bytes) and Article (an escape, a list, 4 texts,
  176 bytes). Valid, a bad field (a number where a text goes), and malformed (cut short).

| Case | Before | After (check first) | Fix (bind first) | Fix vs before |
|---|---|---|---|---|
| Login, valid | 137.3 ns (137.3, 137.9) | 238.0 ns (238.0, 243.0) | 143.3 ns (151.4, 143.3) | +4% |
| Article, valid | 360.9 ns (360.9, 388.7) | 602.2 ns (636.0, 602.2) | 386.9 ns (407.5, 386.9) | +7% |
| Login, bad field | 128.3 ns (131.5, 128.3) | 210.7 ns (211.9, 210.7) | 130.8 ns (138.2, 130.8) | +2% |
| Article, bad field | 288.0 ns (289.7, 288.0) | 466.8 ns (466.8, 474.9) | 310.1 ns (319.8, 310.1) | +8% |
| Login, malformed | 3,802 ns, 1,192 B, 1 exception | 109.8 ns, 0 B | 4,223 ns, 1,192 B, 1 exception | +11% |
| Article, malformed | 4,222 ns, 1,192 B, 1 exception | 231.9 ns, 0 B | 4,748 ns, 1,192 B, 1 exception | +12% |

- **Memory and CPU:** nothing allocates on a well-formed body or a bad field on any side. A malformed body allocates
  its `JsonException`, 1,192 B, before the branch and with the fix; the check-first side allocated nothing.
  `AppAllocationTests` agree over HTTP: 0 B per request for every other case, and the malformed feed case now has an
  allowance of one exception.
- **Conclusion:**
  - Checking first made binding a well-formed body 65 to 75% slower (about 100 ns for Login, 240 ns for Article), and
    a bad field as much. That is under 1% of a request's CPU here (37 to 81 µs, README), but it was paid on every body.
  - Binding first gives that back. The fix is 2 to 8% over the base on the good paths, within this loaded Mac's
    spread (a single side's two rounds differ by up to 8%), and the binder's work is the base's plus an empty-body
    test.
  - The price: a malformed body throws again, about 4 µs and 1,192 B against 0.1 to 0.2 µs and 0 B with the check
    first. Kept, on the coordinator's call: valid bodies are the traffic, malformed ones are rare and cost the sender a
    request each. A body whose root isn't an object is still classified without throwing.
  - Next, if malformed floods matter: an opt-in to check first, or a reader that reports a syntax error without
    throwing.

## Second iteration: one reader that binds and checks in a single pass

- **Question:** can a malformed body cost no exception and no allocation while a well-formed one costs no more than
  before the branch? The binders now read through `JsonCursor`, CodeFusion's own reader built on `JsonSyntax`'s
  state machine. It gives `Utf8JsonReader`'s tokens and reports a syntax error as `Malformed` instead of throwing. One
  pass binds and checks; there is no `try`, no `catch` and no second read.
- **Code:** before = 5eb3a83, check first = 1f80d20, bind first = 7ab7652, single pass = 7ab7652 plus this change
  (committed right after this run). The same `BodyBindingBenchmarks.cs` in each tree.
- **Machine:** as above. Load average 4.5 at the start, 10.7 at the end.
- **Method:** as above, four sides, two rounds: before, check first, bind first, single pass; then the reverse.

| Case | Before | Check first | Bind first | Single pass | Single vs before |
|---|---|---|---|---|---|
| Login, valid | 138.0 ns (138.0, 143.4) | 219.5 ns (223.0, 219.5) | 138.7 ns (138.7, 141.8) | 110.7 ns (115.3, 110.7) | −20% |
| Article, valid | 363.9 ns (363.9, 370.7) | 565.6 ns (572.1, 565.6) | 368.8 ns (368.8, 375.3) | 336.5 ns (355.3, 336.5) | −8% |
| Login, bad field | 129.6 ns (129.6, 130.7) | 211.0 ns (211.0, 217.5) | 127.7 ns (129.4, 127.7) | 108.0 ns (127.7, 108.0) | −17% |
| Article, bad field | 289.8 ns (289.8, 295.0) | 469.0 ns (469.0, 497.7) | 289.1 ns (319.5, 289.1) | 270.2 ns (275.6, 270.2) | −7% |
| Login, malformed | 3,873 ns, 1,192 B, 1 exception | 106.5 ns, 0 B | 3,828 ns, 1,192 B, 1 exception | 98.5 ns, 0 B, none | −97% |
| Article, malformed | 4,340 ns, 1,192 B, 1 exception | 218.3 ns, 0 B | 4,422 ns, 1,192 B, 1 exception | 246.5 ns, 0 B, none | −94% |

- **Memory and CPU:** nothing allocates on any body with the single pass, and nothing throws. `AppAllocationTests`
  has the malformed feed case back under the 0.5 B bar with every other case, with no allowance.
- **Conclusion:**
  - (Narrowed by the third iteration, which measures realistic bodies.) On these two bodies the single pass was 7 to
    20% faster on well-formed and bad-field bodies, and a malformed body costs 0.1 to 0.25 µs and nothing on the heap instead of about 4 µs and an
    exception. Kept.
  - It is faster because the cursor does less than `Utf8JsonReader`: one final block, no comments, no options, no
    value sequences. It isn't "faster JSON" in general: a string's UTF-8 is still checked once, by the binder.
  - What keeps it honest is the oracle, not the speed: `JsonSyntaxTests` and the `json-syntax` fuzz target compare
    every token (type, start, bytes, escape, the Int64, Int32 and Double readings) with `Utf8JsonReader`'s.

## Third iteration: realistic bodies, three rounds, median

- **Question:** the second review found the first two iterations too narrow to support "faster on every case": two
  small bodies of texts and a list, no numbers, bools, nested objects or skipped values, malformed only at the end,
  best of two rounds under load. Does the single pass still cost no more than the base on a realistic body?
- **Code:** before = 5eb3a83, single pass = d1a1f85, current = 9cb0640 (the second review's fixes) plus this
  benchmark. The same `BodyBindingBenchmarks.cs` in each tree; its setup now checks that each body binds, or
  doesn't, as its kind says.
- **Bodies:** Login (57 B, two texts), Article (176 B, an escape and a list), and Order (about 2 KB: an Int64, an
  Int32, a Double, a bool, a list, a required and an optional nested object, unknown keys holding an object and
  nested arrays to skip, a 1.6 KB note with escapes). Each valid, with a bad field (a number as text, or the price
  as a string), malformed at its end (cut short) and malformed early (`{x` at its second byte).
- **Machine:** as above. Load average 3.1 to 10.9.
- **Method:** `--job short`, three rounds: before, single, current; current, single, before; before, single,
  current. The figure is the median of the three rounds' means. One side's rounds differ by up to 20% (Login, valid,
  current: 112.7 to 135.5 ns) and 28% (Order, malformed, before), so differences under about 10% are noise here.

| Case | Before | Single pass | Current | Current vs before |
|---|---|---|---|---|
| Login, valid | 144.3 ns | 110.6 ns | 131.6 ns | −9% (noise) |
| Login, bad field | 130.0 ns | 107.9 ns | 106.5 ns | −18% |
| Login, malformed at the end | 3,938 ns, 1,192 B | 102.9 ns, 0 B | 101.1 ns, 0 B | −97% |
| Login, malformed early | 3,500 ns, 1,040 B | 24.0 ns, 0 B | 23.0 ns, 0 B | −99% |
| Article, valid | 381.8 ns | 336.0 ns | 339.4 ns | −11% |
| Article, bad field | 288.2 ns | 262.1 ns | 263.6 ns | −9% (noise) |
| Article, malformed at the end | 4,969 ns, 1,192 B | 247.9 ns, 0 B | 243.4 ns, 0 B | −95% |
| Article, malformed early | 3,455 ns, 1,040 B | 25.8 ns, 0 B | 28.4 ns, 0 B | −99% |
| Order, valid | 3,203 ns | 2,169 ns | 2,168 ns | −32% |
| Order, bad field | 3,060 ns | 2,075 ns | 2,002 ns | −35% |
| Order, malformed at the end | 6,580 ns, 1,208 B | 1,457 ns, 0 B | 1,446 ns, 0 B | −78% |
| Order, malformed early | 3,446 ns, 1,040 B | 38.6 ns, 0 B | 37.7 ns, 0 B | −99% |

- **Memory and CPU:** no body allocates on the single pass or current; before, every malformed body allocated its
  `JsonException` (1,040 to 1,208 B).
- **Conclusion:**
  - On these three bodies the single pass costs no more than the base on any valid or bad-field body, and less on
    the 2 KB order (a third less), where the cursor's simpler number, string and skip paths have the most to do. The
    first two iterations' "7 to 20% faster on every body" is replaced by this: within noise to a third faster,
    depending on the body, measured on three bodies.
  - A malformed body costs no exception and no allocation, and stops where it breaks: 23 to 38 ns at the second
    byte, against 3.4 µs and 1 KB before.
  - The second review's fixes cost nothing measurable (current against single pass, every case within noise).
  - Not measured: bodies over 16 KB, very deep nesting, and the default (non-short) job.
