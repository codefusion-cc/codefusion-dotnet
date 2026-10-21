using System;

namespace CodeFusion;

/// <summary>
/// A UTC instant to the microsecond, the precision of Postgres's <c>timestamptz</c>. Written into JSON as RFC 3339
/// with exactly three fractional digits, truncated: <c>2025-04-01T12:00:00.000Z</c>.
/// </summary>
public readonly struct Timestamp : IEquatable<Timestamp>, IComparable<Timestamp>
{
    /// <summary>The length of <see cref="TryFormat"/>'s text.</summary>
    public const int FormattedLength = 24;

    const long UnixEpochTicks = 621355968000000000; // DateTime ticks at 1970-01-01

    /// <summary>Microseconds since 1970-01-01 UTC; negative before it.</summary>
    public long UnixMicroseconds { get; }

    /// <summary>The instant <paramref name="unixMicroseconds"/> after 1970-01-01 UTC.</summary>
    public Timestamp(long unixMicroseconds) => UnixMicroseconds = unixMicroseconds;

    /// <summary>A UTC <see cref="DateTime"/>'s instant, to the microsecond (the 100 ns ticks below it are dropped).</summary>
    public static Timestamp FromDateTime(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local) throw new ArgumentException("a UTC or unspecified DateTime", nameof(utc));
        long ticks = utc.Ticks - UnixEpochTicks;
        return new Timestamp(ticks / 10 - (ticks % 10 < 0 ? 1 : 0)); // floored, so instants before 1970 truncate the same way
    }

    /// <summary>As a UTC <see cref="DateTime"/>; false outside years 1 to 9999.</summary>
    public bool TryToDateTime(out DateTime utc)
    {
        utc = default;
        if (!TryTicks(out long ticks)) return false;
        utc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    /// <summary>
    /// Writes the RFC 3339 text (<see cref="FormattedLength"/> bytes) into <paramref name="destination"/>. False when
    /// it is too short or the instant lies outside years 1 to 9999, which RFC 3339 can't write.
    /// </summary>
    public bool TryFormat(Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        if (destination.Length < FormattedLength || !TryTicks(out long ticks)) return false;
        new DateTime(ticks).Deconstruct(out int y, out int mo, out int d);
        long ms = ticks % TimeSpan.TicksPerDay / TimeSpan.TicksPerMillisecond;
        int h = (int)(ms / 3_600_000), mi = (int)(ms / 60_000 % 60), s = (int)(ms / 1000 % 60), f = (int)(ms % 1000);
        var b = destination;
        b[0] = (byte)('0' + y / 1000); b[1] = (byte)('0' + y / 100 % 10); b[2] = (byte)('0' + y / 10 % 10); b[3] = (byte)('0' + y % 10);
        b[4] = (byte)'-'; b[5] = (byte)('0' + mo / 10); b[6] = (byte)('0' + mo % 10);
        b[7] = (byte)'-'; b[8] = (byte)('0' + d / 10); b[9] = (byte)('0' + d % 10);
        b[10] = (byte)'T'; b[11] = (byte)('0' + h / 10); b[12] = (byte)('0' + h % 10);
        b[13] = (byte)':'; b[14] = (byte)('0' + mi / 10); b[15] = (byte)('0' + mi % 10);
        b[16] = (byte)':'; b[17] = (byte)('0' + s / 10); b[18] = (byte)('0' + s % 10);
        b[19] = (byte)'.'; b[20] = (byte)('0' + f / 100); b[21] = (byte)('0' + f / 10 % 10); b[22] = (byte)('0' + f % 10);
        b[23] = (byte)'Z';
        bytesWritten = FormattedLength;
        return true;
    }

    /// <summary>DateTime ticks of the instant, floored to its 100 ns; false outside DateTime's range.</summary>
    bool TryTicks(out long ticks)
    {
        const long MaxMicros = (3155378975999999999 - UnixEpochTicks) / 10, MinMicros = -UnixEpochTicks / 10;
        ticks = 0;
        if (UnixMicroseconds is < MinMicros or > MaxMicros) return false;
        ticks = UnixEpochTicks + UnixMicroseconds * 10;
        return true;
    }

    /// <inheritdoc/>
    public bool Equals(Timestamp other) => UnixMicroseconds == other.UnixMicroseconds;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Timestamp other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => UnixMicroseconds.GetHashCode();

    /// <inheritdoc/>
    public int CompareTo(Timestamp other) => UnixMicroseconds.CompareTo(other.UnixMicroseconds);

    /// <summary>The same instant.</summary>
    public static bool operator ==(Timestamp left, Timestamp right) => left.Equals(right);

    /// <summary>Different instants.</summary>
    public static bool operator !=(Timestamp left, Timestamp right) => !left.Equals(right);

    /// <summary>Earlier.</summary>
    public static bool operator <(Timestamp left, Timestamp right) => left.UnixMicroseconds < right.UnixMicroseconds;

    /// <summary>Later.</summary>
    public static bool operator >(Timestamp left, Timestamp right) => left.UnixMicroseconds > right.UnixMicroseconds;

    /// <summary>Earlier or the same.</summary>
    public static bool operator <=(Timestamp left, Timestamp right) => left.UnixMicroseconds <= right.UnixMicroseconds;

    /// <summary>Later or the same.</summary>
    public static bool operator >=(Timestamp left, Timestamp right) => left.UnixMicroseconds >= right.UnixMicroseconds;

    /// <summary>The RFC 3339 text as a new string, or the microseconds when it can't be written. For logs and tests.</summary>
    public override string ToString()
    {
        Span<byte> b = stackalloc byte[FormattedLength];
        return TryFormat(b, out _) ? System.Text.Encoding.ASCII.GetString(b) : UnixMicroseconds + " µs";
    }
}
