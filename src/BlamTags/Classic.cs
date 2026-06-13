using System.Buffers.Binary;

namespace BlamTags;

/// <summary>
/// Classic (Halo CE / Halo 2) loose-tag decoder + encoder.
///
/// Gen-1/2 tags are <em>not</em> MCC self-describing containers: there is no
/// embedded <c>blay</c>/<c>tgly</c> layout stream and no <c>tgbl</c>/<c>tgst</c>
/// chunking. The body is flat data laid out depth-first, interpreted entirely
/// by an <em>external</em> field definition (synthesized from a JSON schema via
/// <see cref="TagLayout.FromJson"/>, ported from the HABT XML layouts).
///
/// The decoded form is the same <see cref="TagBlockData"/>/<see cref="TagStructData"/>
/// model the MCC reader produces, so the entire downstream API + extractors work
/// unchanged (field byte order comes from the engine: CE big-endian, H2 little).
/// Decoder and encoder share one depth-first traversal and preserve every byte
/// verbatim, so read → write is byte-exact by construction.
/// </summary>
public static class Classic
{
    // ---- engine classification ----

    /// <summary>Numeric body fields (ints/floats/counts) byte order: CE is
    /// big-endian (Xbox-360-derived), H2 little-endian (x86).</summary>
    public static Endian BodyEndian(this ClassicEngine engine) =>
        engine == ClassicEngine.HaloCe ? Endian.Be : Endian.Le;

    private static bool IsHalo2(this ClassicEngine e) => e != ClassicEngine.HaloCe;

    /// <summary>12-byte legacy block/struct headers (<c>4s2hi</c>: version +
    /// count are i16) instead of the 16-byte form. Only Halo 2 V1.</summary>
    private static bool LegacyHeader(this ClassicEngine e) => e == ClassicEngine.Halo2V1;

    /// <summary><c>old_string_id</c> is a 32-byte inline string rather than a
    /// 4-byte length + trailing bytes. Halo 2 V1/V2.</summary>
    private static bool LegacyStrings(this ClassicEngine e) =>
        e is ClassicEngine.Halo2V1 or ClassicEngine.Halo2V2;

    /// <summary><c>useless_pad</c> occupies its real length rather than 0
    /// bytes. Halo 2 V1/V2/V3.</summary>
    private static bool LegacyPadding(this ClassicEngine e) =>
        e is ClassicEngine.Halo2V1 or ClassicEngine.Halo2V2 or ClassicEngine.Halo2V3;

    // ---- header parse ----

