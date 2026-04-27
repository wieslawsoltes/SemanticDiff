using System.Net.Http.Headers;

namespace SemanticDiff.Git;

internal static class GitApiRequestHeaders
{
    public static void AddGitHubHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SemanticDiff", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");

        var token = GetGitHubToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public static void AddGitLabHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SemanticDiff", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var token = Environment.GetEnvironmentVariable("GITLAB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable("GL_TOKEN");
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token.Trim());
        }
    }

    public static string? GetGitHubToken()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            token = Environment.GetEnvironmentVariable("GH_TOKEN");
        }

        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }
}
