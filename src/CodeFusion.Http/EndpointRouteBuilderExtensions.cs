// Written by scripts/MapOverloads.cs; change that and run it again rather than editing this file.
using System;

namespace CodeFusion.Http;

/// <summary>
/// <c>MapGet</c>, <c>MapPost</c>, <c>MapPut</c> and <c>MapDelete</c>. Each call is compiled by the endpoint generator,
/// which replaces it (an interceptor) with code that binds the handler's parameters and sends its reply, so these
/// bodies only run when the generator didn't: they throw. A handler takes route values, a <c>[FromQuery]</c>
/// struct, a body struct, <see cref="Viewer"/>, services, <see cref="IResponse"/> and
/// <see cref="System.Threading.CancellationToken"/>: up to 8 through the typed overloads, and through the
/// <see cref="System.Delegate"/> overload up to 16 for a lambda or any number for a method the generator can call by
/// name (CFH0107 reports one it can't). It returns a reply: a struct implementing <see cref="IReply"/>
/// (such as <see cref="Reply"/>), a <see cref="Reply{T}"/>, an <see cref="ErrorReply"/>, or, from an <c>async</c>
/// handler, any of them through <see cref="Pending{T}"/>.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<TResult>(this IEndpointRouteBuilder routes, string pattern, Func<TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet(this IEndpointRouteBuilder routes, string pattern, Func<ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<TResult>(this IEndpointRouteBuilder routes, string pattern, Func<Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1>(this IEndpointRouteBuilder routes, string pattern, Func<T1, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5, T6, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5, T6>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5, T6, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5, T6, T7, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5, T6, T7>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5, T6, T7, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5, T6, T7, T8>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapGet<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps GET <paramref name="pattern"/> to a static method, such as <c>MapGet("/x", Handlers.X)</c>,
    /// whose parameter types the generic overloads can't infer.</summary>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static method.</param>
    public static EndpointBuilder MapGet(this IEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<TResult>(this IEndpointRouteBuilder routes, string pattern, Func<TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost(this IEndpointRouteBuilder routes, string pattern, Func<ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<TResult>(this IEndpointRouteBuilder routes, string pattern, Func<Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1>(this IEndpointRouteBuilder routes, string pattern, Func<T1, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5, T6, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5, T6>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5, T6, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5, T6, T7, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5, T6, T7>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5, T6, T7, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5, T6, T7, T8>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPost<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps POST <paramref name="pattern"/> to a static method, such as <c>MapPost("/x", Handlers.X)</c>,
    /// whose parameter types the generic overloads can't infer.</summary>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static method.</param>
    public static EndpointBuilder MapPost(this IEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<TResult>(this IEndpointRouteBuilder routes, string pattern, Func<TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut(this IEndpointRouteBuilder routes, string pattern, Func<ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<TResult>(this IEndpointRouteBuilder routes, string pattern, Func<Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1>(this IEndpointRouteBuilder routes, string pattern, Func<T1, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5, T6, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5, T6>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5, T6, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5, T6, T7, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5, T6, T7>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5, T6, T7, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5, T6, T7, T8>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapPut<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps PUT <paramref name="pattern"/> to a static method, such as <c>MapPut("/x", Handlers.X)</c>,
    /// whose parameter types the generic overloads can't infer.</summary>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static method.</param>
    public static EndpointBuilder MapPut(this IEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<TResult>(this IEndpointRouteBuilder routes, string pattern, Func<TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete(this IEndpointRouteBuilder routes, string pattern, Func<ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<TResult>(this IEndpointRouteBuilder routes, string pattern, Func<Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1>(this IEndpointRouteBuilder routes, string pattern, Func<T1, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5, T6, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5, T6>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5, T6, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5, T6, T7, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5, T6, T7>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5, T6, T7, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns its reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <typeparam name="TResult">The reply: a struct implementing <see cref="IReply"/>, or a <see cref="Reply{T}"/>.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, TResult> handler) where TResult : struct =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a handler that returns an error reply.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5, T6, T7, T8>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, ErrorReply> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to an <c>async</c> handler.</summary>
    /// <typeparam name="T1">The handler's parameter 1.</typeparam>
    /// <typeparam name="T2">The handler's parameter 2.</typeparam>
    /// <typeparam name="T3">The handler's parameter 3.</typeparam>
    /// <typeparam name="T4">The handler's parameter 4.</typeparam>
    /// <typeparam name="T5">The handler's parameter 5.</typeparam>
    /// <typeparam name="T6">The handler's parameter 6.</typeparam>
    /// <typeparam name="T7">The handler's parameter 7.</typeparam>
    /// <typeparam name="T8">The handler's parameter 8.</typeparam>
    /// <typeparam name="TResult">The reply the handler returns.</typeparam>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static lambda or a static method.</param>
    public static EndpointBuilder MapDelete<T1, T2, T3, T4, T5, T6, T7, T8, TResult>(this IEndpointRouteBuilder routes, string pattern, Func<T1, T2, T3, T4, T5, T6, T7, T8, Pending<TResult>> handler) =>
        throw NotCompiled(pattern);

    /// <summary>Maps DELETE <paramref name="pattern"/> to a static method, such as <c>MapDelete("/x", Handlers.X)</c>,
    /// whose parameter types the generic overloads can't infer.</summary>
    /// <param name="routes">The app or group.</param>
    /// <param name="pattern">The route template, a literal such as <c>"/articles/{slug}"</c>.</param>
    /// <param name="handler">A static method.</param>
    public static EndpointBuilder MapDelete(this IEndpointRouteBuilder routes, string pattern, Delegate handler) =>
        throw NotCompiled(pattern);

    static InvalidOperationException NotCompiled(string pattern) =>
        new($"the endpoint '{pattern}' was not compiled by the CodeFusion.Http endpoint generator: the package's analyzers are off, or the call was made through reflection or a delegate");
}
