using System.Collections.Concurrent;
using TransactionDispatch.Application.Interfaces;

namespace TransactionDispatch.Infrastructure;

public sealed class JobCancellationRegistry : IJobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _tokens = new();

    public CancellationToken RegisterOrGet(Guid jobId, CancellationToken appStoppingToken)
    {
        var cts = _tokens.GetOrAdd(jobId, _ =>
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(appStoppingToken);
            return linked;
        });

        return cts.Token;
    }

    public void Complete(Guid jobId)
    {
        if (_tokens.TryRemove(jobId, out var cts))
        {
            cts.Dispose();
        }
    }
}
