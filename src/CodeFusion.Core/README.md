# CodeFusion.Core

The values CodeFusion.Http and CodeFusion.Data pass to each other. Nothing here allocates.

- `Utf8`: text as UTF-8 bytes that stay in their buffer (a request's bytes, a row of a database reply), valid as
  long as that buffer is. It compares with `==`, also against `"feed"u8`, and converts to `ReadOnlySpan<byte>`.
  `Slice(start, length)` and `Trim()` (JSON's whitespace: space, tab, CR, LF) are views of the same bytes.
- `Timestamp`: a UTC instant to the microsecond, written as `2025-04-01T12:00:00.000Z`.
- `RawJson`: JSON a database built, written into a response as it is.
- `IRequestScoped`: a per-connection service. `Bind(response)` ties it to the request that response answers;
  `Unbind()` (optional) says that request has ended and its reply is written, so what the service lent it (a data
  context's lists) can go back before the connection's next request.

```csharp
if (slug == "feed"u8) return Feed(...);
Span<byte> text = stackalloc byte[Timestamp.FormattedLength];
createdAt.TryFormat(text, out _);
```
