Feature: File discovery
    As a user
    I want scans to safely index supported videos
    So that rescans avoid duplicate work and preserve records for offline drives

Scenario: Initial scan indexes supported videos and ignores other files
    Given a local root contains a supported video and a non-video file
    When I scan the configured root
    Then the video is recorded once and the non-video file is skipped

Scenario: An unavailable root retains its prior index
    Given a local root has already indexed a video
    When the root becomes unavailable and I scan it again
    Then the scan reports the root unavailable and retains the indexed video

Scenario: Re-encoded visuals are candidates only with a usable multi-frame sequence
    Given two versions of a video have similar fingerprints across three frames
    When visual candidate evidence is evaluated
    Then the versions are only a near-match candidate and one frame is insufficient
    And unrelated visual fingerprints do not become a candidate

Scenario: A video without audio remains complete for visual matching
    Given a video file has no audio stream
    When audio processing completes
    Then audio is recorded as not present without a worker failure
    And visual candidate processing remains enabled
