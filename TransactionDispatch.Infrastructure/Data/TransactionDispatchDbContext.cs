using Microsoft.EntityFrameworkCore;
using TransactionDispatch.Infrastructure.Entities;

namespace TransactionDispatch.Infrastructure.Data;

public class TransactionDispatchDbContext : DbContext
{
    public TransactionDispatchDbContext(DbContextOptions<TransactionDispatchDbContext> options) : base(options) { }

    public DbSet<DispatchJob> DispatchJobs => Set<DispatchJob>();
    public DbSet<ProcessedFile> ProcessedFiles => Set<ProcessedFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DispatchJob>(e =>
        {
            e.HasKey(j => j.JobId);
            e.Property(j => j.State).HasConversion<string>();
        });

        modelBuilder.Entity<ProcessedFile>(e =>
        {
            e.HasKey(pf => new { pf.JobId, pf.FilePath });
            e.HasOne(pf => pf.Job)
             .WithMany(j => j.ProcessedFileRecords)
             .HasForeignKey(pf => pf.JobId)
             .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
