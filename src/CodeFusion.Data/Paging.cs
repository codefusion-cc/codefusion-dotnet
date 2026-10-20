namespace CodeFusion.Data;

/// <summary>Which rows of an ordered list a page holds: <see cref="Limit"/> of them after the first <see cref="Offset"/>.</summary>
/// <param name="Limit">At most this many rows; the query's <c>maxLimit</c> caps it as well.</param>
/// <param name="Offset">Rows skipped before the page.</param>
public readonly record struct Paging(long Limit, long Offset);

/// <summary>How a page counts its rows up to its cap (<c>countUpTo</c>). Both stop at the cap.</summary>
public enum PageCount
{
    /// <summary>Counts the rows of the filtered list, reading at most the cap: <c>count(*)</c> over the list with
    /// <c>LIMIT</c>. The default; right when the filter itself narrows the rows through an index.</summary>
    Limited,
    /// <summary>First probes for the cap's row in the list's key order, which walks the key's index, and counts
    /// only below the cap: right for a whole table or a link table, where a bare limited count would read rows in
    /// heap order.</summary>
    Probe,
}
