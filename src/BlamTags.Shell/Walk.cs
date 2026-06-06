using System.Text;
using BlamTags;

namespace BlamTags.Shell;

/// <summary>Whether the walker recurses into a container.</summary>
public enum VisitControl { Descend, Skip }

/// <summary>
/// Per-node callbacks for <see cref="Walk"/>. Every hook has a default, so
/// implementations override only what they need. Mirrors the Rust
/// <c>FieldVisitor</c>. <c>depth</c> is 0 at root-level fields and increments
/// per struct / block-element / array-element descended into.
/// </summary>
public abstract class FieldVisitor
{
    public virtual bool IncludePadding => false;
    public virtual void VisitLeaf(string path, int depth, TagField field) { }
    public virtual VisitControl EnterResource(string path, int depth, TagField field, TagResource resource) => VisitControl.Descend;
    public virtual VisitControl EnterStruct(string path, int depth, TagField field) => VisitControl.Descend;
    public virtual VisitControl EnterBlock(string path, int depth, TagField field, TagBlock block) => VisitControl.Descend;
    public virtual VisitControl EnterArray(string path, int depth, TagField field, TagArray array) => VisitControl.Descend;
    public virtual VisitControl EnterElement(string path, int depth, int index, TagStruct elem) => VisitControl.Descend;
}

/// <summary>Walk every field under a struct, tracking the <c>/</c>-separated
/// path and nesting depth.</summary>
public static class Walk
{
    public static void Run(TagStruct start, FieldVisitor visitor)
    {
        var path = new StringBuilder();
        WalkStruct(start, path, 0, visitor);
    }

    private static void WalkStruct(TagStruct s, StringBuilder path, int depth, FieldVisitor v)
    {
        var fields = (v.IncludePadding ? s.FieldsAll() : s.Fields()).ToList();
        foreach (var field in fields)
        {
            int saved = path.Length;
            AppendSegment(path, field.Name);

            if (field.AsStruct() is { } nested)
            {
                if (v.EnterStruct(path.ToString(), depth, field) == VisitControl.Descend)
                    WalkStruct(nested, path, depth + 1, v);
            }
            else if (field.AsBlock() is { } block)
            {
                if (v.EnterBlock(path.ToString(), depth, field, block) == VisitControl.Descend)
                    WalkElements(block.Elements(), path, depth, v);
            }
            else if (field.AsArray() is { } array)
            {
                if (v.EnterArray(path.ToString(), depth, field, array) == VisitControl.Descend)
                    WalkElements(array.Elements(), path, depth, v);
            }
            else if (field.AsResource() is { } resource)
            {
                if (v.EnterResource(path.ToString(), depth, field, resource) == VisitControl.Descend
                    && resource.AsStruct() is { } header)
                    WalkStruct(header, path, depth + 1, v);
            }
            else
            {
                v.VisitLeaf(path.ToString(), depth, field);
            }

            path.Length = saved;
        }
    }

    private static void WalkElements(IEnumerable<TagStruct> elements, StringBuilder path, int depth, FieldVisitor v)
    {
        int i = 0;
        foreach (var elem in elements)
        {
            int saved = path.Length;
            path.Append('[').Append(i).Append(']');
            if (v.EnterElement(path.ToString(), depth + 1, i, elem) == VisitControl.Descend)
                WalkStruct(elem, path, depth + 1, v);
            path.Length = saved;
            i++;
        }
    }

    private static void AppendSegment(StringBuilder path, string name)
    {
        if (path.Length != 0) path.Append('/');
        path.Append(name);
    }
}
