using System.Text.RegularExpressions;

namespace CodeFusion.ContestBench;

/// <summary>
/// Entry names and search labels go into remote shell commands and local paths: letters, digits, '.', '_' and '-'
/// only, starting with a letter or digit (so never '.', '..' or an option), at most 64 characters.
/// </summary>
internal static partial class Names
{
    public static bool Valid(string s) => NameRe().IsMatch(s);

    public static string Check(string what, string s) =>
        Valid(s) ? s : throw new RigException($"{what} {s}: letters, digits, '.', '_' and '-' only, starting with a letter or digit, at most 64");

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex NameRe();
}
