using BlamTags;

namespace BlamTags.Tests;

/// <summary>
/// Phase 1 gate: every per-group JSON schema across all shipped games must
/// import into a <see cref="TagLayout"/> with every struct's computed size
/// matching its declared size (<see cref="TagLayout.FromJson"/> throws
/// <see cref="TagSchemaException"/> on any mismatch). This exercises the
/// whole schema path — string interning, ordinal index assignment,
/// parent-chain merge, tmpl expansion, and struct-size computation.
/// </summary>
public sealed class SchemaImportTests
{
    [SkippableFact]
    public void AllDefinitions_Import_WithMatchingSizes()
    {
        var defs = TestEnvironment.DefinitionsRoot;
        Skip.If(defs is null, "definitions/ tree not found.");

        int loaded = 0;
        var failures = new List<string>();

        foreach (var gameDir in Directory.EnumerateDirectories(defs!))
        {
            foreach (var json in Directory.EnumerateFiles(gameDir, "*.json"))
            {
                if (Path.GetFileName(json) == "_meta.json")
                    continue;
                try
                {
                    var layout = TagLayout.FromJson(json);
                    Assert.NotEmpty(layout.StructLayouts);
                    loaded++;
                }
                catch (Exception ex)
                {
                    string rel = Path.GetRelativePath(defs!, json);
                    if (failures.Count < 40)
                        failures.Add($"{rel}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Assert.True(failures.Count == 0,
            $"imported {loaded} schema(s), {failures.Count} failed:\n  " + string.Join("\n  ", failures));
        Assert.True(loaded >= 600, $"expected 600+ schemas, only imported {loaded}");
    }
}
