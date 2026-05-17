using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AerialCity.Core.Primitives;

/// <summary>
/// A globally unique, sortable identifier for AerialCity entities.
/// Inspired by ULID — encodes a millisecond timestamp (48-bit) followed by
/// 80 bits of cryptographic randomness, yielding chronological ordering
/// while preserving uniqueness guarantees stronger than UUIDv4.
/// </summary>
/// <remarks>
/// Binary layout (16 bytes / 128 bits):
/// <code>
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                      Timestamp (High 32)                      |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |   Timestamp (Low 16)  |          Randomness (80 bits)  ...    |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                    ... Randomness (continued)                 |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                    ... Randomness (continued)                 |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// </code>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct AerialId : IEquatable<AerialId>, IComparable<AerialId>
{
    /// <summary>The raw 16-byte representation of this identifier.</summary>
    private readonly byte[] _bytes;

    /// <summary>Represents an empty (all-zero) identifier.</summary>
    public static readonly AerialId Empty = new(new byte[16]);

    /// <summary>
    /// Initializes a new <see cref="AerialId"/> from a raw 16-byte array.
    /// </summary>
    /// <param name="bytes">Exactly 16 bytes representing the identifier.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="bytes"/> is not 16 bytes.</exception>
    private AerialId(byte[] bytes)
    {
        if (bytes.Length != 16)
            throw new ArgumentException("AerialId must be exactly 16 bytes.", nameof(bytes));
        _bytes = bytes;
    }

    /// <summary>
    /// Generates a new <see cref="AerialId"/> using the current UTC timestamp
    /// and cryptographically secure random bytes.
    /// </summary>
    /// <returns>A new, unique, chronologically sortable identifier.</returns>
    public static AerialId NewId()
    {
        var bytes = new byte[16];
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Write 48-bit timestamp in big-endian for lexicographic sortability
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(0), (short)(timestamp >> 32));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(2), (int)(timestamp & 0xFFFFFFFF));

        // Fill remaining 10 bytes with cryptographic randomness
        RandomNumberGenerator.Fill(bytes.AsSpan(6));

        return new AerialId(bytes);
    }

    /// <summary>
    /// Creates an <see cref="AerialId"/> from a 32-character hexadecimal string.
    /// </summary>
    /// <param name="hex">A 32-character hexadecimal string.</param>
    /// <returns>The parsed <see cref="AerialId"/>.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="hex"/> is not valid hex.</exception>
    public static AerialId Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 32)
            throw new FormatException($"AerialId hex string must be exactly 32 characters, got {hex.Length}.");
        return new AerialId(Convert.FromHexString(hex));
    }

    /// <summary>
    /// Attempts to parse a hexadecimal string into an <see cref="AerialId"/>.
    /// </summary>
    public static bool TryParse(string? hex, [NotNullWhen(true)] out AerialId result)
    {
        result = Empty;
        if (hex is null || hex.Length != 32) return false;
        try
        {
            result = new AerialId(Convert.FromHexString(hex));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Extracts the embedded UTC timestamp from this identifier.
    /// </summary>
    public DateTimeOffset Timestamp
    {
        get
        {
            var high = (long)BinaryPrimitives.ReadInt16BigEndian(_bytes.AsSpan(0)) << 32;
            var low = (long)(uint)BinaryPrimitives.ReadInt32BigEndian(_bytes.AsSpan(2));
            return DateTimeOffset.FromUnixTimeMilliseconds(high | low);
        }
    }

    /// <summary>Returns the raw 16-byte representation as a read-only span.</summary>
    public ReadOnlySpan<byte> AsSpan() => _bytes;

    /// <summary>Returns the raw 16-byte representation as a read-only memory.</summary>
    public ReadOnlyMemory<byte> AsMemory() => _bytes;

    /// <summary>Returns the 32-character lowercase hexadecimal representation.</summary>
    public override string ToString() => Convert.ToHexString(_bytes).ToLowerInvariant();

    public bool Equals(AerialId other) => _bytes.AsSpan().SequenceEqual(other._bytes);
    public override bool Equals(object? obj) => obj is AerialId other && Equals(other);
    public override int GetHashCode()
    {
        // Use first 8 bytes for hash — sufficient entropy from timestamp + randomness
        return HashCode.Combine(
            BinaryPrimitives.ReadInt32LittleEndian(_bytes),
            BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(4)));
    }

    public int CompareTo(AerialId other) => _bytes.AsSpan().SequenceCompareTo(other._bytes);

    public static bool operator ==(AerialId left, AerialId right) => left.Equals(right);
    public static bool operator !=(AerialId left, AerialId right) => !left.Equals(right);
    public static bool operator <(AerialId left, AerialId right) => left.CompareTo(right) < 0;
    public static bool operator >(AerialId left, AerialId right) => left.CompareTo(right) > 0;
    public static bool operator <=(AerialId left, AerialId right) => left.CompareTo(right) <= 0;
    public static bool operator >=(AerialId left, AerialId right) => left.CompareTo(right) >= 0;
}
