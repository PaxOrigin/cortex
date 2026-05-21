// INotebookRepository.cs
public interface INotebookRepository
{
    Task<Result<Guid>> AddAsync(NotebookEntry entry);
    Task<Result<IEnumerable<NotebookEntry>>> GetAsync();
    Task<Result<IEnumerable<NotebookEntry>>> SearchAsync(string query);
}