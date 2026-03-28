namespace TransactionDispatch.Application.Options;

public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";
    public int MaxParallelism { get; set; } = 64;
    public int RetryCount { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 100;
    public int ProgressSaveEvery { get; set; } = 200;
    public int PollIntervalSeconds { get; set; } = 5;
    public int MaxPollBatchSize { get; set; } = 100;
    public List<string> SupportedExtensions { get; set; } = [".xml"];
}
