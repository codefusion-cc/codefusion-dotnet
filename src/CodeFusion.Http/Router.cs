using System;
using System.Collections.Generic;
using System.Text;

namespace CodeFusion.Http;

/// <summary>A route template: literal segments and <c>{name}</c> parameters, as <c>/articles/{slug}/comments</c>.</summary>
internal sealed class RouteTemplate
{
    public readonly string Text;
    /// <summary>Each segment's literal UTF-8 bytes, or null for a parameter.</summary>
    public readonly byte[]?[] Literals;
    /// <summary>The parameters' names, in order.</summary>
    public readonly string[] Parameters;

    RouteTemplate(string text, byte[]?[] literals, string[] parameters)
    {
        Text = text;
        Literals = literals;
        Parameters = parameters;
    }

    public static readonly RouteTemplate Root = new("/", [], []);

    /// <summary>
    /// Parses <paramref name="pattern"/>: "" or "/" is the root; otherwise '/' and segments separated by '/', each
    /// non-empty, each a literal or a whole <c>{name}</c> (a C# identifier). Throws <see cref="ArgumentException"/>.
    /// </summary>
    public static RouteTemplate Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length == 0 || pattern == "/") return Root;
        if (pattern[0] != '/') throw Bad(pattern, "it must start with '/'");
        string[] parts = pattern[1..].Split('/');
        var literals = new byte[]?[parts.Length];
        var parameters = new List<string>();
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p.Length == 0) throw Bad(pattern, "a segment is empty");
            if (p[0] == '{')
            {
                if (p[^1] != '}' || !IsName(p.AsSpan(1, p.Length - 2))) throw Bad(pattern, $"'{p}' is not a parameter like {{name}}");
                string name = p[1..^1];
                foreach (string q in parameters)
                    if (string.Equals(q, name, StringComparison.OrdinalIgnoreCase)) throw Bad(pattern, $"{{{name}}} appears twice");
                parameters.Add(name);
            }
            else if (p.AsSpan().IndexOfAny('{', '}') >= 0) throw Bad(pattern, $"'{p}' mixes a literal and a parameter");
            else literals[i] = Encoding.UTF8.GetBytes(p);
        }
        return new RouteTemplate(pattern, literals, [.. parameters]);
    }

    /// <summary>This template under <paramref name="prefix"/>.</summary>
    public RouteTemplate Under(RouteTemplate prefix)
    {
        if (prefix.Literals.Length == 0) return this;
        if (Literals.Length == 0) return prefix;
        foreach (string p in Parameters)
            foreach (string q in prefix.Parameters)
                if (string.Equals(p, q, StringComparison.OrdinalIgnoreCase)) throw Bad(prefix.Text + Text, $"{{{p}}} appears twice");
        return new RouteTemplate(prefix.Text + Text, [.. prefix.Literals, .. Literals], [.. prefix.Parameters, .. Parameters]);
    }

    /// <summary>The index of the parameter <paramref name="name"/> (ignoring case), or -1.</summary>
    public int IndexOf(string name)
    {
        for (int i = 0; i < Parameters.Length; i++)
            if (string.Equals(Parameters[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    static bool IsName(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty || !(char.IsAsciiLetter(s[0]) || s[0] == '_')) return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    static ArgumentException Bad(string pattern, string why) => new($"route '{pattern}': {why}", nameof(pattern));

    public override string ToString() => Text;
}

/// <summary>
/// Finds the endpoint for a method and a path, built once at startup. The path is the server's (percent-decoded
/// except %2F, dot segments removed); segments compare case-sensitively, methods ASCII case-insensitively; an empty
/// segment matches nothing. A literal segment wins over a parameter at the same place, and a path that matches a
/// literal route only for other methods goes on to the parameter routes: PUT /articles/feed is the
/// <c>/articles/{slug}</c> route when <c>/articles/feed</c> has only GET, as in the contest server.
/// </summary>
internal sealed class Router
{
    internal const int Get = 0, Post = 1, Put = 2, Delete = 3, Verbs = 4;

    sealed class Node
    {
        public byte[][] Literals = [];
        public Node[] Children = [];
        public Node? Parameter;
        public readonly Endpoint?[] Endpoints = new Endpoint?[Verbs];
    }

    readonly Node _root = new();

    /// <summary>The most segments any route has: a deeper path matches nothing.</summary>
    public int MaxDepth { get; private set; }

    /// <summary>The most parameters any route has.</summary>
    public int MaxParameters { get; private set; }

    public static int Verb(RequestMethod method) => method switch
    {
        RequestMethod.Get => Get,
        RequestMethod.Post => Post,
        RequestMethod.Put => Put,
        RequestMethod.Delete => Delete,
        _ => throw new ArgumentOutOfRangeException(nameof(method), "GET, POST, PUT or DELETE"),
    };

    public void Add(int verb, RouteTemplate template, Endpoint endpoint)
    {
        Node node = _root;
        foreach (byte[]? literal in template.Literals)
        {
            if (literal is null)
            {
                node = node.Parameter ??= new Node();
                continue;
            }
            int i = Array.FindIndex(node.Literals, l => l.AsSpan().SequenceEqual(literal));
            if (i < 0)
            {
                node.Literals = [.. node.Literals, literal];
                node.Children = [.. node.Children, new Node()];
                i = node.Literals.Length - 1;
            }
            node = node.Children[i];
        }
        if (node.Endpoints[verb] is not null)
            throw new InvalidOperationException($"{VerbName(verb)} {template} is mapped twice (or to a template that differs only in parameter names)");
        node.Endpoints[verb] = endpoint;
        MaxDepth = Math.Max(MaxDepth, template.Literals.Length);
        MaxParameters = Math.Max(MaxParameters, template.Parameters.Length);
    }

    static string VerbName(int verb) => verb switch { Get => "GET", Post => "POST", Put => "PUT", _ => "DELETE" };

    /// <summary>The verb of a request, ignoring the case of its method; -1 for any other method. HEAD runs the GET
    /// endpoint, and the server sends its head without the body.</summary>
    public static int Verb(RequestMethod parsed, ReadOnlySpan<byte> method) => parsed switch
    {
        RequestMethod.Get or RequestMethod.Head => Get,
        RequestMethod.Post => Post,
        RequestMethod.Put => Put,
        RequestMethod.Delete => Delete,
        RequestMethod.Other => Ascii.EqualsIgnoreCase(method, "GET"u8) ? Get
            : Ascii.EqualsIgnoreCase(method, "POST"u8) ? Post
            : Ascii.EqualsIgnoreCase(method, "PUT"u8) ? Put
            : Ascii.EqualsIgnoreCase(method, "DELETE"u8) ? Delete
            : -1,
        _ => -1,
    };

    /// <summary>
    /// The endpoint for <paramref name="verb"/> and <paramref name="path"/>, or null. Each parameter's place in the
    /// path goes into <paramref name="values"/> as (offset, length) pairs, in the template's order.
    /// </summary>
    public Endpoint? Match(int verb, ReadOnlySpan<byte> path, Span<int> values)
    {
        if (verb < 0 || path.IsEmpty || path[0] != '/') return null;
        if (path.Length == 1) return _root.Endpoints[verb];
        Span<int> segments = stackalloc int[2 * MaxDepth + 2];
        int n = 0, start = 1;
        while (true)
        {
            int slash = path[start..].IndexOf((byte)'/');
            int end = slash < 0 ? path.Length : start + slash;
            if (end == start || n > MaxDepth) return null; // an empty segment, or deeper than any route
            segments[2 * n] = start;
            segments[2 * n + 1] = end - start;
            n++;
            if (slash < 0) break;
            start = end + 1;
        }
        if (n > MaxDepth) return null;
        return Find(_root, 0, 0, verb, path, segments[..(2 * n)], values);
    }

    static Endpoint? Find(Node node, int i, int p, int verb, ReadOnlySpan<byte> path, ReadOnlySpan<int> segments, Span<int> values)
    {
        if (2 * i == segments.Length) return node.Endpoints[verb];
        ReadOnlySpan<byte> segment = path.Slice(segments[2 * i], segments[2 * i + 1]);
        byte[][] literals = node.Literals;
        for (int k = 0; k < literals.Length; k++)
        {
            if (!segment.SequenceEqual(literals[k])) continue;
            if (Find(node.Children[k], i + 1, p, verb, path, segments, values) is { } e) return e;
            break; // literals differ from each other: no other can match
        }
        if (node.Parameter is { } parameter)
        {
            values[2 * p] = segments[2 * i];
            values[2 * p + 1] = segments[2 * i + 1];
            return Find(parameter, i + 1, p + 1, verb, path, segments, values);
        }
        return null;
    }
}
