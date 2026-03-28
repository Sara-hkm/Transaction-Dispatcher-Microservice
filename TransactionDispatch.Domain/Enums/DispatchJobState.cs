namespace TransactionDispatch.Domain.Enums;

public enum DispatchJobState
{
    Queued,
    Running,
    Completed,
    [Obsolete("Cancel-request handshake was removed; use Cancelled directly.")]
    CancelRequested,
    Cancelled,
    Failed
}
