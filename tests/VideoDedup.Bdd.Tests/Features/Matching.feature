Feature: Duplicate matching
    As a user
    I want duplicate decisions to be explainable and conservative
    So that every match cites its fingerprint evidence and borderline cases stay on review

Scenario: Identical content hashes are exact duplicates
    Given two indexed files share the same SHA-256 hash
    When the pair is classified
    Then the decision is an exact duplicate with maximum confidence

Scenario: Re-encoded versions with strong visual and audio evidence are near-duplicate candidates
    Given two versions score 93% on aligned visual frames and 94% on audio words
    When the pair is classified
    Then the decision is a near-duplicate citing both signals

Scenario: A decorated excerpt is a partial clip only when visuals confirm the offset
    Given audio of one file sits at offset 8 seconds inside the other with 95% alignment
    And visual content at that offset confirms with 85% similarity
    When the pair is classified
    Then the decision is a partial clip citing the offset and the confirmation

Scenario: An unconfirmed excerpt stays on review without acting like a match
    Given audio of one file sits at offset 4 seconds inside the other with 95% alignment
    And visual content at the offset does not confirm
    When the pair is classified
    Then the decision is manual review citing the unconfirmed alignment
    And the pair is not classified as a partial clip automatically

Scenario: Fully processed files across two drives are compared
    Given one fully processed file on drive one and a matching copy on drive two
    When the match orchestrator runs
    Then the cross-drive pair is evaluated and persisted
    And files that are not fully processed are skipped
