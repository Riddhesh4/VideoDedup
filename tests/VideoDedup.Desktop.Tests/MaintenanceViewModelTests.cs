using FluentAssertions;
using VideoDedup.Application;
using VideoDedup.Desktop.ViewModels;
using VideoDedup.Domain;

namespace VideoDedup.Desktop.Tests;

public sealed class MaintenanceViewModelTests
{
    private static (MaintenanceViewModel ViewModel, FakeMaintenanceService Service, FakeConfirmationPrompt Prompt) Build()
    {
        var service = new FakeMaintenanceService();
        var prompt = new FakeConfirmationPrompt();
        return (new MaintenanceViewModel(service, prompt), service, prompt);
    }

    [Fact]
    public void The_notice_states_what_stays_local_and_that_media_is_never_deleted()
    {
        MaintenanceViewModel.NoticeText.Should().Contain("stays on this PC");
        MaintenanceViewModel.NoticeText.Should().Contain("nothing is uploaded");
        MaintenanceViewModel.NoticeText.Should().Contain("no account");
        MaintenanceViewModel.NoticeText.Should().Contain("no source file is ever deleted");
        MaintenanceViewModel.NoticeText.Should().Contain("Quarantined copies");
        MaintenanceViewModel.DeleteConfirmation.Should().Contain("never deleted");
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2.0 KiB")]
    [InlineData(5 * 1024 * 1024, "5.0 MiB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.0 GiB")]
    public void Sizes_are_shown_in_units_a_person_can_read(long bytes, string expected) =>
        MaintenanceViewModel.FormatSize(bytes).Should().Be(expected);

    [Fact]
    public async Task Refresh_lists_every_area_with_its_size_and_what_deleting_the_index_would_do()
    {
        var (viewModel, service, _) = Build();
        service.Report = new MaintenanceReport(@"C:\Users\bob\AppData\Local\VideoDedup",
            [
                new AppDataAreaUsage(AppDataArea.IndexDatabase, 2048, 1, 2048, 1),
                new AppDataAreaUsage(AppDataArea.QuarantineStore, 4096, 1, 0, 0),
                new AppDataAreaUsage(AppDataArea.Configuration, 20, 1, 0, 0),
            ],
            6164, 2048, 1, 1, 4096, ["Quarantined copies of your media are kept."]);

        await viewModel.RefreshAsync(CancellationToken.None);

        viewModel.Areas.Should().HaveCount(3);
        viewModel.Areas[0].Area.Should().Be("Processing index (database)");
        viewModel.Areas[0].Size.Should().Be("2.0 KiB");
        viewModel.Areas[0].Detail.Should().Contain("deleting the index removes");
        viewModel.Areas[1].Detail.Should().Contain("kept when the index is deleted");
        viewModel.DataRootText.Should().Contain("Application data folder:");
        viewModel.RetentionText.Should().Be("Quarantined copies of your media are kept.");
        viewModel.StatusMessage.Should().Contain("2.0 KiB");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Refresh_says_so_when_there_is_nothing_to_delete_yet()
    {
        var (viewModel, service, _) = Build();
        service.Report = new MaintenanceReport(@"C:\data", [], 0, 0, 0, 0, 0, []);

        await viewModel.RefreshAsync(CancellationToken.None);

        viewModel.Areas.Should().BeEmpty();
        viewModel.RetentionText.Should().BeEmpty();
        viewModel.StatusMessage.Should().Contain("nothing to delete yet");
    }

    [Fact]
    public async Task Export_passes_the_users_path_and_redaction_choice_through_and_reports_the_result()
    {
        var (viewModel, service, _) = Build();
        viewModel.ExportPath = @"C:\exports\index.json";
        viewModel.IncludePaths = false;

        await viewModel.ExportAsync();

        service.Exports.Should().ContainSingle();
        service.Exports[0].DestinationPath.Should().Be(@"C:\exports\index.json");
        service.Exports[0].IncludePaths.Should().BeFalse();
        viewModel.StatusMessage.Should().Contain("File names and paths were left out");
        viewModel.StatusMessage.Should().Contain("C:\\exports\\index.json");
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Export_without_a_path_asks_for_one_and_calls_nothing()
    {
        var (viewModel, service, _) = Build();
        viewModel.ExportPath = "   ";

        await viewModel.ExportAsync();

        service.Exports.Should().BeEmpty();
        viewModel.StatusMessage.Should().Contain("Enter an export file path");
    }

    [Fact]
    public void Paths_are_included_by_default_because_the_index_is_the_users_own_data()
    {
        var (viewModel, _, _) = Build();
        viewModel.IncludePaths.Should().BeTrue();
        viewModel.ExportPath.Should().EndWith("videodedup-index.json");
    }

    [Fact]
    public async Task Deleting_the_index_needs_the_confirmation_and_does_nothing_when_it_is_declined()
    {
        var (viewModel, service, prompt) = Build();
        prompt.Answer = false;

        await viewModel.DeleteIndexAsync();

        prompt.Messages.Should().ContainSingle();
        prompt.Messages[0].Should().Contain("never deleted");
        service.Confirmations.Should().BeEmpty();
        viewModel.StatusMessage.Should().Contain("canceled");
    }

    [Fact]
    public async Task Deleting_the_index_reports_what_was_removed_and_what_was_kept_and_refreshes_the_report()
    {
        var (viewModel, service, _) = Build();
        service.Deletion = new IndexDeletionResult(true, [@"C:\data\videodedup.db"],
            [new AppDataDecision(@"C:\data\Quarantine\x.mkv", AppDataCategory.QuarantineStore, false, "kept")], []);

        await viewModel.DeleteIndexAsync();

        service.Confirmations.Should().Equal(true);
        viewModel.StatusMessage.Should().Contain("Deleted 1 file(s)").And.Contain("kept 1 entry(ies)");
        viewModel.ErrorMessage.Should().BeNull();
        service.ReportCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_partial_delete_reports_the_files_that_could_not_be_removed()
    {
        var (viewModel, service, _) = Build();
        service.Deletion = new IndexDeletionResult(true, [], [], ["<path>: access denied"]);

        await viewModel.DeleteIndexAsync();

        viewModel.StatusMessage.Should().Contain("could not be removed");
        viewModel.ErrorMessage.Should().Contain("<path>");
    }

    [Fact]
    public async Task A_refused_delete_surfaces_the_reason_and_never_claims_success()
    {
        var (viewModel, service, _) = Build();
        service.Failure = new InvalidOperationException("Work is still queued or in progress.");

        await FluentActions.Awaiting(() => viewModel.DeleteIndexAsync()).Should().ThrowAsync<InvalidOperationException>();

        viewModel.StatusMessage.Should().NotContain("Deleted");
        viewModel.ReportExternalFailure(new TimeoutException());
        viewModel.ErrorMessage.Should().Contain(nameof(TimeoutException));
    }
}
