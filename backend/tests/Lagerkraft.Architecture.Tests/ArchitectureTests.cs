using System.Reflection;
using Lagerkraft.Shared;
using NetArchTest.Rules;

namespace Lagerkraft.Architecture.Tests;

public sealed class ArchitectureTests
{
    private static readonly Assembly Shared = typeof(IClock).Assembly;
    private static readonly Assembly ServiceDefaults = Assembly.Load("Lagerkraft.ServiceDefaults");
    private static readonly Assembly Platform = Assembly.Load("Lagerkraft.Platform");
    private static readonly Assembly WmsCoreApi = Assembly.Load("Lagerkraft.WmsCore.Api");
    private static readonly Assembly SyncGateway = Assembly.Load("Lagerkraft.SyncGateway");
    private static readonly Assembly Integrations = Assembly.Load("Lagerkraft.Integrations");

    private static readonly Assembly[] Services = [Platform, WmsCoreApi, SyncGateway, Integrations];

    private static readonly Assembly[] Production =
        [Shared, ServiceDefaults, .. Services];

    [Fact]
    public void Services_DoNotReferenceEachOther()
    {
        foreach (var from in Services)
        {
            foreach (var to in Services.Where(s => s != from))
            {
                AssertNoDependency(from, to.GetName().Name!);
            }
        }
    }

    [Fact]
    public void Shared_DoesNotReferenceServices()
    {
        foreach (var service in Services)
        {
            AssertNoDependency(Shared, service.GetName().Name!);
        }

        AssertNoDependency(Shared, ServiceDefaults.GetName().Name!);
    }

    [Fact]
    public void Identity_OnlyPlatformMayReferenceIt()
    {
        foreach (var from in Production.Where(a => a != Platform))
        {
            AssertNoDependency(from, "Microsoft.AspNetCore.Identity");
            AssertNoDependency(from, "Microsoft.Extensions.Identity.Stores");
            AssertNoDependency(from, "Microsoft.AspNetCore.Identity.EntityFrameworkCore");
        }
    }

    [Fact]
    public void MediatR_NoAssemblyReferencesIt()
    {
        foreach (var from in Production)
        {
            AssertNoDependency(from, "MediatR");
        }
    }

    [Fact]
    public void WmsCoreModules_CrossModule_OnlyThroughContracts()
    {
        var modules = LoadWmsCoreModules();
        foreach (var from in modules)
        {
            foreach (var to in modules.Where(m => m != from))
            {
                var toName = to.GetName().Name!;
                var result = Types.InAssembly(from)
                    .That()
                    .HaveDependencyOn(toName)
                    .Should()
                    .HaveDependencyOn(toName + ".Contracts")
                    .GetResult();

                result.IsSuccessful.ShouldBeTrue(
                    $"{from.GetName().Name} may reference {toName} only through {toName}.Contracts: {Format(result)}");
            }
        }
    }

    private static IReadOnlyList<Assembly> LoadWmsCoreModules()
    {
        var backendRoot = FindBackendRoot();
        var wmsRoot = Path.Combine(backendRoot, "src", "WmsCore");
        var names = Directory.GetFiles(wmsRoot, "Lagerkraft.WmsCore.*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && n != "Lagerkraft.WmsCore.Api")
            .Select(n => n!)
            .ToList();

        var loaded = new List<Assembly>(names.Count);
        foreach (var name in names)
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                          .FirstOrDefault(a => a.GetName().Name == name)
                      ?? TryLoad(name);
            asm.ShouldNotBeNull(
                $"Add a ProjectReference from Architecture.Tests to {name} so C3 cannot skip the module-boundary rule.");
            loaded.Add(asm!);
        }

        return loaded;
    }

    private static Assembly? TryLoad(string name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static string FindBackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lagerkraft.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Could not find backend/Lagerkraft.sln from the test output directory.");
        return dir.FullName;
    }

    private static void AssertNoDependency(Assembly from, string on)
    {
        var result = Types.InAssembly(from).ShouldNot().HaveDependencyOn(on).GetResult();
        result.IsSuccessful.ShouldBeTrue($"{from.GetName().Name} must not depend on {on}: {Format(result)}");
    }

    private static string Format(TestResult result) =>
        result.FailingTypeNames is { } names && names.Any() ? string.Join(", ", names) : "(no failing types listed)";
}
