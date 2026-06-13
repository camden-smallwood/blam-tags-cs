namespace BlamTags;

/// <summary>What kind of state an animation name encodes. Mirrors
/// Foundry's <c>AnimationStateType</c> and the Rust
/// <c>AnimationStateType</c>.</summary>
public enum AnimationStateType
{
    /// <summary>Normal action/overlay (idle, aim, reload, …).</summary>
    Action,
    /// <summary>Damage reaction (<c>*_ping</c> / <c>*_kill</c> + direction + region).</summary>
    Damage,
    /// <summary>State-to-state transition (contains a <c>2</c> separator token).</summary>
    Transition,
}

/// <summary>Parsed animation name — a faithful port of Foundry's
/// <c>utils.AnimationName</c> (and the Rust <c>name.rs</c>). Halo
/// animation names are colon-delimited token strings encoding a
/// <c>(mode, weapon_class, weapon_type, set, state)</c> scope plus
/// optional damage / transition / variant suffixes. Unparsed components
/// default to <c>"any"</c> (Halo's wildcard); <see cref="Valid"/> is
/// <c>false</c> for empty or single-token (<c>custom</c>) names.</summary>
public sealed class AnimationName
{
    public string Mode { get; private set; } = "any";
    public string WeaponClass { get; private set; } = "any";
    public string WeaponType { get; private set; } = "any";
    public string Set { get; private set; } = "any";
    public string State { get; private set; } = "any";
    public string Variant { get; private set; } = "";
    public AnimationStateType StateType { get; private set; } = AnimationStateType.Action;
    /// <summary>Single-token name with no scope — not eligible as a
    /// composition base source.</summary>
    public bool Custom { get; private set; }
    public bool Valid { get; private set; }

    private static readonly string[] DamageStates = ["h_ping", "s_ping", "h_kill", "s_kill"];
    private static readonly string[] Directions = ["front", "left", "right", "back"];
    private static readonly string[] Regions =
        ["gut", "chest", "head", "l_arm", "l_hand", "l_leg", "l_foot", "r_arm", "r_hand", "r_leg", "r_foot"];

    /// <summary>Tokenize + parse an animation name, mirroring Foundry's
    /// <c>AnimationName.__init__</c> token-popping grammar.</summary>
    public static AnimationName Parse(string name)
    {
        var outName = new AnimationName();

        // tokenise(): replace ':' with space, lowercase, split on whitespace.
        var tokens = name.ToLowerInvariant()
            .Split([':', ' ', '\t', '\n', '\r', '\f', '\v'], StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (tokens.Count == 0) return outName;

        // Trailing `var*` is a variant suffix.
        if (tokens[^1].StartsWith("var", StringComparison.Ordinal))
        {
            outName.Variant = tokens[^1];
            tokens.RemoveAt(tokens.Count - 1);
        }

        // Single remaining token → custom (and not `valid`).
        if (tokens.Count == 1)
        {
            outName.Custom = true;
            return outName;
        }

        // Damage: `… <damage_state> <direction> <region>`.
        if (tokens.Count > 2 && Regions.Contains(tokens[^1]))
        {
            string dir = tokens[^2];
            string dmg = tokens[^3];
            if (Directions.Contains(dir) && DamageStates.Contains(dmg))
            {
                outName.StateType = AnimationStateType.Damage;
                tokens.RemoveAt(tokens.Count - 1); // region
                tokens.RemoveAt(tokens.Count - 1); // direction
            }
        }
        else if (tokens.Count > 2 && tokens.Contains("2"))
        {
            // Transition: a `2` separator with tokens on both sides.
            int index2 = tokens.IndexOf("2");
            if (index2 > 0 && index2 < tokens.Count - 1)
            {
                outName.StateType = AnimationStateType.Transition;
                tokens.RemoveAt(tokens.Count - 1); // destination_state
                if (tokens.Count > 0 && tokens[^1] == "2")
                {
                    tokens.RemoveAt(tokens.Count - 1);
                }
                else
                {
                    while (tokens.Count > 0 && tokens[^1] != "2")
                        tokens.RemoveAt(tokens.Count - 1);
                    if (tokens.Count > 0) tokens.RemoveAt(tokens.Count - 1);
                }
            }
        }

        // State is the last remaining token; scope is popped from the
        // front (mode, weapon_class, weapon_type, set).
        outName.State = tokens.Count > 0 ? tokens[^1] : "any";
        if (tokens.Count > 0) tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count > 0) { outName.Mode = tokens[0]; tokens.RemoveAt(0); }
        if (tokens.Count > 0) { outName.WeaponClass = tokens[0]; tokens.RemoveAt(0); }
        if (tokens.Count > 0) { outName.WeaponType = tokens[0]; tokens.RemoveAt(0); }
        if (tokens.Count > 0) { outName.Set = tokens[0]; tokens.RemoveAt(0); }

        outName.Valid = true;
        return outName;
    }

    /// <summary>Ordered, de-duplicated list of base-animation <i>states</i>
    /// to try when resolving the composition base for an overlay/
    /// replacement, in priority order. Port of Foundry's
    /// <c>_base_state_candidates</c> (overlay/replacement branch).</summary>
    public static List<string> BaseStateCandidates(string state)
    {
        var candidates = new List<string>();

        void Add(string s)
        {
            if (!string.IsNullOrEmpty(s) && !candidates.Contains(s))
                candidates.Add(s);
        }
        // add_family: a state plus its slow/fast siblings (or the
        // de-suffixed root if it already is a `_fast`/`_slow`).
        void AddFamily(string s)
        {
            s = s.Trim('_');
            if (s.Length == 0) return;
            Add(s);
            if (s.EndsWith("_fast", StringComparison.Ordinal))
                Add(s[..^"_fast".Length]);
            else if (s.EndsWith("_slow", StringComparison.Ordinal))
                Add(s[..^"_slow".Length]);
            else
            {
                Add(s + "_fast");
                Add(s + "_slow");
            }
        }

        if (state.StartsWith("aim_airborne", StringComparison.Ordinal) ||
            state.StartsWith("look_airborne", StringComparison.Ordinal))
        {
            Add("airborne");
            return candidates;
        }

        var tokens = state.Split('_');
        string[] motion = ["move", "walk", "run", "jog", "locomote", "turn"];
        string[] dir = ["front", "right", "left", "back"];

        for (int index = 0; index < tokens.Length; index++)
        {
            if (!dir.Contains(tokens[index])) continue;
            // Scan backwards for a motion verb starting the run.
            for (int start = index - 1; start >= 0; start--)
            {
                if (!motion.Contains(tokens[start])) continue;
                AddFamily(string.Join('_', tokens[start..(index + 1)]));
                if (tokens[start] == "locomote" && start + 1 < index)
                    AddFamily(string.Join('_', tokens[(start + 1)..(index + 1)]));
            }
        }

        foreach (var prefix in (string[])["aim_", "look_", "acc_", "steer_"])
        {
            if (!state.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string stripped = state[prefix.Length..];
            AddFamily(stripped);
            foreach (var suffix in (string[])["_up", "_down", "_left", "_right"])
                if (stripped.EndsWith(suffix, StringComparison.Ordinal))
                    AddFamily(stripped[..^suffix.Length]);
        }

        Add("idle");
        return candidates;
    }
}
