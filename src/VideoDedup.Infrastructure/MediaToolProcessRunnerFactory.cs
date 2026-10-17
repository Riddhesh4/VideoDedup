namespace VideoDedup.Infrastructure;

/// <summary>Creates the shared runner with every bundled media executable explicitly allow-listed.</summary>
public static class MediaToolProcessRunnerFactory
{
    public static ProcessRunner Create(string ffprobePath, string ffmpegPath, string fpcalcPath) =>
        new([ffprobePath, ffmpegPath, fpcalcPath]);
}
