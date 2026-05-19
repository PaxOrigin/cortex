public interface IVectorStore
{
    public IEnumerable<(string Document, float[] Vector)> GetVectors();
    public void AddVector(string document, ReadOnlyMemory<float> vector);
    public IEnumerable<(string Document, float[] Vector)> SearchCosine(ReadOnlyMemory<float> query, int topK);
    public float CosineSimilarity(float[] vector1, float[] vector2);
}