using System.Collections.Immutable;
using Microsoft.UI.Xaml;
using SemanticDiff.Core;

namespace SemanticDiff.App.ViewModels;

public sealed partial record ReviewCommentItemViewModel(
    string Id,
    string ThreadId,
    string Author,
    string Body,
    string CreatedText,
    string WebUrl,
    bool IsSystem)
{
    public string HeaderText => string.IsNullOrWhiteSpace(CreatedText) ? Author : $"{Author}  {CreatedText}";

    public Visibility SystemVisibility => IsSystem ? Visibility.Visible : Visibility.Collapsed;

    public string SearchText => $"{Author} {Body} {CreatedText} {WebUrl}";

    public static ReviewCommentItemViewModel FromComment(GitReviewCommentInfo comment) => new(
        comment.Id,
        comment.ThreadId,
        string.IsNullOrWhiteSpace(comment.Author) ? "unknown" : comment.Author,
        comment.Body,
        FormatTimestamp(comment.UpdatedAt ?? comment.CreatedAt),
        comment.WebUrl ?? string.Empty,
        comment.IsSystem);

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp is null ? string.Empty : timestamp.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture);
}

public sealed partial record ReviewThreadItemViewModel(
    string Id,
    GitReviewThreadKind Kind,
    string Title,
    string DetailText,
    string Path,
    int? Line,
    string PreviewText,
    string BadgeText,
    bool IsResolved,
    bool CanReply,
    bool CanResolve,
    ImmutableArray<ReviewCommentItemViewModel> Comments)
{
    public string KindText => Kind switch
    {
        GitReviewThreadKind.Diff => "Diff",
        GitReviewThreadKind.Commit => "Commit",
        GitReviewThreadKind.System => "System",
        _ => "Conversation"
    };

    public Visibility ResolvedVisibility => IsResolved ? Visibility.Visible : Visibility.Collapsed;

    public string SearchText => $"{Title} {DetailText} {Path} {Line} {PreviewText} {KindText} {string.Join(' ', Comments.Select(comment => comment.SearchText))}";

    public static ReviewThreadItemViewModel FromThread(GitReviewThreadInfo thread)
    {
        var comments = thread.Comments.Select(ReviewCommentItemViewModel.FromComment).ToImmutableArray();
        var firstBody = comments.FirstOrDefault()?.Body ?? string.Empty;
        var preview = firstBody.Length <= 140 ? firstBody : $"{firstBody[..137]}...";
        var detailParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(thread.Path))
        {
            detailParts.Add(thread.Line is null ? thread.Path! : $"{thread.Path}:{thread.Line}");
        }

        detailParts.Add(thread.CommentCount == 1 ? "1 comment" : $"{thread.CommentCount:N0} comments");
        return new ReviewThreadItemViewModel(
            thread.Id,
            thread.Kind,
            thread.Title,
            string.Join("  ", detailParts),
            thread.Path ?? string.Empty,
            thread.Line,
            preview,
            thread.CommentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            thread.IsResolved,
            thread.CanReply,
            thread.CanResolve,
            comments);
    }
}
