using SemanticDiff.Core;

namespace SemanticDiff.App.ViewModels;

public sealed partial class MainViewModel
{
    public WorkspaceTabViewModel OpenPatchCompareTab()
    {
        var patchCompare = new PatchCompareTabViewModel(
            "Patch Series Compare",
            "Compare any two Git patch stacks, ranges, tags, branches, or commit spans with git range-diff");
        var tab = WorkspaceTabViewModel.CreatePatchCompare(
            $"patch-compare:{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}",
            "Patch compare",
            "Universal range-diff",
            patchCompare);
        AddWorkspaceTab(tab);
        AddDiagnostic("Info", "Opened patch series comparison");
        return tab;
    }

    public async Task RunPatchCompareAsync(WorkspaceTabViewModel? tab)
    {
        tab ??= SelectedWorkspaceTab;
        if (tab?.PatchCompare is not { } patchCompare)
        {
            return;
        }

        var repositoryPath = string.IsNullOrWhiteSpace(patchCompare.ComparisonRepositoryPath)
            ? currentRepositoryPath
            : patchCompare.ComparisonRepositoryPath;
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            patchCompare.SetError("Open a repository, inspect a local repository path, or inspect a remote Git URL before comparing patch series.");
            AddDiagnostic("Warning", "Patch comparison needs a repository source");
            return;
        }

        GitPatchSeriesComparisonRequest request;
        try
        {
            request = patchCompare.CreateRequest(repositoryPath);
        }
        catch (ArgumentException ex)
        {
            patchCompare.SetError(ex.Message);
            AddDiagnostic("Warning", ex.Message);
            return;
        }

        patchCompare.SetRunning();
        var operation = BeginTabOperation(
            tab,
            $"Comparing patch series {request.OldRange} -> {request.NewRange}",
            drivesGlobalProgress: false);

        try
        {
            ReportIndeterminate(operation, "Loading patch series metadata");
            var snapshot = await gitPatchSeriesComparisonService.CompareAsync(request, operation.Token);
            patchCompare.SetResult(snapshot);
            tab.StatusText = patchCompare.StatusText;
            CompleteOperation(operation, "Patch comparison ready");
            AddDiagnostic("Info", $"Patch comparison ready: {patchCompare.SummaryText}");
        }
        catch (OperationCanceledException)
        {
            patchCompare.SetError("Patch comparison canceled.");
            CompleteOperation(operation, "Patch comparison canceled");
        }
        catch (Exception ex)
        {
            patchCompare.SetError(ex.Message);
            AddDiagnostic("Error", $"Patch comparison failed: {ex.Message}");
            CompleteOperation(operation, "Patch comparison failed");
        }
    }

    public void UseCurrentRepositoryForPatchCompare(WorkspaceTabViewModel? tab)
    {
        tab ??= SelectedWorkspaceTab;
        tab?.PatchCompare?.UseCurrentRepository(currentRepositoryPath);
    }

    public async Task DiscoverPatchCompareSourcesAsync(WorkspaceTabViewModel? tab)
    {
        tab ??= SelectedWorkspaceTab;
        if (tab?.PatchCompare is not { } patchCompare)
        {
            return;
        }

        GitPatchSeriesDiscoveryRequest request;
        try
        {
            request = patchCompare.CreateDiscoveryRequest(currentRepositoryPath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            patchCompare.SetWizardError(ex.Message);
            AddDiagnostic("Warning", ex.Message);
            return;
        }

        var sourceText = string.IsNullOrWhiteSpace(request.SourceText)
            ? request.FallbackRepositoryPath ?? "current repository"
            : request.SourceText;
        patchCompare.SetWizardRunning(sourceText);
        var operation = BeginTabOperation(
            tab,
            $"Inspecting patch compare source {sourceText}",
            drivesGlobalProgress: false);

        try
        {
            ReportIndeterminate(operation, "Discovering branches and tags");
            var snapshot = await gitPatchSeriesDiscoveryService.DiscoverAsync(request, operation.Token);
            patchCompare.SetWizardSnapshot(snapshot);
            tab.StatusText = patchCompare.WizardStatusText;
            CompleteOperation(operation, "Patch compare source ready");
            AddDiagnostic("Info", patchCompare.WizardStatusText);
        }
        catch (OperationCanceledException)
        {
            patchCompare.SetWizardError("Patch compare source discovery canceled.");
            CompleteOperation(operation, "Patch compare source discovery canceled");
        }
        catch (Exception ex)
        {
            patchCompare.SetWizardError(ex.Message);
            AddDiagnostic("Error", $"Patch compare source discovery failed: {ex.Message}");
            CompleteOperation(operation, "Patch compare source discovery failed");
        }
    }

    public void ApplyPatchCompareWizardSelection(WorkspaceTabViewModel? tab)
    {
        tab ??= SelectedWorkspaceTab;
        if (tab?.PatchCompare is not { } patchCompare)
        {
            return;
        }

        try
        {
            patchCompare.ApplyWizardSelection();
            tab.StatusText = patchCompare.StatusText;
            AddDiagnostic("Info", $"Patch compare ranges set: {patchCompare.OldRangeText} vs {patchCompare.NewRangeText}");
        }
        catch (ArgumentException ex)
        {
            patchCompare.SetWizardError(ex.Message);
            AddDiagnostic("Warning", ex.Message);
        }
    }

    public void ResetPatchCompare(WorkspaceTabViewModel? tab)
    {
        tab ??= SelectedWorkspaceTab;
        tab?.PatchCompare?.Reset();
    }
}
