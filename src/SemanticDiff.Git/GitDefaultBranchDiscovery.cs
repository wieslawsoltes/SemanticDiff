namespace SemanticDiff.Git;

public sealed class GitDefaultBranchDiscovery
{
    private readonly IGitCommandRunner commandRunner;

    public GitDefaultBranchDiscovery(IGitCommandRunner commandRunner)
    {
        this.commandRunner = commandRunner;
    }

    public async Task<string?> DiscoverAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        var symbolicRef = await commandRunner.RunAsync(repositoryPath, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], cancellationToken).ConfigureAwait(false);
        if (symbolicRef.Succeeded && !string.IsNullOrWhiteSpace(symbolicRef.StandardOutput))
        {
            return symbolicRef.StandardOutput.Trim();
        }

        foreach (var candidate in new[] { "origin/main", "origin/master", "main", "master", "trunk" })
        {
            var result = await commandRunner.RunAsync(repositoryPath, ["rev-parse", "--verify", candidate], cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return candidate;
            }
        }

        return null;
    }
}