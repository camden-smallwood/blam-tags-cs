using System.Buffers.Binary;
using System.Text.Json;

namespace BlamTags.Tests;

/// <summary>
/// Endian-aware editing gate for classic Halo CE (big-endian) tags. Reads a
/// CE tag, mutates a primitive root field through the api facade, writes the
/// tag back, reloads it, and requires the edited value to round-trip. A
/// little-endian write into a big-endian tag would byte-swap on reload and
/// fail — so this proves <see cref="TagFieldCodec.Serialize"/> honors the
/// tag's endianness (Phase 8).
/// </summary>
public sealed class ClassicEditTests
{
    [SkippableFact]
    public void CeEdit_PrimitiveField_RoundTripsBigEndian()
    {
        var defs = TestEnvironment.DefinitionsRoot;
        Skip.If(defs is null, "definitions/ not found");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string corpus = Environment.GetEnvironmentVariable("BLAM_CE_CORPUS") is { Length: > 0 } c && Directory.Exists(c)
            ? c : Path.Combine(home, "Halo", "haloce_mcc/tags");
        Skip.If(!Directory.Exists(corpus), "Halo CE corpus not found (set BLAM_CE_CORPUS)");

        var groupNameByTag = LoadTagIndex(Path.Combine(defs!, "haloce_mcc", "_meta.json"));
        var layoutCache = new Dictionary<string, TagLayout>(StringComparer.Ordinal);

        int edited = 0;
        foreach (var path in TestEnvironment.EnumerateCorpusTags(corpus))
        {
            byte[] original;
            try { original = File.ReadAllBytes(path); } catch { continue; }
            if (Classic.ParseHeader(original) is not { } p) continue;
            // CE only (big-endian); skip H2.
            if (p.Engine != ClassicEngine.HaloCe) continue;

            uint groupTag = BinaryPrimitives.ReadUInt32BigEndian(p.Header.GroupTag);
            if (!groupNameByTag.TryGetValue(groupTag, out string? defName)) continue;
            if (!layoutCache.TryGetValue(defName, out var layout))
            {
                try { layout = TagLayout.FromJson(Path.Combine(defs!, "haloce_mcc", $"{defName}.json")); }
                catch { continue; }
                layoutCache[defName] = layout;
            }

            TagFile tag;
            try { tag = Classic.ReadClassicTagFile(original, layout); } catch { continue; }
            Assert.Equal(Endian.Be, tag.Endian);

            // Find the first editable primitive field on the root struct.
            var target = tag.Root.Fields().FirstOrDefault(f =>
                f.Value is TagFieldData.Real or TagFieldData.LongInteger or TagFieldData.ShortInteger);
            if (target is null) continue;

            // Mutate to a distinctive value, write, reload, read back.
            TagFieldData newValue;
            Func<TagFieldData, bool> check;
            switch (target.Value)
            {
                case TagFieldData.Real:
                    newValue = new TagFieldData.Real(123.456f);
                    check = v => v is TagFieldData.Real r && System.Math.Abs(r.Value - 123.456f) < 1e-3f;
                    break;
                case TagFieldData.LongInteger:
                    newValue = new TagFieldData.LongInteger(0x11223344);
                    check = v => v is TagFieldData.LongInteger r && r.Value == 0x11223344;
                    break;
                default:
                    newValue = new TagFieldData.ShortInteger(0x1234);
                    check = v => v is TagFieldData.ShortInteger r && r.Value == 0x1234;
                    break;
            }
            int fieldOffset = (int)layout.Fields[target.FieldIndex].Offset;

            string fieldName = target.Name;
            target.Set(newValue);
            byte[] written = tag.WriteToBytes();
            var reloaded = Classic.ReadClassicTagFile(written, layout);
            var roundtripped = reloaded.Root.Fields().First(f => f.Name == fieldName).Value;

            Assert.True(check(roundtripped!),
                $"{Path.GetFileName(path)}: field {fieldName} did not round-trip (got {roundtripped})");

            // Spot-check the on-disk bytes are big-endian for a LongInteger:
            // 0x11223344 stored BE = 11 22 33 44 at the field's body offset.
            if (newValue is TagFieldData.LongInteger)
            {
                int bodyOffset = 64 + fieldOffset; // 64-byte classic header
                Assert.Equal(0x11, written[bodyOffset]);
                Assert.Equal(0x22, written[bodyOffset + 1]);
                Assert.Equal(0x33, written[bodyOffset + 2]);
                Assert.Equal(0x44, written[bodyOffset + 3]);
            }

            edited++;
            if (edited >= 25) break;
        }

        Skip.If(edited == 0, "no CE tag with an editable primitive root field found");
    }

    private static Dictionary<uint, string> LoadTagIndex(string metaPath)
    {
        var map = new Dictionary<uint, string>();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(metaPath));
        if (doc.RootElement.TryGetProperty("tag_index", out var ti) && ti.ValueKind == JsonValueKind.Object)
            foreach (var prop in ti.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String && GroupTag.Parse(prop.Name) is { } tag)
                    map[tag] = prop.Value.GetString()!;
        return map;
    }
}
