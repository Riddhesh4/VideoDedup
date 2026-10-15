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
            typeof(App).Assembly)
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
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(App).Assembly))
            .Check(Architecture);
    }

    [Fact]
    public void Desktop_types_must_not_depend_on_infrastructure()
    {
        Types().That().ResideInAssembly(typeof(App).Assembly)
            .Should().NotDependOnAny(Types().That().ResideInAssembly(typeof(IProcessRunner).Assembly))
            .Check(Architecture);
    }
}
