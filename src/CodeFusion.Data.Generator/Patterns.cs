namespace CodeFusion.Data.Generator;

/// <summary>
/// The regular expressions .NET's Regex and SQL's (Postgres's AREs) read alike, for ReplaceMatches: literals,
/// escaped punctuation and <c>[...]</c> classes of ASCII characters and ranges, each once or with <c>+</c>. A
/// <c>[^...]</c> class matches characters outside ASCII, which .NET reads as two UTF-16 halves where SQL reads one
/// code point: once with <c>+</c>, both take the whole character; a second such class, or one without <c>+</c>, could
/// take a half. Anything else (<c>.</c>, <c>\d</c>, <c>*</c>, <c>?</c>, anchors, groups) may differ, and is refused.
/// </summary>
static class Patterns
{
    /// <summary>Characters that are themselves in both, outside a class.</summary>
    const string Literals = " _-,:;!@#%&=~'\"/<>`";

    /// <summary>Characters that are themselves in both, inside a class, besides letters, digits and a last '-'.</summary>
    const string InClass = " _.,:;!?@#%&=~'\"/<>`+*()${}|";

    /// <summary>Why .NET and SQL may read <paramref name="pattern"/> differently; null when they read it alike.</summary>
    public static string? Problem(string pattern)
    {
        if (pattern.Length == 0) return "an empty pattern";
        int wide = 0;
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            bool matchesWide = false;
            if (c == '[')
            {
                i++;
                if (i < pattern.Length && pattern[i] == '^')
                {
                    matchesWide = true;
                    i++;
                }
                int items = 0;
                while (true)
                {
                    if (i >= pattern.Length) return "a '[' without its ']'";
                    char x = pattern[i];
                    if (x == ']' && items > 0)
                    {
                        i++;
                        break;
                    }
                    if (x == '-' && items > 0 && i + 1 < pattern.Length && pattern[i + 1] == ']') i++;
                    else if (IsLetterOrDigit(x) && i + 2 < pattern.Length && pattern[i + 1] == '-' && pattern[i + 2] != ']')
                    {
                        char y = pattern[i + 2];
                        if (!IsLetterOrDigit(y) || Kind(x) != Kind(y) || y < x)
                            return $"the range '{x}-{y}' (a range is of digits, capitals or small letters, in order)";
                        i += 3;
                    }
                    else if (IsLetterOrDigit(x) || InClass.IndexOf(x) >= 0) i++;
                    else return $"'{x}' in a [...] class";
                    items++;
                }
            }
            else if (c == '\\')
            {
                if (i + 1 >= pattern.Length) return "a '\\' at the end";
                char x = pattern[i + 1];
                if (x <= ' ' || x > '~' || IsLetterOrDigit(x) || x == '_')
                    return $"'\\{x}' (a backslash escapes punctuation only: \\d, \\w, \\s and the like differ between .NET and SQL, and .NET refuses \\_)";
                i += 2;
            }
            else if (IsLetterOrDigit(c) || Literals.IndexOf(c) >= 0) i++;
            else return $"'{c}' (only literals, escaped punctuation, [...] classes and + read alike in .NET and SQL)";
            bool plus = i < pattern.Length && pattern[i] == '+';
            if (plus) i++;
            if (i < pattern.Length && pattern[i] is '*' or '?' or '{' or '+') return $"'{pattern[i]}' after a '+' or a character";
            if (matchesWide)
            {
                if (!plus) return "a [^...] class without '+' (it matches a character outside ASCII, which .NET reads as two halves)";
                if (++wide > 1) return "a second [^...] class (.NET could split a character outside ASCII between the two)";
            }
        }
        return null;
    }

    /// <summary>Why <paramref name="replacement"/> may be read differently; null when it is plain text in both.</summary>
    public static string? ReplacementProblem(string replacement) =>
        replacement.IndexOf('$') >= 0 ? "a '$' in the replacement (.NET's substitutions)"
        : replacement.IndexOf('\\') >= 0 ? "a '\\' in the replacement (SQL's back references)"
        : null;

    static bool IsLetterOrDigit(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    static int Kind(char c) => c <= '9' ? 0 : c <= 'Z' ? 1 : 2;
}
