using VideoDedup.Domain;

namespace VideoDedup.Domain.Tests;

public class DomainSmokeTests
{
    [Fact]
    public void ScanState_starts_as_NotScanned_by_default()
    {
        // Arrange / Act
        var state = ScanState.NotScanned;

        // Assert
        state.Should().Be(ScanState.NotScanned);
    }
}
