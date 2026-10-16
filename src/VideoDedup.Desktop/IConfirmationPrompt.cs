using System.Windows;

namespace VideoDedup.Desktop;

/// <summary>
/// Explicit user confirmation for every quarantine action. The abstraction keeps the rule "nothing moves without
/// a confirmation" testable: view-model tests substitute it and prove that declining moves nothing.
/// </summary>
public interface IConfirmationPrompt
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken);
}

/// <summary>Production confirmation dialog. Text is rendered as plain text, never as markup.</summary>
public sealed class MessageBoxConfirmationPrompt : IConfirmationPrompt
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No, MessageBoxOptions.None);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }
}
