namespace TransactionDispatch.Application.Interfaces;

public interface ITransactionDispatcher
{
    Task<bool> DispatchAsync(string filePath, CancellationToken cancellationToken);
}
