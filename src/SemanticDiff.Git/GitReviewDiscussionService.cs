using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using SemanticDiff.Core;

namespace SemanticDiff.Git;

public sealed class GitReviewDiscussionService : IGitReviewDiscussionService
{
    private const int PageSize = 100;
    private const int MaxPages = 20;
    private readonly IGitCommandRunner commandRunner;
    private readonly HttpClient httpClient;

    public GitReviewDiscussionService()
        : this(new GitCommandRunner(), new HttpClient())
    {
    }

    public GitReviewDiscussionService(IGitCommandRunner commandRunner, HttpClient? httpClient = null)
    {
        this.commandRunner = commandRunner;
        this.httpClient = httpClient ?? new HttpClient();
    }

    public async Task<GitReviewDiscussionSnapshot> GetDiscussionAsync(
        string repositoryPath,
        GitPullRequestInfo reviewRequest,
        CancellationToken cancellationToken)
    {
        if (reviewRequest.Kind == GitReviewRequestKind.MergeRequest)
        {
            var remote = await ResolveGitLabRemoteAsync(repositoryPath, reviewRequest.RemoteName, cancellationToken).ConfigureAwait(false);
            if (remote is null)
            {
                return EmptySnapshot(reviewRequest, "GitLab remote unavailable");
            }

            var threads = await GetGitLabDiscussionsAsync(remote, reviewRequest.Number, cancellationToken).ConfigureAwait(false);
            return new GitReviewDiscussionSnapshot(reviewRequest, threads, FormatStatus(threads, GitReviewRequestKind.MergeRequest));
        }

        var githubRemote = await ResolveGitHubRemoteAsync(repositoryPath, reviewRequest.RemoteName, cancellationToken).ConfigureAwait(false);
        if (githubRemote is null)
        {
            return EmptySnapshot(reviewRequest, "GitHub remote unavailable");
        }

        var githubThreads = await GetGitHubDiscussionsAsync(githubRemote, reviewRequest.Number, cancellationToken).ConfigureAwait(false);
        return new GitReviewDiscussionSnapshot(reviewRequest, githubThreads, FormatStatus(githubThreads, GitReviewRequestKind.PullRequest));
    }

    public async Task<GitReviewOperationResult> AddCommentAsync(
        string repositoryPath,
        GitPullRequestInfo reviewRequest,
        string body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new GitReviewOperationResult(false, "Comment is empty");
        }

