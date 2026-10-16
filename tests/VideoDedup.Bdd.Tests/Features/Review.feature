Feature: Review and safe quarantine
    As a user
    I want review decisions and reversible quarantine
    So that duplicates are handled deliberately and no file is ever lost

Scenario: Duplicate groups explain themselves
    Given two copies of the same video are indexed and reachable
    When I ask for the duplicate groups
    Then the group explains its match and lists both copies
    And the group suggests which copy to keep without acting on it

Scenario: A copy can be marked keep or reject
    Given two copies of the same video are indexed and reachable
    And I ask for the duplicate groups
    When I mark one copy as keep
    Then the store records the keep decision for that copy

Scenario: Quarantine needs confirmation and can be undone
    Given two copies of the same video are indexed and reachable
    When I quarantine the group without confirming
    Then nothing moves and the plan asks for confirmation
    When I quarantine the group after confirming
    Then the first copy is quarantined and the log records an undo entry
    When I restore the quarantined file
    Then the copy is back at its original path and the log marks it restored

Scenario: The last remaining copy is never quarantined
    Given two copies of the same video are indexed and reachable
    And one copy has already been removed from the drive
    When I plan a quarantine of the remaining copy
    Then the plan blocks the copy because it is the last one

Scenario: Offline copies cannot be acted upon
    Given two copies of the same video are indexed and reachable
    And the drive holding the copies is offline
    When I plan a quarantine of the first copy
    Then the plan blocks the copy because the root is unavailable

Scenario: Pausing workers keeps queued work
    Given probe and match workers are registered
    When the workers are paused
    Then every worker reports the paused state
    When the workers are resumed
    Then every worker reports the idle state
