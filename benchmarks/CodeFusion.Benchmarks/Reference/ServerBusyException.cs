// ServerBusyException.cs at 21abce6: what the old reader, writer and bcrypt queue threw for a full queue.
using System;

namespace CodeFusion.Benchmarks.Reference;

/// <summary>A queue the request needed was full: the server refuses it at once (503) instead of queueing without limit.</summary>
public sealed class ServerBusyException(string message) : Exception(message);
