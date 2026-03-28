namespace TransactionDispatch.Infrastructure.Repositories;

public interface IProcessedFileRepository
{
    Task<bool> IsFileAlreadyProcessedAsync(Guid jobId, string filePath, CancellationToken cancellationToken = default);
    Task MarkFileAsProcessedAsync(Guid jobId, string filePath, bool success, CancellationToken cancellationToken = default);
}
