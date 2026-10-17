using System.IO;
using System.Text;
using System.Windows;

namespace VideoDedup.Launcher;

/// <summary>
/// Last resort for a failure that happens before the main window exists. It exists because the alternative is what
/// happened in the field: an unhandled <see cref="UnauthorizedAccessException"/> ended the process with a Windows
/// crash report and no window, which is indistinguishable from "the application does not run".
/// </summary>
/// <remarks>
/// Only the exception <em>type</em> is shown, never its message, so a raw path or file name cannot leak through a
/// dialog. The data folder is named because the user needs it to act, and it is their own folder on their own screen.
/// </remarks>
internal static class StartupFailure
{
    private const int StartupFailureExitCode = 2;

    public static void Report(string detail, string dataFolder, Exception? cause)
    {
        var message = new StringBuilder()
            .AppendLine("VideoDedup could not start.")
            .AppendLine()
            .AppendLine(detail)
            .AppendLine()
            .AppendLine($"Data folder: {dataFolder}");
        message.AppendLine(cause is UnauthorizedAccessException or IOException
            ? "Windows denies access to a file left behind in that folder by an earlier version. Nothing was deleted."
            : "Nothing was deleted.");

        message
            .AppendLine()
            .AppendLine("To recover, either give your own account full access to that folder, or start once with a")
            .AppendLine("different data folder:")
            .AppendLine()
            .AppendLine("    VideoDedup.Launcher.exe --data \"%LOCALAPPDATA%\\VideoDedup-profile2\"")
            .AppendLine()
            .AppendLine("The new folder starts with an empty index; your videos are never modified.");

        TryShow(message.ToString());
        Environment.Exit(StartupFailureExitCode);
    }

    private static void TryShow(string message)
    {
        try
        {
            MessageBox.Show(message, "VideoDedup could not start", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No desktop to show a dialog on (for example a headless session). The exit code is all that is left.
        }
    }
}
