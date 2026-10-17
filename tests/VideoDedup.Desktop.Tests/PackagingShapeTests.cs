using System.Reflection;
using FluentAssertions;
using VideoDedup.Desktop;
using VideoDedup.Desktop.ViewModels;

namespace VideoDedup.Desktop.Tests;

/// <summary>
/// Guards the shape of the shipped application, not its behaviour. A tester reported "it does not look like it is
/// running": the package contained a second executable, <c>VideoDedup.Desktop.exe</c>, whose generated WPF entry point
/// had no startup window, so starting it produced an invisible process and the real entry point was never found.
/// </summary>
public sealed class PackagingShapeTests
{
    [Fact]
    public void The_ui_assembly_is_not_an_entry_point()
    {
        typeof(MainWindow).Assembly.EntryPoint.Should().BeNull(
            "VideoDedup.Launcher is the only composition root; a second entry point ships a broken start");
    }

    [Fact]
    public void The_ui_assembly_declares_no_application_definition()
    {
        // WPF generates that entry point from an Application-derived type declared in the UI project, so its absence is
        // the structural cause of the test above.
        var applicationTypes = typeof(MainWindow).Assembly
            .GetTypes()
            .Where(type => typeof(System.Windows.Application).IsAssignableFrom(type))
            .ToList();

        applicationTypes.Should().BeEmpty(
            "an Application type in this assembly compiles into a Main that starts a windowless process");
    }

    [Fact]
    public void The_ui_assembly_exposes_the_views_and_view_models_the_smoke_test_drives()
    {
        // Names the release smoke test looks up by UI Automation, so a rename fails here rather than in packaging.
        typeof(System.Windows.Window).IsAssignableFrom(typeof(MainWindow)).Should().BeTrue();
        typeof(ProcessingViewModel).Assembly.Should().BeSameAs(typeof(MainWindow).Assembly);
    }

    [Fact]
    public void No_public_type_of_the_ui_assembly_is_a_program_entry_class()
    {
        var mainMethods = typeof(MainWindow).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(method => method.Name == "Main" && method.ReturnType == typeof(void))
            .ToList();

        mainMethods.Should().BeEmpty();
    }
}
