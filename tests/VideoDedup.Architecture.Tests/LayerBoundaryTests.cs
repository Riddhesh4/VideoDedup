using ArchitectureModel = ArchUnitNET.Domain.Architecture;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using VideoDedup.Application;
using VideoDedup.Desktop;
using VideoDedup.Domain;
using VideoDedup.Infrastructure;
using VideoDedup.Workers;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace VideoDedup.Architecture.Tests;

public sealed class LayerBoundaryTests
{
    private static readonly ArchitectureModel Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(ScanState).Assembly,
            typeof(IScanOrchestrator).Assembly,
            typeof(IProcessRunner).Assembly,
            typeof(IWorkerHeartbeat).Assembly,
            typeof(MainWindow).Assembly)
        .Build();

    [Fact]
    public void Domain_types_must_not_depend_on_other_solution_layers()
    {
        Types().That().ResideInAssembly(typeof(ScanState).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(IScanOrchestrator).Assembly))
            .Check(Architecture);
        Types().That().ResideInAssembly(typeof(IScanOrchestrator).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(IProcessRunner).Assembly))
            .Check(Architecture);
        Types().That().ResideInAssembly(typeof(ScanState).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(IProcessRunner).Assembly))
            .Check(Architecture);
        Types().That().ResideInAssembly(typeof(ScanState).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(IWorkerHeartbeat).Assembly))
            .Check(Architecture);
        Types().That().ResideInAssembly(typeof(ScanState).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(MainWindow).Assembly))
            .Check(Architecture);
    }

    [Fact]
    public void Desktop_types_must_not_depend_on_infrastructure()
    {
        Types().That().ResideInAssembly(typeof(MainWindow).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(IProcessRunner).Assembly))
            .Check(Architecture);
    }

    [Fact]
    public void Application_types_must_not_depend_on_adapters_or_orchestration()
    {
        Types().That().ResideInAssembly(typeof(IScanOrchestrator).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(IProcessRunner).Assembly))
            .Check(Architecture);
        Types().That().ResideInAssembly(typeof(IScanOrchestrator).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(IWorkerHeartbeat).Assembly))
            .Check(Architecture);
        Types().That().ResideInAssembly(typeof(IScanOrchestrator).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(MainWindow).Assembly))
            .Check(Architecture);
    }

    [Fact]
    public void Infrastructure_types_must_not_depend_on_workers_or_desktop()
    {
        Types().That().ResideInAssembly(typeof(IProcessRunner).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(IWorkerHeartbeat).Assembly))
            .Check(Architecture);
        Types().That().ResideInAssembly(typeof(IProcessRunner).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(MainWindow).Assembly))
            .Check(Architecture);
    }

    [Fact]
    public void Worker_types_must_not_depend_on_infrastructure_or_desktop()
    {
        Types().That().ResideInAssembly(typeof(IWorkerHeartbeat).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(IProcessRunner).Assembly))
            .Check(Architecture);
        Types().That().ResideInAssembly(typeof(IWorkerHeartbeat).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(MainWindow).Assembly))
            .Check(Architecture);
    }

    [Fact]
    public void Desktop_types_must_not_depend_on_workers()
    {
        Types().That().ResideInAssembly(typeof(MainWindow).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(IWorkerHeartbeat).Assembly))
            .Check(Architecture);
    }
}
