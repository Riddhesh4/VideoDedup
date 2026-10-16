using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using VideoDedup.Application;

namespace VideoDedup.Desktop.ViewModels;

/// <summary>
/// Review flow view model: duplicate groups with their explainable evidence, side-by-side aligned previews, manual
/// decisions, and reversible quarantine. Nothing here acts on a file without an explicit confirmation, and the
/// view model never touches FFmpeg, SQLite, or the filesystem directly - only Application ports.
/// </summary>
public sealed class ReviewViewModel : ObservableObject
{
    private const int PreviewWidth = 480;

    private readonly IReviewService _review;
    private readonly IQuarantineService _quarantine;
    private readonly IConfirmationPrompt _prompt;
    private readonly IFramePreviewSource? _previews;
    private readonly List<ReviewGroup> _allGroups = new();

    private ReviewGroup? _selectedGroup;
    private ReviewFileInfo? _selectedFile;
    private bool _showOnlyUndecided;
    private string _statusMessage = "Ready.";
    private string? _errorMessage;
    private byte[]? _leftPreviewBytes;
    private byte[]? _rightPreviewBytes;
    private string _leftPreviewLabel = "No preview loaded.";
    private string _rightPreviewLabel = "No preview loaded.";
    private double _previewPositionSeconds;
    private double _previewMaximumSeconds = 1;

