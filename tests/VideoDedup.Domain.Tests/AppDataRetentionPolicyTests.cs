using FluentAssertions;
using VideoDedup.Domain;

namespace VideoDedup.Domain.Tests;

public sealed class AppDataRetentionPolicyTests
{
    private static string DataRoot => Path.Combine(Path.GetTempPath(), "VideoDedup", "AppDataPolicy");

    private static AppDataEntry Entry(string relative, AppDataCategory category, bool reparse = false, long size = 100, bool directory = false) =>
        new(Path.Combine(DataRoot, relative), category, size, reparse, directory);

    [Fact]
    public void Classify_relative_path_maps_every_owned_area_and_rejects_the_unknown()
    {
        AppDataRetentionPolicy.ClassifyRelativePath("videodedup.db").Should().Be(AppDataCategory.Index);
        AppDataRetentionPolicy.ClassifyRelativePath("videodedup.db-wal").Should().Be(AppDataCategory.Index);
        AppDataRetentionPolicy.ClassifyRelativePath(@"Discovery\files.json").Should().Be(AppDataCategory.DiscoveryIndex);
        AppDataRetentionPolicy.ClassifyRelativePath("logs/processing.jsonl").Should().Be(AppDataCategory.Logs);
        AppDataRetentionPolicy.ClassifyRelativePath(@"Quarantine\2026\clip.mkv").Should().Be(AppDataCategory.QuarantineStore);
        AppDataRetentionPolicy.ClassifyRelativePath("scan-roots.json").Should().Be(AppDataCategory.Configuration);
        AppDataRetentionPolicy.ClassifyRelativePath("notes.txt").Should().Be(AppDataCategory.Unknown);
        AppDataRetentionPolicy.ClassifyRelativePath(@"secret\videodedup.db").Should().Be(AppDataCategory.Unknown);
        Assert.Throws<ArgumentException>(() => AppDataRetentionPolicy.ClassifyRelativePath(" "));
        Assert.Throws<ArgumentException>(() => AppDataRetentionPolicy.ClassifyRelativePath("a\u0000b"));
    }

    [Theory]
    [InlineData("videodedup.db", AppDataCategory.Index)]
    [InlineData("Discovery/files.json", AppDataCategory.DiscoveryIndex)]
    [InlineData("logs/processing.jsonl", AppDataCategory.Logs)]
    public void Index_discovery_and_logs_may_be_deleted_and_are_documented_as_rebuilt(string relative, AppDataCategory category)
    {
        var decision = AppDataRetentionPolicy.Classify(DataRoot, Entry(relative, category));

        decision.Deletable.Should().BeTrue();
        decision.Reason.Should().Be(AppDataRetentionPolicy.RebuiltReason);
    }

    [Fact]
    public void Quarantined_media_is_never_index_data_so_it_survives_a_delete()
    {
        var decision = AppDataRetentionPolicy.Classify(DataRoot, Entry("Quarantine/clip.mkv", AppDataCategory.QuarantineStore));

        decision.Deletable.Should().BeFalse();
        decision.Reason.Should().Be(AppDataRetentionPolicy.QuarantineRetainedReason);
    }

    [Fact]
    public void Scan_root_settings_and_unrecognized_entries_are_retained_with_the_reason_shown_to_the_user()
    {
        AppDataRetentionPolicy.Classify(DataRoot, Entry("scan-roots.json", AppDataCategory.Configuration)).Reason
            .Should().Be(AppDataRetentionPolicy.ConfigurationRetainedReason);
        AppDataRetentionPolicy.Classify(DataRoot, Entry("mystery.bin", AppDataCategory.Unknown)).Reason
            .Should().Be(AppDataRetentionPolicy.UnrecognizedReason);
    }

