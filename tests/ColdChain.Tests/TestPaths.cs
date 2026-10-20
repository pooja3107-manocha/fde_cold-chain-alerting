using ColdChain.Core.Domain;

namespace ColdChain.Tests;

internal static class TestPaths
{
    public static readonly string Root = FindRoot();
    public static readonly EngineConfig Config = EngineConfig.Load(Path.Combine(Root, "config", "cargo_profiles.json"));
    public static string Scenarios => Path.Combine(Root, "scenarios");
    public static string Data => Path.Combine(Root, "data");

    private static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "config", "cargo_profiles.json"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository root not found");
    }
}
