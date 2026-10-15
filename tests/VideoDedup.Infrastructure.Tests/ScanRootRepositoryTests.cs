using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class ScanRootRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VideoDedup.Roots.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Manifest_round_trips_drive_filters_and_enable_state()
    {
        var repository = new DirectoryScanRootRepository(_directory);
        var drive = DriveDetails.Create("usb-volume", DriveKind.Usb, "Movies", 1000);
        var root = ScanRoot.Create(Guid.NewGuid(), "USB Movies", "E:/", ScanRootType.UsbDrive,
            enabled: false, includedPatterns: new[] { "*.mp4" }, excludedPatterns: new[] { "cache/**" }, driveDetails: drive);
        await repository.SaveAsync(root, TestContext.Current.CancellationToken);

        var reopened = new DirectoryScanRootRepository(_directory);
        var restored = (await reopened.GetAllAsync(TestContext.Current.CancellationToken)).Should().ContainSingle().Subject;
        restored.Id.Should().Be(root.Id);
        restored.Label.Should().Be(root.Label);
        restored.Path.Should().Be(root.Path);
        restored.RootType.Should().Be(root.RootType);
        restored.Enabled.Should().Be(root.Enabled);
        restored.IncludeSubfolders.Should().Be(root.IncludeSubfolders);
        restored.ExcludedPatterns.Should().Equal(root.ExcludedPatterns);
        restored.IncludedPatterns.Should().Equal(root.IncludedPatterns);
        restored.DriveDetails.Should().Be(drive);
        restored.IncludedPatterns.Should().ContainSingle().Which.Should().Be("*.mp4");
        Directory.GetFiles(_directory, "*.tmp").Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
