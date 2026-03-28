namespace TransactionDispatch.Application.Interfaces;
public interface IJobCancellationRegistry
{
    CancellationToken RegisterOrGet(Guid jobId, CancellationToken appStoppingToken);
    void Complete(Guid jobId);
}
