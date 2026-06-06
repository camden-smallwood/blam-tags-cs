using System.Text;

namespace BlamTags;

/// <summary>
/// Render and parse 4-byte group tags (stored as BE-packed <see cref="uint"/>
/// with trailing-space padding for short tags), e.g. <c>0x62697064</c> ↔
/// <c>"bipd"</c>, <c>0x726D2020</c> ↔ <c>"rm"</c>.
/// </summary>
public static class GroupTag
{
    /// <summary>ASCII form with trailing spaces / NULs stripped.</summary>
    public static string Format(uint tag)
    {
        Span<byte> b = stackalloc byte[4]
        {
            (byte)(tag >> 24), (byte)(tag >> 16), (byte)(tag >> 8), (byte)tag,
        };
        return Encoding.UTF8.GetString(b).TrimEnd('\0', ' ');
    }

    /// <summary>Parse a 1–4 char ASCII group tag (right-padded with spaces) to
    /// its BE-packed form. Null if longer than 4 bytes.</summary>
    public static uint? Parse(string s)
    {
        if (s.Length > 4) return null;
        Span<byte> b = stackalloc byte[4] { (byte)' ', (byte)' ', (byte)' ', (byte)' ' };
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
}
