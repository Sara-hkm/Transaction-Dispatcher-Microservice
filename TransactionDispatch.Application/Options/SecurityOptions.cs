namespace TransactionDispatch.Application.Options;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// API key that callers must supply in the <c>X-Api-Key</c> request header.
    /// Set to a non-empty value to enable API key enforcement; leave empty to disable (development only).
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Origins permitted by the CORS policy. Use ["*"] to allow any origin (development only).
    /// If empty, CORS is not configured.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];
}
