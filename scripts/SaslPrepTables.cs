// Writes the Unicode range tables in src/CodeFusion.Data.Postgres/PgSaslPrep.cs (SASLprep, RFC 4013, which is RFC 3454's
// stringprep): the RFC's tables from its text, and MayChange from the Unicode 16.0.0 data (Postgres 18's).
// Run it after changing it: dotnet run scripts/SaslPrepTables.cs (an argument writes the file somewhere else instead, to compare).
// The inputs come from rfc-editor.org and unicode.org into artifacts/saslprep/ the first time; delete a file there to fetch it again.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

const string Unicode = "https://www.unicode.org/Public/16.0.0/ucd/";
const int LineWidth = 119;

string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, ".."));
string cache = Path.Combine(root, "artifacts", "saslprep");
string target = Path.Combine(root, "src", "CodeFusion.Data.Postgres", "PgSaslPrep.cs");
string path = args.Length > 0 ? args[0] : target;

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
string[] Lines(string name, string url)
{
    string file = Path.Combine(cache, name);
    if (!File.Exists(file))
    {
        Directory.CreateDirectory(cache);
        string part = file + ".part";
        File.WriteAllBytes(part, http.GetByteArrayAsync(url).GetAwaiter().GetResult());
        File.Move(part, file);
    }
    return File.ReadAllLines(file);
}

string[] rfc = Lines("rfc3454.txt", "https://www.rfc-editor.org/rfc/rfc3454.txt");
string[] props = Lines("DerivedNormalizationProps.txt", Unicode + "DerivedNormalizationProps.txt");
string[] unicodeData = Lines("UnicodeData.txt", Unicode + "UnicodeData.txt");
string[] ages = Lines("DerivedAge.txt", Unicode + "extracted/DerivedAge.txt");

// One RFC table: the lines between "----- Start Table X -----" and "----- End Table X -----"; "0000-001F; ...", "00A0; ..." or just "05D0-05EA".
List<(int, int)> RfcTable(string name)
{
    int start = Array.FindIndex(rfc, l => l.Trim() == $"----- Start Table {name} -----");
    int end = Array.FindIndex(rfc, l => l.Trim() == $"----- End Table {name} -----");
    if (start < 0 || end < start) throw new InvalidOperationException($"table {name} not found in the RFC");
    var ranges = new List<(int, int)>();
    foreach (string line in rfc[(start + 1)..end])
    {
        Match m = Regex.Match(line, @"^\s*([0-9A-F]{4,6})(?:-([0-9A-F]{4,6}))?\s*(?:;|$)");
        if (!m.Success) continue; // a blank line, or a page break (footer, form feed, header)
        int lo = Hex(m.Groups[1].Value);
        ranges.Add((lo, m.Groups[2].Success ? Hex(m.Groups[2].Value) : lo));
    }
    if (ranges.Count == 0) throw new InvalidOperationException($"table {name} is empty");
    return ranges;
}

static int Hex(string s) => int.Parse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

// Sorted, with overlapping and adjacent ranges joined.
static List<(int, int)> Merge(IEnumerable<(int Lo, int Hi)> ranges)
{
    var merged = new List<(int Lo, int Hi)>();
    foreach (var (lo, hi) in ranges.OrderBy(r => r.Lo))
    {
        if (merged.Count > 0 && lo <= merged[^1].Hi + 1) merged[^1] = (merged[^1].Lo, Math.Max(merged[^1].Hi, hi));
        else merged.Add((lo, hi));
    }
    return merged;
}

// The code points of "XXXX" or "XXXX..YYYY" at the start of a UCD line, and the fields after it.
static (int Lo, int Hi, string[] Fields)? UcdLine(string line)
{
    int comment = line.IndexOf('#');
    if (comment >= 0) line = line[..comment];
    if (string.IsNullOrWhiteSpace(line)) return null;
    string[] fields = [.. line.Split(';').Select(f => f.Trim())];
    string[] cp = fields[0].Split("..");
    return (Hex(cp[0]), Hex(cp[^1]), fields[1..]);
}

var space = Merge(RfcTable("C.1.2"));
var nothing = Merge(RfcTable("B.1"));
string[] prohibitedTables = ["C.1.2", "C.2.1", "C.2.2", "C.3", "C.4", "C.5", "C.6", "C.7", "C.8", "C.9"];
var prohibited = Merge(prohibitedTables.SelectMany(RfcTable));
var unassigned = Merge(RfcTable("A.1"));
var randAL = Merge(RfcTable("D.1"));
var l = Merge(RfcTable("D.2"));

// MayChange: assigned in Unicode 3.2 (RFC 3454's Unicode) and either NFKC_QC is not Yes (No, or Maybe: it composes with
// the character before it), or it has a combining class.
var assigned = new List<(int, int)>();
foreach (string line in ages)
{
    if (UcdLine(line) is { } u && double.Parse(u.Fields[0], CultureInfo.InvariantCulture) <= 3.2) assigned.Add((u.Lo, u.Hi));
}
var assignedIn32 = Merge(assigned);

var notYes = new List<(int, int)>();
foreach (string line in props)
{
    if (UcdLine(line) is { Fields: ["NFKC_QC", "N" or "M", ..] } u) notYes.Add((u.Lo, u.Hi));
}

var combining = new List<(int, int)>();
foreach (string line in unicodeData)
{
    string[] f = line.Split(';');
    if (f.Length > 3 && f[3] != "0") { int c = Hex(f[0]); combining.Add((c, c)); }
}

// Both sets are far smaller than the code space: intersect them by walking the (sorted, merged) ranges.
static List<(int, int)> Intersect(List<(int Lo, int Hi)> a, List<(int Lo, int Hi)> b)
{
    var result = new List<(int, int)>();
    int i = 0, j = 0;
    while (i < a.Count && j < b.Count)
    {
        int lo = Math.Max(a[i].Lo, b[j].Lo), hi = Math.Min(a[i].Hi, b[j].Hi);
        if (lo <= hi) result.Add((lo, hi));
        if (a[i].Hi < b[j].Hi) i++; else j++;
    }
    return result;
}

var mayChange = Merge(Intersect(assignedIn32, Merge(notYes.Concat(combining))));

string Table(string name, List<(int Lo, int Hi)> ranges)
{
    var sb = new StringBuilder($"    static ReadOnlySpan<int> {name} =>\n    [\n");
    var line = new StringBuilder("       ");
    foreach ((int lo, int hi) in ranges)
    {
        string pair = $" 0x{lo:X}, 0x{hi:X},"; // a range stays on one line
        if (line.Length > 7 && line.Length + pair.Length > LineWidth)
        {
            sb.Append(line).Append('\n');
            line.Clear().Append("       ");
        }
        line.Append(pair);
    }
    return sb.Append(line).Append("\n    ];\n").ToString();
}

string text = File.ReadAllText(target);
const string First = "    static ReadOnlySpan<int> Space =>";
int from = text.IndexOf(First, StringComparison.Ordinal);
int to = text.LastIndexOf("    ];\n", StringComparison.Ordinal) + "    ];\n".Length;
if (from < 0 || to < from) throw new InvalidOperationException($"{target}: the tables' region was not found");

string tables = string.Join("\n", new[]
{
    Table("Space", space), Table("Nothing", nothing), Table("Prohibited", prohibited), Table("Unassigned", unassigned),
    Table("RandAL", randAL), Table("L", l), Table("MayChange", mayChange),
});
File.WriteAllText(path, text[..from] + tables + text[to..]);
Console.WriteLine($"wrote {path}");

static string Source([CallerFilePath] string path = "") => path;
