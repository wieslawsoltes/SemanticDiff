namespace SemanticDiff.App.ViewModels;

public enum LoadingOperationKind
{
    Foreground,
    Background,
    Tab
}

public sealed partial class LoadingOperationViewModel : ObservableObject
{
    public LoadingOperationViewModel(string id, LoadingOperationKind kind, string message, bool drivesGlobalProgress)
    {
        Id = id;
        Kind = kind;
        DrivesGlobalProgress = drivesGlobalProgress;
        this.message = message;
    }

    public string Id { get; }

    public LoadingOperationKind Kind { get; }

    public bool DrivesGlobalProgress { get; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public string SummaryText => IsIndeterminate ? Message : $"{Progress:P0} {Message}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private string message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private double progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private bool isIndeterminate = true;

    public void Report(double value, string nextMessage)
    {
        Progress = Math.Clamp(value, 0, 1);
        IsIndeterminate = false;
        Message = nextMessage;
    }

    public void ReportIndeterminate(string nextMessage)
    {
        IsIndeterminate = true;
        Message = nextMessage;
    }
}
