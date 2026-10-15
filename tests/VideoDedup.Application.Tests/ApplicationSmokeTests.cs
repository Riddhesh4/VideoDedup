using NSubstitute;
using VideoDedup.Application;
using VideoDedup.Domain;

namespace VideoDedup.Application.Tests;

public class ApplicationSmokeTests
{
    [Fact]
    public async Task IScanOrchestrator_is_mockable_with_NSubstitute()
    {
        var orchestrator = Substitute.For<IScanOrchestrator>();
        var root = ScanRoot.Create(Guid.NewGuid(), "Movies", "C:/Movies", ScanRootType.LocalFolder);
        var expected = new DiscoveryResult(root.Id, DiscoveryOutcome.Completed, 0, 0, 0, 0, 0, 0, Array.Empty<string>());
        orchestrator.ScanAsync(root, Arg.Any<CancellationToken>()).Returns(expected);

        var actual = await orchestrator.ScanAsync(root, CancellationToken.None);

        actual.Should().Be(expected);
    }
}
