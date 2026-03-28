namespace TransactionDispatch.Application.Options;

public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";
    public string Provider { get; set; } = "PostgreSQL";
    public string ConnectionString { get; set; } = "Host=localhost;Port=5432;Database=TransactionDispatch;Username=postgres;Password=postgres;";
}