    [Fact]
    public void Entries_outside_the_data_folder_are_refused_even_when_they_look_like_index_files()
    {
        var outside = new AppDataEntry(Path.Combine(Path.GetTempPath(), "elsewhere", "videodedup.db"), AppDataCategory.Index, 10, false);

        var decision = AppDataRetentionPolicy.Classify(DataRoot, outside);

        decision.Deletable.Should().BeFalse();
        decision.Reason.Should().Be(AppDataRetentionPolicy.OutsideDataRootReason);
    }

    [Fact]
    public void The_data_folder_itself_and_reparse_points_are_refused()
    {
        var root = new AppDataEntry(DataRoot, AppDataCategory.Index, 10, false, true);
        AppDataRetentionPolicy.Classify(DataRoot, root).Reason.Should().Be(AppDataRetentionPolicy.DataRootItselfReason);

        var link = new AppDataEntry(Path.Combine(DataRoot, "logs", "linked.jsonl"), AppDataCategory.Logs, 10, true);
        var decision = AppDataRetentionPolicy.Classify(DataRoot, link);
        decision.Deletable.Should().BeFalse();
        decision.Reason.Should().Be(AppDataRetentionPolicy.ReparsePointReason);
    }

    [Fact]
    public void A_data_folder_that_sits_inside_a_scan_root_is_left_alone()
    {
        var scanRoot = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var decision = AppDataRetentionPolicy.Classify(DataRoot, Entry("logs/processing.jsonl", AppDataCategory.Logs), [scanRoot]);

        decision.Deletable.Should().BeFalse();
        decision.Reason.Should().Be(AppDataRetentionPolicy.InsideScanRootReason);
    }

    [Fact]
    public void IsInside_requires_a_real_folder_boundary_and_absolute_paths()
    {
        var parent = Path.Combine(Path.GetTempPath(), "Vd");
        AppDataRetentionPolicy.IsInside(parent, Path.Combine(parent, "child", "file.txt")).Should().BeTrue();
        AppDataRetentionPolicy.IsInside(parent, parent).Should().BeFalse();
        AppDataRetentionPolicy.IsInside(parent, Path.Combine(Path.GetTempPath(), "VdOther", "file.txt")).Should().BeFalse();
        Assert.Throws<ArgumentException>(() => AppDataRetentionPolicy.IsInside("relative", Path.Combine(parent, "x.txt")));
        Assert.Throws<ArgumentException>(() => AppDataRetentionPolicy.IsInside(parent, "relative.txt"));
    }

    [Fact]
    public void Classify_rejects_handed_in_nonsense_instead_of_guessing()
    {
        Assert.Throws<ArgumentException>(() => AppDataRetentionPolicy.Classify("relative-root", Entry("logs/a.jsonl", AppDataCategory.Logs)));
        Assert.Throws<ArgumentException>(() => AppDataRetentionPolicy.Classify(DataRoot, new AppDataEntry("relative.txt", AppDataCategory.Logs, 1, false)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AppDataRetentionPolicy.Classify(DataRoot, Entry("logs/a.jsonl", AppDataCategory.Logs, size: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AppDataRetentionPolicy.Classify(DataRoot, Entry("logs/a.jsonl", (AppDataCategory)99)));
        Assert.Throws<ArgumentNullException>(() => AppDataRetentionPolicy.Classify(DataRoot, null!));
    }

    [Fact]
    public void ClassifyAll_returns_one_decision_per_entry_in_order()
    {
        AppDataEntry[] entries =
        [
            Entry("videodedup.db", AppDataCategory.Index),
            Entry("Quarantine/keep.mkv", AppDataCategory.QuarantineStore),
            Entry("scan-roots.json", AppDataCategory.Configuration),
        ];

        var decisions = AppDataRetentionPolicy.ClassifyAll(DataRoot, entries);

        decisions.Should().HaveCount(3);
        decisions.Select(decision => decision.Deletable).Should().Equal(true, false, false);
        decisions.Select(decision => decision.FullPath).Should().Equal(entries.Select(entry => entry.FullPath));
    }
}
