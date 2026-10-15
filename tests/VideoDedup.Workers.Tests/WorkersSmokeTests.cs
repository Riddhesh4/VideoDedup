using VideoDedup.Workers;

namespace VideoDedup.Workers.Tests;

public class WorkersSmokeTests
{
    [Fact]
    public void Worker_heartbeat_defaults_to_not_alive()
    {
        IWorkerHeartbeat heartbeat = new NeverAliveHeartbeat();

        heartbeat.IsAlive.Should().BeFalse();
    }

    private sealed class NeverAliveHeartbeat : IWorkerHeartbeat
    {
        public bool IsAlive => false;
    }
}
