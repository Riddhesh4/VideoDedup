using System.Reflection;
using VideoDedup.Application;
using VideoDedup.Desktop;
using VideoDedup.Desktop.ViewModels;
using VideoDedup.Domain;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Desktop.Tests;

public sealed class ReviewViewModelTests
{
    private static readonly Guid IdA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid IdB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private static (ReviewViewModel ViewModel, FakeReviewService Review, FakeQuarantineService Quarantine,
        FakeConfirmationPrompt Prompt, FakePreviewSource Previews) Build(bool withPreviews = true)
    {
        var review = new FakeReviewService();
        var quarantine = new FakeQuarantineService();
        var prompt = new FakeConfirmationPrompt();
        var previews = new FakePreviewSource();
        var viewModel = new ReviewViewModel(review, quarantine, prompt, withPreviews ? previews : null);
        return (viewModel, review, quarantine, prompt, previews);
    }

    private static QuarantinePlanItem Allowed(Guid fileId, string destination = @"D:\q\a.mkv", bool crossVolume = false) =>
        new(fileId, @"C:\Videos\a.mkv", "a.mkv", true, null, ["Two other copies are available."], destination, crossVolume);

    private static QuarantinePlanItem Blocked(Guid fileId) =>
        new(fileId, @"C:\Videos\b.mkv", "b.mkv", false, QuarantineBlockReason.LastRemainingCopy,
            ["This is the last copy."], null, false);

