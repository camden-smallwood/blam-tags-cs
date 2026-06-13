namespace BlamTags;

/// <summary>Reference to an animation entry — either local
/// (<c>GraphIndex = -1</c>, <see cref="AnimationIndex"/> indexes into our
/// own animations block) or inherited (positive <c>GraphIndex</c>
/// references a parent animation_graph chain entry). Port of the Rust
/// <c>GraphActionAnimation</c>.</summary>
public readonly record struct GraphActionAnimation(short GraphIndex, short AnimationIndex)
{
    /// <summary><c>true</c> when the reference resolves via this jmad's
    /// own animations block.</summary>
    public bool IsLocal => GraphIndex < 0;

    public static GraphActionAnimation FromStruct(TagStruct s) => new(
        (short)(s.ReadIntAny("graph index") ?? -1),
        s.ReadBlockIndex("animation"));
}

/// <summary>One state→animation binding (action or overlay).</summary>
public sealed class GraphAction
{
    public string Label { get; init; } = "";
    public GraphActionAnimation Animation { get; init; }

    public static GraphAction FromStruct(TagStruct s) => new()
    {
        Label = s.ReadStringId("label") ?? "",
        Animation = s.Field("animation")?.AsStruct() is { } st
            ? GraphActionAnimation.FromStruct(st) : default,
    };
}

/// <summary>One transition between two states, referenced by destination
/// state name.</summary>
public sealed class GraphTransition
{
    public string DestinationState { get; init; } = "";
    public GraphActionAnimation Animation { get; init; }

    public static GraphTransition FromStruct(TagStruct s) => new()
    {
        DestinationState = s.ReadStringId("state name") ?? "",
        Animation = s.Field("animation")?.AsStruct() is { } st
            ? GraphActionAnimation.FromStruct(st) : default,
    };
}

/// <summary>One animation <b>set</b> under a weapon type. Real block in
/// Reach+; synthesized (label <c>"any"</c>) from the weapon type's direct
/// fields in H3.</summary>
public sealed class GraphSet
{
    public string Label { get; init; } = "";
    public List<GraphAction> Actions { get; init; } = new();
    public List<GraphAction> Overlays { get; init; } = new();
    public List<GraphTransition> Transitions { get; init; } = new();

    /// <summary>Reach+ <c>animation_set_block</c> element.</summary>
    public static GraphSet FromStruct(TagStruct s)
    {
        var overlays = AnimationGraph.ActionBlock(s, "overlay animations");
        if (overlays.Count == 0) overlays = AnimationGraph.ActionBlock(s, "overlays");
        return new GraphSet
        {
            Label = s.ReadStringId("label") ?? "",
            Actions = AnimationGraph.ActionBlock(s, "actions"),
            Overlays = overlays,
            Transitions = AnimationGraph.ReadBlock(s, "transitions", GraphTransition.FromStruct),
        };
    }

    /// <summary>H3: the weapon type itself carries the action blocks; wrap
    /// them in a single implicit <c>"any"</c> set.</summary>
    public static GraphSet FromWeaponType(TagStruct s) => new()
    {
        Label = "any",
        Actions = AnimationGraph.ActionBlock(s, "actions"),
        Overlays = AnimationGraph.ActionBlock(s, "overlays"),
        Transitions = AnimationGraph.ReadBlock(s, "transitions", GraphTransition.FromStruct),
    };
}

public sealed class GraphWeaponType
{
    public string Label { get; init; } = "";
    /// <summary>Animation <b>sets</b>. Reach+ has an explicit
    /// <c>sets[]</c> level; for H3 a single implicit set labeled
    /// <c>"any"</c> is synthesized so both engines walk the same shape.</summary>
    public List<GraphSet> Sets { get; init; } = new();

    public static GraphWeaponType FromStruct(TagStruct s)
    {
        var setsBlock = s.Field("sets")?.AsBlock();
        var sets = setsBlock is not null
            ? AnimationGraph.ReadBlock(s, "sets", GraphSet.FromStruct)
            : new List<GraphSet> { GraphSet.FromWeaponType(s) };
        return new GraphWeaponType { Label = s.ReadStringId("label") ?? "", Sets = sets };
    }
}

