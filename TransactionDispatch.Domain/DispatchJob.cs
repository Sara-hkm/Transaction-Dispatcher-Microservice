using TransactionDispatch.Domain.Enums;

namespace TransactionDispatch.Domain;

public sealed class DispatchJob
{
    public required Guid JobId { get; init; }
    public required string FolderPath { get; init; }
    public required bool DeleteAfterSend { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DispatchJobState State { get; set; } = DispatchJobState.Queued;
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int SuccessfulFiles { get; set; }
    public int FailedFiles { get; set; }
    public string? Error { get; set; }
    public string? ClaimedBy { get; set; }
    public string Progress => TotalFiles == 0 ? "0%" : $"{(int)Math.Round((double)ProcessedFiles / TotalFiles * 100)}%";
}
