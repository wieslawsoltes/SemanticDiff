using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed class GitReviewService : IGitReviewService
{
    private readonly IGitCommandRunner commandRunner;

    public GitReviewService()
        : this(new GitCommandRunner())
    {
    }

    public GitReviewService(IGitCommandRunner commandRunner)
    {
        this.commandRunner = commandRunner;
    }

    public Task<GitReviewOperationResult> StageFileAsync(string repositoryPath, string path, CancellationToken cancellationToken) =>
        RunReviewCommandAsync(repositoryPath, ["add", "--", path], $"Staged {path}", cancellationToken);

    public Task<GitReviewOperationResult> UnstageFileAsync(string repositoryPath, string path, CancellationToken cancellationToken) =>
        RunReviewCommandAsync(repositoryPath, ["restore", "--staged", "--", path], $"Unstaged {path}", cancellationToken);

    private async Task<GitReviewOperationResult> RunReviewCommandAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        string successMessage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || string.IsNullOrWhiteSpace(arguments.LastOrDefault()))
        {
            return new GitReviewOperationResult(false, "No repository file selected");
        }

        var result = await commandRunner.RunAsync(repositoryPath, arguments, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return new GitReviewOperationResult(true, successMessage);
        }

        var message = string.IsNullOrWhiteSpace(result.StandardError)
            ? $"Git exited with code {result.ExitCode}"
            : result.StandardError.Trim();
        return new GitReviewOperationResult(false, message);
    }
}