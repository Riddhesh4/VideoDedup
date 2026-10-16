using System.IO;
using VideoDedup.Application;
using VideoDedup.Domain;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Desktop.Tests;

internal sealed class FakeReviewService : IReviewService
{
    public IReadOnlyList<ReviewGroup> Groups { get; set; } = Array.Empty<ReviewGroup>();

    public List<(string GroupKey, Guid FileId, ReviewDecision Decision)> Decisions { get; } = new();

    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<ReviewGroup>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(Groups);
    }

    public Task SetDecisionAsync(string groupKey, Guid fileId, ReviewDecision decision, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Decisions.Add((groupKey, fileId, decision));
        Groups = Groups.Select(group => group.Key == groupKey
            ? ReviewGroup.Create(group.Key, group.MatchType, group.OverallScore, group.VisualScore, group.AudioScore,
                group.OffsetSeconds, group.Reasons,
                group.Files.Select(file => file.FileId == fileId ? file with { Decision = decision } : file),
                group.RecommendedKeepFileId, group.RecommendationReason)
            : group).ToArray();
        return Task.CompletedTask;
    }
}

internal sealed class FakeQuarantineService : IQuarantineService
{
    public QuarantinePlan Plan { get; set; } = new(Array.Empty<QuarantinePlanItem>());

    public QuarantineBatchResult Result { get; set; } = new(false, Array.Empty<QuarantineOutcome>());

    public List<Guid[]> PlannedSelections { get; } = new();

    public List<bool> ConfirmedFlags { get; } = new();

    public List<Guid[]> UndoneRecords { get; } = new();

    public List<QuarantineRecord> History { get; } = new();

    public QuarantineRecoveryReport Recovery { get; set; } = new(Array.Empty<QuarantineRecord>(), Array.Empty<QuarantineRecord>());

    public Task<QuarantinePlan> PlanAsync(IReadOnlyList<Guid> fileIds, CancellationToken cancellationToken)
    {
        PlannedSelections.Add(fileIds.ToArray());
        return Task.FromResult(Plan);
    }

    public Task<QuarantineBatchResult> ExecuteAsync(QuarantinePlan plan, bool confirmed, CancellationToken cancellationToken)
    {
        ConfirmedFlags.Add(confirmed);
        return Task.FromResult(Result);
    }

    public Task<IReadOnlyList<QuarantineRecord>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<QuarantineRecord> snapshot = History.ToArray();
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<QuarantineOutcome>> UndoAsync(IReadOnlyList<Guid> recordIds, CancellationToken cancellationToken)
    {
        UndoneRecords.Add(recordIds.ToArray());
        IReadOnlyList<QuarantineOutcome> outcomes = recordIds.Select(id => new QuarantineOutcome(id, true, null, id, false)).ToArray();
        return Task.FromResult(outcomes);
    }

    public Task<QuarantineRecoveryReport> RecoverInterruptedAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Recovery);
}

internal sealed class FakeConfirmationPrompt : IConfirmationPrompt
{
    public bool Answer { get; set; } = true;

    public List<string> Messages { get; } = new();

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
    {
        Messages.Add(message);
        return Task.FromResult(Answer);
    }
}

internal sealed class FakePreviewSource : IFramePreviewSource
{
    public byte[] Bytes { get; set; } = [0xFF, 0xD8, 0xFF, 0xD9];

    public List<(string Path, TimeSpan Timestamp)> Captures { get; } = new();

    public bool Available { get; set; } = true;

    public Task<FramePreview?> CaptureAsync(string path, FileRevision expected, TimeSpan timestamp, int width,
        CancellationToken cancellationToken)
    {
        Captures.Add((path, timestamp));
        if (!Available)
        {
            return Task.FromResult<FramePreview?>(null);
        }

        return Task.FromResult<FramePreview?>(FramePreview.Create(Bytes, timestamp, 320, 180));
    }
}

internal sealed class FakeDashboard : IProcessingDashboard
{
    public List<string> Scans { get; } = new();

    public List<ProcessingRow> Rows { get; } = new();

    public QueueStats Stats { get; set; } = new(3, 1, 5, 2);

    public List<WorkerStatus> Workers { get; } = new()
    {
        new("Probe", WorkerRunState.Running, 4, 0, null, DateTimeOffset.UtcNow),
        new("Match", WorkerRunState.Idle, 2, 1, "MediaProcessingException: InvalidData", DateTimeOffset.UtcNow),
    };

    public int Pauses { get; private set; }

    public int Resumes { get; private set; }

    public Func<CancellationToken, Task>? ScanBehavior { get; set; }

    public Task ScanAsync(string folder, CancellationToken cancellationToken)
    {
        Scans.Add(folder);
        return ScanBehavior?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProcessingRow>> RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessingRow> snapshot = Rows.ToArray();
        return Task.FromResult(snapshot);
    }

    public Task<QueueStats> StatsAsync(CancellationToken cancellationToken) => Task.FromResult(Stats);

    public IReadOnlyList<WorkerStatus> WorkerStates()
    {
        IReadOnlyList<WorkerStatus> snapshot = Workers.ToArray();
        return snapshot;
    }

    public void PauseWorkers() => Pauses++;

    public void ResumeWorkers() => Resumes++;
}

internal static class ReviewBuilders
{
    public static readonly Guid Root = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static ReviewFileInfo File(Guid fileId, string relativePath, string? fullPath = null, bool present = true,
        bool quarantined = false, bool rootAvailable = true, bool rootEnabled = true, TimeSpan? duration = null,
        ReviewDecision decision = ReviewDecision.Pending, int width = 1920, int height = 1080, long size = 1_000_000) =>
        new(fileId, Root, "Videos", @"C:\Videos", relativePath, fullPath ?? Path.Combine(@"C:\Videos", relativePath),
            HasSafePath: true, rootAvailable, rootEnabled, present, IsLink: false, quarantined, size,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), duration ?? TimeSpan.FromMinutes(1), width, height,
            "h264", "aac", true, "a".PadRight(64, 'a'), decision);

    public static ReviewGroup Group(Guid first, Guid second, MatchType type = MatchType.Exact, decimal score = 1m,
        decimal? offset = null, params ReviewFileInfo[] files) =>
        ReviewGroup.Create($"{first:D}|{second:D}", type, score, null, null, offset,
            ["SHA-256 content hashes are identical."],
            files.Length > 0 ? files : [File(first, "original.mkv"), File(second, "copy.mkv")],
            first, "Keep the richer encode.");
}
