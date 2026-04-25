namespace SemanticDiff.Core;

public sealed class LatestRequestGate
{
    private long latestRequestId;

    public long BeginRequest() => Interlocked.Increment(ref latestRequestId);

    public bool IsCurrent(long requestId) => requestId > 0 && Volatile.Read(ref latestRequestId) == requestId;

    public void ThrowIfStale(long requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(requestId))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}