    [Fact]
    public async Task Refreshing_selects_the_first_group_and_reports_the_group_count()
    {
        var (viewModel, review, _, _, _) = Build();
        review.Groups = [ReviewBuilders.Group(IdA, IdB), ReviewBuilders.Group(IdB, Guid.NewGuid(), MatchType.NearDuplicate, 0.8m)];

        await viewModel.RefreshAsync(Token);

        viewModel.Groups.Should().HaveCount(2);
        viewModel.SelectedGroup.Should().BeSameAs(viewModel.Groups[0]);
        viewModel.SelectedFile.Should().BeSameAs(viewModel.Groups[0].Files[0]);
        viewModel.StatusMessage.Should().Contain("2 duplicate group(s)");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task A_group_with_no_groups_explains_what_to_do_next()
    {
        var (viewModel, _, _, _, _) = Build();

        await viewModel.RefreshAsync(Token);

        viewModel.Groups.Should().BeEmpty();
        viewModel.SelectedGroup.Should().BeNull();
        viewModel.StatusMessage.Should().Contain("No duplicate groups yet");
        viewModel.KeepCommand.CanExecute(null).Should().BeFalse();
        viewModel.QuarantineCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Decision_and_quarantine_commands_enable_only_with_a_selected_actionable_copy()
    {
        var (viewModel, review, _, _, _) = Build();
        review.Groups = [ReviewBuilders.Group(IdA, IdB)];

        await viewModel.RefreshAsync(Token);

        viewModel.KeepCommand.CanExecute(null).Should().BeTrue();
        viewModel.RejectCommand.CanExecute(null).Should().BeTrue();
        viewModel.NeedsReviewCommand.CanExecute(null).Should().BeTrue();
        viewModel.QuarantineCommand.CanExecute(null).Should().BeTrue();
        viewModel.PreviewCommand.CanExecute(null).Should().BeTrue();

        viewModel.SelectedFile = null;
        viewModel.KeepCommand.CanExecute(null).Should().BeFalse();

        viewModel.SelectedGroup = null;
        viewModel.QuarantineCommand.CanExecute(null).Should().BeFalse();
        viewModel.PreviewCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Offline_or_quarantined_copies_cannot_be_quarantined_again()
    {
        var (viewModel, review, _, _, _) = Build();
        review.Groups = [ReviewBuilders.Group(IdA, IdB, MatchType.Exact, 1m, null,
            ReviewBuilders.File(IdA, "original.mkv", rootAvailable: false),
            ReviewBuilders.File(IdB, "copy.mkv", present: false, quarantined: true))];

        await viewModel.RefreshAsync(Token);

        viewModel.CanQuarantineGroup.Should().BeFalse();
        viewModel.QuarantineCommand.CanExecute(null).Should().BeFalse();

        await viewModel.QuarantineAsync(Token);

        viewModel.StatusMessage.Should().Contain("Nothing in this group can be quarantined");
    }

    [Fact]
    public async Task Keeping_a_copy_records_the_decision_and_the_group_is_reloaded()
    {
        var (viewModel, review, _, _, _) = Build();
        review.Groups = [ReviewBuilders.Group(IdA, IdB)];
        await viewModel.RefreshAsync(Token);
        viewModel.SelectedFile = viewModel.SelectedGroup!.Files[0];

        await viewModel.SetDecisionAsync(ReviewDecision.Keep, Token);

        review.Decisions.Should().ContainSingle().Which.Should().Be((viewModel.SelectedGroup.Key, IdA, ReviewDecision.Keep));
        viewModel.StatusMessage.Should().Contain("Keep recorded");
        viewModel.SelectedGroup!.Files[0].Decision.Should().Be(ReviewDecision.Keep);
    }

    [Fact]
    public async Task Declining_the_confirmation_moves_nothing_and_says_so()
    {
        var (viewModel, review, quarantine, prompt, _) = Build();
        review.Groups = [ReviewBuilders.Group(IdA, IdB)];
        await viewModel.RefreshAsync(Token);
        quarantine.Plan = new QuarantinePlan([Allowed(IdA)]);
        prompt.Answer = false;

        await viewModel.QuarantineAsync(Token);

        prompt.Messages.Should().ContainSingle();
        quarantine.ConfirmedFlags.Should().BeEmpty();
        viewModel.StatusMessage.Should().Contain("canceled; nothing was moved");
    }

    [Fact]
    public async Task Confirming_executes_the_plan_and_reports_every_outcome()
    {
        var (viewModel, review, quarantine, _, _) = Build();
        review.Groups = [ReviewBuilders.Group(IdA, IdB)];
        await viewModel.RefreshAsync(Token);
        var recordId = Guid.NewGuid();
        quarantine.Plan = new QuarantinePlan([Allowed(IdA), Allowed(IdB, @"D:\q\b.mkv", crossVolume: true)]);
        quarantine.Result = new QuarantineBatchResult(false,
            [QuarantineOutcome.Moved(IdA, recordId, true), QuarantineOutcome.Blocked(IdB, "The destination already exists.")]);

        await viewModel.QuarantineAsync(Token);

        quarantine.PlannedSelections.Should().ContainSingle();
        quarantine.ConfirmedFlags.Should().Equal(true);
        viewModel.StatusMessage.Should().Contain("1 file(s) moved");
        viewModel.StatusMessage.Should().Contain("1 could not be quarantined");
        viewModel.StatusMessage.Should().Contain("copied across drives");
    }

    [Fact]
    public async Task A_fully_blocked_plan_never_asks_for_confirmation()
    {
        var (viewModel, review, quarantine, prompt, _) = Build();
        review.Groups = [ReviewBuilders.Group(IdA, IdB)];
        await viewModel.RefreshAsync(Token);
        quarantine.Plan = new QuarantinePlan([Blocked(IdA)]);

        await viewModel.QuarantineAsync(Token);

        prompt.Messages.Should().BeEmpty();
        quarantine.ConfirmedFlags.Should().BeEmpty();
        viewModel.StatusMessage.Should().Contain("last copy");
    }

    [Fact]
    public void Confirmation_text_names_destinations_blocked_copies_and_the_drive_warning()
    {
        var plan = new QuarantinePlan([Allowed(IdA, @"D:\q\a.mkv", crossVolume: true), Blocked(IdB)]);

        var text = ReviewViewModel.BuildConfirmationText(plan);

        text.Should().Contain(@"D:\q\a.mkv");
        text.Should().Contain("never deleted");
        text.Should().Contain("different drive");
        text.Should().Contain("Not quarantined:");
        text.Should().Contain("last copy");
        text.Should().Contain("Quarantine these files?");
    }

    [Fact]
    public async Task Undo_requires_confirmation_and_never_moves_without_it()
    {
        var (viewModel, _, quarantine, prompt, _) = Build();
        var record = QuarantineRecord.Create(Guid.NewGuid(), IdA, @"C:\Videos\a.mkv", @"D:\q\a.mkv",
            QuarantineState.Quarantined, false, DateTimeOffset.UtcNow);
        quarantine.History.Add(record);
        await viewModel.RefreshAsync(Token);

        viewModel.UndoLastCommand.CanExecute(null).Should().BeTrue();
        prompt.Answer = false;
        await viewModel.UndoLastAsync(Token);
        quarantine.UndoneRecords.Should().BeEmpty();
        viewModel.StatusMessage.Should().Contain("Restore canceled");

        prompt.Answer = true;
        await viewModel.UndoLastAsync(Token);
        quarantine.UndoneRecords.Should().ContainSingle().Which.Should().Equal(record.RecordId);
        viewModel.StatusMessage.Should().Contain("Restored a.mkv");
    }

    [Fact]
    public async Task Undo_is_disabled_without_a_quarantined_entry()
    {
        var (viewModel, _, quarantine, _, _) = Build();
        quarantine.History.Add(QuarantineRecord.Create(Guid.NewGuid(), IdA, @"C:\Videos\a.mkv", @"D:\q\a.mkv",
            QuarantineState.Restored, false, DateTimeOffset.UtcNow));

        await viewModel.RefreshAsync(Token);

        viewModel.UndoLastCommand.CanExecute(null).Should().BeFalse();
        await viewModel.UndoLastAsync(Token);
        viewModel.StatusMessage.Should().Contain("nothing to restore");
    }

    [Fact]
    public async Task Previews_capture_both_copies_at_the_same_content_moment()
    {
        var (viewModel, review, _, _, previews) = Build();
        review.Groups = [ReviewBuilders.Group(IdA, IdB, MatchType.PartialClip, 0.9m, 8m,
            ReviewBuilders.File(IdA, "container.mkv", duration: TimeSpan.FromMinutes(5)),
            ReviewBuilders.File(IdB, "excerpt.mkv", duration: TimeSpan.FromSeconds(10)))];
        await viewModel.RefreshAsync(Token);
        viewModel.PreviewPositionSeconds = 2;

        await viewModel.LoadPreviewAsync(Token);

        previews.Captures.Should().HaveCount(2);
        previews.Captures[0].Path.Should().EndWith("container.mkv");
        previews.Captures[0].Timestamp.Should().Be(TimeSpan.FromSeconds(10), "the container frame sits at the offset plus the excerpt position");
        previews.Captures[1].Path.Should().EndWith("excerpt.mkv");
        previews.Captures[1].Timestamp.Should().Be(TimeSpan.FromSeconds(2));
        viewModel.LeftPreviewBytes.Should().NotBeNull();
        viewModel.RightPreviewBytes.Should().NotBeNull();
        viewModel.LeftPreviewLabel.Should().Contain("00:10.0");
        viewModel.RightPreviewLabel.Should().Contain("00:02.0");
        viewModel.StatusMessage.Should().Contain("offset");
    }

    [Fact]
    public async Task Preview_labels_explain_when_a_frame_is_unavailable()
    {
        var (viewModel, review, _, _, previews) = Build();
        review.Groups = [ReviewBuilders.Group(IdA, IdB)];
        await viewModel.RefreshAsync(Token);
        previews.Available = false;

        await viewModel.LoadPreviewAsync(Token);

        viewModel.LeftPreviewBytes.Should().BeNull();
        viewModel.LeftPreviewLabel.Should().Contain("No frame available");
        viewModel.RightPreviewLabel.Should().Contain("No frame available");
    }

    [Fact]
    public async Task Previews_are_not_offered_without_a_preview_source()
    {
        var (viewModel, review, _, _, _) = Build(withPreviews: false);
        review.Groups = [ReviewBuilders.Group(IdA, IdB)];
        await viewModel.RefreshAsync(Token);

        viewModel.PreviewCommand.CanExecute(null).Should().BeFalse();
        viewModel.PreviewHint.Should().Contain("unavailable");

        await viewModel.LoadPreviewAsync(Token);
        viewModel.LeftPreviewBytes.Should().BeNull();
    }

    [Fact]
    public async Task Service_failures_are_surfaced_as_text_and_never_thrown()
    {
        var (viewModel, review, _, _, _) = Build();
        review.Failure = new MediaProcessingException(MediaFailure.InvalidData);

        await viewModel.RefreshAsync(Token);

        viewModel.ErrorMessage.Should().Contain("MediaProcessingException");
        viewModel.Groups.Should().BeEmpty();

        review.Failure = null;
        review.Groups = [ReviewBuilders.Group(IdA, IdB)];
        await viewModel.RefreshAsync(Token);
        await viewModel.SetDecisionAsync(ReviewDecision.Reject, Token);
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.StatusMessage.Should().Contain("Reject recorded");
    }

    [Fact]
    public async Task The_undecided_filter_hides_decided_groups_and_keeps_the_selection()
    {
        var (viewModel, review, _, _, _) = Build();
        review.Groups =
        [
            ReviewBuilders.Group(IdA, IdB),
            ReviewBuilders.Group(IdB, Guid.NewGuid(), MatchType.NearDuplicate, 0.8m, null,
                ReviewBuilders.File(IdB, "b.mkv", decision: ReviewDecision.Keep),
                ReviewBuilders.File(Guid.NewGuid(), "c.mkv", decision: ReviewDecision.Reject)),
        ];
        await viewModel.RefreshAsync(Token);
        viewModel.SelectedGroup = viewModel.Groups[0];

        viewModel.ShowOnlyUndecided = true;

        viewModel.Groups.Should().ContainSingle();
        viewModel.SelectedGroup!.Key.Should().Be(viewModel.Groups[0].Key);
    }

    [Fact]
    public async Task Displayed_texts_carry_no_control_characters_from_hostile_names()
    {
        var (viewModel, review, quarantine, _, _) = Build();
        var hostile = "evil\u001b\u202E[31m.mkv";
        review.Groups = [ReviewBuilders.Group(IdA, IdB, MatchType.Exact, 1m, null,
            ReviewBuilders.File(IdA, hostile, fullPath: @"C:\Videos\" + hostile),
            ReviewBuilders.File(IdB, "copy.mkv"))];
        await viewModel.RefreshAsync(Token);
        viewModel.SelectedFile = viewModel.SelectedGroup!.Files[0];
        // The durable log refuses control characters in stored paths, so the history row uses a benign path;
        // the hostile name reaches the UI through the review group instead.
        quarantine.History.Add(QuarantineRecord.Create(Guid.NewGuid(), IdA, @"C:\Videos\old.mkv", @"D:\q\x.mkv",
            QuarantineState.Quarantined, false, DateTimeOffset.UtcNow));
        await viewModel.RefreshAsync(Token);

        viewModel.SelectedFileSummary.Should().NotContain("\u001b");
        viewModel.SelectedFileSummary.Should().NotContain("\u202E");
        viewModel.SelectedFileSummary.Should().Contain("evil");
        viewModel.StatusMessage.Should().NotContain("\u001b");
    }

    [Fact]
    public async Task Startup_recovery_reports_interrupted_entries()
    {
        var (viewModel, review, quarantine, _, _) = Build();
        review.Groups = [ReviewBuilders.Group(IdA, IdB)];
        var resolved = QuarantineRecord.Create(Guid.NewGuid(), IdA, @"C:\Videos\a.mkv", @"D:\q\a.mkv",
            QuarantineState.Failed, false, DateTimeOffset.UtcNow);
        quarantine.Recovery = new QuarantineRecoveryReport([resolved], Array.Empty<QuarantineRecord>());

        await viewModel.RecoverThenRefreshAsync(Token);

        viewModel.StatusMessage.Should().Contain("Quarantine recovery");
        viewModel.Groups.Should().HaveCount(1);
    }

    [Fact]
    public void The_review_view_model_offers_no_deletion_path()
    {
        var members = typeof(ReviewViewModel).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(method => method.Name)
            .Concat(typeof(ReviewViewModel).GetProperties().Select(property => property.Name))
            .ToArray();

        members.Should().NotContain(name => name.Contains("Delete", StringComparison.OrdinalIgnoreCase));
        members.Should().NotContain(name => name.Contains("Permanent", StringComparison.OrdinalIgnoreCase));
    }
}
