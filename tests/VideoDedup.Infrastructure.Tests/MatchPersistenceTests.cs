using Microsoft.Data.Sqlite;
using VideoDedup.Application;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;
using MatchType = VideoDedup.Domain.MatchType;

namespace VideoDedup.Infrastructure.Tests;

public sealed class MatchPersistenceTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "VideoDedup.MatchStore", Guid.NewGuid().ToString("N"));
    private string Database { get { Directory.CreateDirectory(_temp); return Path.Combine(_temp, "index.db"); } }
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Guid IdA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid IdB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Decision_rows_roundtrip_all_versioned_components()
    {
        var repository = new SqliteMatchResultRepository(Database);
        var original = MatchPersistenceRecord.Create(IdA, IdB, MatchType.PartialClip, 0.925m,
            new EvidenceVersions(VisualAlgorithm.Version, AudioAlgorithm.Version), MatchPolicyOptions.PolicyVersion,
            ["Sliding-window audio alignment matched 100% at offset 8.5 s."], 8.5m);
        await repository.SaveAsync(original, Token);

        // Repos are stateless across open handles; a new instance simulates the next process start.
        var retrieved = (await new SqliteMatchResultRepository(Database).GetAllAsync(Token)).Should().ContainSingle().Subject;
        retrieved.MatchType.Should().Be(MatchType.PartialClip);
        retrieved.OverallScore.Should().Be(0.925m);
        retrieved.AudioOffsetSeconds.Should().Be(8.5m);
        retrieved.PolicyVersion.Should().Be(MatchPolicyOptions.PolicyVersion);
        retrieved.EvidenceVersions.VisualVersion.Should().Be(VisualAlgorithm.Version);
        retrieved.EvidenceVersions.AudioVersion.Should().Be(AudioAlgorithm.Version);
        retrieved.Reasons.Should().Equal(original.Reasons);
        retrieved.FileIdA.Should().Be(original.FileIdA);
        retrieved.FileIdB.Should().Be(original.FileIdB);
    }

    [Fact]
    public async Task Upsert_replaces_a_pair_decision_and_delete_retracts_it()
    {
        var repository = new SqliteMatchResultRepository(Database);
        await repository.SaveAsync(MatchPersistenceRecord.Create(IdA, IdB, MatchType.NearDuplicate, 0.74m,
            new EvidenceVersions(null, null), MatchPolicyOptions.PolicyVersion, ["Audio fingerprint similarity: 74%."]), Token);
        await repository.SaveAsync(MatchPersistenceRecord.Create(IdA, IdB, MatchType.PartialClip, 0.86m,
            new EvidenceVersions(null, null), MatchPolicyOptions.PolicyVersion, ["Strong alignment."], 4m), Token);

        var rows = await repository.GetAllAsync(Token);
        rows.Should().ContainSingle().Which.MatchType.Should().Be(MatchType.PartialClip);

        (await repository.DeleteAsync(IdA, IdB, Token)).Should().BeTrue();
        (await repository.GetAllAsync(Token)).Should().BeEmpty();
    }

    [Fact]
    public async Task Corrupt_rows_surface_typed_invalid_data()
    {
        var repository = new SqliteMatchResultRepository(Database);
        await repository.SaveAsync(MatchPersistenceRecord.Create(IdA, IdB, MatchType.NearDuplicate, 0.9m,
            new EvidenceVersions(null, null), MatchPolicyOptions.PolicyVersion, ["Visual fingerprint similarity: 90%."]), Token);
        await using var connection = new SqliteConnection("Data Source=" + Database);
        await connection.OpenAsync(Token);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE match_results SET Score='nonsense' WHERE FileIdA=@a;";
        command.Parameters.AddWithValue("@a", IdA.ToString("D"));
        await command.ExecuteNonQueryAsync(Token);

        await FluentActions.Awaiting(() => new SqliteMatchResultRepository(Database).GetAllAsync(Token))
            .Should().ThrowAsync<MediaProcessingException>()
            .Where(exception => exception.Failure == MediaFailure.InvalidData);
    }

    [Fact]
    public void Noncanonical_or_unexplainable_records_are_rejected()
    {
        FluentActions.Invoking(() => MatchPersistenceRecord.Create(IdB, IdA, MatchType.Exact, 1m,
                new EvidenceVersions(null, null), MatchPolicyOptions.PolicyVersion, ["SHA-256 content hashes are identical."]))
            .Should().NotThrow("canonical ordering normalizes inverted pairs");
        FluentActions.Invoking(() => MatchPersistenceRecord.Create(IdA, IdB, MatchType.Exact, 1.2m,
                new EvidenceVersions(null, null), MatchPolicyOptions.PolicyVersion, ["score"]))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => MatchPersistenceRecord.Create(IdA, IdB, MatchType.Exact, 1m,
                new EvidenceVersions(null, null), MatchPolicyOptions.PolicyVersion, Array.Empty<string>()))
            .Should().Throw<ArgumentException>();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_temp)) Directory.Delete(_temp, true);
    }
}
