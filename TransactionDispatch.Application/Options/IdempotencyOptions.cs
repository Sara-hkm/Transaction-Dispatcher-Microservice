namespace TransactionDispatch.Application.Options;

public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";
    
    /// <summary>
    /// Enable file-level idempotency to prevent duplicate files within a job
    /// </summary>
    public bool EnableFileIdempotency { get; set; } = true;
    
    /// <summary>
    /// Enable folder-level idempotency to prevent duplicate folder dispatches
    /// </summary>
    public bool EnableFolderIdempotency { get; set; } = true;
    
    /// <summary>
    /// Time window in minutes to prevent duplicate folder dispatches
    /// </summary>
    public int FolderIdempotencyWindowMinutes { get; set; } = 60;
}
