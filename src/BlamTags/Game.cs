namespace BlamTags;

/// <summary>
/// The Halo game (engine generation) a tag belongs to — the single dispatch
/// point for engine-specific asset extraction. It decides which tag-structure
/// reader to use and which JMS / ASS / JMA text-format version to emit. The
/// classic engines (Halo CE, Halo 2) carry their own older render/collision
/// structures and older format versions; the gen3+ MCC engines (Halo 3, ODST,
/// Reach, Halo 4, H2A) share one structure and one version set.
/// </summary>
public enum Game
{
    /// <summary>Halo 1 / Combat Evolved (Anniversary). <c>gbxmodel</c> render
    /// geometry, JMS version 8200, no ASS (its BSP source is also JMS).</summary>
    Halo1,
    /// <summary>Halo 2. <c>render_model</c> (section-based) geometry, JMS
    /// version 8210, ASS version 2.</summary>
    Halo2,
    /// <summary>Halo 3 and the later gen3/gen4 MCC engines (ODST, Reach, Halo 4,
    /// H2A) — shared <c>render_model</c> geometry, JMS 8213, ASS 7.</summary>
    Halo3,
}

public static class GameExtensions
{
    /// <summary>Classify a tag by its container engine: classic Halo CE → Halo1,
    /// classic Halo 2 (any sub-version) → Halo2, MCC self-describing → Halo3.</summary>
    public static Game GameOf(this TagFile tag) => tag.ClassicEngine switch
    {
        ClassicEngine.HaloCe => Game.Halo1,
        not null => Game.Halo2,
        null => Game.Halo3,
    };

    /// <summary>The JMS text-format version this game's tools read/write.</summary>
    public static int JmsVersion(this Game game) => game switch
    {
        Game.Halo1 => 8200,
        Game.Halo2 => 8210,
        _ => 8213,
    };

    /// <summary>The ASS text-format version, or null for Halo 1 (no ASS —
    /// Halo 1 BSP source is JMS).</summary>
    public static int? AssVersion(this Game game) => game switch
    {
        Game.Halo1 => null,
        Game.Halo2 => 2,
        _ => 7,
    };

    /// <summary>The JMA-family (animation) text-format version. All three
    /// generations share 16392 (HABT lists it valid for CE/H2/H3).</summary>
    public static int JmaVersion(this Game game) => 16392;
}
