using System.Numerics.Tensors;
using System.Runtime.InteropServices;

namespace AerialCity.Core.Primitives;

/// <summary>
/// Represents a dense embedding vector produced by an embedding model.
/// Wraps a <see cref="ReadOnlyMemory{Single}"/> and provides SIMD-accelerated
/// operations via <see cref="TensorPrimitives"/> for high-performance similarity
/// computation in the vector store.
/// </summary>
/// <remarks>
/// <para>
/// Vectors are immutable after construction. All arithmetic operations return
/// new instances. For bulk operations (index building, batch search), prefer
/// the static methods that operate on spans to avoid allocation overhead.
/// </para>
/// <para>
/// Typical dimensionalities: 384 (MiniLM), 768 (BERT), 1024 (BGE-M3), 1536 (text-embedding-3-small).
/// </para>
/// </remarks>
public readonly struct EmbeddingVector : IEquatable<EmbeddingVector>
{
    private readonly float[] _values;

    /// <summary>The dimensionality (number of components) of this vector.</summary>
    public int Dimensions => _values.Length;

    /// <summary>Returns the vector components as a read-only span for zero-copy access.</summary>
    public ReadOnlySpan<float> Span => _values;

    /// <summary>Returns the vector components as read-only memory.</summary>
    public ReadOnlyMemory<float> Memory => _values;

    /// <summary>
    /// Creates an embedding vector from the given float array.
    /// The array is copied to ensure immutability.
    /// </summary>
    /// <param name="values">The vector components. Must not be empty.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="values"/> is empty.</exception>
    public EmbeddingVector(ReadOnlySpan<float> values)
    {
        if (values.IsEmpty)
            throw new ArgumentException("Embedding vector must have at least one dimension.", nameof(values));
        _values = values.ToArray();
    }

    /// <summary>
    /// Creates a zero vector with the specified dimensionality.
    /// </summary>
    public static EmbeddingVector Zero(int dimensions) => new(new float[dimensions]);

    /// <summary>
    /// Computes the L2 (Euclidean) norm of this vector using SIMD acceleration.
    /// </summary>
    public float Norm() => TensorPrimitives.Norm(_values);

    /// <summary>
    /// Returns a unit-length (L2-normalized) copy of this vector.
    /// </summary>
    /// <returns>A new vector with the same direction and unit length.</returns>
    public EmbeddingVector Normalize()
    {
        var norm = Norm();
        if (norm == 0f) return this;
        var result = new float[_values.Length];
        TensorPrimitives.Divide(_values, norm, result);
        return new EmbeddingVector(result);
    }

    /// <summary>
    /// Computes the cosine similarity between this vector and <paramref name="other"/>.
    /// Returns a value in [-1, 1] where 1 indicates identical direction.
    /// </summary>
    public float CosineSimilarity(in EmbeddingVector other)
    {
        EnsureSameDimensions(in other);
        return TensorPrimitives.CosineSimilarity(_values, other._values);
    }

    /// <summary>
    /// Computes the dot product between this vector and <paramref name="other"/>.
    /// </summary>
    public float DotProduct(in EmbeddingVector other)
    {
        EnsureSameDimensions(in other);
        return TensorPrimitives.Dot(_values, other._values);
    }

    /// <summary>
    /// Computes the Euclidean (L2) distance between this vector and <paramref name="other"/>.
    /// </summary>
    public float EuclideanDistance(in EmbeddingVector other)
    {
        EnsureSameDimensions(in other);
        return TensorPrimitives.Distance(_values, other._values);
    }

    /// <summary>
    /// Accesses the vector component at the given index.
    /// </summary>
    public float this[int index] => _values[index];

    private void EnsureSameDimensions(in EmbeddingVector other)
    {
        if (Dimensions != other.Dimensions)
            throw new InvalidOperationException(
                $"Vector dimension mismatch: {Dimensions} vs {other.Dimensions}.");
    }

    public bool Equals(EmbeddingVector other) =>
        _values.AsSpan().SequenceEqual(other._values);

    public override bool Equals(object? obj) => obj is EmbeddingVector v && Equals(v);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var v in _values.AsSpan(0, Math.Min(8, _values.Length)))
            hash.Add(v);
        return hash.ToHashCode();
    }

    public override string ToString() => $"EmbeddingVector[{Dimensions}d, norm={Norm():F4}]";

    public static bool operator ==(EmbeddingVector left, EmbeddingVector right) => left.Equals(right);
    public static bool operator !=(EmbeddingVector left, EmbeddingVector right) => !left.Equals(right);
}
