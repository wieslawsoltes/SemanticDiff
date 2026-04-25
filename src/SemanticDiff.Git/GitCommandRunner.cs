using System.Diagnostics;

namespace SemanticDiff.Git;

public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IGitCommandRunner
{
    Task<GitCommandResult> RunAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed class GitCommandRunner : IGitCommandRunner
{
    public async Task<GitCommandResult> RunAsync(string repositoryPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start git process.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new GitCommandResult(
            process.ExitCode,
            await standardOutputTask.ConfigureAwait(false),
            await standardErrorTask.ConfigureAwait(false));
    }
}