using System.Reflection.Metadata;

public sealed class Documents : IDocuments
{
    static readonly string[] _documents =
[
    // .NET / C#
    "C# is a strongly typed object-oriented language developed by Microsoft",
    "ASP.NET Core is a cross-platform framework for building web APIs and web apps",
    "Minimal APIs in .NET allow building lightweight HTTP endpoints with minimal boilerplate",
    "Entity Framework Core is an ORM for .NET that maps objects to relational databases",
    "Blazor allows building interactive web UIs using C# instead of JavaScript",
    "gRPC in .NET enables high-performance remote procedure calls using Protocol Buffers",

    // Python / Data Science
    "Python is a dynamically typed language popular in data science and scripting",
    "Pandas and NumPy are foundational Python libraries for data manipulation and analysis",
    "Matplotlib and Seaborn are Python libraries for data visualization and plotting",
    "Scikit-learn provides classical machine learning algorithms for Python developers",

    // Deep Learning / AI
    "TensorFlow is an open-source deep learning framework developed by Google",
    "PyTorch is a deep learning framework popular in academic research and production",
    "Neural networks are computational models inspired by the structure of the human brain",
    "Transformers are neural network architectures designed for sequence-to-sequence tasks",
    "Large language models like GPT are trained on vast text corpora using self-supervision",

    // Databases
    "PostgreSQL is an open-source relational database with strong ACID guarantees",
    "Redis is an in-memory key-value store used for caching and pub/sub messaging",
    "MongoDB is a document-oriented NoSQL database that stores data as BSON",
    "Vector databases like Qdrant and Pinecone store and query high-dimensional embeddings",

    // Cloud / DevOps
    "Docker packages applications into containers for consistent deployment across environments",
    "Kubernetes orchestrates containerized workloads across clusters of machines",
    "Azure Functions is a serverless compute service for event-driven workloads",
    "GitHub Actions enables CI/CD pipelines defined as YAML workflows in a repository",
];

    public IEnumerable<string> GetDocuments()
    {
        var enumerator = _documents.GetEnumerator();
        while (enumerator.MoveNext())
        {
            yield return (string)enumerator.Current;
        }
    }
};