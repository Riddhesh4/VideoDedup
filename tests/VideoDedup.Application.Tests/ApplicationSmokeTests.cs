using NSubstitute;
using VideoDedup.Application;

namespace VideoDedup.Application.Tests;

public class ApplicationSmokeTests
{
    [Fact]
    public void IScanOrchestrator_is_mockable_with_NSubstitute()
    {
        var orchestrator = Substitute.For<IScanOrchestrator>();
        orchestrator.ScanRootCount.Returns(2);

        orchestrator.ScanRootCount.Should().Be(2);
    }
}
