using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace System.Runtime.CompilerServices
{
    // records and init accessors on netstandard2.0
    static class IsExternalInit;
}

namespace CodeFusion.Data.Generator
{
    /// <summary>The naming conventions: EF's snake_case for tables and columns, System.Text.Json's camelCase for JSON.</summary>
    static class Names
    {
        /// <summary>
        /// snake_case as EFCore.NamingConventions writes it: a word starts at a capital after a lower-case letter or a
        /// digit, and at the last capital of a run followed by a lower-case letter (<c>HTMLParser</c> is <c>html_parser</c>).
        /// </summary>
        public static string Snake(string name)
        {
            var b = new StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c))
                {
                    bool afterLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                    bool endsRun = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if ((afterLower || endsRun) && b.Length > 0 && b[b.Length - 1] != '_') b.Append('_');
                    b.Append(char.ToLowerInvariant(c));
                }
                else b.Append(c);
            }
            return b.ToString();
        }

        /// <summary>System.Text.Json's JsonNamingPolicy.CamelCase, character for character.</summary>
        public static string Camel(string name)
        {
            if (name.Length == 0 || !char.IsUpper(name[0])) return name;
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (i == 1 && !char.IsUpper(chars[i])) break;
                bool hasNext = i + 1 < chars.Length;
                // stop when the next character is already lower case
                if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                {
                    if (chars[i + 1] == ' ') chars[i] = char.ToLowerInvariant(chars[i]);
                    break;
                }
                chars[i] = char.ToLowerInvariant(chars[i]);
            }
            return new string(chars);
        }

        /// <summary>A C# string literal of <paramref name="s"/>.</summary>
        public static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

        /// <summary>An identifier as C# source: <c>@class</c> for a keyword.</summary>
        public static string Identifier(string name) =>
            SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(name) is SyntaxKind.FieldKeyword ? "@" + name : name;

        /// <summary>A number as invariant text.</summary>
        public static string Number(long n) => n.ToString(CultureInfo.InvariantCulture);
    }
}
