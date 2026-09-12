using System.Text.Json;

namespace Lagerkraft.Contracts.Tests;

public sealed class EventContractTests
{
    private static readonly string EventsRoot = FindEventsRoot();

    public static IEnumerable<object[]> EventFixtures()
    {
        foreach (var schemaPath in Directory.EnumerateFiles(EventsRoot, "schema.json", SearchOption.AllDirectories))
        {
            var dir = Path.GetDirectoryName(schemaPath)!;
            var fixturesDir = Path.Combine(dir, "fixtures");
            if (!Directory.Exists(fixturesDir))
            {
                continue;
            }

            foreach (var fixture in Directory.EnumerateFiles(fixturesDir, "*.json"))
            {
                yield return [schemaPath, fixture];
            }
        }
    }

    [Theory]
    [MemberData(nameof(EventFixtures))]
    public void Fixture_Matches_Schema(string schemaPath, string fixturePath)
    {
        using var schemaDoc = JsonDocument.Parse(File.ReadAllText(schemaPath));
        using var fixtureDoc = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var schema = schemaDoc.RootElement;
        var fixture = fixtureDoc.RootElement;

        fixture.ValueKind.ShouldBe(JsonValueKind.Object);

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(e => e.GetString()!))
            {
                fixture.TryGetProperty(name, out _).ShouldBeTrue($"fixture {fixturePath} missing required '{name}'");
            }
        }

        if (schema.TryGetProperty("properties", out var props))
        {
            foreach (var prop in props.EnumerateObject())
            {
                if (!fixture.TryGetProperty(prop.Name, out var value))
                {
                    continue;
                }

                if (prop.Value.TryGetProperty("const", out var constVal))
                {
                    value.GetRawText().Trim('"').ShouldBe(constVal.GetString(),
                        $"fixture {fixturePath} property '{prop.Name}' const mismatch");
                }

                if (prop.Value.TryGetProperty("type", out var typeEl))
                {
                    var expected = typeEl.GetString();
                    var actual = value.ValueKind switch
                    {
                        JsonValueKind.String => "string",
                        JsonValueKind.Object => "object",
                        JsonValueKind.Array => "array",
                        JsonValueKind.Number => "number",
                        JsonValueKind.True or JsonValueKind.False => "boolean",
                        JsonValueKind.Null => "null",
                        _ => value.ValueKind.ToString()
                    };
                    if (expected is not null)
                    {
                        actual.ShouldBe(expected, $"fixture {fixturePath} property '{prop.Name}' type");
                    }
                }
            }
        }
    }

    [Fact]
    public void Every_Event_Folder_Has_Schema_And_Fixture()
    {
        var folders = Directory.GetDirectories(EventsRoot)
            .Where(d => !Path.GetFileName(d).Equals("fixtures", StringComparison.OrdinalIgnoreCase))
            .Where(d => File.Exists(Path.Combine(d, "schema.json")) || Directory.Exists(Path.Combine(d, "fixtures")) || File.Exists(Path.Combine(d, "README.md")) == false)
            .Where(d => Path.GetFileName(d) is not ("README.md"))
            .ToList();

        // Only event type folders (contain schema.json)
        var eventFolders = Directory.GetDirectories(EventsRoot)
            .Where(d => File.Exists(Path.Combine(d, "schema.json")))
            .ToList();

        eventFolders.Count.ShouldBeGreaterThanOrEqualTo(8);
        foreach (var dir in eventFolders)
        {
            Directory.Exists(Path.Combine(dir, "fixtures")).ShouldBeTrue(dir);
            Directory.EnumerateFiles(Path.Combine(dir, "fixtures"), "*.json").Any().ShouldBeTrue(dir);
        }
    }

    private static string FindEventsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "contracts", "events");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("contracts/events not found from " + AppContext.BaseDirectory);
    }
}