    public ReviewViewModel(IReviewService review, IQuarantineService quarantine, IConfirmationPrompt prompt,
        IFramePreviewSource? previews = null)
    {
        _review = review ?? throw new ArgumentNullException(nameof(review));
        _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _previews = previews;

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None), onError: Report);
        PreviewCommand = new AsyncRelayCommand(() => LoadPreviewAsync(CancellationToken.None),
            () => SelectedGroup is not null && _previews is not null, Report);
        KeepCommand = new AsyncRelayCommand(() => SetDecisionAsync(ReviewDecision.Keep), () => SelectedFile is not null, Report);
        RejectCommand = new AsyncRelayCommand(() => SetDecisionAsync(ReviewDecision.Reject), () => SelectedFile is not null, Report);
        NeedsReviewCommand = new AsyncRelayCommand(() => SetDecisionAsync(ReviewDecision.NeedsReview), () => SelectedFile is not null, Report);
        QuarantineCommand = new AsyncRelayCommand(() => QuarantineAsync(CancellationToken.None),
            () => SelectedGroup is not null && SelectedGroup.Files.Any(file => file.CanBeActedOn), Report);
        UndoLastCommand = new AsyncRelayCommand(() => UndoLastAsync(CancellationToken.None),
            () => History.Any(record => record.State == QuarantineState.Quarantined), Report);
    }

    public ObservableCollection<ReviewGroup> Groups { get; } = new();

    public ObservableCollection<QuarantineRecord> History { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand PreviewCommand { get; }

    public AsyncRelayCommand KeepCommand { get; }

    public AsyncRelayCommand RejectCommand { get; }

    public AsyncRelayCommand NeedsReviewCommand { get; }

    public AsyncRelayCommand QuarantineCommand { get; }

    public AsyncRelayCommand UndoLastCommand { get; }

    public ReviewGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value))
            {
                return;
            }

            SelectedFile = value?.Files.FirstOrDefault();
            PreviewPositionSeconds = 0;
            PreviewMaximumSeconds = Math.Max(1, (value?.Files
                .Where(file => file.Duration is { } duration && duration > TimeSpan.Zero)
                .Select(file => file.Duration!.Value.TotalSeconds)
                .DefaultIfEmpty(1).Min() ?? 1) - 0.5);
            LeftPreviewBytes = null;
            RightPreviewBytes = null;
            LeftPreviewLabel = "No preview loaded.";
            RightPreviewLabel = "No preview loaded.";
            RaiseFor(nameof(CanQuarantineGroup), nameof(PreviewHint));
            RaiseCommandStates();
        }
    }

    public ReviewFileInfo? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                RaiseFor(nameof(SelectedFileSummary));
                RaiseCommandStates();
            }
        }
    }

    public bool ShowOnlyUndecided
    {
        get => _showOnlyUndecided;
        set
        {
            if (SetProperty(ref _showOnlyUndecided, value))
            {
                ApplyFilter();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public byte[]? LeftPreviewBytes
    {
        get => _leftPreviewBytes;
        private set => SetProperty(ref _leftPreviewBytes, value);
    }

    public byte[]? RightPreviewBytes
    {
        get => _rightPreviewBytes;
        private set => SetProperty(ref _rightPreviewBytes, value);
    }

    public string LeftPreviewLabel
    {
        get => _leftPreviewLabel;
        private set => SetProperty(ref _leftPreviewLabel, value);
    }

    public string RightPreviewLabel
    {
        get => _rightPreviewLabel;
        private set => SetProperty(ref _rightPreviewLabel, value);
    }

    public double PreviewPositionSeconds
    {
        get => _previewPositionSeconds;
        set => SetProperty(ref _previewPositionSeconds, Math.Max(0, value));
    }

    public double PreviewMaximumSeconds
    {
        get => _previewMaximumSeconds;
        private set => SetProperty(ref _previewMaximumSeconds, value);
    }

    public bool CanQuarantineGroup => SelectedGroup?.Files.Any(file => file.CanBeActedOn) == true;

    public string PreviewHint => _previews is null
        ? "Frame previews are unavailable in this session."
        : "Loads one frame from each copy at the same content moment (aligned by the detected offset).";

    public string SelectedFileSummary => SelectedFile is null
        ? "No copy selected."
        : $"{DisplayText.FileName(SelectedFile.FullPath)} — {SelectedFile.DecisionText}";

    /// <summary>Loads the groups and the quarantine history. Every failure surfaces as text, never as a crash.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (await LoadAsync(cancellationToken).ConfigureAwait(true))
        {
            StatusMessage = SummaryText();
        }
    }

    /// <summary>Resolves quarantine entries interrupted by a crash, then loads the groups.</summary>
    public async Task RecoverThenRefreshAsync(CancellationToken cancellationToken)
    {
        string? recovery = null;
        try
        {
            var report = await _quarantine.RecoverInterruptedAsync(cancellationToken).ConfigureAwait(true);
            if (report.Resolved.Count > 0 || report.NeedsAttention.Count > 0)
            {
                recovery = $"Quarantine recovery: {report.Resolved.Count} interrupted entr(ies) resolved, " +
                    $"{report.NeedsAttention.Count} quarantined file(s) missing from the quarantine folder.";
            }
        }
        catch (Exception exception)
        {
            Report(exception);
        }

        if (await LoadAsync(cancellationToken).ConfigureAwait(true))
        {
            StatusMessage = recovery ?? SummaryText();
        }
    }

    private string SummaryText() => _allGroups.Count == 0
        ? "No duplicate groups yet. Scan a folder in the Processing tab, then refresh."
        : $"{_allGroups.Count} duplicate group(s), {_allGroups.Count(group => group.IsUndecided)} still undecided.";

    /// <summary>Reloads groups and history without overwriting the message of the action that asked for it.</summary>
    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        try
        {
            var groups = await _review.GetGroupsAsync(cancellationToken).ConfigureAwait(true);
            _allGroups.Clear();
            _allGroups.AddRange(groups);
            ApplyFilter();
            await ReloadHistoryAsync(cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch (Exception exception)
        {
            Report(exception);
            return false;
        }
    }

    public async Task SetDecisionAsync(ReviewDecision decision, CancellationToken cancellationToken = default)
    {
        var group = SelectedGroup;
        var file = SelectedFile;
        if (group is null || file is null)
        {
            return;
        }

        ErrorMessage = null;
        try
        {
            await _review.SetDecisionAsync(group.Key, file.FileId, decision, cancellationToken).ConfigureAwait(true);
            var message = $"{Describe(decision)} recorded for {DisplayText.FileName(file.FullPath)}.";
            await LoadAsync(cancellationToken).ConfigureAwait(true);
            StatusMessage = message;
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    /// <summary>
    /// Quarantine is the only action that moves data. It plans first, asks for an explicit confirmation, and can be
    /// undone; declining leaves every file untouched.
    /// </summary>
    public async Task QuarantineAsync(CancellationToken cancellationToken)
    {
        var group = SelectedGroup;
        if (group is null)
        {
            return;
        }

        ErrorMessage = null;
        var targets = group.Files.Where(file => file.CanBeActedOn).Select(file => file.FileId).ToArray();
        if (targets.Length == 0)
        {
            StatusMessage = "Nothing in this group can be quarantined right now; every copy is blocked or already quarantined.";
            return;
        }

        try
        {
            var plan = await _quarantine.PlanAsync(targets, cancellationToken).ConfigureAwait(true);
            if (!plan.RequiresConfirmation)
            {
                StatusMessage = "Nothing was quarantined: " + string.Join(' ', plan.BlockedItems.SelectMany(item => item.Reasons));
                return;
            }

            var confirmed = await _prompt.ConfirmAsync("Quarantine files",
                BuildConfirmationText(plan), cancellationToken).ConfigureAwait(true);
            if (!confirmed)
            {
                StatusMessage = "Quarantine canceled; nothing was moved.";
                return;
            }

            var result = await _quarantine.ExecuteAsync(plan, confirmed: true, cancellationToken).ConfigureAwait(true);
            var moved = result.Moved.Count;
            var failures = result.Failures;
            var message = failures.Count == 0
                ? $"{moved} file(s) moved to quarantine. Undo restores them."
                : $"{moved} file(s) moved; {failures.Count} could not be quarantined: " +
                  string.Join(' ', failures.Select(failure => failure.Failure));
            if (plan.SpansVolumes)
            {
                message += " Some files were copied across drives.";
            }

            await LoadAsync(cancellationToken).ConfigureAwait(true);
            StatusMessage = message;
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    /// <summary>Restores the most recent quarantined file after an explicit confirmation.</summary>
    public async Task UndoLastAsync(CancellationToken cancellationToken)
    {
        var record = History.Where(item => item.State == QuarantineState.Quarantined)
            .OrderByDescending(item => item.CreatedUtc)
            .FirstOrDefault();
        if (record is null)
        {
            StatusMessage = "There is nothing to restore.";
            return;
        }

        ErrorMessage = null;
        try
        {
            var confirmed = await _prompt.ConfirmAsync("Restore file",
                $"Restore {DisplayText.FileName(record.SourcePath)} from quarantine to its original folder?\n\n" +
                $"{DisplayText.Sanitize(record.SourcePath, 300)}", cancellationToken).ConfigureAwait(true);
            if (!confirmed)
            {
                StatusMessage = "Restore canceled; nothing was moved.";
                return;
            }

            var outcomes = await _quarantine.UndoAsync([record.RecordId], cancellationToken).ConfigureAwait(true);
            var message = outcomes.All(outcome => outcome.Succeeded)
                ? $"Restored {DisplayText.FileName(record.SourcePath)}."
                : string.Join(' ', outcomes.Where(outcome => !outcome.Succeeded).Select(outcome => outcome.Failure));
            await LoadAsync(cancellationToken).ConfigureAwait(true);
            StatusMessage = message;
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    /// <summary>Captures one frame from each copy at the same content moment, aligned by the detected offset.</summary>
    public async Task LoadPreviewAsync(CancellationToken cancellationToken)
    {
        LeftPreviewBytes = null;
        RightPreviewBytes = null;
        LeftPreviewLabel = "No preview loaded.";
        RightPreviewLabel = "No preview loaded.";
        if (_previews is null || SelectedGroup is not { } group)
        {
            return;
        }

        var usable = group.Files
            .Where(file => file.CanBeActedOn && file.Duration is { } duration && duration > TimeSpan.Zero)
            .OrderByDescending(file => file.Duration)
            .ToArray();
        if (usable.Length < 2)
        {
            StatusMessage = "Aligned previews need two reachable copies with known duration.";
            return;
        }

        var offset = group.OffsetSeconds is decimal seconds and > 0 ? TimeSpan.FromSeconds((double)seconds) : TimeSpan.Zero;
        var container = usable[0];
        var excerpt = usable[1];
        var position = TimeSpan.FromSeconds(Math.Min(PreviewPositionSeconds, Math.Max(0, excerpt.Duration!.Value.TotalSeconds - 0.1)));
        var containerTimestamp = Clamp(offset + position, container.Duration!.Value);
        var excerptTimestamp = Clamp(position, excerpt.Duration.Value);
        try
        {
            var left = await _previews.CaptureAsync(container.FullPath, Revision(container), containerTimestamp, PreviewWidth, cancellationToken)
                .ConfigureAwait(true);
            var right = await _previews.CaptureAsync(excerpt.FullPath, Revision(excerpt), excerptTimestamp, PreviewWidth, cancellationToken)
                .ConfigureAwait(true);
            LeftPreviewBytes = left?.ImageBytes;
            RightPreviewBytes = right?.ImageBytes;
            LeftPreviewLabel = left is null
                ? $"No frame available for {DisplayText.FileName(container.FullPath)} at {Describe(containerTimestamp)}."
                : $"{DisplayText.FileName(container.FullPath)} at {Describe(containerTimestamp)}";
            RightPreviewLabel = right is null
                ? $"No frame available for {DisplayText.FileName(excerpt.FullPath)} at {Describe(excerptTimestamp)}."
                : $"{DisplayText.FileName(excerpt.FullPath)} at {Describe(excerptTimestamp)}";
            StatusMessage = offset > TimeSpan.Zero
                ? $"Aligned preview at the detected {Describe(offset)} offset."
                : "Side-by-side preview at the same position in both copies.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Report(exception);
        }
    }

    private static FileRevision Revision(ReviewFileInfo file) => new(file.SizeBytes, file.ModifiedUtc);

    private static TimeSpan Clamp(TimeSpan timestamp, TimeSpan duration)
    {
        var limit = duration - TimeSpan.FromMilliseconds(100);
        if (limit <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return timestamp > limit ? limit : timestamp;
    }

    private static string Describe(TimeSpan timestamp) =>
        timestamp.TotalHours >= 1
            ? timestamp.ToString(@"hh\:mm\:ss\.f", CultureInfo.InvariantCulture)
            : timestamp.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);

    private static string Describe(ReviewDecision decision) => decision switch
    {
        ReviewDecision.Keep => "Keep",
        ReviewDecision.Reject => "Reject",
        ReviewDecision.NeedsReview => "Needs review",
        _ => "Undecided",
    };

    /// <summary>Confirmation text always names the destinations, the volume warning, and every blocked copy.</summary>
    internal static string BuildConfirmationText(QuarantinePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var builder = new StringBuilder();
        builder.AppendLine($"{plan.AllowedItems.Count} file(s) will move to quarantine. Files are never deleted and this can be undone.");
        builder.AppendLine();
        foreach (var item in plan.AllowedItems.Take(10))
        {
            builder.AppendLine($"  - {DisplayText.Sanitize(item.DisplayName)} -> {DisplayText.Sanitize(item.DestinationPath, 300)}");
        }

        if (plan.AllowedItems.Count > 10)
        {
            builder.AppendLine($"  ...and {plan.AllowedItems.Count - 10} more.");
        }

        if (plan.SpansVolumes)
        {
            builder.AppendLine();
            builder.AppendLine("Warning: some files are on a different drive than the quarantine folder, so they are copied across drives.");
        }

        if (plan.BlockedItems.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Not quarantined:");
            foreach (var item in plan.BlockedItems)
            {
                builder.AppendLine($"  - {DisplayText.Sanitize(item.DisplayName)}: {string.Join(' ', item.Reasons)}");
            }
        }

        builder.AppendLine();
        builder.Append("Quarantine these files?");
        return builder.ToString();
    }

    private void ApplyFilter()
    {
        var previous = SelectedGroup?.Key;
        Groups.Clear();
        foreach (var group in _allGroups.Where(group => !ShowOnlyUndecided || group.IsUndecided))
        {
            Groups.Add(group);
        }

        SelectedGroup = Groups.FirstOrDefault(group => group.Key == previous) ?? Groups.FirstOrDefault();
    }

    private async Task ReloadHistoryAsync(CancellationToken cancellationToken)
    {
        History.Clear();
        foreach (var record in await _quarantine.GetHistoryAsync(cancellationToken).ConfigureAwait(true))
        {
            History.Add(record);
        }

        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        foreach (var command in new[] { PreviewCommand, KeepCommand, RejectCommand, NeedsReviewCommand, QuarantineCommand, UndoLastCommand })
        {
            command.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Reports a failure observed by the view itself (for example startup recovery).</summary>
    public void ReportExternalFailure(Exception exception) => Report(exception);

    private void Report(Exception exception) =>
        ErrorMessage = exception is OperationCanceledException
            ? "The operation was canceled; completed steps are recorded in the quarantine log."
            : $"The action failed ({exception.GetType().Name}).";
}
