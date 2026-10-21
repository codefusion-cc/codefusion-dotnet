using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace CodeFusion.Http.Generators;

/// <summary>
/// What the analyzers (CFH0001 to CFH0005, each with the rule it keeps) and the generator (CFH01xx: a Map call it
/// can't compile) report. All are errors, each one either an allocation per request or code that would fail, except
/// CFH0109, a warning about a member a binder leaves at its default.
/// </summary>
internal static class Diagnostics
{
    const string Rules = "CodeFusion.Http";

    public static readonly DiagnosticDescriptor NotStatic = new(
        "CFH0001", "Handlers are static", "{0}",
        Rules, DiagnosticSeverity.Error, true,
        "A handler must be a static lambda or a static method: a lambda that captures allocates a closure, and one that isn't marked static can start capturing unnoticed.");

    public static readonly DiagnosticDescriptor StringParameter = new(
        "CFH0002", "Text is Utf8", "The handler's parameter '{0}' is a string: take a Utf8, which points into the request instead of copying it into a new string",
        Rules, DiagnosticSeverity.Error, true,
        "Every string made from a request allocates. Utf8 compares with == and \"text\"u8 and converts to ReadOnlySpan<byte>.");

    public static readonly DiagnosticDescriptor CannotSend = new(
        "CFH0003", "The reply can't be sent", "{0}",
        Rules, DiagnosticSeverity.Error, true,
        "A handler returns a struct implementing IReply (such as Reply), a Reply<T> whose T the generator can write as JSON, an ErrorReply, or one of them through an async handler.");

    public static readonly DiagnosticDescriptor Utf8InClass = new(
        "CFH0004", "A Utf8 doesn't outlive its request", "'{0}' keeps a {1} in a class: it points into a request's or a row's buffer, which is reused once the response is sent",
        Rules, DiagnosticSeverity.Error, true,
        "Utf8, RawJson, Utf8List, Viewer and ArenaBytes (and structs holding them) are valid only until the response has been sent. Copy the bytes (ToString, ToArray) to keep them.");

    public static readonly DiagnosticDescriptor LinqInHandler = new(
        "CFH0005", "No LINQ in handlers", "'{0}' runs LINQ inside a handler, which allocates its enumerators and delegates on every request: write the query as a method on the data context, or a loop",
        Rules, DiagnosticSeverity.Error, true,
        "LINQ over objects allocates per call. CodeFusion.Data compiles LINQ written in a context's query methods to SQL at build time; a handler calls those methods.");

    public static readonly DiagnosticDescriptor BadPattern = new(
        "CFH0101", "The route is a literal template", "{0}",
        Rules, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BadHandler = new(
        "CFH0102", "The handler is a lambda or a method", "The handler must be written in the Map call, as a static lambda or a static method's name: the generator compiles what it can see",
        Rules, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BadParameter = new(
        "CFH0103", "The parameter can't be bound", "{0}",
        Rules, DiagnosticSeverity.Error, true,
        "A handler's parameters are Utf8, long or int route values, one [FromQuery] struct, one body struct, Viewer, services (classes registered on the builder), IResponse, HttpExchange or CancellationToken.");

    public static readonly DiagnosticDescriptor BadStruct = new(
        "CFH0104", "The struct can't be bound", "{0}",
        Rules, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor BadMember = new(
        "CFH0105", "The member can't be bound", "{0}",
        Rules, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor NotAccessible = new(
        "CFH0106", "The type must be visible to generated code", "'{0}' is private, protected, file-local or internal to another assembly: the generated endpoint can't name it; make it internal or public",
        Rules, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor UnnamedDelegate = new(
        "CFH0107", "The handler's delegate is a Func", "{0}",
        Rules, DiagnosticSeverity.Error, true,
        "A handler the Map call takes as a Delegate (a method group with parameters, or a lambda of more than 8) is held as the Func of its parameters. A delegate of more than 16 parameters, or with a default value or params, has a type the compiler makes up instead.");

    public static readonly DiagnosticDescriptor NotInterceptable = new(
        "CFH0108", "The Map call can be intercepted", "The generator can't replace this Map call with its endpoint, so it would throw when it runs: call it as routes.MapGet(...), routes?.MapGet(...) or EndpointRouteBuilderExtensions.MapGet(routes, ...)",
        Rules, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor NotBound = new(
        "CFH0109", "An internal member isn't bound", "'{0}' is internal, so the {1} binder leaves it at its default: make it public to bind it, or private or [JsonIgnore] to say it isn't",
        Rules, DiagnosticSeverity.Warning, true,
        "A binder sets only what code outside the struct could set, its public fields and public properties with a public setter, so a client can't reach a member such as IsAdmin. An internal one could look like data the request fills in.");

    public static readonly IReadOnlyDictionary<string, DiagnosticDescriptor> ById = new[]
    {
        NotStatic, StringParameter, CannotSend, Utf8InClass, LinqInHandler, BadPattern, BadHandler, BadParameter, BadStruct, BadMember, NotAccessible,
        UnnamedDelegate, NotInterceptable, NotBound,
    }.ToDictionary(d => d.Id);

    /// <summary>The rules the analyzers report; the generator leaves them to them.</summary>
    public static bool IsAnalyzerRule(string id) => id is "CFH0001" or "CFH0002" or "CFH0003" or "CFH0004" or "CFH0005";
}
