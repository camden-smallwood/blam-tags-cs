using BlamTags;

namespace BlamTags.Shell.Commands;

/// <summary>
/// <c>list-animations</c> — enumerate the animations in a
/// <c>model_animation_graph</c> with header metadata only (no codec decode).
/// Inheriting jmads (zero local animations + a parent ref) print a notice.
/// </summary>
public static class ListAnimationsCommand
{
    public static int Run(CliContext ctx, Args args)
    {
        if (args.TakeFlag("--json")) throw new CliError("list-animations --json is not yet implemented");
        string file = args.Positional(0) ?? throw new CliError("list-animations: missing <file>");

        ctx.EnsureLoaded(file);
        var loaded = ctx.LoadedOrThrow("list-animations");
        Animation animation;
        try { animation = Animation.New(loaded.Tag); }
        catch (AnimationException e) { throw new CliError(e.Message); }

        if (animation.IsEmpty)
        {
            Console.WriteLine(animation.Parent is { } p ? $"(no animations — inherits from {p})" : "(no animations)");
            return 0;
        }

        Console.WriteLine($"{"idx",5}  {"cdc",3}  {"frame",5} {"node",4}  {"type",-10}  {"movement",-14}  {"blob",9}  name");
        foreach (var g in animation.Groups)
        {
            string codec = g.CodecByte is { } c ? c.ToString() : "-";
            string typ = g.AnimationType ?? "";
            string mvmt = g.MovementType ?? g.FrameInfoType ?? "";
            string warn = g.MovementTypeMismatch ? " !" : "";
            Console.WriteLine(
                $"{g.Index,5}  {codec,3}  {g.FrameCount,5} {g.NodeCount,4}  {typ,-10}  {mvmt,-14}{warn,2}  {g.Blob.Length,9}  {g.Name ?? "(unnamed)"}");
        }

        int unresolved = animation.UnresolvedCount;
        if (unresolved > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{unresolved} animation(s) have no resolved group_member (likely inherited)");
            if (animation.Parent is { } p) Console.WriteLine($"parent: {p}");
        }
        return 0;
    }
}
