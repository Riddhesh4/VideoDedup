Feature: Solution smoke test
    As a developer
    I want the BDD toolchain to execute end to end
    So that every future scenario shares one proven pipeline

Scenario: Reqnroll executes a scenario with xUnit v3
    Given the BDD runner is wired up
    When a scenario runs
    Then it passes on the first attempt
