using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SkyrimCraftingTool.Services
{
    // One drawable piece of a NIF: the four arrays WPF's MeshGeometry3D takes, and nothing else.
    //
    // Deliberately flat float/int arrays rather than Point3D lists: this is what gets written to a
    // BLOB and read back, and a struct list would be converted twice for no gain.
    public sealed class NifShape
    {
        public string Name = "";

        // 3 floats per vertex.
        public float[] Positions = Array.Empty<float>();

        // 3 floats per vertex, or empty when the mesh carries no normals (then the renderer computes
        // them). Not null - an empty array is one less thing every caller has to check.
        public float[] Normals = Array.Empty<float>();

        // 2 floats per vertex, or empty. Kept even though nothing textures yet: it falls out of the
        // same vertex record for free, and leaving it out would mean re-parsing every mesh for stage 4.
        public float[] Uvs = Array.Empty<float>();

        // 3 floats per vertex each, or empty.
        //
        // WHAT THEY ARE FOR: a normal map stores its vectors in TANGENT SPACE - relative to the surface,
        // not to the world - so turning one into a usable normal needs the surface's own frame at that
        // vertex. Normal, tangent and bitangent are that frame. Without them a normal map can only be
        // applied as a rough perturbation, which shows the detail but puts its light in the wrong place.
        //
        // SKYRIM SCATTERS THE BITANGENT across three different fields of the packed vertex - X sits in
        // the position block, Y in the normal block, Z in the tangent block - so it is read out rather
        // than reconstructed. A cross product would be close but would lose the handedness, and a
        // flipped bitangent inverts every bump on the model.
        public float[] Tangents = Array.Empty<float>();
        public float[] Bitangents = Array.Empty<float>();

        public bool HasTangentFrame => Normals.Length > 0 && Tangents.Length > 0 && Bitangents.Length > 0;

        // 3 indices per triangle.
        public int[] Indices = Array.Empty<int>();

        // Was this shape moved by a PARENT node, rather than only by its own transform?
        //
        // Carried as a flag because node composition is the likeliest remaining source of a
        // wrongly-placed mesh, and a wrongly-placed mesh is otherwise indistinguishable from a
        // correctly-placed ugly one. Measured over a real load order: 390 of 11.393 shapes have a
        // non-identity parent chain, 66 of them move by more than one unit and 16 by more than 50. So
        // when something looks displaced, this says whether this code had a hand in it.
        public bool MovedByParentNode;

        // The diffuse texture this shape names, normalised, or empty.
        //
        // Only the diffuse one of the nine slots a BSShaderTextureSet carries: the rest are normal,
        // specular, glow and environment maps, which a shape inspector has no use for. Measured on a
        // real load order, 8.332 of 9.090 shapes name one, and they collapse onto 1.578 distinct files.
        public string DiffuseTexture = "";

        // The normal map, slot 1 of the same texture set.
        //
        // Kept not for its RGB - Viewport3D cannot do normal mapping - but for its ALPHA, which is where
        // Skyrim traditionally stores the specular mask. Measured over a real load order: slot 1 is
        // present for 97,3 % of shapes and 39 of 40 sampled normal maps have a varying alpha, against
        // 57,0 % for the separate _m.dds. It is by far the better source for "where should this shine".
        public string NormalTexture = "";

        // Slot 5, the environment mask - the _m.dds. Present for 57,0 % of shapes.
        //
        // A SECOND answer to "where should this shine", and a different one: the normal map's alpha is
        // the direct specular mask, while this says where the material reflects its surroundings. On a
        // latex garment the alpha marks only the seams and this one covers the whole suit, which is why
        // neither alone is enough.
        public string EnvironmentMask = "";

        public int VertexCount => Positions.Length / 3;
        public int TriangleCount => Indices.Length / 3;
    }

    public sealed class NifModel
    {
        public uint Version;
        public uint BsVersion;
        public List<NifShape> Shapes = new();

        public int VertexCount { get { int n = 0; foreach (var s in Shapes) n += s.VertexCount; return n; } }
        public int TriangleCount { get { int n = 0; foreach (var s in Shapes) n += s.TriangleCount; return n; } }
    }

    // Geometry out of a Skyrim NIF. No bones, no materials, no collision, no animation.
    //
    // WHY THIS IS TRACTABLE AT ALL: the header carries a Block Sizes array, so every block's length is
    // known before it is read. Anything this reader does not understand - and that is most of a NIF -
    // is skipped by its declared size rather than parsed. Six block types matter; the other ~60 are
    // stepped over. That same array is also the layout's CHECKSUM: when a block parses to exactly its
    // declared end, the field layout is right, and when it does not, the difference says by how much
    // it is wrong. Every layout here was confirmed that way against real files before being trusted,
    // and two of them were wrong on the first try by exactly the amount the checksum reported.
    //
    // THERE ARE TWO FILE FORMATS IN ANY REAL LOAD ORDER, and that is the thing to understand before
    // changing anything here. Measured over 4.460 meshes named by 124 plugins: 73 % are SSE-format and
    // 26 % are LE-format - including vanilla paths like meshes\armor\steel\1stpersongauntlets_1.nif.
    // A reader that handles only one of them loses a quarter of the load order.
    //
    //   * SSE (BS version 100) uses BSTriShape with a PACKED vertex buffer. Unskinned shapes carry
    //     their own arrays. SKINNED shapes (every armor) report numVertices = 0, numTriangles = 0,
    //     dataSize = 0 and keep the geometry in the NiSkinPartition their skin instance points at -
    //     measured on daedriccuirass_1.nif, a 120-byte shape block against a 271.968-byte partition.
    //     A reader that stops at BSTriShape therefore produces an EMPTY armor and no error, which is
    //     the most misleading failure available here.
    //
    //   * LE (BS version 83) uses NiTriShape + NiTriShapeData, with plain float arrays and NO packing.
    //     Skinning does not matter: the vertices sit in the data block either way. It is the simpler
    //     format of the two.
    //
    // For SSE the partition's vertex array is CONSOLIDATED at the top of the block and each partition
    // only carries triangles indexing into it - verified: max index 3.422 against 3.423 vertices. So
    // the partitions are concatenated, not merged vertex-wise.
    //
    // SSE POSITIONS ARE FULL-PRECISION FLOATS, and the FULLPREC flag in the vertex descriptor does not
    // say so - it reads 0 on files whose positions are plainly float32. What does say so is the UV
    // offset in the same descriptor: 16 means 12 bytes of position plus 4 of bitangent, 8 means half
    // precision. Measured both ways on two real files: as halfs, 193 of 3.423 cuirass vertices came out
    // NaN and X spanned +/-64.000; as floats, no NaN and X spanned exactly +/-30,66 - a torso. This
    // reader trusts the offsets, not the flag.
    //
    // ROBUSTNESS IS NOT OPTIONAL: these files come from 200+ mods written by strangers over 15 years.
    // Every offset and length is range-checked before use, every array size is checked against the
    // bytes actually present, and TryRead never throws. A malformed mesh has to come back as "could
    // not be read" - not as an exception that takes a scan or a UI thread with it.
    public static class NifReader
    {
        // The header line of every Gamebryo/Bethesda NIF. Checked rather than assumed, so a DDS or a
        // truncated download fails with "not a NIF" instead of as a wild offset somewhere inside.
        private const string Magic = "Gamebryo File Format";

        // Skyrim LE is 83, SSE is 100. Below that the header layout itself differs; above it is
        // Fallout 4 / 76, whose BSTriShape carries fields this reader does not know. Refusing beats
        // reading nonsense: a wrong vertex count turns into a multi-gigabyte allocation.
        private const uint MinBsVersion = 83;
        private const uint MaxBsVersion = 100;

        // Sanity ceilings on anything length-prefixed in the header. Real values are tiny (the daedric
        // cuirass has 55 blocks, 13 block types, 40 strings); these exist so a corrupt length cannot
        // make the reader allocate before it has read a byte of payload.
        private const int MaxBlocks = 200_000;
        private const int MaxStrings = 200_000;
        private const int MaxStringLength = 4096;
        private const int MaxVertices = 10_000_000;

        public static bool TryRead(byte[] data, out NifModel model, out string error)
        {
            model = new NifModel();
            error = "";

            if (data == null || data.Length < 64) { error = "file is too short to be a NIF"; return false; }

            try
            {
                return Read(data, model, out error);
            }
            catch (Exception ex)
            {
                // Anything that slipped past the explicit checks. The type is in the message because an
                // IndexOutOfRange here means a layout assumption is wrong and is worth seeing.
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static bool Read(byte[] data, NifModel model, out string error)
        {
            error = "";
            var r = new Cursor(data);

            string header = r.Line(64);
            if (!header.StartsWith(Magic, StringComparison.Ordinal))
            {
                error = "not a NIF (header line missing)";
                return false;
            }

            model.Version = r.U32();
            byte endian = r.U8();
            if (endian != 1) { error = "big-endian NIF is not supported"; return false; }

            r.U32();                                  // user version
            long numBlocksRaw = r.U32();
            model.BsVersion = r.U32();

            if (model.BsVersion < MinBsVersion || model.BsVersion > MaxBsVersion)
            {
                error = $"unsupported BS version {model.BsVersion} (this reader handles {MinBsVersion}-{MaxBsVersion})";
                return false;
            }
            if (numBlocksRaw < 0 || numBlocksRaw > MaxBlocks)
            {
                error = $"implausible block count {numBlocksRaw}";
                return false;
            }
            int numBlocks = (int)numBlocksRaw;

            r.ShortString();                          // author
            r.ShortString();                          // process script
            r.ShortString();                          // export script

            int numBlockTypes = r.U16();
            var blockTypes = new string[numBlockTypes];
            for (int i = 0; i < numBlockTypes; i++) blockTypes[i] = r.SizedString(MaxStringLength);

            var typeIndex = new ushort[numBlocks];
            for (int i = 0; i < numBlocks; i++)
            {
                typeIndex[i] = r.U16();
                if (typeIndex[i] >= numBlockTypes)
                {
                    error = $"block {i} names block type {typeIndex[i]} of {numBlockTypes}";
                    return false;
                }
            }

            var blockSizes = new uint[numBlocks];
            for (int i = 0; i < numBlocks; i++) blockSizes[i] = r.U32();

            long numStrings = r.U32();
            r.U32();                                  // max string length, not trusted
            if (numStrings < 0 || numStrings > MaxStrings) { error = $"implausible string count {numStrings}"; return false; }

            var strings = new string[numStrings];
            for (int i = 0; i < numStrings; i++) strings[i] = r.SizedString(MaxStringLength);

            long numGroups = r.U32();
            if (numGroups < 0 || numGroups > MaxBlocks) { error = $"implausible group count {numGroups}"; return false; }
            r.Skip((int)numGroups * 4);

            // ---- pass 1: where each block begins, by its declared size ----
            // Two passes because a shape points FORWARD at the block holding its vertices, and one pass
            // would have to read blocks in an order the file does not use.
            var blockStart = new int[numBlocks];
            int cursor = r.Pos;
            for (int i = 0; i < numBlocks; i++)
            {
                blockStart[i] = cursor;
                long next = (long)cursor + blockSizes[i];
                if (next > data.Length)
                {
                    error = $"block {i} ({blockTypes[typeIndex[i]]}) runs past the end of the file";
                    return false;
                }
                cursor = (int)next;
            }

            // ---- pass 2: the six types that carry geometry, plus the node tree ----
            var shapes = new List<PendingShape>();
            var skinToPartition = new Dictionary<int, int>();        // skin instance block -> partition block
            var geometryByBlock = new Dictionary<int, RawGeometry>(); // partition or data block -> geometry
            var parentOf = new Dictionary<int, int>();                // block -> its parent node block
            var nodeTransform = new Dictionary<int, Xform>();         // node block -> its own transform
            var shaderToTextureSet = new Dictionary<int, int>();      // shader property block -> texture set block
            var texturesOfSet = new Dictionary<int, (string Diffuse, string Normal, string EnvironmentMask)>();

            for (int i = 0; i < numBlocks; i++)
            {
                string type = blockTypes[typeIndex[i]];
                int start = blockStart[i];
                int end = start + (int)blockSizes[i];

                if (IsNodeType(type))
                {
                    ReadNode(data, start, end, i, parentOf, nodeTransform, numBlocks);
                    continue;
                }

                switch (type)
                {
                    // --- SSE ---
                    case "BSTriShape":
                    case "BSSubIndexTriShape":
                    case "BSDynamicTriShape":
                        var sse = ReadBsTriShape(data, start, end, strings, model.BsVersion,
                                                 isDynamic: type == "BSDynamicTriShape");
                        if (sse != null) { sse.Block = i; shapes.Add(sse); }
                        break;

                    case "NiSkinPartition":
                        var partition = ReadSkinPartition(data, start, end, model.BsVersion);
                        if (partition != null) geometryByBlock[i] = partition;
                        break;

                    // Both skin instances begin with the same two refs; only the second is needed.
                    case "NiSkinInstance":
                    case "BSDismemberSkinInstance":
                        if (end - start >= 8)
                            skinToPartition[i] = BitConverter.ToInt32(data, start + 4);
                        break;

                    // --- LE ---
                    case "NiTriShape":
                        var le = ReadNiTriShape(data, start, end, strings);
                        if (le != null) { le.Block = i; shapes.Add(le); }
                        break;

                    case "NiTriShapeData":
                        var leData = ReadNiTriShapeData(data, start, end);
                        if (leData != null) geometryByBlock[i] = leData;
                        break;

                    // --- textures, both formats ---
                    case "BSLightingShaderProperty":
                        int set = ReadShaderTextureSetRef(data, start, end);
                        if (set >= 0) shaderToTextureSet[i] = set;
                        break;

                    case "BSShaderTextureSet":
                        var textures = ReadTextureSet(data, start, end);
                        if (textures.Diffuse.Length > 0 || textures.Normal.Length > 0 || textures.EnvironmentMask.Length > 0)
                            texturesOfSet[i] = textures;
                        break;
                }
            }

            // ---- resolve each shape's geometry and emit ----
            foreach (var pending in shapes)
            {
                RawGeometry? geometry = pending.Geometry;

                // LE: the shape names its data block directly.
                if (geometry == null && pending.DataRef >= 0)
                    geometryByBlock.TryGetValue(pending.DataRef, out geometry);

                // SSE skinned: shape -> skin instance -> partition.
                if (geometry == null
                    && pending.SkinRef >= 0
                    && skinToPartition.TryGetValue(pending.SkinRef, out int partitionRef))
                {
                    geometryByBlock.TryGetValue(partitionRef, out geometry);
                }

                // A shape with no geometry of its own and nothing reachable. Normal for the marker and
                // effect shapes a NIF carries; not worth an error.
                if (geometry == null) continue;

                // A dynamic shape's packed records carry no positions at all (its vertex descriptor has
                // the VERTEX bit clear); they sit in the shape's own Vector4 array instead, while the
                // normals, UVs and triangles still come from the partition. So the two halves are
                // combined here rather than in either reader.
                float[] positions = geometry.Positions.Length > 0
                    ? (float[])geometry.Positions.Clone()
                    : pending.DynamicPositions;

                var shape = new NifShape
                {
                    Name = pending.Name,
                    Positions = (float[])positions.Clone(),
                    Normals = (float[])geometry.Normals.Clone(),
                    Uvs = (float[])geometry.Uvs.Clone(),
                    Tangents = (float[])geometry.Tangents.Clone(),
                    Bitangents = (float[])geometry.Bitangents.Clone(),
                    Indices = geometry.Indices,
                };

                if (shape.VertexCount == 0 || shape.TriangleCount == 0) continue;

                // Normals and UVs are indexed by the same vertex number as the positions. When the two
                // halves disagree on the count, dropping the shorter is the only safe thing: a renderer
                // handed mismatched arrays throws, and a silently truncated one shades wrongly.
                if (shape.Normals.Length != 0 && shape.Normals.Length != shape.Positions.Length)
                    shape.Normals = Array.Empty<float>();
                if (shape.Uvs.Length != 0 && shape.Uvs.Length / 2 != shape.VertexCount)
                    shape.Uvs = Array.Empty<float>();

                // The tangent frame is only usable whole. A mismatched tangent would rotate the normal
                // map by an arbitrary amount, which is worse than not using it at all.
                if (shape.Tangents.Length != shape.Positions.Length
                    || shape.Bitangents.Length != shape.Positions.Length)
                {
                    shape.Tangents = Array.Empty<float>();
                    shape.Bitangents = Array.Empty<float>();
                }

                // The shape's own transform, with its parent node chain composed on top.
                var parents = ComposeParents(pending.Block, parentOf, nodeTransform);
                var total = Xform.Multiply(parents, pending.Own);

                shape.MovedByParentNode = !parents.IsIdentity;

                if (pending.ShaderRef >= 0
                    && shaderToTextureSet.TryGetValue(pending.ShaderRef, out int setRef)
                    && texturesOfSet.TryGetValue(setRef, out var textures))
                {
                    shape.DiffuseTexture = textures.Diffuse;
                    shape.NormalTexture = textures.Normal;
                    shape.EnvironmentMask = textures.EnvironmentMask;
                }

                ApplyTransform(shape, total);
                model.Shapes.Add(shape);
            }

            if (model.Shapes.Count == 0)
            {
                error = "no geometry found";
                return false;
            }

            return true;
        }

        // The shape's full transform, baked into the positions.
        //
        // The positions are cloned per shape before this runs, precisely so that two shapes sharing one
        // data block do not transform each other's vertices.
        private static void ApplyTransform(NifShape shape, Xform t)
        {
            if (t.IsIdentity) return;

            var p = shape.Positions;
            for (int i = 0; i < p.Length; i += 3)
            {
                float x = p[i], y = p[i + 1], z = p[i + 2];

                p[i + 0] = (t.R0 * x + t.R1 * y + t.R2 * z) * t.Scale + t.Tx;
                p[i + 1] = (t.R3 * x + t.R4 * y + t.R5 * z) * t.Scale + t.Ty;
                p[i + 2] = (t.R6 * x + t.R7 * y + t.R8 * z) * t.Scale + t.Tz;
            }

            // Directions rotate but neither translate nor scale. The tangent and bitangent are directions
            // too, and leaving them behind while the normal turns would tear the frame apart - every
            // normal-mapped texel would then be lit from the wrong side.
            Rotate(shape.Normals, t);
            Rotate(shape.Tangents, t);
            Rotate(shape.Bitangents, t);
        }

        private static void Rotate(float[] vectors, Xform t)
        {
            for (int i = 0; i + 2 < vectors.Length; i += 3)
            {
                float x = vectors[i], y = vectors[i + 1], z = vectors[i + 2];

                vectors[i + 0] = t.R0 * x + t.R1 * y + t.R2 * z;
                vectors[i + 1] = t.R3 * x + t.R4 * y + t.R5 * z;
                vectors[i + 2] = t.R6 * x + t.R7 * y + t.R8 * z;
            }
        }

        // ===================================================================
        // the node tree
        // ===================================================================

        // Node types that can hold children. All of them inherit NiNode and add their extra fields AFTER
        // the child and effect lists, so one reader covers every one of them - and anything this list
        // misses simply means its children keep their own transforms, never a wrong one.
        //
        // Verified against a real load order: 96.947 node blocks parsed to exactly their declared size,
        // 45 did not.
        private static bool IsNodeType(string type) => type is
            "NiNode" or "BSFadeNode" or "BSLeafAnimNode" or "BSTreeNode" or "BSOrderedNode" or
            "BSMultiBoundNode" or "BSValueNode" or "NiBillboardNode" or "NiSwitchNode" or "BSBlastNode" or
            "BSDamageStage" or "BSDebrisNode" or "BSMasterParticleSystem" or "NiSortAdjustNode";

        private static void ReadNode(byte[] data, int start, int end, int block,
                                     Dictionary<int, int> parentOf, Dictionary<int, Xform> transform,
                                     int numBlocks)
        {
            try
            {
                var r = new Cursor(data) { Pos = start };

                r.I32();                                   // name
                long numExtra = r.U32();
                if (numExtra < 0 || numExtra > 100_000) return;
                r.Skip((int)numExtra * 4);
                r.I32();                                   // controller
                r.U32();                                   // flags

                var own = ReadXform(r);
                r.I32();                                   // collision object

                long numChildren = r.U32();
                if (numChildren < 0 || numChildren > 100_000) return;

                transform[block] = own;

                for (long i = 0; i < numChildren; i++)
                {
                    int child = r.I32();

                    // A child claimed by two parents cannot happen in a well-formed NIF, and the first
                    // claim is as good as the second. TryAdd rather than assignment so a malformed file
                    // cannot produce a chain that depends on block order.
                    if (child >= 0 && child < numBlocks) parentOf.TryAdd(child, block);
                }
            }
            catch (InvalidDataException)
            {
                // A node that cannot be read costs its children their parent transform, nothing more.
            }
        }

        // The parent chain, composed root-first.
        //
        // WHY THIS MATTERS AND HOW MUCH: measured over a real load order, 390 of 11.393 shapes sit under
        // a parent whose transform is not identity. Most of those move by nothing - but 66 move by more
        // than a unit, 37 by more than ten, and the worst by 90,8 units (a draugr ground-object mesh).
        // Without this they are simply drawn in the wrong place, and nothing says so.
        private static Xform ComposeParents(int block, Dictionary<int, int> parentOf, Dictionary<int, Xform> transform)
        {
            var chain = new List<int>();
            int at = block;

            // A cycle is impossible in a well-formed file and must not hang the reader on a malformed
            // one. The guard also bounds the depth, which is otherwise whatever the file claims.
            var seen = new HashSet<int> { block };
            while (parentOf.TryGetValue(at, out int parent) && seen.Add(parent))
            {
                chain.Add(parent);
                at = parent;
            }

            var composed = Xform.Identity;
            for (int i = chain.Count - 1; i >= 0; i--)
                if (transform.TryGetValue(chain[i], out var x))
                    composed = Xform.Multiply(composed, x);

            return composed;
        }

        private static Xform ReadXform(Cursor r)
        {
            var x = new Xform { Tx = r.F32(), Ty = r.F32(), Tz = r.F32() };
            x.R0 = r.F32(); x.R1 = r.F32(); x.R2 = r.F32();
            x.R3 = r.F32(); x.R4 = r.F32(); x.R5 = r.F32();
            x.R6 = r.F32(); x.R7 = r.F32(); x.R8 = r.F32();
            x.Scale = r.F32();
            return x;
        }

        // Translation, rotation and uniform scale. A full 4x4 would carry three rows of zeroes and a
        // one; this is what a NIF actually stores.
        private struct Xform
        {
            public float Tx, Ty, Tz, Scale;
            public float R0, R1, R2, R3, R4, R5, R6, R7, R8;

            public static Xform Identity => new()
            {
                Scale = 1f, R0 = 1f, R4 = 1f, R8 = 1f,
            };

            // Tolerant rather than exact: an exported matrix is rarely bit-identical to the identity, and
            // transforming a hundred thousand vertices by something that rounds to the identity is work
            // for nothing.
            public bool IsIdentity =>
                Math.Abs(Tx) < 1e-6f && Math.Abs(Ty) < 1e-6f && Math.Abs(Tz) < 1e-6f &&
                Math.Abs(Scale - 1f) < 1e-6f &&
                Math.Abs(R0 - 1f) < 1e-5f && Math.Abs(R1) < 1e-5f && Math.Abs(R2) < 1e-5f &&
                Math.Abs(R3) < 1e-5f && Math.Abs(R4 - 1f) < 1e-5f && Math.Abs(R5) < 1e-5f &&
                Math.Abs(R6) < 1e-5f && Math.Abs(R7) < 1e-5f && Math.Abs(R8 - 1f) < 1e-5f;

            // outer after inner: the result applied to a vertex equals outer(inner(vertex)). Parent nodes
            // are the outer one, which is why the chain is composed from the root down.
            public static Xform Multiply(Xform outer, Xform inner)
            {
                var m = new Xform { Scale = outer.Scale * inner.Scale };

                m.R0 = outer.R0 * inner.R0 + outer.R1 * inner.R3 + outer.R2 * inner.R6;
                m.R1 = outer.R0 * inner.R1 + outer.R1 * inner.R4 + outer.R2 * inner.R7;
                m.R2 = outer.R0 * inner.R2 + outer.R1 * inner.R5 + outer.R2 * inner.R8;
                m.R3 = outer.R3 * inner.R0 + outer.R4 * inner.R3 + outer.R5 * inner.R6;
                m.R4 = outer.R3 * inner.R1 + outer.R4 * inner.R4 + outer.R5 * inner.R7;
                m.R5 = outer.R3 * inner.R2 + outer.R4 * inner.R5 + outer.R5 * inner.R8;
                m.R6 = outer.R6 * inner.R0 + outer.R7 * inner.R3 + outer.R8 * inner.R6;
                m.R7 = outer.R6 * inner.R1 + outer.R7 * inner.R4 + outer.R8 * inner.R7;
                m.R8 = outer.R6 * inner.R2 + outer.R7 * inner.R5 + outer.R8 * inner.R8;

                m.Tx = (outer.R0 * inner.Tx + outer.R1 * inner.Ty + outer.R2 * inner.Tz) * outer.Scale + outer.Tx;
                m.Ty = (outer.R3 * inner.Tx + outer.R4 * inner.Ty + outer.R5 * inner.Tz) * outer.Scale + outer.Ty;
                m.Tz = (outer.R6 * inner.Tx + outer.R7 * inner.Ty + outer.R8 * inner.Tz) * outer.Scale + outer.Tz;

                return m;
            }
        }

        // ===================================================================
        // SSE (BS version 100)
        // ===================================================================

        // NiAVObject preamble + BSTriShape body. Returns null when the block is not shaped as expected
        // rather than throwing: one odd shape in a mesh should cost that shape, not the file.
        private static PendingShape? ReadBsTriShape(byte[] data, int start, int end, string[] strings,
                                                   uint bsVersion, bool isDynamic)
        {
            var r = new Cursor(data) { Pos = start };

            int nameIndex = r.I32();
            long numExtra = r.U32();
            if (numExtra < 0 || numExtra > 100_000) return null;
            r.Skip((int)numExtra * 4);

            r.I32();                                   // controller
            r.U32();                                   // flags

            var pending = ReadAvObjectTransform(r, nameIndex, strings);
            r.I32();                                   // collision object

            r.Skip(16);                                // bounding sphere
            pending.SkinRef = r.I32();
            pending.ShaderRef = r.I32();               // shader property - carries the texture set
            r.I32();                                   // alpha property

            ulong desc = r.U64();
            int numTriangles = r.U16();
            int numVertices = r.U16();
            long dataSize = r.U32();

            if (r.Pos > end) return null;

            int vertexSize = (int)(desc & 0xF) * 4;

            // dataSize == 0 means skinned: the geometry is in the partition, not here. A dynamic shape
            // still has its own position array further down, so reading continues in that case.
            if (dataSize > 0 && numVertices > 0 && vertexSize > 0)
            {
                long vertexBytes = (long)numVertices * vertexSize;
                long triangleBytes = (long)numTriangles * 6;
                if (r.Pos + vertexBytes + triangleBytes > end) return pending;

                int vertexStart = r.Pos;
                int triangleStart = vertexStart + (int)vertexBytes;

                var geometry = UnpackSseVertices(data, vertexStart, numVertices, vertexSize, desc);
                geometry.Indices = ReadTriangles(data, triangleStart, numTriangles, numVertices);
                pending.Geometry = geometry;

                r.Skip((int)(vertexBytes + triangleBytes));
            }

            if (!isDynamic) return pending;

            // BSDynamicTriShape: BSTriShape's body, then a full-precision Vector4 per vertex. These are
            // head, gore and hair meshes - anything the engine morphs at runtime - and their packed
            // records carry normals and UVs but NO position, which is why the descriptor's VERTEX bit
            // reads 0. Measured: 193 shapes across 29 files in a real load order, every one of them a
            // dynamic shape, and reading their packed records as positions produced NaN.
            try
            {
                if (bsVersion == 100)
                {
                    long particleDataSize = r.U32();                 // see the BSTriShape note above
                    if (particleDataSize > 0) r.Skip((int)particleDataSize * 2);
                }

                // A BYTE count, not a count of vertices and not of floats. Measured: 3.296 for a
                // 206-vertex gore head and 8.144 for a 509-vertex scalp - both exactly 16 bytes per
                // vertex. Reading it as floats made the array four times too long, the bounds check
                // then refused it, and six files came back as "no geometry found" instead of wrong -
                // which is the failure mode worth having while a layout is still being pinned down.
                long dynamicBytes = r.U32();
                long dynamicVertices = dynamicBytes / 16;            // Vector4 each

                if (dynamicVertices > 0 && dynamicVertices <= MaxVertices
                    && r.Pos + dynamicVertices * 16 <= end)
                {
                    var positions = new float[dynamicVertices * 3];
                    int at = r.Pos;
                    for (long v = 0; v < dynamicVertices; v++)
                    {
                        int b = at + (int)(v * 16);
                        positions[v * 3 + 0] = BitConverter.ToSingle(data, b);
                        positions[v * 3 + 1] = BitConverter.ToSingle(data, b + 4);
                        positions[v * 3 + 2] = BitConverter.ToSingle(data, b + 8);
                        // w is the morph weight; not geometry.
                    }
                    pending.DynamicPositions = positions;
                }
            }
            catch (InvalidDataException)
            {
                // The dynamic array is an extra: without it the shape falls back to whatever the
                // partition holds, which is the behaviour before this existed. Not worth losing the
                // shape over.
            }

            return pending;
        }

        // Where a skinned SSE mesh keeps its geometry.
        //
        // The partition list is walked in full even though only the triangles are wanted: the fields
        // are variable-length, so there is no way to reach partition 2 except by reading partition 1
        // exactly right. Landing on the block's declared end is what says the walk was correct, and a
        // walk that lands anywhere else returns null rather than triangles nobody can trust.
        private static RawGeometry? ReadSkinPartition(byte[] data, int start, int end, uint bsVersion)
        {
            var r = new Cursor(data) { Pos = start };

            long numPartitions = r.U32();
            long dataSize = r.U32();
            long vertexSize = r.U32();
            ulong desc = r.U64();

            if (numPartitions < 0 || numPartitions > 100_000) return null;
            if (dataSize <= 0 || vertexSize <= 0) return null;
            if (dataSize % vertexSize != 0) return null;

            long vertexCount = dataSize / vertexSize;
            if (vertexCount <= 0 || vertexCount > MaxVertices) return null;

            int vertexStart = r.Pos;
            if (vertexStart + dataSize > end) return null;
            r.Skip((int)dataSize);

            var indices = new List<int>();

            for (long p = 0; p < numPartitions; p++)
            {
                if (r.Pos + 10 > end) return null;

                int partVertices = r.U16();
                int partTriangles = r.U16();
                int numBones = r.U16();
                int numStrips = r.U16();
                int weightsPerVertex = r.U16();

                r.Skip(numBones * 2);

                if (r.U8() != 0) r.Skip(partVertices * 2);                      // vertex map
                if (r.U8() != 0) r.Skip(partVertices * weightsPerVertex * 4);   // vertex weights
                r.Skip(numStrips * 2);                                           // strip lengths

                bool hasFaces = r.U8() != 0;
                if (hasFaces && numStrips == 0) r.Skip(partTriangles * 6);       // triangles
                if (r.U8() != 0) r.Skip(partVertices * weightsPerVertex);        // bone indices

                if (bsVersion > 34) r.U16();                                     // unknown short

                // SSE repeats the descriptor per partition and follows it with the triangle list that
                // indexes the CONSOLIDATED vertex array above - which is the one worth having.
                if (bsVersion == 100)
                {
                    r.U64();
                    int triangleStart = r.Pos;
                    long triangleBytes = (long)partTriangles * 6;
                    if (triangleStart + triangleBytes > end) return null;
                    r.Skip((int)triangleBytes);

                    AppendTriangles(data, triangleStart, partTriangles, (int)vertexCount, indices);
                }

                if (r.Pos > end) return null;
            }

            // The checksum. A partition walk that ends anywhere but exactly here read some field wrong,
            // and every triangle it produced is suspect.
            if (r.Pos != end) return null;

            var geometry = UnpackSseVertices(data, vertexStart, (int)vertexCount, (int)vertexSize, desc);
            geometry.Indices = indices.ToArray();
            return geometry;
        }

        // Pulls positions, normals and UVs out of SSE's packed vertex records.
        //
        // The layout is not fixed: a BSVertexDesc says how large one record is and at what offset each
        // field sits. Reading those offsets rather than reconstructing the layout from the flags is what
        // makes this work across meshes carrying different field sets - and it is the only thing that
        // gets the position size right (see the class comment).
        private static RawGeometry UnpackSseVertices(byte[] data, int at, int count, int stride, ulong desc)
        {
            ulong flags = (desc >> 44) & 0x3FF;
            int uvOffset = (int)((desc >> 8) & 0xF) * 4;
            int normalOffset = (int)((desc >> 16) & 0xF) * 4;

            var geometry = new RawGeometry();

            // THE VERTEX BIT IS NOT DECORATION. When it is clear, the record holds no position at all -
            // the first field is the normal, at offset 4 - and reading three numbers from offset 0
            // yields garbage, NaN included. Dynamic shapes are exactly this case and keep their
            // positions in a separate array; leaving Positions empty here is what lets the caller
            // substitute them.
            bool hasPosition = (flags & 0x001) != 0;

            // How many bytes the position block occupies, taken from where the next field starts. Falls
            // back to the record size when the mesh carries position and nothing else. Hoisted out of the
            // branch below because the bitangent's X component lives at the end of this same block.
            int positionBlock = uvOffset > 0 ? uvOffset
                              : normalOffset > 0 ? normalOffset
                              : stride;
            bool fullPrecision = positionBlock >= 16;

            if (hasPosition)
            {
                var positions = new float[count * 3];
                for (int v = 0; v < count; v++)
                {
                    int b = at + v * stride;
                    if (fullPrecision)
                    {
                        positions[v * 3 + 0] = BitConverter.ToSingle(data, b);
                        positions[v * 3 + 1] = BitConverter.ToSingle(data, b + 4);
                        positions[v * 3 + 2] = BitConverter.ToSingle(data, b + 8);
                    }
                    else
                    {
                        positions[v * 3 + 0] = Half(data, b);
                        positions[v * 3 + 1] = Half(data, b + 2);
                        positions[v * 3 + 2] = Half(data, b + 4);
                    }
                }
                geometry.Positions = positions;
            }

            int tangentOffset = (int)((desc >> 20) & 0xF) * 4;

            // Normals are three bytes rather than floats - and they are NOT signed bytes. See ByteToUnit:
            // reading them as signed yields vectors about 0.97 long, close enough to pass a casual look
            // and wrong by a sign for every component above 127.
            if ((flags & 0x008) != 0 && normalOffset > 0 && normalOffset + 3 <= stride)
            {
                var normals = new float[count * 3];
                for (int v = 0; v < count; v++)
                {
                    int b = at + v * stride + normalOffset;
                    normals[v * 3 + 0] = ByteToUnit(data[b]);
                    normals[v * 3 + 1] = ByteToUnit(data[b + 1]);
                    normals[v * 3 + 2] = ByteToUnit(data[b + 2]);
                }
                geometry.Normals = normals;
            }

            // The tangent frame, with the bitangent gathered from the three places Skyrim keeps it: X is
            // the fourth component of the position block, Y the fourth byte of the normal block, Z the
            // fourth byte of the tangent block.
            //
            // THE OFFSET IS NOT ENOUGH TO DECIDE WHETHER THE FIELD IS THERE. Meshes exist whose
            // descriptor hands out a tangent offset while the TANGENTS bit is clear, and reading it
            // anyway yields a frame of all-0x80 bytes - the decoded 0.004 that static world meshes like
            // 'Sbridge01' and 'L1_moss' produced, which is zero wearing a disguise. The bitangent's X
            // also sits at the end of the position block, so without a position there is no X either.
            bool hasTangents = (flags & 0x010) != 0;

            if (hasTangents && hasPosition && tangentOffset > 0 && tangentOffset + 4 <= stride
                && geometry.Normals.Length > 0)
            {
                var tangents = new float[count * 3];
                var bitangents = new float[count * 3];

                for (int v = 0; v < count; v++)
                {
                    int record = at + v * stride;
                    int tan = record + tangentOffset;

                    tangents[v * 3 + 0] = ByteToUnit(data[tan]);
                    tangents[v * 3 + 1] = ByteToUnit(data[tan + 1]);
                    tangents[v * 3 + 2] = ByteToUnit(data[tan + 2]);

                    bitangents[v * 3 + 0] = fullPrecision
                        ? BitConverter.ToSingle(data, record + 12)
                        : Half(data, record + 6);
                    bitangents[v * 3 + 1] = ByteToUnit(data[record + normalOffset + 3]);
                    bitangents[v * 3 + 2] = ByteToUnit(data[tan + 3]);
                }

                geometry.Tangents = tangents;
                geometry.Bitangents = bitangents;
            }

            if (uvOffset > 0 && uvOffset + 4 <= stride)
            {
                var uvs = new float[count * 2];
                for (int v = 0; v < count; v++)
                {
                    int b = at + v * stride + uvOffset;
                    uvs[v * 2 + 0] = Half(data, b);
                    uvs[v * 2 + 1] = Half(data, b + 2);
                }
                geometry.Uvs = uvs;
            }

            return geometry;
        }

        // ===================================================================
        // LE (BS version 83)
        // ===================================================================

        // NiTriShape: NiAVObject, then NiGeometry's Data/SkinInstance refs and MaterialData.
        //
        // MATERIAL DATA ENDS IN A SINGLE BYTE that is easy to miss and cost one round of debugging:
        // after Active Material comes Material Needs Update, a bool. Without it the two refs that
        // follow read one byte early and come back as 12288 and -256 instead of 9 and -1 - plausible
        // enough to not look wrong, which is what the block-size check is for.
        private static PendingShape? ReadNiTriShape(byte[] data, int start, int end, string[] strings)
        {
            var r = new Cursor(data) { Pos = start };

            int nameIndex = r.I32();
            long numExtra = r.U32();
            if (numExtra < 0 || numExtra > 100_000) return null;
            r.Skip((int)numExtra * 4);

            r.I32();                                   // controller
            r.U32();                                   // flags

            var pending = ReadAvObjectTransform(r, nameIndex, strings);
            r.I32();                                   // collision object

            pending.DataRef = r.I32();
            r.I32();                                   // skin instance - geometry is in the data block

            long numMaterials = r.U32();
            if (numMaterials < 0 || numMaterials > 100_000) return null;
            r.Skip((int)numMaterials * 4);             // material name string indices
            r.Skip((int)numMaterials * 4);             // material extra data
            r.I32();                                   // active material
            r.U8();                                    // material needs update - see above

            pending.ShaderRef = r.I32();               // shader property - carries the texture set
            r.I32();                                   // alpha property

            // Not fatal if it does not land exactly: the transform and the data ref are already read,
            // and both sit well before the end. Reported nowhere because a shape block that is longer
            // than this reader expects is the normal consequence of a field it does not know about.
            return pending;
        }

        // NiGeometryData + NiTriBasedGeomData + NiTriShapeData. Plain float arrays, no packing.
        //
        // MATERIAL CRC is the field that is easy to leave out: a uint32 between BS Vector Flags and
        // Has Normals. Without it the block came out 4 bytes short of its declared size, and the
        // triangle count stopped dividing evenly - which is how it was found.
        private static RawGeometry? ReadNiTriShapeData(byte[] data, int start, int end)
        {
            var r = new Cursor(data) { Pos = start };

            r.I32();                                   // group id
            int numVertices = r.U16();
            r.U8();                                    // keep flags
            r.U8();                                    // compress flags

            bool hasVertices = r.U8() != 0;
            int vertexStart = r.Pos;
            if (hasVertices) r.Skip(numVertices * 12);

            ushort vectorFlags = r.U16();
            r.U32();                                   // material CRC - see above

            bool hasNormals = r.U8() != 0;
            int normalStart = r.Pos;
            if (hasNormals) r.Skip(numVertices * 12);

            // Tangents and bitangents only exist alongside normals, and only when the flag says so. LE
            // keeps them as two plain arrays rather than scattering them the way the packed SSE record
            // does, so there is nothing to gather here.
            bool hasTangents = (vectorFlags & 0x1000) != 0;
            int tangentStart = r.Pos, bitangentStart = r.Pos + numVertices * 12;
            if (hasNormals && hasTangents) r.Skip(numVertices * 24);

            r.Skip(12 + 4);                            // center + radius

            bool hasColors = r.U8() != 0;
            if (hasColors) r.Skip(numVertices * 16);   // Color4 = 4 floats

            int uvSets = vectorFlags & 0x3F;
            int uvStart = r.Pos;
            r.Skip(uvSets * numVertices * 8);          // TexCoord = 2 floats

            r.U16();                                   // consistency flags
            r.I32();                                   // additional data

            int numTriangles = r.U16();
            r.U32();                                   // num triangle points
            bool hasTriangles = r.U8() != 0;
            int triangleStart = r.Pos;
            if (hasTriangles) r.Skip(numTriangles * 6);

            long numMatchGroups = r.U16();
            for (long g = 0; g < numMatchGroups; g++) r.Skip(r.U16() * 2);

            // The checksum, same role as in the skin partition: a walk that ends anywhere else read a
            // field wrong, and nothing it produced can be trusted.
            if (r.Pos != end) return null;
            if (!hasVertices || numVertices == 0) return null;

            var geometry = new RawGeometry
            {
                Positions = ReadVector3Array(data, vertexStart, numVertices),
                Indices = hasTriangles
                    ? ReadTriangles(data, triangleStart, numTriangles, numVertices)
                    : Array.Empty<int>(),
            };

            if (hasNormals) geometry.Normals = ReadVector3Array(data, normalStart, numVertices);
            if (uvSets > 0) geometry.Uvs = ReadVector2Array(data, uvStart, numVertices);

            if (hasNormals && hasTangents)
            {
                geometry.Tangents = ReadVector3Array(data, tangentStart, numVertices);
                geometry.Bitangents = ReadVector3Array(data, bitangentStart, numVertices);
            }

            return geometry;
        }

        private static float[] ReadVector3Array(byte[] data, int at, int count)
        {
            var result = new float[count * 3];
            for (int i = 0; i < count; i++)
            {
                int b = at + i * 12;
                result[i * 3 + 0] = BitConverter.ToSingle(data, b);
                result[i * 3 + 1] = BitConverter.ToSingle(data, b + 4);
                result[i * 3 + 2] = BitConverter.ToSingle(data, b + 8);
            }
            return result;
        }

        private static float[] ReadVector2Array(byte[] data, int at, int count)
        {
            var result = new float[count * 2];
            for (int i = 0; i < count; i++)
            {
                int b = at + i * 8;
                result[i * 2 + 0] = BitConverter.ToSingle(data, b);
                result[i * 2 + 1] = BitConverter.ToSingle(data, b + 4);
            }
            return result;
        }

        // ===================================================================
        // shared
        // ===================================================================

        // Translation, rotation and scale, which sit in the same place in both formats because both
        // shapes inherit NiAVObject. Leaves the cursor on the collision-object ref.
        private static PendingShape ReadAvObjectTransform(Cursor r, int nameIndex, string[] strings)
            => new()
            {
                Name = nameIndex >= 0 && nameIndex < strings.Length ? strings[nameIndex] : "",
                Own = ReadXform(r),
            };

        private static float Half(byte[] data, int at)
            => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(data, at));

        // How SSE packs a direction component into one byte: UNSIGNED across the full range, so 0 is -1,
        // 128 is about 0 and 255 is +1. The obvious reading - a signed byte over 127 - is wrong and hides
        // itself well: it produces vectors roughly 0.97 long, which looks like rounding noise rather than
        // a bug, while flipping the sign of every component the mesh stored above 127. The frame being
        // orthonormal is the test that catches it; vector length alone does not.
        private static float ByteToUnit(byte value) => value / 255f * 2f - 1f;

        // ===================================================================
        // textures
        // ===================================================================

        // BSLightingShaderProperty, up to its texture set reference and no further.
        //
        // THE FIRST FIELD IS NOT THE NAME. This block alone begins with the Skyrim shader type, ahead of
        // everything NiObjectNET normally starts with - a quirk of how nif.xml conditions that field on
        // the block type. Reading it as the name puts every field after it one slot out, and the texture
        // set reference then points at whatever block happens to be there.
        //
        // The rest of the block is a long tail of material parameters, so this does not read to the end
        // and cannot use the block size as a check. What it does instead is refuse a reference that is
        // not a plausible block index.
        private static int ReadShaderTextureSetRef(byte[] data, int start, int end)
        {
            try
            {
                var r = new Cursor(data) { Pos = start };

                r.U32();                                   // Skyrim shader type - see above
                r.I32();                                   // name
                long numExtra = r.U32();
                if (numExtra < 0 || numExtra > 100_000) return -1;
                r.Skip((int)numExtra * 4);
                r.I32();                                   // controller
                r.U32();                                   // shader flags 1
                r.U32();                                   // shader flags 2
                r.F32(); r.F32();                          // UV offset
                r.F32(); r.F32();                          // UV scale

                int textureSet = r.I32();
                return r.Pos <= end ? textureSet : -1;
            }
            catch (InvalidDataException)
            {
                return -1;
            }
        }

        // BSShaderTextureSet: a count and then that many length-prefixed paths. Slot 0 is the diffuse
        // map, slot 1 the normal map; the rest are glow, environment, parallax and so on.
        //
        // Only two of the nine are taken. The paths are length-prefixed, so reaching slot 1 means reading
        // slot 0 anyway - and after that there is nothing more this preview can use.
        private static (string Diffuse, string Normal, string EnvironmentMask) ReadTextureSet(byte[] data, int start, int end)
        {
            try
            {
                var r = new Cursor(data) { Pos = start };

                long count = r.U32();
                if (count <= 0 || count > 64) return ("", "", "");

                // Every slot up to 5 has to be read because they are length-prefixed - there is nothing
                // to skip past. The ones in between are glow, parallax and the cubemap itself, none of
                // which this preview can use.
                var slots = new string[Math.Min(count, 6)];
                for (int i = 0; i < slots.Length; i++)
                {
                    slots[i] = r.SizedString(1024);
                    if (r.Pos > end) return ("", "", "");
                }

                string At(int index) => index < slots.Length ? MeshPath.NormalizeTexture(slots[index]) ?? "" : "";

                return (At(0), At(1), At(5));
            }
            catch (InvalidDataException)
            {
                return ("", "", "");
            }
        }

        // Triangles whose indices fall outside the vertex array are dropped, not clamped. A clamped
        // index is a triangle that renders as a stray spike across the model; a dropped one is a hole
        // nobody notices. Both are wrong, only one is loud.
        private static int[] ReadTriangles(byte[] data, int at, int count, int vertexCount)
        {
            var list = new List<int>(count * 3);
            AppendTriangles(data, at, count, vertexCount, list);
            return list.ToArray();
        }

        private static void AppendTriangles(byte[] data, int at, int count, int vertexCount, List<int> into)
        {
            for (int t = 0; t < count; t++)
            {
                int b = at + t * 6;
                int a = BitConverter.ToUInt16(data, b);
                int c = BitConverter.ToUInt16(data, b + 2);
                int d = BitConverter.ToUInt16(data, b + 4);

                if (a >= vertexCount || c >= vertexCount || d >= vertexCount) continue;

                into.Add(a);
                into.Add(c);
                into.Add(d);
            }
        }

        private sealed class PendingShape
        {
            public string Name = "";

            // Which block this shape is, so its parent chain can be looked up.
            public int Block = -1;

            // The shape's own transform, before the parent chain is composed on top.
            public Xform Own = Xform.Identity;

            // SSE skinned path: the skin instance whose partition holds the vertices.
            public int SkinRef = -1;

            // LE path: the NiTriShapeData block holding the vertices.
            public int DataRef = -1;

            // The BSLightingShaderProperty block, which is what leads to the texture set. Both formats
            // carry it in the same place relative to their own fields.
            public int ShaderRef = -1;

            // SSE unskinned path: the shape carried its own arrays.
            public RawGeometry? Geometry;

            // BSDynamicTriShape only: positions from the shape's own Vector4 array, because its packed
            // records carry none.
            public float[] DynamicPositions = Array.Empty<float>();

        }

        // Unpacked geometry, whichever format it came out of. One shape of data for both readers, so
        // everything downstream of the parse is format-blind.
        private sealed class RawGeometry
        {
            public float[] Positions = Array.Empty<float>();
            public float[] Normals = Array.Empty<float>();
            public float[] Uvs = Array.Empty<float>();
            public float[] Tangents = Array.Empty<float>();
            public float[] Bitangents = Array.Empty<float>();
            public int[] Indices = Array.Empty<int>();
        }

        // Bounds-checked little-endian reader. Every accessor validates before it reads, because the
        // input is a file written by a stranger and an unchecked offset here is an unchecked offset
        // into someone else's buffer.
        private sealed class Cursor
        {
            private readonly byte[] _data;
            public int Pos;

            public Cursor(byte[] data) { _data = data; }

            private void Need(int bytes)
            {
                if (Pos < 0 || Pos + bytes > _data.Length)
                    throw new InvalidDataException($"read of {bytes} byte(s) at {Pos} runs past the end ({_data.Length})");
            }

            public byte U8() { Need(1); return _data[Pos++]; }
            public ushort U16() { Need(2); var v = BitConverter.ToUInt16(_data, Pos); Pos += 2; return v; }
            public uint U32() { Need(4); var v = BitConverter.ToUInt32(_data, Pos); Pos += 4; return v; }
            public int I32() { Need(4); var v = BitConverter.ToInt32(_data, Pos); Pos += 4; return v; }
            public ulong U64() { Need(8); var v = BitConverter.ToUInt64(_data, Pos); Pos += 8; return v; }
            public float F32() { Need(4); var v = BitConverter.ToSingle(_data, Pos); Pos += 4; return v; }

            public void Skip(int bytes)
            {
                if (bytes < 0) throw new InvalidDataException($"negative skip of {bytes}");
                Need(bytes);
                Pos += bytes;
            }

            public string Line(int max)
            {
                int s = Pos;
                while (Pos < _data.Length && Pos - s < max && _data[Pos] != (byte)'\n') Pos++;
                if (Pos >= _data.Length) throw new InvalidDataException("header line is not terminated");
                var v = Encoding.ASCII.GetString(_data, s, Pos - s);
                Pos++;
                return v;
            }

            // Byte-length prefixed, and the stored bytes include a trailing NUL.
            public string ShortString()
            {
                int n = U8();
                Need(n);
                var v = Encoding.ASCII.GetString(_data, Pos, n).TrimEnd('\0');
                Pos += n;
                return v;
            }

            // uint32-length prefixed, no terminator.
            public string SizedString(int max)
            {
                long n = U32();
                if (n < 0 || n > max) throw new InvalidDataException($"string length {n} exceeds {max}");
                Need((int)n);
                var v = Encoding.ASCII.GetString(_data, Pos, (int)n);
                Pos += (int)n;
                return v;
            }
        }
    }
}
