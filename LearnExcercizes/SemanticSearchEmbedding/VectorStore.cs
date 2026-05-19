public sealed class VectorStore : IVectorStore
{
    private readonly List<(string Document, float[] Vector)> _vectors = new();

    public IEnumerable<(string Document, float[] Vector)> GetVectors()
    {
        return _vectors;
    }

    public void AddVector(string document, ReadOnlyMemory<float> vector)
    {
        _vectors.Add((document, vector.ToArray()));
    }

    public IEnumerable<(string Document, float[] Vector)> SearchCosine(ReadOnlyMemory<float> query, int topK)
    {
        return _vectors
            .Select(v => (v.Document, v.Vector, Similarity: CosineSimilarity(query.ToArray(), v.Vector)))
            .OrderByDescending(v => v.Similarity)
            .Take(topK)
            .Select(v => (v.Document, v.Vector));
    }

    public float CosineSimilarity(float[] vector1, float[] vector2)
    {
        float dotProduct = 0;
        float magnitude1 = 0;
        float magnitude2 = 0;
        for (int i = 0; i < vector1.Length; i++)
        {
            dotProduct += vector1[i] * vector2[i];
            magnitude1 += vector1[i] * vector1[i];
            magnitude2 += vector2[i] * vector2[i];
        }
        magnitude1 = (float)Math.Sqrt(magnitude1);
        magnitude2 = (float)Math.Sqrt(magnitude2);
        if (magnitude1 == 0 || magnitude2 == 0)
        {
            return 0;
        }
        return dotProduct / (magnitude1 * magnitude2);
    }
}