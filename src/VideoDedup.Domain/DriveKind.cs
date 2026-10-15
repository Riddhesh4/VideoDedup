namespace VideoDedup.Domain;

/// <summary>Physical or logical storage category, without depending on platform drive APIs.</summary>
public enum DriveKind
{
    Local,
    Usb,
    Network,
}
