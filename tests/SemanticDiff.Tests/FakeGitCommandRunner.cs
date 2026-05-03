using SemanticDiff.Git;

namespace SemanticDiff.Tests;

internal sealed class FakeGitCommandRunner : IGitCommandRunner
{
    private readonly Func<IReadOnlyList<string>, GitCommandResult> handler;

    public FakeGitCommandRunner(Func<IReadOnlyList<string>, GitCommandResult> handler)
    {
        this.handler = handler;
    }

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public List<string> RepositoryPaths { get; } = [];

    public Task<GitCommandResult> RunAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        RepositoryPaths.Add(repositoryPath);
        Calls.Add(arguments.ToArray());
        return Task.FromResult(handler(arguments));
    }
}
