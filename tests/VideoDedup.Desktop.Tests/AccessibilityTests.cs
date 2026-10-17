using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Xml.Linq;
using VideoDedup.Desktop;
using VideoDedup.Desktop.ViewModels;

namespace VideoDedup.Desktop.Tests;

public sealed class AccessibilityTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VideoDedup.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Solution not found.");
        }
    }

    private static string XamlPath => Path.Combine(Root, "src/VideoDedup.Desktop/MainWindow.xaml");

    [Fact]
    public void Every_interactive_element_declares_an_automation_name_and_an_access_key()
    {
        var document = XDocument.Load(XamlPath);
        string[] interactive = ["Button", "TextBox", "CheckBox", "ListBox", "DataGrid", "Slider", "TabItem", "Image"];
        var elements = document.Descendants().Where(element => interactive.Contains(element.Name.LocalName)).ToArray();
        elements.Should().NotBeEmpty();

        var missingNames = elements.Where(element => string.IsNullOrWhiteSpace((string?)element.Attribute("AutomationProperties.Name")))
            .Select(element => element.Name.LocalName)
            .ToArray();
        missingNames.Should().BeEmpty("screen readers need a name for every control");

        var missingKeys = elements.Where(element => element.Name.LocalName is "Button" or "TabItem")
            .Where(element => element.Attribute("Content")?.Value.Contains('_') != true &&
                element.Attribute("Header")?.Value.Contains('_') != true)
            .Select(element => element.Attribute("Content")?.Value ?? element.Attribute("Header")?.Value ?? element.Name.LocalName)
            .ToArray();
        missingKeys.Should().BeEmpty("every action must be reachable from the keyboard");
    }

    [Fact]
    public void The_window_documents_that_media_is_never_deleted_and_scopes_the_one_delete_action_to_the_index()
    {
        var xaml = File.ReadAllText(XamlPath);

        xaml.Should().Contain("permanent deletion is not available");
        xaml.Should().Contain("Your video files are never deleted");
        xaml.Should().NotContain("DeleteFile");
        // The only destructive control is the index action, and it is named as such for screen readers.
        xaml.Should().Contain("AutomationProperties.Name=\"Delete index data\"");
        xaml.Should().NotContain("AutomationProperties.Name=\"Delete file\"");
    }

    [Fact]
    public void The_desktop_never_deletes_files_itself_because_deletion_belongs_to_the_application_layer()
    {
        var directory = Path.Combine(Root, "src/VideoDedup.Desktop");
        var sources = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories);
        sources.Should().NotBeEmpty();

        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            text.Should().NotContain("File.Delete", $"{source} must not delete files");
            text.Should().NotContain("Directory.Delete", $"{source} must not delete folders");
        }
    }

    [Fact]
    public void The_window_loads_with_bound_view_models_and_reachable_controls()
    {
        RunOnStaThread(() =>
        {
            var dashboard = new FakeDashboard();
            var window = new MainWindow(new ProcessingViewModel(dashboard),
                new ReviewViewModel(new FakeReviewService(), new FakeQuarantineService(), new FakeConfirmationPrompt(),
                    new FakePreviewSource()),
                new MaintenanceViewModel(new FakeMaintenanceService(), new FakeConfirmationPrompt()));
            try
            {
                window.DataContext.Should().BeSameAs(window);
                window.Processing.Should().NotBeNull();
                window.Review.Should().NotBeNull();
                window.Maintenance.Should().NotBeNull();
                foreach (var name in new[] { "Tabs", "Folder", "Scan", "Workers", "Results", "RefreshReviews", "Quarantine", "Groups", "Copies", "LoadPreview", "History", "Storage", "RefreshStorage", "ExportPath", "IncludePaths", "ExportIndex", "DeleteIndex" })
                {
                    var element = window.FindName(name) as DependencyObject;
                    element.Should().NotBeNull($"the window must declare a '{name}' control");
                    AutomationProperties.GetName(element).Should().NotBeNullOrWhiteSpace($"'{name}' needs an automation name");
                }

                window.Close();
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));
        thread.IsAlive.Should().BeFalse("the STA assertion thread must finish");
        failure.Should().BeNull($"WPF verification failed: {failure}");
        Debug.Assert(true);
    }
}
