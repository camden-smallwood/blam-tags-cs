namespace BlamTags;

/// <summary>
/// Chunk signatures ("tags") are 4 ASCII bytes packed into a
/// <see cref="uint"/> as if read big-endian (e.g. <c>Tag.Of("blay")</c>),
/// so signature comparisons in source read naturally as the chunk they
/// represent. On disk the same bytes are written in the file's endian;
/// reading them back with the matching endian recovers this BE-packed value.
/// </summary>
internal static class Tag
{
    /// <summary>BE-pack a 4-character signature into a <see cref="uint"/>.</summary>
    public static uint Of(string s)
    {
        // Signatures are always exactly 4 bytes (some contain non-letters
        // like "sz+x", "ti][", "tg\0c"); each char is one ASCII byte.
        return ((uint)(byte)s[0] << 24)
             | ((uint)(byte)s[1] << 16)
             | ((uint)(byte)s[2] << 8)
             | (byte)s[3];
    }

    /// <summary>The 4 ASCII bytes of a BE-packed signature.</summary>
    public static byte[] Bytes(uint sig) =>
        [(byte)(sig >> 24), (byte)(sig >> 16), (byte)(sig >> 8), (byte)sig];

    /// <summary>Render a signature as readable ASCII (non-printables → '?').</summary>
    public static string Show(uint sig)
    {
        Span<char> c = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(sig >> (24 - i * 8));
            c[i] = (b >= 0x21 && b <= 0x7E) || b == (byte)' ' ? (char)b : '?';
        }
        return new string(c);
    }
}
