// Checks the packed CodeFusion packages: each depends on the other CodeFusion packages at exactly its own version
// ("[1.2.3]"), since they use each other's internals and two releases mixed would fail at the first call that crosses;
// and CodeFusion.Data.Sqlite depends on the one SQLite build it was tested with, exactly.
// Run it on what dotnet pack wrote: dotnet run scripts/CheckPackages.cs -- artifacts (CI does, right after packing).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

const string SqliteNative = "SourceGear.sqlite3";

string dir = args.Length > 0 ? args[0] : "artifacts";
var packages = Directory.GetFiles(dir, "CodeFusion.*.nupkg").Where(p => !Path.GetFileName(p).StartsWith("CodeFusion.Security.", StringComparison.Ordinal)).ToArray();
if (packages.Length == 0)
{
    Console.Error.WriteLine($"no CodeFusion packages in {dir}");
    return 1;
}

var problems = new List<string>();
foreach (string path in packages)
{
    using var zip = ZipFile.OpenRead(path);
    var entry = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal) && !e.FullName.Contains('/'));
    using var stream = entry.Open();
    var nuspec = XDocument.Load(stream);
    XNamespace ns = nuspec.Root!.Name.Namespace;
    var metadata = nuspec.Root.Element(ns + "metadata")!;
    string id = metadata.Element(ns + "id")!.Value, version = metadata.Element(ns + "version")!.Value;
    var dependencies = metadata.Descendants(ns + "dependency").ToArray();
    foreach (var d in dependencies)
    {
        string on = d.Attribute("id")!.Value, range = d.Attribute("version")?.Value ?? "";
        if (on.StartsWith("CodeFusion.", StringComparison.Ordinal) && range != $"[{version}]")
            problems.Add($"{id} {version} depends on {on} {range}, not exactly [{version}]");
        if (on == SqliteNative && !(range.StartsWith('[') && range.EndsWith(']') && !range.Contains(',')))
            problems.Add($"{id} {version} depends on {on} {range}, not on one version exactly");
    }
    Console.WriteLine($"{id} {version}: {string.Join(", ", dependencies.Select(d => $"{d.Attribute("id")!.Value} {d.Attribute("version")?.Value}"))}");
}

foreach (string p in problems) Console.Error.WriteLine(p);
return problems.Count == 0 ? 0 : 1;
