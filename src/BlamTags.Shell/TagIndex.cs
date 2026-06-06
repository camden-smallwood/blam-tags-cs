using System.Text.Json;
using BlamTags;

namespace BlamTags.Shell;

/// <summary>
/// group-tag ↔ group-name index from <c>definitions/&lt;game&gt;/_meta.json</c>'s
/// <c>tag_index</c> map (e.g. <c>bipd</c> ↔ <c>biped</c>). Empty when no
/// <c>--game</c> is set — tag-references then render as the raw 4-byte tag.
/// </summary>
public sealed class TagIndex
{
    private readonly Dictionary<uint, string> _nameByTag = new();
    private readonly Dictionary<string, uint> _tagByName = new(StringComparer.Ordinal);

    public static TagIndex Empty { get; } = new();

    /// <summary>The friendly group name for a group tag, or null.</summary>
    public string? NameFor(uint groupTag) => _nameByTag.GetValueOrDefault(groupTag);

    /// <summary>The group tag for a friendly group name, or null.</summary>
    public uint? GroupTagFor(string name) => _tagByName.TryGetValue(name, out var t) ? t : null;

    /// <summary>Load from <c>&lt;definitionsRoot&gt;/&lt;game&gt;/_meta.json</c>.
    /// Throws if the file is missing or malformed (matching the Rust eager load).</summary>
    public static TagIndex Load(string definitionsRoot, string game)
    {
        var path = Path.Combine(definitionsRoot, game, "_meta.json");
        byte[] bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes);
        var index = new TagIndex();
        if (doc.RootElement.TryGetProperty("tag_index", out var ti) && ti.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ti.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                // Keys are 4-char group tags (space-padded), e.g. "bipd", "rm  ".
                uint? tag = ParseGroupTagLoose(prop.Name);
                if (tag is null) continue;
                string name = prop.Value.GetString()!;
                index._nameByTag[tag.Value] = name;
                index._tagByName[name] = tag.Value;
            }
        }
        return index;
    }

    /// <summary>BE-pack a 1–4 char group tag, right-padding short tags with
    /// spaces. Returns null if longer than 4 chars.</summary>
    private static uint? ParseGroupTagLoose(string s)
    {
        if (s.Length > 4) return null;
        Span<byte> b = stackalloc byte[4] { (byte)' ', (byte)' ', (byte)' ', (byte)' ' };
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
