// NotebookJsonRepository.cs
using System.Text.Json;

public sealed class NotebookJsonRepository : INotebookRepository
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public NotebookJsonRepository(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<Result<Guid>> AddAsync(NotebookEntry entry)
    {
        try
        {
            var entries = await ReadAllAsync();
            var normalizedEntry = entry with { Tags = entry.Tags.Select(NormalizeForTags).ToArray() };
            entries.Add(normalizedEntry);
            await WriteAllAsync(entries);
            return Result<Guid>.Success(entry.Id);
        }
        catch (Exception ex)
        {
            return Result<Guid>.Failure(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<NotebookEntry>>> GetAsync()
    {
        try
        {
            var entries = await ReadAllAsync();
            return Result<IEnumerable<NotebookEntry>>.Success(entries);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<NotebookEntry>>.Failure(ex.Message);
        }
    }

    public async Task<Result<IEnumerable<NotebookEntry>>> SearchAsync(string query)
    {
        try
        {
            var entries = await ReadAllAsync();
            var matches = entries.Where(e =>
                e.Entry.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Tags.Any(t => NormalizeForTags(t).Contains(
    NormalizeForTags(query), StringComparison.OrdinalIgnoreCase)));
            return Result<IEnumerable<NotebookEntry>>.Success(matches);
        }
        catch (Exception ex)
        {
            return Result<IEnumerable<NotebookEntry>>.Failure(ex.Message);
        }
    }

    private string NormalizeForTags(string query) => query.Replace("@", "").Trim();

    private async Task<List<NotebookEntry>> ReadAllAsync()
    {
        if (!File.Exists(_filePath))
            return [];

        var json = await File.ReadAllTextAsync(_filePath);
        return JsonSerializer.Deserialize<List<NotebookEntry>>(json) ?? [];
    }

    private async Task WriteAllAsync(List<NotebookEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json);
    }
}