// Registrazione dei servizi, del logger, dell'embedding generator
// useremo un dictionary manuale per gestire ed emulare il "vector store"
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Serilog;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Caching.Distributed;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .CreateLogger();

IEmbeddingGenerator<string, Embedding<float>> ollamaEmbedding =
    new OllamaApiClient(new Uri("http://localhost:11434"), "nomic-embed-text");

var services = new ServiceCollection();
services.AddLogging(b => b.AddSerilog());
services.AddDistributedMemoryCache();
services.AddSingleton
    (p => new EmbeddingGeneratorBuilder<string, Embedding<float>>(ollamaEmbedding)
        .UseLogging()
        .UseDistributedCache(p.GetRequiredService<IDistributedCache>())
        .Build(p)
    );
services.AddScoped<IDocuments, Documents>();
services.TryAddSingleton<IVectorStore, VectorStore>();
var serviceProvider = services.BuildServiceProvider();
// === Main ===
var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
var embeddingGenerator = serviceProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var documents = serviceProvider.GetRequiredService<IDocuments>();
var vectorStore = serviceProvider.GetRequiredService<IVectorStore>();
logger.LogInformation("App configured, starting Main...");

// Embedding and fake Vector Store
var docs = documents.GetDocuments();
foreach (var doc in docs)
{
    var embedding = await embeddingGenerator.GenerateAsync(doc);
    vectorStore.AddVector(doc, embedding.Vector);
}

// Search
var query = "I want to build a REST API in .NET";
var embeddedQuery = await embeddingGenerator.GenerateAsync(query);
var results = vectorStore.SearchCosine(embeddedQuery.Vector, topK: 3);
logger.LogInformation("Search results for query: {Query}", query);
foreach (var result in results)
{
    logger.LogInformation("Document: {Document}", result.Document);
}

// Search
query = "Where is C# used?";
embeddedQuery = await embeddingGenerator.GenerateAsync(query);
results = vectorStore.SearchCosine(embeddedQuery.Vector, topK: 3);
logger.LogInformation("Search results for query: {Query}", query);
foreach (var result in results)
{
    logger.LogInformation("Document: {Document}", result.Document);
}

query = "I want to use neural networks for deep learning";
embeddedQuery = await embeddingGenerator.GenerateAsync(query);
results = vectorStore.SearchCosine(embeddedQuery.Vector, topK: 3);
logger.LogInformation("Search results for query: {Query}", query);
foreach (var result in results)
{
    logger.LogInformation("Document: {Document}", result.Document);
}