public sealed class GraphWeaponClass
{
    public string Label { get; init; } = "";
    public List<GraphWeaponType> WeaponTypes { get; init; } = new();

    public static GraphWeaponClass FromStruct(TagStruct s) => new()
    {
        Label = s.ReadStringId("label") ?? "",
        WeaponTypes = AnimationGraph.ReadBlock(s, "weapon type", GraphWeaponType.FromStruct),
    };
}

public sealed class GraphMode
{
    public string Label { get; init; } = "";
    public List<GraphWeaponClass> WeaponClasses { get; init; } = new();

    public static GraphMode FromStruct(TagStruct s) => new()
    {
        Label = s.ReadStringId("label") ?? "",
        WeaponClasses = AnimationGraph.ReadBlock(s, "weapon class", GraphWeaponClass.FromStruct),
    };
}

/// <summary>Result of walking a jmad's <c>content/modes[]</c> tree — the
/// (mode, weapon_class, weapon_type, set, state) → animation index map.
/// Port of the Rust <c>graph.rs</c>.</summary>
public sealed class AnimationGraph
{
    public List<GraphMode> Modes { get; init; } = new();

    public static AnimationGraph FromTag(TagFile tag) => FromStruct(tag.Root);

    public static AnimationGraph FromStruct(TagStruct root)
    {
        var content = root.Field("content")?.AsStruct();
        var modes = content is not null
            ? ReadBlock(content, "modes", GraphMode.FromStruct)
            : new List<GraphMode>();
        return new AnimationGraph { Modes = modes };
    }

    /// <summary>Look up an action animation by walking the (mode,
    /// weapon_class, weapon_type, set, action) tuple. Each scope component
    /// falls back to <c>"any"</c> if the exact name doesn't match (Halo's
    /// wildcard resolution). Returns <c>null</c> if no action matches.</summary>
    public GraphActionAnimation? FindAction(string mode, string weaponClass, string weaponType, string set, string action)
    {
        var m = Modes.FirstOrDefault(m => m.Label == mode) ?? Modes.FirstOrDefault(m => m.Label == "any");
        if (m is null) return null;
        var wc = m.WeaponClasses.FirstOrDefault(w => w.Label == weaponClass)
            ?? m.WeaponClasses.FirstOrDefault(w => w.Label == "any");
        if (wc is null) return null;
        var wt = wc.WeaponTypes.FirstOrDefault(w => w.Label == weaponType)
            ?? wc.WeaponTypes.FirstOrDefault(w => w.Label == "any");
        if (wt is null) return null;

        static GraphActionAnimation? Pick(GraphSet s, string action)
        {
            var a = s.Actions.FirstOrDefault(a => a.Label == action);
            return a is null ? null : a.Animation;
        }
        // Exact set, then the "any" set, then any set carrying the action.
        var exact = wt.Sets.FirstOrDefault(s => s.Label == set);
        if (exact is not null && Pick(exact, action) is { } e) return e;
        var any = wt.Sets.FirstOrDefault(s => s.Label == "any");
        if (any is not null && Pick(any, action) is { } a2) return a2;
        foreach (var s in wt.Sets)
            if (Pick(s, action) is { } p) return p;
        return null;
    }

    /// <summary>Find the first action available anywhere in the tree —
    /// "just play SOMETHING".</summary>
    public GraphActionAnimation? FirstAction()
    {
        foreach (var mode in Modes)
            foreach (var wc in mode.WeaponClasses)
                foreach (var wt in wc.WeaponTypes)
                    foreach (var set in wt.Sets)
                        if (set.Actions.Count > 0) return set.Actions[0].Animation;
        return null;
    }

    //==== helpers ====

    internal static List<GraphAction> ActionBlock(TagStruct s, string name) =>
        ReadBlock(s, name, GraphAction.FromStruct);

    internal static List<T> ReadBlock<T>(TagStruct s, string name, Func<TagStruct, T> f)
    {
        var block = s.Field(name)?.AsBlock();
        var outList = new List<T>(block?.Count ?? 0);
        if (block is null) return outList;
        for (int i = 0; i < block.Count; i++)
            if (block.Element(i) is { } elem)
                outList.Add(f(elem));
        return outList;
    }
}
