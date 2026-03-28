using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TransactionDispatch.Infrastructure.Entities;

public class ProcessedFile
{
    [Required]
    public Guid JobId { get; set; }
    [Required]
    public string FilePath { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; set; }
    public bool Success { get; set; }
    [ForeignKey("JobId")]
    public DispatchJob? Job { get; set; }
}
