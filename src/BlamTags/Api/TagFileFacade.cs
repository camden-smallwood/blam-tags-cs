namespace BlamTags;

public sealed partial class TagFile
{
    /// <summary>What kind of tag this is — group tag + group version.</summary>
    public TagGroup Group => new(Header.GroupTag, Header.GroupVersion);

    /// <summary>Schema facade — navigate the definitions tree.</summary>
    public TagDefinitions Definitions => new(TagStream.Layout);

    /// <summary>The tag's root element — the single element of the <c>tag!</c>
    /// stream's root block.</summary>
    public TagStruct Root =>
        StreamRoot(TagStream) ?? throw new InvalidOperationException("tag has no root element");

    /// <summary>Root element of the <c>want</c> (dependency-list) stream, if present.</summary>
    public TagStruct? DependencyList => DependencyListStream is null ? null : StreamRoot(DependencyListStream);

    /// <summary>Root element of the <c>info</c> (import-info) stream, if present.</summary>
    public TagStruct? ImportInfo => ImportInfoStream is null ? null : StreamRoot(ImportInfoStream);

    /// <summary>Root element of the <c>assd</c> (asset-depot-storage) stream, if present.</summary>
    public TagStruct? AssetDepotStorage =>
        AssetDepotStorageStream is null ? null : StreamRoot(AssetDepotStorageStream);

    private static TagStruct? StreamRoot(TagStream stream)
    {
        if (stream.Data.Elements.Count == 0)
            return null;
        int size = stream.Data.ElementSize(stream.Layout);
        var region = new StructRegion(stream.Data.Elements[0], stream.Data.RawData, 0, size);
        return new TagStruct(stream.Layout, region, stream.Data.Endian);
    }
}
