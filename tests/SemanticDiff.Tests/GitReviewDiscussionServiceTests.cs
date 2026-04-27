using System.Net;
using SemanticDiff.Core;
using SemanticDiff.Git;

namespace SemanticDiff.Tests;

public sealed class GitReviewDiscussionServiceTests
{
    [Fact]
    public async Task GetDiscussionAsync_LoadsGitHubConversationAndReviewThreads()
    {
        var runner = CreateRemoteRunner("https://github.com/owner/repo.git");
        var handler = new RoutingResponseHandler(request =>
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("/issues/42/comments", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        [
                          {
                            "id": 10,
                            "body": "General direction looks good.",
                            "html_url": "https://github.com/owner/repo/pull/42#issuecomment-10",
                            "created_at": "2026-04-01T10:00:00Z",
                            "updated_at": "2026-04-01T10:00:00Z",
                            "user": { "login": "alice" }
                          }
                        ]
                        """)
                };
            }

            if (url.Contains("/pulls/42/comments", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        [
                          {
                            "id": 77,
                            "body": "Please rename this.",
                            "path": "src/App.cs",
                            "line": 12,
                            "html_url": "https://github.com/owner/repo/pull/42#discussion_r77",
                            "created_at": "2026-04-01T11:00:00Z",
                            "updated_at": "2026-04-01T11:00:00Z",
                            "user": { "login": "bob" }
                          },
                          {
                            "id": 78,
                            "in_reply_to_id": 77,
                            "body": "Renamed.",
                            "path": "src/App.cs",
                            "line": 12,
                            "html_url": "https://github.com/owner/repo/pull/42#discussion_r78",
                            "created_at": "2026-04-01T12:00:00Z",
                            "updated_at": "2026-04-01T12:00:00Z",
                            "user": { "login": "carol" }
                          }
                        ]
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });
        using var httpClient = new HttpClient(handler);
        var service = new GitReviewDiscussionService(runner, httpClient);

        var snapshot = await service.GetDiscussionAsync("/repo", CreateGitHubPullRequest(), CancellationToken.None);

        Assert.Equal(2, snapshot.Threads.Length);
        Assert.Contains(snapshot.Threads, thread => thread.Kind == GitReviewThreadKind.Conversation && thread.Comments.Single().Author == "alice");
        var reviewThread = Assert.Single(snapshot.Threads, thread => thread.Kind == GitReviewThreadKind.Diff);
        Assert.Equal("src/App.cs", reviewThread.Path);
        Assert.Equal(12, reviewThread.Line);
        Assert.Equal(2, reviewThread.Comments.Length);
    }

    [Fact]
    public async Task AddCommentAsync_PostsGitHubIssueComment()
    {
        var runner = CreateRemoteRunner("https://github.com/owner/repo.git");
        var handler = new RoutingResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") });
        using var httpClient = new HttpClient(handler);
        var service = new GitReviewDiscussionService(runner, httpClient);

        var result = await service.AddCommentAsync("/repo", CreateGitHubPullRequest(), "Looks good.", CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("/repos/owner/repo/issues/42/comments", request.Url);
        Assert.Contains("\"body\":\"Looks good.\"", request.Body);
    }

    [Fact]
    public async Task ReplyToThreadAsync_PostsGitHubReviewReply()
    {
        var runner = CreateRemoteRunner("https://github.com/owner/repo.git");
        var handler = new RoutingResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") });
        using var httpClient = new HttpClient(handler);
        var service = new GitReviewDiscussionService(runner, httpClient);

        var result = await service.ReplyToThreadAsync("/repo", CreateGitHubPullRequest(), "review:77", "Updated.", CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("/repos/owner/repo/pulls/42/comments", request.Url);
        Assert.Contains("\"in_reply_to\":77", request.Body);
    }

    [Fact]
    public async Task GetDiscussionAsync_LoadsGitLabDiscussions()
    {
        var runner = CreateRemoteRunner("https://gitlab.com/group/repo.git");
        var handler = new RoutingResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                [
                  {
                    "id": "abc",
                    "individual_note": false,
                    "resolved": false,
                    "notes": [
                      {
                        "id": 5,
                        "type": "DiffNote",
                        "body": "Can simplify this branch.",
                        "system": false,
                        "created_at": "2026-04-01T10:00:00Z",
                        "updated_at": "2026-04-01T10:00:00Z",
                        "web_url": "https://gitlab.com/group/repo/-/merge_requests/24#note_5",
                        "author": { "username": "dana" },
                        "position": { "new_path": "src/App.cs", "new_line": 21 }
                      }
                    ]
                  }
                ]
                """)
        });
        using var httpClient = new HttpClient(handler);
        var service = new GitReviewDiscussionService(runner, httpClient);

