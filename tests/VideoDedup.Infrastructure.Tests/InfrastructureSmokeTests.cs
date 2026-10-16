using NSubstitute;
using VideoDedup.Infrastructure;

namespace VideoDedup.Infrastructure.Tests;

public class InfrastructureSmokeTests
{
    [Fact]
    public async Task IProcessRunner_fake_returns_configured_exit_code()
    {
        var runner = Substitute.For<IProcessRunner>();
        var request = new ProcessRequest("ffmpeg", ["-version"], TimeSpan.FromSeconds(5));
        runner.RunAsync(request, Arg.Any<CancellationToken>()).Returns(new ProcessResult(0, "", ""));

        var exitCode = await runner.RunAsync(request, TestContext.Current.CancellationToken);
        exitCode.ExitCode.Should().Be(0);
    }
}
