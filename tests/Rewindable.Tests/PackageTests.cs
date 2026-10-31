using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rewindable.Tests;

/// <summary>
/// The Unity package is installed from git, where it is immutable: a missing .meta is an import error on every
/// machine. These checks keep the package publishable.
/// </summary>
public class PackageTests
{
    private static readonly string PackageDir = FindPackage();

    private static string FindPackage()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Rewindable.slnx")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("Repository root (Rewindable.slnx) not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, "unity", "com.aqua0801.rewindable");
    }

    private static bool IsHidden(string path)
    {
        string rel = Path.GetRelativePath(PackageDir, path);
        return rel.Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.') || p.EndsWith('~'));
    }

    private static IEnumerable<string> Assets() =>
        Directory.EnumerateFileSystemEntries(PackageDir, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith(".meta", StringComparison.Ordinal) && !IsHidden(p));

    [Fact]
    public void EveryAsset_HasAMeta_AndNoMetaIsOrphaned()
    {
        var missing = Assets().Where(p => !File.Exists(p + ".meta")).Select(p => Path.GetRelativePath(PackageDir, p)).ToList();
        var orphans = Directory.EnumerateFiles(PackageDir, "*.meta", SearchOption.AllDirectories)
            .Where(m => !IsHidden(m))
            .Where(m => { string asset = m[..^".meta".Length]; return !File.Exists(asset) && !Directory.Exists(asset); })
            .Select(m => Path.GetRelativePath(PackageDir, m))
            .ToList();

        Assert.True(missing.Count == 0, "Run `python scripts/unity_meta.py`. Missing: " + string.Join(", ", missing));
        Assert.True(orphans.Count == 0, "Orphaned .meta: " + string.Join(", ", orphans));
    }

    [Fact]
    public void MetaGuids_AreUnique()
    {
        var guids = Directory.EnumerateFiles(PackageDir, "*.meta", SearchOption.AllDirectories)
            .Select(m => Regex.Match(File.ReadAllText(m), @"^guid: ([0-9a-f]{32})$", RegexOptions.Multiline).Groups[1].Value)
            .ToList();

        Assert.DoesNotContain(string.Empty, guids);
        Assert.Equal(guids.Count, guids.Distinct().Count());
    }

    [Fact]
    public void PackageVersion_MatchesTheAssemblyVersion()
    {
        using JsonDocument package = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackageDir, "package.json")));
        string packageVersion = package.RootElement.GetProperty("version").GetString()!;
        string assemblyVersion = typeof(RollbackStateAttribute).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

        Assert.Equal(assemblyVersion, packageVersion);
    }

    [Fact]
    public void AnalyzerDll_IsShippedAsARoslynAnalyzer()
    {
        string dll = Path.Combine(PackageDir, "Runtime", "Analyzers", "Rewindable.Generator.dll");

        Assert.True(File.Exists(dll), "Build Release once (dotnet build -c Release) to copy the generator into the package.");
        Assert.Contains("- RoslynAnalyzer", File.ReadAllText(dll + ".meta"));
    }

    [Fact]
    public void RuntimeSources_EnableNullable()
    {
        // Unity compiles packages with nullable disabled: without the directive every `?` annotation warns (CS8632).
        var bad = Directory.EnumerateFiles(PackageDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !File.ReadLines(f).First().StartsWith("#nullable enable", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(PackageDir, f))
            .ToList();

        Assert.True(bad.Count == 0, "Add `#nullable enable` as the first line of: " + string.Join(", ", bad));
    }
}