    /// <summary>Parse the 64-byte classic header and classify the engine from
    /// the offset-60 signature word. Returns null when the signature isn't a
    /// known classic engine (caller falls through to the MCC reader).</summary>
    public static (ClassicHeader Header, ClassicEngine Engine)? ParseHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 64)
            return null;

        var rawEngine = bytes.Slice(60, 4).ToArray();
        var rawGroup = bytes.Slice(36, 4).ToArray();

        uint be = BinaryPrimitives.ReadUInt32BigEndian(rawEngine);

        ClassicEngine engine;
        byte[] groupTag;
        bool engineLe;
        if (be == Tag.Of("blam"))
        {
            engine = ClassicEngine.HaloCe;
            groupTag = rawGroup;
            engineLe = false;
        }
        else
        {
            // H2: the on-disk word is little-endian; reversing gives the
            // logical engine tag. Each selects a sub-version.
            var rev = new[] { rawEngine[3], rawEngine[2], rawEngine[1], rawEngine[0] };
            uint logical = BinaryPrimitives.ReadUInt32BigEndian(rev);
            ClassicEngine? h2 = logical == Tag.Of("ambl") ? ClassicEngine.Halo2V1
                : logical == Tag.Of("LAMB") ? ClassicEngine.Halo2V2
                : logical == Tag.Of("MLAB") ? ClassicEngine.Halo2V3
                : logical == Tag.Of("BLM!") ? ClassicEngine.Halo2V4
                : null;
            if (h2 is null)
                return null;
            engine = h2.Value;
            groupTag = new[] { rawGroup[3], rawGroup[2], rawGroup[1], rawGroup[0] };
            engineLe = true;
        }

        byte[] engineLogical = engineLe
            ? new[] { rawEngine[3], rawEngine[2], rawEngine[1], rawEngine[0] }
            : rawEngine;

        // Version (offset 56) + checksum (offset 40) byte order matches the
        // engine's signature order.
        var vb = bytes.Slice(56, 2);
        var cb = bytes.Slice(40, 4);
        ushort version;
        uint checksum;
        if (engine == ClassicEngine.HaloCe)
        {
            version = BinaryPrimitives.ReadUInt16BigEndian(vb);
            checksum = BinaryPrimitives.ReadUInt32BigEndian(cb);
        }
        else
        {
            version = BinaryPrimitives.ReadUInt16LittleEndian(vb);
            checksum = BinaryPrimitives.ReadUInt32LittleEndian(cb);
        }

        var header = new ClassicHeader
        {
            GroupTag = groupTag,
            Engine = engineLogical,
            Version = version,
            Checksum = checksum,
        };
        return (header, engine);
    }

    // ---- public entry points ----

    /// <summary>Read a complete classic tag into a <see cref="TagFile"/>, using
    /// <paramref name="layout"/> (synthesized from the group's JSON def) for
    /// structure. The result behaves like any MCC-loaded tag.</summary>
    public static TagFile ReadClassicTagFile(byte[] bytes, TagLayout layout)
    {
        var parsed = ParseHeader(bytes) ?? throw TagReadException.ClassicDecode("not a classic (CE/H2) tag header");
        var (header, engine) = parsed;
        byte[] body = bytes[64..];
        var root = ReadClassicBody(body, layout, engine);

        var fileHeader = new TagFileHeader
        {
            Pad = new byte[36],
            BuildVersion = 0,
            BuildNumber = 0,
            Version = header.Version,
            GroupTag = BinaryPrimitives.ReadUInt32BigEndian(header.GroupTag),
            GroupVersion = 0,
            // Preserve the stored checksum; the writer recomputes unless it's
            // the 0xFFFFFFFF "unchecksummed" sentinel.
            Checksum = header.Checksum,
            Signature = Tag.Of("BLAM"),
        };

        return new TagFile
        {
            Header = fileHeader,
            Endian = engine.BodyEndian(),
            TagStream = new TagStream { Layout = layout, Data = root },
            ClassicEngine = engine,
            ClassicHeaderBytes = bytes[..64],
        };
    }

    /// <summary>Decode then re-encode a classic tag body, returning the
    /// re-encoded bytes (the byte-exact roundtrip gate).</summary>
    public static byte[] ClassicRoundtrip(byte[] body, TagLayout layout, ClassicEngine engine)
    {
        var root = ReadClassicBody(body, layout, engine);
        return WriteClassicBody(layout, root, engine);
    }

    /// <summary>Serialize a complete classic tag: the original 64-byte header
    /// (build strings etc. preserved verbatim) with the checksum recomputed
    /// over the re-encoded body, followed by that body.
    ///
    /// The checksum is the proper CRC32 (see <see cref="ClassicChecksum"/>) —
    /// a modding tool should emit a correct checksum. The only exception is the
    /// <c>0xFFFFFFFF</c> "unchecksummed" sentinel (HEK tags), preserved as a
    /// deliberate marker. A handful of shipped tags carry stale/incorrect
    /// checksums from the original toolchain; we (correctly) rewrite those to
    /// the real CRC, so a full-file round-trip of those specific tags differs
    /// only in the corrected checksum word.</summary>
    internal static byte[] WriteClassicTag(TagFile file, ClassicEngine engine, byte[] header)
    {
        byte[] body = WriteClassicBody(file.TagStream.Layout, file.TagStream.Data, engine);
        uint checksum = file.Header.Checksum == 0xFFFFFFFF ? 0xFFFFFFFF : ClassicChecksum(body);
        var checksumBytes = new byte[4];
        if (engine == ClassicEngine.HaloCe)
            BinaryPrimitives.WriteUInt32BigEndian(checksumBytes, checksum);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(checksumBytes, checksum);

        var outBytes = new byte[64 + body.Length];
        Array.Copy(header, 0, outBytes, 0, 64);
        Array.Copy(checksumBytes, 0, outBytes, 40, 4);
        Array.Copy(body, 0, outBytes, 64, body.Length);
        return outBytes;
    }

    // ---- endian-aware primitive reads ----

    private static uint RdU32(byte[] raw, int off, Endian e) =>
        e == Endian.Le ? BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(off, 4))
                       : BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(off, 4));

    private static int RdI32(byte[] raw, int off, Endian e) => (int)RdU32(raw, off, e);

    private static void WrU32(byte[] raw, int off, uint v, Endian e)
    {
        if (e == Endian.Le) BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(off, 4), v);
        else BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(off, 4), v);
    }

    // ---- versioned-layout helpers ----

    /// <summary>Extract the version field from a 12/16-byte Halo 2 header
    /// (little-endian): i32 at +4 for the modern form, i16 at +4 for the
    /// legacy 12-byte form.</summary>
    private static uint HeaderVersion(byte[] h, ClassicEngine engine) =>
        engine.LegacyHeader()
            ? (uint)(short)BinaryPrimitives.ReadInt16LittleEndian(h.AsSpan(4, 2))
            : BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4, 4));

    /// <summary>Struct index for statically sizing an inline struct field. An
    /// untagged inline struct is always on-disk version 0; a tagged one uses
    /// the base (latest) — the decoder overrides per live element.</summary>
    private static uint InlineStructStaticIndex(TagLayout layout, uint baseIndex) =>
        layout.StructTag(baseIndex) != 0 ? baseIndex : layout.ResolveVersionVariant(baseIndex, 0);

    /// <summary>On-disk size (bytes) of one struct field for <paramref name="engine"/>,
    /// using classic packed layout (no alignment). <c>useless_pad</c> and
    /// <c>old_string_id</c> change width across the legacy H2 variants.</summary>
    private static int ClassicFieldSize(TagLayout layout, TagFieldLayout field, ClassicEngine engine)
    {
        switch (field.FieldType)
        {
            case TagFieldType.Terminator:
                return 0;
            case TagFieldType.Struct:
                return ClassicStructSize(layout, InlineStructStaticIndex(layout, field.Definition), engine);
            case TagFieldType.Array:
            {
                var a = layout.ArrayLayouts[(int)field.Definition];
                uint esi = layout.ResolveVersionVariant(a.StructIndex, 0);
                return ClassicStructSize(layout, esi, engine) * (int)a.Count;
            }
            case TagFieldType.Pad:
            case TagFieldType.Skip:
                return (int)field.Definition;
            case TagFieldType.UselessPad:
                return engine.LegacyPadding() ? (int)field.Definition : 0;
            case TagFieldType.OldStringId:
                return engine.LegacyStrings() ? 32 : 4;
            case TagFieldType.Custom:
                return (int)field.Definition;
            default:
                return (int)layout.FieldTypes[(int)field.TypeIndex].Size;
        }
    }

    /// <summary>On-disk size of a struct's fixed region for <paramref name="engine"/>
    /// (sum of its fields, classic packed).</summary>
    private static int ClassicStructSize(TagLayout layout, uint structIndex, ClassicEngine engine)
    {
        int size = 0;
        int fi = (int)layout.StructLayouts[(int)structIndex].FirstFieldIndex;
        while (true)
        {
            var f = layout.Fields[fi];
            if (f.FieldType == TagFieldType.Terminator)
                break;
            size += ClassicFieldSize(layout, f, engine);
            fi++;
        }
        return size;
    }

    /// <summary>Validate a <c>(count, elementSize)</c> pair and return the
    /// total fixed-region byte length, guarding the allocation blow-ups a
    /// desynced cursor can trigger.</summary>
    private static int CheckedBlockExtent(long count, long elemSize)
    {
        if (elemSize == 0 && count != 0)
            throw TagReadException.ClassicDecode($"corrupt classic block header: count={count} element_size={elemSize}");
        long total = count * elemSize;
        if (count < 0 || elemSize < 0 || total < 0 || total > int.MaxValue)
            throw TagReadException.ClassicDecode($"corrupt classic block header: count={count} element_size={elemSize}");
        return (int)total;
    }

    // ---- cursor ----

    private sealed class Cursor(byte[] data)
    {
        public readonly byte[] Data = data;
        public int Pos;

        public byte[] Take(int n, string context)
        {
            long end = (long)Pos + n;
            if (n < 0 || end > Data.Length)
                throw TagReadException.ClassicDecode(
                    $"unexpected EOF reading {context}: need {n} bytes, have {Data.Length - Pos}");
            var slice = Data[Pos..(int)end];
            Pos = (int)end;
            return slice;
        }
    }

    // ---- header reads ----

    /// <summary>Read a Halo 2 block header (little-endian). Modern (V2/V3/V4):
    /// 16 bytes <c>4cc + version(i32) + count(i32) + size(i32)</c>. Legacy V1:
    /// 12 bytes <c>4cc + version(i16) + count(i16) + size(i32)</c>.</summary>
    private static (byte[] Header, int Count, int Size) ReadH2BlockHeader(Cursor cur, ClassicEngine engine)
    {
        if (engine.LegacyHeader())
        {
            byte[] h = cur.Take(12, "h2 block header (legacy)");
            int count = BinaryPrimitives.ReadInt16LittleEndian(h.AsSpan(6, 2));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8, 4));
            return (h, count, size);
        }
        else
        {
            byte[] h = cur.Take(16, "h2 block header");
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(8, 4));
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12, 4));
            return (h, count, size);
        }
    }

    /// <summary>Halo 2 only: if struct <paramref name="childSi"/> is tagged and
    /// the next 4 trailing bytes match its tag (stored little-endian), consume
    /// + return its block-style header. Otherwise none.</summary>
    private static byte[]? ReadH2StructHeader(TagLayout layout, uint childSi, Cursor cur, ClassicEngine engine)
    {
        if (!engine.IsHalo2())
            return null;
        uint tag = layout.StructTag(childSi);
        if (tag == 0)
            return null;
        if (cur.Data.Length - cur.Pos >= 4
            && BinaryPrimitives.ReadUInt32LittleEndian(cur.Data.AsSpan(cur.Pos, 4)) == tag)
        {
            int n = engine.LegacyHeader() ? 12 : 16;
            return cur.Take(n, "h2 struct header");
        }
        return null;
    }

    // ---- decode ----

    /// <summary>Decode a classic tag body (everything after the 64-byte header)
    /// into the root <see cref="TagBlockData"/>.</summary>
    internal static TagBlockData ReadClassicBody(byte[] body, TagLayout layout, ClassicEngine engine)
    {
        Endian endian = engine.BodyEndian();
        var cur = new Cursor(body);

        uint rootBlockIndex = layout.Header.TagGroupBlockIndex;
        uint structIndex = layout.BlockLayouts[(int)rootBlockIndex].StructIndex;

        byte[]? header;
        int count, elemSize;
        if (engine.IsHalo2())
        {
            var (h, c, s) = ReadH2BlockHeader(cur, engine);
            (header, count, elemSize) = (h, c, s);
        }
        else
        {
            (header, count, elemSize) = (null, 1, layout.StructLayouts[(int)structIndex].Size);
        }

        uint version = header is null ? 0 : HeaderVersion(header, engine);
        structIndex = layout.ResolveVersionVariant(structIndex, version);

        int total = CheckedBlockExtent(count, elemSize);
        byte[] rawData = cur.Take(total, "root struct");
        var elements = new List<TagStructData>(count);
        for (int i = 0; i < count; i++)
        {
            byte[] elemRaw = rawData[(i * elemSize)..((i + 1) * elemSize)];
            var sub = DecodeStructElement(layout, structIndex, elemRaw, cur, engine, endian);
            elements.Add(new TagStructData { StructIndex = structIndex, SubChunks = sub });
        }

        // Trailing appended sample/cache data (Halo 2 root only) — preserved
        // verbatim. CE has no such appendage, so CE trailing is a real desync.
        byte[]? classicTrailing = null;
        if (cur.Pos != body.Length)
        {
            if (!engine.IsHalo2())
                throw TagReadException.ClassicDecode($"classic layout walk consumed {cur.Pos} of {body.Length} body bytes");
            classicTrailing = body[cur.Pos..];
        }

        return new TagBlockData
        {
            BlockIndex = rootBlockIndex,
            Flags = 0,
            RawData = rawData,
            Endian = endian,
            Elements = elements,
            ClassicBlockHeader = header,
            ClassicTrailing = classicTrailing,
        };
    }

    private static List<TagSubChunkEntry> DecodeStructElement(
        TagLayout layout, uint structIndex, byte[] raw, Cursor cur, ClassicEngine engine, Endian endian)
    {
        int off = 0;
        return DecodeStructTrailing(layout, structIndex, raw, ref off, cur, engine, endian);
    }

    /// <summary>Walk a struct's fields in order, pulling each field's
    /// trailing/variable data from the cursor and building the sub-chunk list.
    /// <paramref name="raw"/> is the enclosing element's fixed bytes and
    /// <paramref name="off"/> the running position into it (shared with the
    /// parent so a version-resized inline struct advances the cursor exactly).</summary>
    private static List<TagSubChunkEntry> DecodeStructTrailing(
        TagLayout layout, uint structIndex, byte[] raw, ref int off, Cursor cur, ClassicEngine engine, Endian endian)
    {
        var entries = new List<TagSubChunkEntry>();
        int fi = (int)layout.StructLayouts[(int)structIndex].FirstFieldIndex;
        while (true)
        {
            var field = layout.Fields[fi];
            if (field.FieldType == TagFieldType.Terminator)
                break;
            // Truncated element: once past the on-disk element end, every
            // remaining field is absent (HABT clamps reads to the available
            // bytes). Offsets only grow, so stop.
            if (off >= raw.Length)
                break;

            if (field.FieldType == TagFieldType.Struct)
            {
                uint baseSi = field.Definition;
                byte[]? structHeader = ReadH2StructHeader(layout, baseSi, cur, engine);
                uint version = structHeader is null ? 0 : HeaderVersion(structHeader, engine);
                uint childSi = layout.ResolveVersionVariant(baseSi, version);
                var childSub = DecodeStructTrailing(layout, childSi, raw, ref off, cur, engine, endian);
                entries.Add(new TagSubChunkEntry
                {
                    FieldIndex = (uint)fi,
                    Content = new TagSubChunkContent.StructContent(new TagStructData
                    {
                        StructIndex = childSi,
                        SubChunks = childSub,
                        ClassicStructHeader = structHeader,
                    }),
                });
                fi++;
                continue;
            }
            if (field.FieldType == TagFieldType.Array)
            {
                var array = layout.ArrayLayouts[(int)field.Definition];
                int count = (int)array.Count;
                uint asi = layout.ResolveVersionVariant(array.StructIndex, 0);
                var elems = new List<TagStructData>(count);
                for (int i = 0; i < count; i++)
                {
                    var sub = DecodeStructTrailing(layout, asi, raw, ref off, cur, engine, endian);
                    elems.Add(new TagStructData { StructIndex = asi, SubChunks = sub });
                }
                entries.Add(new TagSubChunkEntry
                {
                    FieldIndex = (uint)fi,
                    Content = new TagSubChunkContent.ArrayContent(elems),
                });
                fi++;
                continue;
            }

            int fsize = ClassicFieldSize(layout, field, engine);
            // A sub-chunk field needs its 4-byte inline slot fully present to
            // know whether/how much trailing data to read. At a truncated
            // tail that slot can fall partly past the end — treat as absent.
            bool needsInlineSlot =
                field.FieldType is TagFieldType.Block or TagFieldType.Data
                    or TagFieldType.TagReference or TagFieldType.StringId
                || (field.FieldType == TagFieldType.OldStringId && !engine.LegacyStrings());
            if (needsInlineSlot && off + 4 > raw.Length)
            {
                off += fsize;
                fi++;
                continue;
            }

            switch (field.FieldType)
            {
                case TagFieldType.Block:
                {
                    int count = (int)RdU32(raw, off, endian);
                    var block = DecodeBlock(layout, field.Definition, count, cur, engine, endian);
                    entries.Add(new TagSubChunkEntry
                    {
                        FieldIndex = (uint)fi,
                        Content = new TagSubChunkContent.BlockContent(block),
                    });
                    break;
                }
                case TagFieldType.Data:
                {
                    int len = (int)RdU32(raw, off, endian);
                    byte[] blob = cur.Take(len, "data blob");
                    entries.Add(new TagSubChunkEntry
                    {
                        FieldIndex = (uint)fi,
                        Content = new TagSubChunkContent.DataContent(blob),
                    });
                    break;
                }
                case TagFieldType.TagReference:
                {
                    int group = RdI32(raw, off, endian);
                    // Length is signed: H2 null refs carry a valid group but
                    // length -1 (CE used group == -1). Non-positive ⇒ no path.
                    int lenI = off + 12 <= raw.Length ? RdI32(raw, off + 8, endian) : 0;
                    int len = lenI > 0 ? lenI : 0;
                    // MCC TagReference payload = group_tag(4) + null-term path.
                    // The classic inline header keeps the group; only path+NUL
                    // is trailing. Prepend the inline group bytes.
                    var payload = new List<byte>(raw[off..(off + 4)]);
                    if (group != -1 && len != 0)
                        payload.AddRange(cur.Take(len + 1, "tag_reference path"));
                    entries.Add(new TagSubChunkEntry
                    {
                        FieldIndex = (uint)fi,
                        Content = new TagSubChunkContent.TagReferenceContent(payload.ToArray()),
                    });
                    break;
                }
                case TagFieldType.StringId:
                {
                    int len = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(off + 2, 2));
                    byte[] s = cur.Take(len, "string_id value");
                    entries.Add(new TagSubChunkEntry
                    {
                        FieldIndex = (uint)fi,
                        Content = new TagSubChunkContent.StringIdContent(s),
                    });
                    break;
                }
                case TagFieldType.OldStringId:
                {
                    if (engine.LegacyStrings())
                    {
                        // 32-byte inline null-terminated string living in the
                        // fixed bytes — no trailing data, no sub-chunk.
                    }
                    else
                    {
                        int len = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(off + 2, 2));
                        byte[] s = cur.Take(len, "old_string_id value");
                        entries.Add(new TagSubChunkEntry
                        {
                            FieldIndex = (uint)fi,
                            Content = new TagSubChunkContent.OldStringIdContent(s),
                        });
                    }
                    break;
                }
                // Everything else lives entirely in the fixed bytes.
                default:
                    break;
            }
            off += fsize;
            fi++;
        }
        return entries;
    }

    /// <summary>Decode a tag block's <paramref name="inlineCount"/> elements:
    /// all element fixed bytes contiguous, then each element's nested variable
    /// data in element order.</summary>
    private static TagBlockData DecodeBlock(
        TagLayout layout, uint blockIndex, int inlineCount, Cursor cur, ClassicEngine engine, Endian endian)
    {
        uint structIndex = layout.BlockLayouts[(int)blockIndex].StructIndex;

        if (inlineCount == 0)
        {
            return new TagBlockData
            {
                BlockIndex = blockIndex,
                Flags = 0,
                RawData = [],
                Endian = endian,
                Elements = [],
            };
        }

        byte[]? header;
        int count, elemSize;
        if (engine.IsHalo2())
        {
            var (h, c, s) = ReadH2BlockHeader(cur, engine);
            (header, count, elemSize) = (h, c, s);
        }
        else
        {
            (header, count, elemSize) = (null, inlineCount, layout.StructLayouts[(int)structIndex].Size);
        }

        uint version = header is null ? 0 : HeaderVersion(header, engine);
        structIndex = layout.ResolveVersionVariant(structIndex, version);

        int total = CheckedBlockExtent(count, elemSize);
        byte[] rawData = cur.Take(total, "block elements");

        var elements = new List<TagStructData>(count);
        for (int i = 0; i < count; i++)
        {
            byte[] elemRaw = rawData[(i * elemSize)..((i + 1) * elemSize)];
            var sub = DecodeStructElement(layout, structIndex, elemRaw, cur, engine, endian);
            elements.Add(new TagStructData { StructIndex = structIndex, SubChunks = sub });
        }

        return new TagBlockData
        {
            BlockIndex = blockIndex,
            Flags = 0,
            RawData = rawData,
            Endian = endian,
            Elements = elements,
            ClassicBlockHeader = header,
        };
    }

    // ---- encode ----

    /// <summary>Re-encode a classic tag body from the root block. Inline
    /// counts/lengths are derived from the live model before each struct's
    /// fixed bytes are emitted, so structural edits serialize correctly. For
    /// an unmodified tree this is a no-op, so read → write stays byte-exact.</summary>
    internal static byte[] WriteClassicBody(TagLayout layout, TagBlockData root, ClassicEngine engine)
    {
        var outBytes = new List<byte>();
        EncodeBlock(layout, root, engine, outBytes);
        if (root.ClassicTrailing is { } trailing)
            outBytes.AddRange(trailing);
        return outBytes.ToArray();
    }

    private static void EncodeBlock(TagLayout layout, TagBlockData block, ClassicEngine engine, List<byte> outBytes)
    {
        int elemCount = block.Elements.Count;
        // On-disk element size from raw_data (preserved verbatim) so the H2
        // header's authoritative size is honoured.
        int sz = elemCount > 0 ? block.RawData.Length / elemCount : 0;
        byte[] raw = (byte[])block.RawData.Clone();
        if (sz > 0)
        {
            for (int i = 0; i < elemCount; i++)
                SyncFixedCountsElement(layout, raw, i * sz, sz, block.Elements[i], engine, block.Endian);
        }

        // Halo 2: re-emit the block header, re-syncing count from the live
        // element count. Modern 16-byte: count = LE i32 at +8; legacy 12-byte:
        // LE i16 at +6. Empty H2 blocks carry no header.
        if (block.ClassicBlockHeader is { } hdr)
        {
            byte[] h = (byte[])hdr.Clone();
            if (h.Length == 12)
                BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6, 2), (ushort)elemCount);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8, 4), (uint)elemCount);
            outBytes.AddRange(h);
        }
        outBytes.AddRange(raw);
        foreach (var elem in block.Elements)
            EncodeStructTrailing(layout, elem, engine, outBytes);
    }

    private static void SyncFixedCountsElement(
        TagLayout layout, byte[] raw, int baseOff, int size, TagStructData elem, ClassicEngine engine, Endian endian)
    {
        // Operate on the element's slice [baseOff, baseOff+size). We pass the
        // whole `raw` and bound the walk by `size`.
        int off = 0;
        SyncFixedCounts(layout, raw, baseOff, ref off, size, elem, engine, endian);
    }

    /// <summary>Rewrite a struct's inline count/length slots from the live
    /// model, recursing through inline structs/arrays on the shared offset.</summary>
    private static void SyncFixedCounts(
        TagLayout layout, byte[] raw, int baseOff, ref int off, int size, TagStructData elem, ClassicEngine engine, Endian endian)
    {
        int fi = (int)layout.StructLayouts[(int)elem.StructIndex].FirstFieldIndex;
        while (true)
        {
            var field = layout.Fields[fi];
            if (field.FieldType == TagFieldType.Terminator)
                break;
            if (off >= size)
                break;
            var content = elem.SubChunkFor(fi);

            switch (field.FieldType)
            {
                case TagFieldType.Struct when content is TagSubChunkContent.StructContent sc:
                    SyncFixedCounts(layout, raw, baseOff, ref off, size, sc.Struct, engine, endian);
                    fi++;
                    continue;
                case TagFieldType.Array when content is TagSubChunkContent.ArrayContent ac:
                    foreach (var child in ac.Elements)
                        SyncFixedCounts(layout, raw, baseOff, ref off, size, child, engine, endian);
                    fi++;
                    continue;
                case TagFieldType.Block when content is TagSubChunkContent.BlockContent bc:
                    // CE: inline count is authoritative. H2: element count
                    // lives in the block's own trailing header (synced in
                    // EncodeBlock); this inline word is runtime garbage —
                    // preserve verbatim.
                    if (!engine.IsHalo2())
                        WrU32(raw, baseOff + off, (uint)bc.Block.Elements.Count, endian);
                    break;
                case TagFieldType.Data when content is TagSubChunkContent.DataContent dc:
                    WrU32(raw, baseOff + off, (uint)dc.Payload.Length, endian);
                    break;
                case TagFieldType.TagReference when content is TagSubChunkContent.TagReferenceContent tr:
                    // 16-byte inline: group(4) + ptr(4) + length(4) + tag_id(4).
                    // Payload = group(4) + path + NUL. Sync group + path length.
                    if (tr.Payload.Length >= 4)
                        Array.Copy(tr.Payload, 0, raw, baseOff + off, 4);
                    if (tr.Payload.Length > 4)
                        WrU32(raw, baseOff + off + 8, (uint)(tr.Payload.Length - 5), endian);
                    break;
                case TagFieldType.StringId when content is TagSubChunkContent.StringIdContent si:
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(baseOff + off + 2, 2), (ushort)si.Payload.Length);
                    break;
                case TagFieldType.OldStringId when content is TagSubChunkContent.OldStringIdContent osi:
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(baseOff + off + 2, 2), (ushort)osi.Payload.Length);
                    break;
            }
            off += ClassicFieldSize(layout, field, engine);
            fi++;
        }
    }

    private static void EncodeStructTrailing(TagLayout layout, TagStructData elem, ClassicEngine engine, List<byte> outBytes)
    {
        int fi = (int)layout.StructLayouts[(int)elem.StructIndex].FirstFieldIndex;
        while (true)
        {
            var field = layout.Fields[fi];
            if (field.FieldType == TagFieldType.Terminator)
                break;
            bool hasTrailing = field.FieldType is TagFieldType.Block or TagFieldType.Data
                or TagFieldType.TagReference or TagFieldType.StringId or TagFieldType.OldStringId
                or TagFieldType.Struct or TagFieldType.Array;
            if (hasTrailing && elem.SubChunkFor(fi) is { } content)
            {
                switch (content)
                {
                    case TagSubChunkContent.BlockContent bc:
                        EncodeBlock(layout, bc.Block, engine, outBytes);
                        break;
                    case TagSubChunkContent.DataContent dc:
                        outBytes.AddRange(dc.Payload);
                        break;
                    case TagSubChunkContent.TagReferenceContent tr:
                        // Payload = group(4) + path + NUL; group already in
                        // raw_data, so only path+NUL is trailing.
                        if (tr.Payload.Length > 4)
                            outBytes.AddRange(tr.Payload[4..]);
                        break;
                    case TagSubChunkContent.StringIdContent si:
                        outBytes.AddRange(si.Payload);
                        break;
                    case TagSubChunkContent.OldStringIdContent osi:
                        outBytes.AddRange(osi.Payload);
                        break;
                    case TagSubChunkContent.StructContent scc:
                        if (scc.Struct.ClassicStructHeader is { } sh)
                            outBytes.AddRange(sh);
                        EncodeStructTrailing(layout, scc.Struct, engine, outBytes);
                        break;
                    case TagSubChunkContent.ArrayContent ac:
                        foreach (var child in ac.Elements)
                            EncodeStructTrailing(layout, child, engine, outBytes);
                        break;
                }
            }
            fi++;
        }
    }

    // ---- checksum ----

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i;
            for (int j = 0; j < 8; j++)
                r = (r & 1) == 1 ? (r >> 1) ^ 0xEDB88320 : r >> 1;
            t[i] = r;
        }
        return t;
    }

    /// <summary>Classic tag checksum: CRC32 (poly 0xEDB88320, init 0xFFFFFFFF)
    /// over the body, with <em>no</em> final XOR inversion (matches HABT
    /// <c>checksum_calculate</c>).</summary>
    public static uint ClassicChecksum(ReadOnlySpan<byte> body)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in body)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c;
    }
}
