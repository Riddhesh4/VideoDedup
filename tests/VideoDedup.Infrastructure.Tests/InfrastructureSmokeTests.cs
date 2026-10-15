using NSubstitute;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public class InfrastructureSmokeTests
{
    [Fact]
    public async Task IProcessRunner_fake_returns_configured_exit_code()
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync("ffmpeg", "-version", Arg.Any<CancellationToken>()).Returns(0);

        var exitCode = await runner.RunAsync("ffmpeg", "-version", TestContext.Current.CancellationToken);
        exitCode.Should().Be(0);
    }
}
