using ColdChain.Core.Scenarios;
using Xunit;

namespace ColdChain.Tests;

/// <summary>Every file in scenarios/ is a test. Add a file, get a test.</summary>
public class ScenarioTests
{
    public static IEnumerable<object[]> Names() =>
        Directory.GetFiles(TestPaths.Scenarios, "*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new object[] { Path.GetFileNameWithoutExtension(f) });

    [Theory]
    [MemberData(nameof(Names))]
    public void Scenario_behaves_as_designed(string name)
    {
        var def = ScenarioRunner.Load(Path.Combine(TestPaths.Scenarios, name + ".json"));
        var result = ScenarioRunner.Run(def, TestPaths.Config);
        Assert.True(result.Passed, string.Join(Environment.NewLine, result.Failures));
    }
}
