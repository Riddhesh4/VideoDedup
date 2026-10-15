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
