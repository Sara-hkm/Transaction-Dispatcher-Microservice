using System.ComponentModel.DataAnnotations;
using TransactionDispatch.Domain.Enums;

namespace TransactionDispatch.Infrastructure.Entities;

public class DispatchJob
{
    [Key]
    public Guid JobId { get; set; }
    [Required]
    public string FolderPath { get; set; } = string.Empty;
    [Required]
    public bool DeleteAfterSend { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DispatchJobState State { get; set; }
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int SuccessfulFiles { get; set; }
    public int FailedFiles { get; set; }
    public string? Error { get; set; }
    public string? ClaimedBy { get; set; }
    public ICollection<ProcessedFile> ProcessedFileRecords { get; set; } = new List<ProcessedFile>();
}
