using System.IO.Hashing;
using System.Text;
using AerialCity.Core.Primitives;

namespace AerialCity.Retrieval.Scoring;

/// <summary>
/// Self-implemented BM25 (Best Matching 25) scorer for lexical text retrieval.
/// Computes relevance scores based on term frequency, inverse document frequency,
/// and document length normalization.
/// </summary>
/// <remarks>
/// <para>
/// BM25 formula: score(q,d) = Σ IDF(qi) · (tf(qi,d) · (k1+1)) / (tf(qi,d) + k1·(1 - b + b·|d|/avgdl))
/// </para>
/// <para>
/// This scorer maintains an inverted index of all documents for IDF computation.
/// Call <see cref="AddDocument"/> for each segment in the corpus before scoring queries.
/// </para>
/// </remarks>
public sealed class Bm25Scorer : IScorer
{
    private readonly float _k1;
    private readonly float _b;
    private readonly Dictionary<string, int> _documentFrequency = [];
    private readonly Dictionary<AerialId, int> _documentLengths = [];
    private int _totalDocuments;
    private double _averageDocumentLength;

    public string Name => "BM25";

    /// <param name="k1">Term frequency saturation parameter. Default: 1.2.</param>
    /// <param name="b">Document length normalization. Default: 0.75.</param>
    public Bm25Scorer(float k1 = 1.2f, float b = 0.75f)
    {
        _k1 = k1;
        _b = b;
    }

    /// <summary>
    /// Registers a document (segment) in the BM25 corpus for IDF computation.
    /// Must be called for all segments before scoring queries.
    /// </summary>
    public void AddDocument(Segment segment)
    {
        var tokens = Tokenize(segment.Content);
        _documentLengths[segment.Id] = tokens.Length;
        _totalDocuments++;

        var uniqueTerms = new HashSet<string>(tokens);
        foreach (var term in uniqueTerms)
        {
            _documentFrequency.TryGetValue(term, out var df);
            _documentFrequency[term] = df + 1;
        }

        _averageDocumentLength = _documentLengths.Values.Average();
    }

    /// <summary>Removes a document from the BM25 corpus.</summary>
    public void RemoveDocument(Segment segment)
    {
        if (!_documentLengths.Remove(segment.Id)) return;
        _totalDocuments--;

        var tokens = Tokenize(segment.Content);
        var uniqueTerms = new HashSet<string>(tokens);
        foreach (var term in uniqueTerms)
        {
            if (_documentFrequency.TryGetValue(term, out var df))
            {
                if (df <= 1) _documentFrequency.Remove(term);
                else _documentFrequency[term] = df - 1;
            }
        }

        _averageDocumentLength = _totalDocuments > 0 ? _documentLengths.Values.Average() : 0;
    }

    public float Score(Segment segment, RetrievalQuery query)
    {
        if (query.TextQuery is null || _totalDocuments == 0) return 0f;

        var queryTerms = Tokenize(query.TextQuery);
        var docTokens = Tokenize(segment.Content);
        var docLength = docTokens.Length;

        // Build term frequency map for this document
        var tf = new Dictionary<string, int>();
        foreach (var t in docTokens)
        {
            tf.TryGetValue(t, out var count);
            tf[t] = count + 1;
        }

        var score = 0.0;
        foreach (var term in queryTerms)
        {
            if (!tf.TryGetValue(term, out var termFreq)) continue;

            _documentFrequency.TryGetValue(term, out var docFreq);

            // IDF: ln((N - df + 0.5) / (df + 0.5) + 1)
            var idf = Math.Log(((_totalDocuments - docFreq + 0.5) / (docFreq + 0.5)) + 1.0);

            // TF normalization
            var tfNorm = (termFreq * (_k1 + 1.0)) /
                         (termFreq + _k1 * (1.0 - _b + _b * docLength / _averageDocumentLength));

            score += idf * tfNorm;
        }

        return (float)score;
    }

    /// <summary>
    /// Simple whitespace + punctuation tokenizer with lowercasing.
    /// Suitable for general-purpose BM25; can be replaced with a more
    /// sophisticated tokenizer for specific languages.
    /// </summary>
    private static string[] Tokenize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