        return reviewRequest.Kind == GitReviewRequestKind.MergeRequest
            ? await AddGitLabThreadAsync(repositoryPath, reviewRequest, body, cancellationToken).ConfigureAwait(false)
            : await AddGitHubIssueCommentAsync(repositoryPath, reviewRequest, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitReviewOperationResult> ReplyToThreadAsync(
        string repositoryPath,
        GitPullRequestInfo reviewRequest,
        string threadId,
        string body,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return new GitReviewOperationResult(false, "Select a review thread");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return new GitReviewOperationResult(false, "Reply is empty");
        }

        return reviewRequest.Kind == GitReviewRequestKind.MergeRequest
            ? await ReplyToGitLabThreadAsync(repositoryPath, reviewRequest, threadId, body, cancellationToken).ConfigureAwait(false)
            : await ReplyToGitHubThreadAsync(repositoryPath, reviewRequest, threadId, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitReviewOperationResult> SetThreadResolvedAsync(
        string repositoryPath,
        GitPullRequestInfo reviewRequest,
        string threadId,
        bool isResolved,
        CancellationToken cancellationToken)
    {
        if (reviewRequest.Kind == GitReviewRequestKind.PullRequest)
        {
            return await SetGitHubThreadResolvedAsync(threadId, isResolved, cancellationToken).ConfigureAwait(false);
        }

        var remote = await ResolveGitLabRemoteAsync(repositoryPath, reviewRequest.RemoteName, cancellationToken).ConfigureAwait(false);
        if (remote is null)
        {
            return new GitReviewOperationResult(false, "GitLab remote unavailable");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"{remote.Repository.ApiBaseUrl}/api/v4/projects/{remote.Repository.EncodedProjectPath}/merge_requests/{reviewRequest.Number}/discussions/{Uri.EscapeDataString(threadId)}");
        GitApiRequestHeaders.AddGitLabHeaders(request);
        request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("resolved", isResolved ? "true" : "false")]);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? new GitReviewOperationResult(true, isResolved ? "Thread resolved" : "Thread reopened")
            : new GitReviewOperationResult(false, await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false));
    }

    private static GitReviewDiscussionSnapshot EmptySnapshot(GitPullRequestInfo reviewRequest, string message) =>
        new(reviewRequest, ImmutableArray<GitReviewThreadInfo>.Empty, message);

    private async Task<GitHubRemote?> ResolveGitHubRemoteAsync(string repositoryPath, string remoteName, CancellationToken cancellationToken)
    {
        var remote = NormalizeRemoteName(remoteName);
        var result = await commandRunner.RunAsync(repositoryPath, ["remote", "get-url", remote], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        var url = result.StandardOutput.Trim();
        var repository = GitReferenceDiscoveryService.TryParseGitHubRemoteUrl(url);
        return repository is null ? null : new GitHubRemote(remote, url, repository);
    }

    private async Task<GitLabRemote?> ResolveGitLabRemoteAsync(string repositoryPath, string remoteName, CancellationToken cancellationToken)
    {
        var remote = NormalizeRemoteName(remoteName);
        var result = await commandRunner.RunAsync(repositoryPath, ["remote", "get-url", remote], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        var url = result.StandardOutput.Trim();
        var repository = GitReferenceDiscoveryService.TryParseGitLabRemoteUrl(url);
        return repository is null ? null : new GitLabRemote(remote, url, repository);
    }

    private async Task<ImmutableArray<GitReviewThreadInfo>> GetGitHubDiscussionsAsync(
        GitHubRemote remote,
        int number,
        CancellationToken cancellationToken)
    {
        var issueComments = await GetPagedJsonArrayAsync(
            page => $"https://api.github.com/repos/{remote.Repository.Owner}/{remote.Repository.Name}/issues/{number}/comments?per_page={PageSize}&page={page}",
            GitApiRequestHeaders.AddGitHubHeaders,
            cancellationToken).ConfigureAwait(false);

        var builder = ImmutableArray.CreateBuilder<GitReviewThreadInfo>();
        foreach (var item in issueComments)
        {
            if (TryReadGitHubIssueComment(item, out var comment))
            {
                builder.Add(new GitReviewThreadInfo(
                    comment.ThreadId,
                    GitReviewThreadKind.Conversation,
                    $"Conversation by {comment.Author}",
                    null,
                    null,
                    false,
                    true,
                    false,
                    [comment]));
            }
        }

        var graphQlThreads = await GetGitHubReviewThreadsFromGraphQlAsync(remote, number, cancellationToken).ConfigureAwait(false);
        if (!graphQlThreads.IsDefaultOrEmpty)
        {
            builder.AddRange(graphQlThreads);
            return SortThreads(builder.ToImmutable());
        }

        builder.AddRange(await GetGitHubReviewThreadsFromRestAsync(remote, number, cancellationToken).ConfigureAwait(false));
        return SortThreads(builder.ToImmutable());
    }

    private async Task<ImmutableArray<GitReviewThreadInfo>> GetGitHubReviewThreadsFromRestAsync(
        GitHubRemote remote,
        int number,
        CancellationToken cancellationToken)
    {
        var reviewComments = (await GetPagedJsonArrayAsync(
                page => $"https://api.github.com/repos/{remote.Repository.Owner}/{remote.Repository.Name}/pulls/{number}/comments?per_page={PageSize}&page={page}",
                GitApiRequestHeaders.AddGitHubHeaders,
                cancellationToken).ConfigureAwait(false))
            .Select(TryReadGitHubReviewCommentFromRest)
            .Where(comment => comment is not null)
            .Cast<GitHubReviewComment>()
            .ToArray();
        var commentsById = reviewComments.ToDictionary(comment => comment.Id, StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<GitReviewThreadInfo>();
        foreach (var group in reviewComments.GroupBy(comment => GetGitHubReviewThreadId(comment, commentsById), StringComparer.Ordinal))
        {
            var comments = group
                .OrderBy(comment => comment.CreatedAt ?? DateTimeOffset.MinValue)
                .Select(comment => comment.Info)
                .ToImmutableArray();
            var root = group.FirstOrDefault(comment => string.Equals(comment.Id, group.Key, StringComparison.Ordinal)) ?? group.First();
            builder.Add(new GitReviewThreadInfo(
                $"review:{group.Key}",
                GitReviewThreadKind.Diff,
                FormatLocationTitle(root.Path, root.Line, root.Author),
                root.Path,
                root.Line,
                false,
                true,
                false,
                comments));
        }

        return builder.ToImmutable();
    }

    private async Task<ImmutableArray<GitReviewThreadInfo>> GetGitHubReviewThreadsFromGraphQlAsync(
        GitHubRemote remote,
        int number,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(GitApiRequestHeaders.GetGitHubToken()))
        {
            return ImmutableArray<GitReviewThreadInfo>.Empty;
        }

        const string query = """
            query($owner: String!, $name: String!, $number: Int!, $after: String) {
              repository(owner: $owner, name: $name) {
                pullRequest(number: $number) {
                  reviewThreads(first: 100, after: $after) {
                    pageInfo { hasNextPage endCursor }
                    nodes {
                      id
                      isResolved
                      isOutdated
                      path
                      line
                      comments(first: 100) {
                        nodes {
                          id
                          databaseId
                          body
                          url
                          createdAt
                          updatedAt
                          author { login }
                        }
                      }
                    }
                  }
                }
              }
            }
            """;

        var builder = ImmutableArray.CreateBuilder<GitReviewThreadInfo>();
        string? after = null;
        for (var page = 0; page < MaxPages; page++)
        {
            using var response = await SendGitHubGraphQlAsync(
                query,
                new Dictionary<string, object?>
                {
                    ["owner"] = remote.Repository.Owner,
                    ["name"] = remote.Repository.Name,
                    ["number"] = number,
                    ["after"] = after
                },
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ImmutableArray<GitReviewThreadInfo>.Empty;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("errors", out _))
            {
                return ImmutableArray<GitReviewThreadInfo>.Empty;
            }

            if (!TryGetGitHubReviewThreadsConnection(document.RootElement, out var connection))
            {
                return builder.ToImmutable();
            }

            if (connection.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in nodes.EnumerateArray())
                {
                    if (TryReadGitHubGraphQlReviewThread(node, out var thread))
                    {
                        builder.Add(thread);
                    }
                }
            }

            if (!connection.TryGetProperty("pageInfo", out var pageInfo) ||
                TryGetBoolean(pageInfo, "hasNextPage") != true)
            {
                break;
            }

            after = TryGetString(pageInfo, "endCursor");
            if (string.IsNullOrWhiteSpace(after))
            {
                break;
            }
        }

        return builder.ToImmutable();
    }

    private async Task<ImmutableArray<GitReviewThreadInfo>> GetGitLabDiscussionsAsync(
        GitLabRemote remote,
        int number,
        CancellationToken cancellationToken)
    {
        var items = await GetPagedJsonArrayAsync(
            page => $"{remote.Repository.ApiBaseUrl}/api/v4/projects/{remote.Repository.EncodedProjectPath}/merge_requests/{number}/discussions?per_page={PageSize}&page={page}",
            GitApiRequestHeaders.AddGitLabHeaders,
            cancellationToken).ConfigureAwait(false);

        var builder = ImmutableArray.CreateBuilder<GitReviewThreadInfo>();
        foreach (var item in items)
        {
            if (TryReadGitLabDiscussion(item, out var thread))
            {
                builder.Add(thread);
            }
        }

        return SortThreads(builder.ToImmutable());
    }

    private async Task<ImmutableArray<JsonElement>> GetPagedJsonArrayAsync(
        Func<int, string> createUrl,
        Action<HttpRequestMessage> configureHeaders,
        CancellationToken cancellationToken)
    {
        var builder = ImmutableArray.CreateBuilder<JsonElement>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, createUrl(page));
            configureHeaders(request);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var pageCount = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                pageCount++;
                builder.Add(item.Clone());
            }

            if (pageCount < PageSize)
            {
                break;
            }
        }

        return builder.ToImmutable();
    }

    private async Task<HttpResponseMessage> SendGitHubGraphQlAsync(
        string query,
        IReadOnlyDictionary<string, object?> variables,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql");
        GitApiRequestHeaders.AddGitHubHeaders(request);
        request.Content = CreateJsonContent(new Dictionary<string, object?>
        {
            ["query"] = query,
            ["variables"] = variables
        });
        return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetGitHubReviewThreadsConnection(JsonElement root, out JsonElement connection)
    {
        connection = default;
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("repository", out var repository) ||
            repository.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            !repository.TryGetProperty("pullRequest", out var pullRequest) ||
            pullRequest.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            !pullRequest.TryGetProperty("reviewThreads", out connection) ||
            connection.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return true;
    }

    private static bool TryReadGitHubGraphQlReviewThread(JsonElement item, out GitReviewThreadInfo thread)
    {
        thread = default!;
        var id = TryGetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var path = TryGetString(item, "path");
        var line = TryGetInt32OrNull(item, "line");
        var isResolved = TryGetBoolean(item, "isResolved") ?? false;
        if (!item.TryGetProperty("comments", out var commentsElement) ||
            !commentsElement.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var comments = ImmutableArray.CreateBuilder<GitReviewCommentInfo>();
        foreach (var node in nodes.EnumerateArray())
        {
            var commentId = TryGetInt64(node, "databaseId", out var databaseId)
                ? databaseId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : TryGetString(node, "id");
            if (string.IsNullOrWhiteSpace(commentId))
            {
                continue;
            }

            comments.Add(new GitReviewCommentInfo(
                commentId,
                id,
                TryGetNestedString(node, "author", "login") ?? "unknown",
                TryGetString(node, "body") ?? string.Empty,
                TryGetDateTimeOffset(node, "createdAt"),
                TryGetDateTimeOffset(node, "updatedAt"),
                TryGetString(node, "url"),
                false));
        }

        if (comments.Count == 0)
        {
            return false;
        }

        var first = comments.OrderBy(comment => comment.CreatedAt ?? DateTimeOffset.MinValue).First();
        thread = new GitReviewThreadInfo(
            id,
            GitReviewThreadKind.Diff,
            FormatLocationTitle(path, line, first.Author),
            path,
            line,
            isResolved,
            true,
            true,
            comments.OrderBy(comment => comment.CreatedAt ?? DateTimeOffset.MinValue).ToImmutableArray());
        return true;
    }

    private async Task<GitReviewOperationResult> AddGitHubIssueCommentAsync(
        string repositoryPath,
        GitPullRequestInfo reviewRequest,
        string body,
        CancellationToken cancellationToken)
    {
        var remote = await ResolveGitHubRemoteAsync(repositoryPath, reviewRequest.RemoteName, cancellationToken).ConfigureAwait(false);
        if (remote is null)
        {
            return new GitReviewOperationResult(false, "GitHub remote unavailable");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/repos/{remote.Repository.Owner}/{remote.Repository.Name}/issues/{reviewRequest.Number}/comments");
        GitApiRequestHeaders.AddGitHubHeaders(request);
        request.Content = CreateJsonContent(new Dictionary<string, object?> { ["body"] = body.Trim() });
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.Created
            ? new GitReviewOperationResult(true, "Comment added")
            : new GitReviewOperationResult(false, await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false));
    }

    private async Task<GitReviewOperationResult> ReplyToGitHubThreadAsync(
        string repositoryPath,
        GitPullRequestInfo reviewRequest,
        string threadId,
        string body,
        CancellationToken cancellationToken)
    {
        var isRestReviewThread = TryGetGitHubReviewRootCommentId(threadId, out var rootCommentId);
        if (!isRestReviewThread && !string.IsNullOrWhiteSpace(GitApiRequestHeaders.GetGitHubToken()))
        {
            const string mutation = """
                mutation($threadId: ID!, $body: String!) {
                  addPullRequestReviewThreadReply(input: {pullRequestReviewThreadId: $threadId, body: $body}) {
                    comment { id }
                  }
                }
                """;
            using var graphQlResponse = await SendGitHubGraphQlAsync(
                mutation,
                new Dictionary<string, object?> { ["threadId"] = threadId, ["body"] = body.Trim() },
                cancellationToken).ConfigureAwait(false);
            return await CreateGitHubGraphQlOperationResultAsync(graphQlResponse, "Reply added", cancellationToken).ConfigureAwait(false);
        }

        if (!isRestReviewThread)
        {
            return await AddGitHubIssueCommentAsync(repositoryPath, reviewRequest, body, cancellationToken).ConfigureAwait(false);
        }

        var remote = await ResolveGitHubRemoteAsync(repositoryPath, reviewRequest.RemoteName, cancellationToken).ConfigureAwait(false);
        if (remote is null)
        {
            return new GitReviewOperationResult(false, "GitHub remote unavailable");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/repos/{remote.Repository.Owner}/{remote.Repository.Name}/pulls/{reviewRequest.Number}/comments");
        GitApiRequestHeaders.AddGitHubHeaders(request);
        request.Content = CreateJsonContent(new Dictionary<string, object?> { ["body"] = body.Trim(), ["in_reply_to"] = rootCommentId });
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.Created
            ? new GitReviewOperationResult(true, "Reply added")
            : new GitReviewOperationResult(false, await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false));
    }

    private async Task<GitReviewOperationResult> SetGitHubThreadResolvedAsync(string threadId, bool isResolved, CancellationToken cancellationToken)
    {
        if (TryGetGitHubReviewRootCommentId(threadId, out _) || string.IsNullOrWhiteSpace(GitApiRequestHeaders.GetGitHubToken()))
        {
            return new GitReviewOperationResult(false, "GitHub thread resolve requires authenticated GraphQL review threads");
        }

        var mutationName = isResolved ? "resolveReviewThread" : "unresolveReviewThread";
        var mutation = $$"""
            mutation($threadId: ID!) {
              {{mutationName}}(input: {threadId: $threadId}) {
                thread { id isResolved }
              }
            }
            """;
        using var response = await SendGitHubGraphQlAsync(
            mutation,
            new Dictionary<string, object?> { ["threadId"] = threadId },
            cancellationToken).ConfigureAwait(false);
        return await CreateGitHubGraphQlOperationResultAsync(response, isResolved ? "Thread resolved" : "Thread reopened", cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitReviewOperationResult> AddGitLabThreadAsync(
        string repositoryPath,
        GitPullRequestInfo reviewRequest,
        string body,
        CancellationToken cancellationToken)
    {
        var remote = await ResolveGitLabRemoteAsync(repositoryPath, reviewRequest.RemoteName, cancellationToken).ConfigureAwait(false);
        if (remote is null)
        {
            return new GitReviewOperationResult(false, "GitLab remote unavailable");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{remote.Repository.ApiBaseUrl}/api/v4/projects/{remote.Repository.EncodedProjectPath}/merge_requests/{reviewRequest.Number}/discussions");
        GitApiRequestHeaders.AddGitLabHeaders(request);
        request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("body", body.Trim())]);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.Created
            ? new GitReviewOperationResult(true, "Thread added")
            : new GitReviewOperationResult(false, await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false));
    }

    private async Task<GitReviewOperationResult> ReplyToGitLabThreadAsync(
        string repositoryPath,
        GitPullRequestInfo reviewRequest,
        string threadId,
        string body,
        CancellationToken cancellationToken)
    {
        var remote = await ResolveGitLabRemoteAsync(repositoryPath, reviewRequest.RemoteName, cancellationToken).ConfigureAwait(false);
        if (remote is null)
        {
            return new GitReviewOperationResult(false, "GitLab remote unavailable");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{remote.Repository.ApiBaseUrl}/api/v4/projects/{remote.Repository.EncodedProjectPath}/merge_requests/{reviewRequest.Number}/discussions/{Uri.EscapeDataString(threadId)}/notes");
        GitApiRequestHeaders.AddGitLabHeaders(request);
        request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("body", body.Trim())]);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.Created
            ? new GitReviewOperationResult(true, "Reply added")
            : new GitReviewOperationResult(false, await ReadErrorMessageAsync(response, cancellationToken).ConfigureAwait(false));
    }

    private static bool TryReadGitHubIssueComment(JsonElement item, out GitReviewCommentInfo comment)
    {
        comment = default!;
        if (!TryGetInt64(item, "id", out var id))
        {
            return false;
        }

        var author = TryGetNestedString(item, "user", "login") ?? "unknown";
        comment = new GitReviewCommentInfo(
            id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"conversation:{id}",
            author,
            TryGetString(item, "body") ?? string.Empty,
            TryGetDateTimeOffset(item, "created_at"),
            TryGetDateTimeOffset(item, "updated_at"),
            TryGetString(item, "html_url"),
            false);
        return true;
    }

    private static GitHubReviewComment? TryReadGitHubReviewCommentFromRest(JsonElement item)
    {
        if (!TryGetInt64(item, "id", out var id))
        {
            return null;
        }

        var idText = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var inReplyToId = TryGetInt64(item, "in_reply_to_id", out var replyId)
            ? replyId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
        var author = TryGetNestedString(item, "user", "login") ?? "unknown";
        var path = TryGetString(item, "path");
        var line = TryGetInt32OrNull(item, "line") ?? TryGetInt32OrNull(item, "original_line") ?? TryGetInt32OrNull(item, "position");
        var threadId = inReplyToId is null ? idText : $"review:{inReplyToId}";
        var info = new GitReviewCommentInfo(
            idText,
            threadId,
            author,
            TryGetString(item, "body") ?? string.Empty,
            TryGetDateTimeOffset(item, "created_at"),
            TryGetDateTimeOffset(item, "updated_at"),
            TryGetString(item, "html_url"),
            false);
        return new GitHubReviewComment(idText, inReplyToId, author, path, line, info.CreatedAt, info);
    }

    private static bool TryReadGitLabDiscussion(JsonElement item, out GitReviewThreadInfo thread)
    {
        thread = default!;
        var discussionId = TryGetString(item, "id");
        if (string.IsNullOrWhiteSpace(discussionId) || !item.TryGetProperty("notes", out var notesElement) || notesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var comments = ImmutableArray.CreateBuilder<GitReviewCommentInfo>();
        var kind = GitReviewThreadKind.Conversation;
        string? path = null;
        int? line = null;
        var resolved = TryGetBoolean(item, "resolved") ?? false;
        foreach (var note in notesElement.EnumerateArray())
        {
            if (!TryGetInt64(note, "id", out var noteId))
            {
                continue;
            }

            var noteType = TryGetString(note, "type");
            if (string.Equals(noteType, "DiffNote", StringComparison.OrdinalIgnoreCase))
            {
                kind = GitReviewThreadKind.Diff;
            }

            if (TryGetGitLabPosition(note, out var notePath, out var noteLine))
            {
                path ??= notePath;
                line ??= noteLine;
                kind = GitReviewThreadKind.Diff;
            }

            var isSystem = TryGetBoolean(note, "system") ?? false;
            comments.Add(new GitReviewCommentInfo(
                noteId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                discussionId,
                TryGetNestedString(note, "author", "username") ?? TryGetNestedString(note, "author", "name") ?? "unknown",
                TryGetString(note, "body") ?? string.Empty,
                TryGetDateTimeOffset(note, "created_at"),
                TryGetDateTimeOffset(note, "updated_at"),
                TryGetString(note, "web_url"),
                isSystem));
        }

        if (comments.Count == 0)
        {
            return false;
        }

        if (comments.All(comment => comment.IsSystem))
        {
            kind = GitReviewThreadKind.System;
        }

        var first = comments.OrderBy(comment => comment.CreatedAt ?? DateTimeOffset.MinValue).First();
        thread = new GitReviewThreadInfo(
            discussionId,
            kind,
            kind == GitReviewThreadKind.Diff ? FormatLocationTitle(path, line, first.Author) : $"Conversation by {first.Author}",
            path,
            line,
            resolved,
            true,
            kind == GitReviewThreadKind.Diff,
            comments.OrderBy(comment => comment.CreatedAt ?? DateTimeOffset.MinValue).ToImmutableArray());
        return true;
    }

    private static bool TryGetGitLabPosition(JsonElement note, out string? path, out int? line)
    {
        path = null;
        line = null;
        if (!note.TryGetProperty("position", out var position) || position.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        path = TryGetString(position, "new_path") ?? TryGetString(position, "old_path");
        line = TryGetInt32OrNull(position, "new_line") ?? TryGetInt32OrNull(position, "old_line");
        return !string.IsNullOrWhiteSpace(path) || line is not null;
    }

    private static string GetGitHubReviewThreadId(GitHubReviewComment comment, IReadOnlyDictionary<string, GitHubReviewComment> commentsById)
    {
        var current = comment;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!string.IsNullOrWhiteSpace(current.InReplyToId) && seen.Add(current.Id) && commentsById.TryGetValue(current.InReplyToId, out var parent))
        {
            current = parent;
        }

        return current.InReplyToId ?? current.Id;
    }

    private static bool TryGetGitHubReviewRootCommentId(string threadId, out long commentId)
    {
        commentId = 0;
        var value = threadId.StartsWith("review:", StringComparison.Ordinal) ? threadId[7..] : threadId;
        return long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out commentId);
    }

    private static ImmutableArray<GitReviewThreadInfo> SortThreads(ImmutableArray<GitReviewThreadInfo> threads) => threads
        .OrderByDescending(thread => thread.Comments.LastOrDefault()?.UpdatedAt ?? thread.Comments.LastOrDefault()?.CreatedAt ?? DateTimeOffset.MinValue)
        .ThenBy(thread => thread.Title, StringComparer.OrdinalIgnoreCase)
        .ToImmutableArray();

    private static string FormatStatus(ImmutableArray<GitReviewThreadInfo> threads, GitReviewRequestKind kind)
    {
        var requestLabel = kind == GitReviewRequestKind.MergeRequest ? "MR" : "PR";
        return threads.Length == 1 ? $"1 {requestLabel} review thread" : $"{threads.Length:N0} {requestLabel} review threads";
    }

    private static string FormatLocationTitle(string? path, int? line, string author)
    {
        var file = string.IsNullOrWhiteSpace(path) ? "diff" : Path.GetFileName(path);
        return line is null ? $"{file} by {author}" : $"{file}:{line} by {author}";
    }

    private static HttpContent CreateJsonContent(IReadOnlyDictionary<string, object?> values) =>
        new StringContent(JsonSerializer.Serialize(values), Encoding.UTF8, "application/json");

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(content)
            ? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
            : content.Trim();
    }

    private static async Task<GitReviewOperationResult> CreateGitHubGraphQlOperationResultAsync(
        HttpResponseMessage response,
        string successMessage,
        CancellationToken cancellationToken)
    {
        var content = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new GitReviewOperationResult(false, string.IsNullOrWhiteSpace(content) ? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}" : content.Trim());
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("errors", out var errors))
            {
                return new GitReviewOperationResult(false, errors.ToString());
            }
        }

        return new GitReviewOperationResult(true, successMessage);
    }

    private static string NormalizeRemoteName(string? remoteName) => string.IsNullOrWhiteSpace(remoteName) ? "origin" : remoteName.Trim();

    private static bool TryGetInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }

    private static int? TryGetInt32OrNull(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;

    private static bool? TryGetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? TryGetNestedString(JsonElement element, string objectPropertyName, string propertyName)
    {
        return element.TryGetProperty(objectPropertyName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? TryGetString(nested, propertyName)
            : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(property.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    private sealed record GitHubReviewComment(
        string Id,
        string? InReplyToId,
        string Author,
        string? Path,
        int? Line,
        DateTimeOffset? CreatedAt,
        GitReviewCommentInfo Info);
}
