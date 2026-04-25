using SemanticDiff.Core;

namespace SemanticDiff.Tests;

public sealed class LatestRequestGateTests
{
    [Fact]
    public void BeginRequest_MakesPreviousRequestsStale()
    {
        var gate = new LatestRequestGate();

        var firstRequest = gate.BeginRequest();
        var secondRequest = gate.BeginRequest();

        Assert.False(gate.IsCurrent(firstRequest));
        Assert.True(gate.IsCurrent(secondRequest));
    }

    [Fact]
    public void ThrowIfStale_RejectsOlderRequest()
    {
        var gate = new LatestRequestGate();
        var firstRequest = gate.BeginRequest();
        gate.BeginRequest();

        Assert.Throws<OperationCanceledException>(() => gate.ThrowIfStale(firstRequest, CancellationToken.None));
    }
}