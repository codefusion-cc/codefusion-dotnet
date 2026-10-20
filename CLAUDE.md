# Repository rules

## Scripts are C#

- Every script in this repository is C#: a .NET 10 file-based app in `scripts/`, run with
  `dotnet run scripts/<Name>.cs -- <args>`. Building, testing and maintaining the repository needs nothing but the .NET SDK.
- No Python, shell, PowerShell, JavaScript or any other language, not even for a one-off helper that gets committed.
- Tooling goes in `scripts/`, never in `eng/`, `tools/` or `build/`.
- A script finds the repository root through `[CallerFilePath]`, so it runs from any directory.
- The only non-C# allowed is the CI workflow YAML and the short commands in its `run:` steps. Anything longer than a
  few commands there becomes a script in `scripts/`.
- Examples: `scripts/MapOverloads.cs` writes the Map overloads, and `scripts/Fuzz.cs` builds and runs the fuzzers.

## Every benchmark run is written down

- After each benchmark iteration (BenchmarkDotNet, a load test, a contest rig run), add a file to `docs/benchmarks/`
  before moving on, even when the change made things worse or was thrown away. A run nobody wrote down didn't happen.
- One file per iteration, named `YYYY-MM-DD-<short-slug>.md`, so branches never edit the same file. Add a line for it
  to the table in `docs/benchmarks/README.md`.
- Each file says what was tested and why, the commit or branch, the machine, the method and the numbers, and what we
  concluded. `docs/benchmarks/README.md` has the template.
