using VideoDedup.Application;

namespace VideoDedup.Application.Tests;

public sealed class QueuePolicyTests
{
    [Fact]
    public void Valid_payload_returns_its_version()
    {
        QueuePayloadValidator.Validate("""{"version":1,"file":"clip.mp4"}""").Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-json")]
    [InlineData("[1,2]")]
    [InlineData("{}")]
    [InlineData("""{"version":"1"}""")]
    [InlineData("""{"version":0}""")]
    [InlineData("""{"version":2}""")]
    public void Invalid_payloads_are_rejected(string? payload)
    {
        FluentActions.Invoking(() => QueuePayloadValidator.Validate(payload))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Oversized_payload_is_rejected()
    {
        var payload = "{\"version\":1,\"blob\":\"" + new string('x', QueuePayloadValidator.MaxPayloadChars) + "\"}";
        FluentActions.Invoking(() => QueuePayloadValidator.Validate(payload))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Retry_delay_is_deterministic_bounded_and_grows()
    {
        var jobId = Guid.NewGuid();
        var first = QueueRetryPolicy.ComputeDelay(jobId, 1, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
        var repeat = QueueRetryPolicy.ComputeDelay(jobId, 1, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));
        var second = QueueRetryPolicy.ComputeDelay(jobId, 2, TimeSpan.FromSeconds(30), TimeSpan.FromHours(1));

        repeat.Should().Be(first);
        first.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromHours(1));
        second.Should().BeGreaterThan(first);

        var capped = QueueRetryPolicy.ComputeDelay(jobId, 20, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
        capped.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Retry_delay_differs_per_job_but_stays_within_jitter_bounds()
    {
        var first = QueueRetryPolicy.ComputeDelay(Guid.NewGuid(), 1, TimeSpan.FromSeconds(60), TimeSpan.FromHours(1));
        var second = QueueRetryPolicy.ComputeDelay(Guid.NewGuid(), 1, TimeSpan.FromSeconds(60), TimeSpan.FromHours(1));

        first.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(48));
        first.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(72));
        second.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(48));
        second.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(72));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Retry_rejects_non_positive_attempts(int attempt)
    {
        FluentActions.Invoking(() => QueueRetryPolicy.ComputeDelay(Guid.NewGuid(), attempt, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Claim_request_validation_rejects_bad_workers_limits_and_leases()
    {
        FluentActions.Invoking(() => new QueueClaimRequest("", TimeSpan.FromMinutes(5)).Validate())
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new QueueClaimRequest("w", TimeSpan.Zero).Validate())
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new QueueClaimRequest("w", TimeSpan.FromMinutes(5), MaxJobs: 0).Validate())
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new QueueClaimRequest("w", TimeSpan.FromMinutes(5), MaxPerDrive: 0).Validate())
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Enqueue_options_validation_rejects_bad_attempts_and_drive_ids()
    {
        FluentActions.Invoking(() => new EnqueueOptions(MaxAttempts: 0).Validate())
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new EnqueueOptions(DriveId: "   ").Validate())
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new EnqueueOptions(DriveId: "a\0b").Validate())
            .Should().Throw<ArgumentException>();
    }
}
