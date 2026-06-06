namespace BlamTags;

public sealed partial class TagFile
{
    /// <summary>
    /// Create a fresh tag from a group schema JSON. The result has a header
    /// with <c>group_tag</c>/<c>group_version</c> from the schema, signature
    /// <c>BLAM</c>, everything else zeroed; a <c>tag!</c> stream with one
    /// zero-filled root element; and no optional streams.
    /// </summary>
    public static TagFile New(string schemaPath)
    {
        var (layout, meta) = TagLayout.FromJsonWithMeta(schemaPath);
        var header = new TagFileHeader
        {
            Pad = new byte[36],
            BuildVersion = 0,
            BuildNumber = 0,
            Version = 0,
            GroupTag = meta.Tag,
            GroupVersion = meta.Version,
            Checksum = 0,
            Signature = Tag.Of("BLAM"),
        };
        return new TagFile
        {
            Header = header,
            Endian = Endian.Le,
            TagStream = TagStream.NewDefault(layout),
        };
    }

    /// <summary>Attach an empty <c>want</c> (dependency-list) stream. No-op if
    /// one is already present.</summary>
    public void AddDependencyList(string schemaPath)
    {
        if (DependencyListStream is null)
            DependencyListStream = TagStream.NewDefault(TagLayout.FromJson(schemaPath));
    }

    /// <summary>Drop the <c>want</c> stream if present.</summary>
    public void RemoveDependencyList() => DependencyListStream = null;

    /// <summary>Attach an empty <c>info</c> (import-info) stream. No-op if present.</summary>
    public void AddImportInfo(string schemaPath)
    {
        if (ImportInfoStream is null)
            ImportInfoStream = TagStream.NewDefault(TagLayout.FromJson(schemaPath));
    }

    /// <summary>Drop the <c>info</c> stream if present.</summary>
    public void RemoveImportInfo() => ImportInfoStream = null;

    /// <summary>Attach an empty <c>assd</c> (asset-depot-storage) stream. No-op if present.</summary>
    public void AddAssetDepotStorage(string schemaPath)
    {
        if (AssetDepotStorageStream is null)
            AssetDepotStorageStream = TagStream.NewDefault(TagLayout.FromJson(schemaPath));
    }

    /// <summary>Drop the <c>assd</c> stream if present.</summary>
    public void RemoveAssetDepotStorage() => AssetDepotStorageStream = null;

    /// <summary>
    /// Rebuild the <c>want</c> dependency list from this tag's own data:
    /// every non-null <c>tag_reference</c> (duplicates preserved, in encounter
    /// order, <c>impo</c>-group filtered out), one entry per remaining ref.
    /// Creates the stream first via <paramref name="schemaPath"/> if missing.
    /// </summary>
    public void RebuildDependencyList(string schemaPath)
    {
        uint impo = Tag.Of("impo");
        var refs = new List<(uint Group, string Path)>();
        CollectTagReferences(Root, refs);
        refs.RemoveAll(r => r.Group == impo);

        AddDependencyList(schemaPath);
        var root = DependencyList ?? throw new InvalidOperationException("dependency_list stream missing after add");
        var depsField = root.FieldPath("dependencies") ?? throw new InvalidOperationException("want root missing `dependencies` field");
        var deps = depsField.AsBlock() ?? throw new InvalidOperationException("`dependencies` is not a block");

        deps.Clear();
        foreach (var (group, path) in refs)
        {
            int i = deps.AddElement();
            var elem = deps.Element(i)!;
            var df = elem.FieldPath("dependency") ?? throw new InvalidOperationException("dependency element missing `dependency` field");
            df.Set(new TagFieldData.TagReference(new TagReferenceData { GroupTagAndName = (group, path) }));
        }
    }

    private static void CollectTagReferences(TagStruct s, List<(uint, string)> outRefs)
    {
        foreach (var f in s.Fields())
        {
            switch (f.FieldType)
            {
                case TagFieldType.Struct:
                    if (f.AsStruct() is { } ns) CollectTagReferences(ns, outRefs);
                    break;
                case TagFieldType.Block:
                    if (f.AsBlock() is { } b)
                        foreach (var el in b.Elements()) CollectTagReferences(el, outRefs);
                    break;
                case TagFieldType.Array:
                    if (f.AsArray() is { } a)
                        foreach (var el in a.Elements()) CollectTagReferences(el, outRefs);
                    break;
                case TagFieldType.TagReference:
                    if (f.Value is TagFieldData.TagReference tr && tr.Value.GroupTagAndName is { } gp)
                        outRefs.Add(gp);
                    break;
            }
        }
    }
}
