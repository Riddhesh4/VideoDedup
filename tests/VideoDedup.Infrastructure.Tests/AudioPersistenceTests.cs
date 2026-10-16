using Microsoft.Data.Sqlite;
using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public sealed class AudioPersistenceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "VideoDedup.AudioStore", Guid.NewGuid().ToString("N"));
    private string Database { get { Directory.CreateDirectory(_temp); return Path.Combine(_temp, "index.db"); } }
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Existing_database_roundtrips_versioned_audio_and_normal_no_audio_skip()
    {
        using var queue = new SqliteJobQueue(Database); await queue.InitializeAsync(Token);
        var repository = new SqliteAudioProcessingRepository(Database);
        var fileId = Guid.NewGuid();
        var revision = new FileRevision(42, DateTimeOffset.UnixEpoch, "file-id");
        var bytes = new byte[] { 1, 2, 255, 0 };
        var record = AudioProcessingRecord.Create(fileId, revision, AudioAlgorithm.Name, AudioAlgorithm.Version,
            TimeSpan.FromSeconds(8), bytes, true);
        bytes[0] = 99;
        await repository.SaveAsync(record, Token);
        var stored = await new SqliteAudioProcessingRepository(Database).GetAsync(fileId, Token);
        stored.Should().NotBeNull();
        stored!.Fingerprint.Should().Equal(new byte[] { 1, 2, 255, 0 });
        stored.IsCurrent(revision).Should().BeTrue();
        stored.IsCurrent(revision with { FileIdentity = "replacement" }).Should().BeFalse();
        stored.IsCurrent(revision).Should().BeTrue();
        AudioProcessingRecord.Create(fileId, revision, AudioAlgorithm.Name, FingerprintVersion.Create(3, 0),
            TimeSpan.FromSeconds(8), new byte[] { 1, 2, 255, 0 }, true).IsCurrent(revision).Should().BeFalse(
            "records must stop being current when the algorithm publishes a newer version");
        var exposed = stored.Fingerprint; exposed[0] = 0;
        stored.Fingerprint.Should().Equal(new byte[] { 1, 2, 255, 0 });

        var skip = AudioProcessingRecord.Create(fileId, revision, AudioAlgorithm.Name, AudioAlgorithm.Version,
            TimeSpan.Zero, [], false);
        await repository.SaveAsync(skip, Token);
        var noAudio = await repository.GetAsync(fileId, Token);
        noAudio!.HasAudio.Should().BeFalse();
        noAudio.Fingerprint.Should().BeEmpty();
        noAudio.Failure.Should().BeNull();
        noAudio.IsCurrent(revision).Should().BeTrue();
    }

    [Fact]
    public async Task Failure_status_is_isolated_and_invalid_values_are_rejected()
    {
        using var queue = new SqliteJobQueue(Database); await queue.InitializeAsync(Token);
        var repository = new SqliteAudioProcessingRepository(Database);
        var fileId = Guid.NewGuid();
        var revision = new FileRevision(20, DateTimeOffset.UnixEpoch);
        var failure = AudioProcessingRecord.Create(fileId, revision, AudioAlgorithm.Name, AudioAlgorithm.Version,
            TimeSpan.Zero, [], false, MediaFailure.InvalidData);
        await repository.SaveAsync(failure, Token);
        (await repository.GetAsync(fileId, Token))!.Failure.Should().Be(MediaFailure.InvalidData);
        foreach (var invalid in new[]
                 {
                     AudioProcessingRecord.Create(Guid.Empty, revision, AudioAlgorithm.Name, AudioAlgorithm.Version, TimeSpan.Zero, [], false),
                     AudioProcessingRecord.Create(Guid.NewGuid(), revision, AudioAlgorithm.Name, AudioAlgorithm.Version, TimeSpan.Zero, [1], false),
                     AudioProcessingRecord.Create(Guid.NewGuid(), revision, AudioAlgorithm.Name, AudioAlgorithm.Version, TimeSpan.Zero, [], true),
                     AudioProcessingRecord.Create(Guid.NewGuid(), revision, AudioAlgorithm.Name, AudioAlgorithm.Version, TimeSpan.FromSeconds(1), [1], false),
                     AudioProcessingRecord.Create(Guid.NewGuid(), revision, AudioAlgorithm.Name, FingerprintVersion.Create(1, 0), TimeSpan.FromSeconds(1), [1], true,
                         (MediaFailure)999),
                     AudioProcessingRecord.Create(Guid.NewGuid(), revision, AudioAlgorithm.Name, AudioAlgorithm.Version, TimeSpan.FromDays(8), [1], true),
                     AudioProcessingRecord.Create(Guid.NewGuid(), revision, AudioAlgorithm.Name, AudioAlgorithm.Version, TimeSpan.FromSeconds(1), new byte[FpcalcAudioFingerprinter.MaxFingerprintBytes + 1], true)
                 })
            await FluentActions.Awaiting(() => repository.SaveAsync(invalid, Token)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Corrupt_persisted_audio_status_is_typed_invalid_data()
    {
        var repository = new SqliteAudioProcessingRepository(Database);
        var fileId = Guid.NewGuid();
        await repository.SaveAsync(AudioProcessingRecord.Create(fileId, new FileRevision(1, DateTimeOffset.UnixEpoch),
            AudioAlgorithm.Name, AudioAlgorithm.Version, TimeSpan.FromSeconds(1), [1, 2], true), Token);
        await using var connection = new SqliteConnection("Data Source=" + Database);
        await connection.OpenAsync(Token);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE audio_results SET DurationTicks=-1 WHERE FileId=@id;";
        command.Parameters.AddWithValue("@id", fileId.ToString("D"));
        await command.ExecuteNonQueryAsync(Token);
        await FluentActions.Awaiting(() => repository.GetAsync(fileId, Token)).Should().ThrowAsync<MediaProcessingException>()
            .Where(exception => exception.Failure == MediaFailure.InvalidData);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
}
