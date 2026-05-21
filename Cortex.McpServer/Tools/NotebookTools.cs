// NotebookTools.cs
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

[McpServerToolType]
public class NotebookTools
{
    private readonly INotebookRepository _repository;
    private readonly ILogger<NotebookTools> _logger;

    public NotebookTools(INotebookRepository repository, ILogger<NotebookTools> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    [McpServerTool, Description("Adds an entry to the notebook. Returns the id of the created entry.")]
    public async Task<string> AddEntry(
        [Description("The text content of the entry")] string content,
        [Description("Optional tags to associate with the entry")] string[] tags = null!)
    {
        _logger.LogInformation("AddEntry called. ContentLength={Length} Tags={Tags}",
            content.Length, string.Join(",", tags ?? []));
        try
        {
            var entry = new NotebookEntry(content, DateTime.UtcNow, Guid.NewGuid(), tags ?? []);
            var result = await _repository.AddAsync(entry);

            if (!result.IsSuccess)
            {
                _logger.LogError("AddEntry failed: {Error}", result.ErrorMessage);
                throw new InvalidOperationException(result.ErrorMessage);
            }

            _logger.LogInformation("Entry added: {Id}", result.Value);
            return $"Entry added with id {result.Value}";
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in AddEntry");
            throw new InvalidOperationException("Unexpected error while adding entry.", ex);
        }
    }

    [McpServerTool, Description("Retrieves all entries from the notebook.")]
    public async Task<string> GetEntries()
    {
        _logger.LogInformation("GetEntries called.");
        try
        {
            var result = await _repository.GetAsync();

            if (!result.IsSuccess)
            {
                _logger.LogError("GetEntries failed: {Error}", result.ErrorMessage);
                throw new InvalidOperationException(result.ErrorMessage);
            }

            var entries = result.Value?.ToList() ?? [];
            _logger.LogInformation("Retrieved {Count} entries.", entries.Count);

            return entries.Count > 0
                ? string.Join("\n---\n", entries.Select(e =>
                    $"Id: {e.Id}\nTimestamp: {e.Timestamp:O}\nTags: {string.Join(", ", e.Tags)}\nContent: {e.Entry}"))
                : "No entries found.";
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in GetEntries");
            throw new InvalidOperationException("Unexpected error while retrieving entries.", ex);
        }
    }

    [McpServerTool, Description("Searches entries in the notebook by text or tag.")]
    public async Task<string> SearchEntries(
        [Description("The text to search for in content and tags")] string query)
    {
        _logger.LogInformation("SearchEntries called. Query={Query}", query);
        try
        {
            var result = await _repository.SearchAsync(query);

            if (!result.IsSuccess)
            {
                _logger.LogError("SearchEntries failed: {Error}", result.ErrorMessage);
                throw new InvalidOperationException(result.ErrorMessage);
            }

            var entries = result.Value?.ToList() ?? [];
            _logger.LogInformation("Search returned {Count} entries.", entries.Count);

            return entries.Count > 0
                ? string.Join("\n---\n", entries.Select(e =>
                    $"Id: {e.Id}\nTimestamp: {e.Timestamp:O}\nTags: {string.Join(", ", e.Tags)}\nContent: {e.Entry}"))
                : $"No entries found matching '{query}'.";
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in SearchEntries");
            throw new InvalidOperationException("Unexpected error while searching entries.", ex);
        }
    }
}