        var snapshot = await service.GetDiscussionAsync("/repo", CreateGitLabMergeRequest(), CancellationToken.None);

        var thread = Assert.Single(snapshot.Threads);
        Assert.Equal("abc", thread.Id);
        Assert.Equal(GitReviewThreadKind.Diff, thread.Kind);
        Assert.Equal("src/App.cs", thread.Path);
        Assert.Equal(21, thread.Line);
        Assert.True(thread.CanResolve);
        Assert.Equal("dana", thread.Comments.Single().Author);
    }

    [Fact]
    public async Task GitLabWriteOperationsUseDiscussionEndpoints()
    {
        var runner = CreateRemoteRunner("https://gitlab.com/group/repo.git");
        var handler = new RoutingResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") });
        using var httpClient = new HttpClient(handler);
        var service = new GitReviewDiscussionService(runner, httpClient);

        var addResult = await service.AddCommentAsync("/repo", CreateGitLabMergeRequest(), "Top-level thread", CancellationToken.None);
        var replyResult = await service.ReplyToThreadAsync("/repo", CreateGitLabMergeRequest(), "abc", "Reply body", CancellationToken.None);

        Assert.True(addResult.Succeeded);
        Assert.True(replyResult.Succeeded);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Url.Contains("/merge_requests/24/discussions", StringComparison.Ordinal) && request.Body.Contains("Top-level+thread", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Post && request.Url.Contains("/merge_requests/24/discussions/abc/notes", StringComparison.Ordinal) && request.Body.Contains("Reply+body", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetThreadResolvedAsync_UsesGitLabResolveEndpoint()
    {
        var runner = CreateRemoteRunner("https://gitlab.com/group/repo.git");
        var handler = new RoutingResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var httpClient = new HttpClient(handler);
        var service = new GitReviewDiscussionService(runner, httpClient);

        var result = await service.SetThreadResolvedAsync("/repo", CreateGitLabMergeRequest(), "abc", true, CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Contains("/merge_requests/24/discussions/abc", request.Url);
        Assert.Contains("resolved=true", request.Body);
    }

    private static FakeGitCommandRunner CreateRemoteRunner(string remoteUrl) =>
        new(arguments => arguments.SequenceEqual(["remote", "get-url", "upstream"])
            ? new GitCommandResult(0, $"{remoteUrl}\n", string.Empty)
            : new GitCommandResult(1, string.Empty, "not found"));

    private static GitPullRequestInfo CreateGitHubPullRequest() =>
        new(42, "Review app", "main", "feature/review", "owner/repo", true, "upstream", GitReviewRequestKind.PullRequest);

    private static GitPullRequestInfo CreateGitLabMergeRequest() =>
        new(24, "Review app", "main", "feature/review", "group/repo", true, "upstream", GitReviewRequestKind.MergeRequest);

    private sealed class RoutingResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

        public List<CapturedRequest> Requests { get; } = [];

        public RoutingResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            this.respond = respond;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.RequestUri?.ToString() ?? string.Empty, body));
            return respond(request);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Url, string Body);
}
