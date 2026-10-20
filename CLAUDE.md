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
