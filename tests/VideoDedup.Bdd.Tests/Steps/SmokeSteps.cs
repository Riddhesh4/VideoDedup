using Reqnroll;

namespace VideoDedup.Bdd.Tests;

[Binding]
public sealed class SmokeSteps
{
    private bool _ran;

    [Given("the BDD runner is wired up")]
    public void GivenRunnerWiredUp() => _ran = false;

    [When("a scenario runs")]
    public void WhenScenarioRuns() => _ran = true;

    [Then("it passes on the first attempt")]
    public void ThenScenarioPasses() => _ran.Should().BeTrue();
}
