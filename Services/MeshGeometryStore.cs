using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Mesh.GeomState: has this mesh been parsed, and did it work.
    //
    // Failed is a RESULT, not an absence. Without it a mesh that cannot be parsed is retried on every
    // single view - 3,6 MB read and thrown away each time - and the UI has nothing to say beyond an
    // empty viewport. One mesh in the measured load order is in this state (an effects placeholder with
    // no geometry at all), which is exactly the kind of thing worth remembering rather than rediscovering.
    public enum MeshGeomState
    {
        Unparsed = 0,
        Parsed = 1,
        Failed = 2,
    }

    // The parsed-geometry half of the mesh index: BLOBs in and out of MeshGeom, and the lazy
    // parse-on-first-view that fills it.
    //
    // WHY LAZY AND NOT PART OF THE SCAN: the whole load order is 17,7 million vertices and 27 million
    // triangles, roughly 890 MB of BLOB if every mesh were cached. A scan that did that would multiply
    // item.db by twenty for meshes nobody opens. Parsing one mesh costs 0,83 ms and reads it straight
    // out of the archive into memory, so the first view is already fast enough that pre-warming buys
    // nothing - and MeshId is stable across scans, so a mesh parsed once stays parsed.
    //
    // NOTHING HERE IS WRITTEN BY THE SCAN. The scan owns Mesh and MeshRef; this owns MeshGeom and the
    // GeomState column. Keeping that line clean is what lets a rescan refresh paths without throwing
    // away geometry that is still correct.
    public sealed class MeshGeometryStore
    {
        private readonly string _dbPath;

        public MeshGeometryStore(string dbPath) { _dbPath = dbPath; }

        public MeshGeometryStore() : this(DefaultPath()) { }

        private static string DefaultPath()
        {
            var input = GlobalState.Tool?.InputFolder ?? "";
            return Path.Combine(input, "Item", "item.db");
        }

        // The entry point a viewer uses: cached geometry if there is any, otherwise read the file,
        // parse it, store the result and hand it back.
        //
        // Returns an empty list for a mesh that cannot be parsed, having recorded that fact, so the
        // next call is a single state read rather than another 3,6 MB round trip.
        public IReadOnlyList<NifShape> GetOrParse(long meshId, MeshLocator locator, out string error)
        {
            error = "";

            if (!File.Exists(_dbPath)) { error = "no database"; return Array.Empty<NifShape>(); }

            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                connection.Open();

                var (pathNorm, state) = ReadMesh(connection, meshId);
                if (pathNorm == null) { error = $"no mesh {meshId}"; return Array.Empty<NifShape>(); }

                if (state == MeshGeomState.Parsed)
                {
                    var cached = ReadShapes(connection, meshId);
                    if (cached.Count > 0) return cached;

                    // Marked parsed but holding nothing: a half-written row from an interrupted save.
                    // Fall through and parse again rather than show an empty model.
                }

                if (state == MeshGeomState.Failed)
                {
                    error = "this mesh could not be read (recorded on an earlier attempt)";
                    return Array.Empty<NifShape>();
                }

                if (!locator.TryReadBytes(pathNorm, out var bytes, out error))
                {
                    // Not recorded as Failed: the file is missing, which is a fact about the setup and
                    // may be fixed by enabling a mod. Only a mesh that was READ and could not be
                    // PARSED is a property of the mesh itself.
                    return Array.Empty<NifShape>();
                }

                if (!NifReader.TryRead(bytes, out var model, out error))
                {
                    SetState(connection, meshId, MeshGeomState.Failed);
                    return Array.Empty<NifShape>();
                }

                Save(connection, meshId, model.Shapes);
                return model.Shapes;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Mesh geometry for {meshId}", ex);
                error = ex.Message;
                return Array.Empty<NifShape>();
            }
        }

        // Cached geometry only - no parsing, no file access. For a caller that wants to know whether a
        // mesh is ready without paying for it if it is not.
        public IReadOnlyList<NifShape> LoadCached(long meshId)
        {
            if (!File.Exists(_dbPath)) return Array.Empty<NifShape>();

            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                connection.Open();
                return ReadShapes(connection, meshId);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Mesh geometry read for {meshId}", ex);
                return Array.Empty<NifShape>();
            }
        }

        // How much of the index has been parsed. The numbers the report shows.
        public (int Parsed, int Failed, int Unparsed, long Bytes) Stats()
        {
            if (!File.Exists(_dbPath)) return (0, 0, 0, 0);

            try
            {
                using var connection = new SqliteConnection($"Data Source={_dbPath}");
                connection.Open();

                int parsed = 0, failed = 0, unparsed = 0;
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT GeomState, COUNT(*) FROM Mesh GROUP BY GeomState;";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        int n = r.GetInt32(1);
                        switch ((MeshGeomState)r.GetInt32(0))
                        {
                            case MeshGeomState.Parsed: parsed = n; break;
                            case MeshGeomState.Failed: failed = n; break;
                            default: unparsed = n; break;
                        }
                    }
                }

                long bytes;
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT COALESCE(SUM(LENGTH(Positions) + COALESCE(LENGTH(Normals),0) " +
                        "+ COALESCE(LENGTH(Uvs),0) + LENGTH(Indices)), 0) FROM MeshGeom;";
                    bytes = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                }

                return (parsed, failed, unparsed, bytes);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"Mesh geometry stats: {ex.Message}");
                return (0, 0, 0, 0);
            }
        }

        // Throws away every cached shape and sets each mesh back to unparsed. For the case where this
        // reader itself changed: the BLOBs are only as correct as the parser that wrote them, and a
        // fixed parser has to be able to invalidate what the broken one stored.
        public int ClearCache()
        {
            if (!File.Exists(_dbPath)) return 0;

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var transaction = connection.BeginTransaction();

            int removed;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM MeshGeom;";
                removed = cmd.ExecuteNonQuery();
            }
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE Mesh SET GeomState = 0;";
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
            return removed;
        }

        private static (string? PathNorm, MeshGeomState State) ReadMesh(SqliteConnection connection, long meshId)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT PathNorm, GeomState FROM Mesh WHERE MeshId = @id;";
            cmd.Parameters.AddWithValue("@id", meshId);

            using var r = cmd.ExecuteReader();
            if (!r.Read()) return (null, MeshGeomState.Unparsed);
            return (r.GetString(0), (MeshGeomState)r.GetInt32(1));
        }

        private static List<NifShape> ReadShapes(SqliteConnection connection, long meshId)
        {
            var result = new List<NifShape>();

            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "SELECT Name, Positions, Normals, Uvs, Indices, MovedByParent, DiffuseTexture, NormalTexture, EnvironmentMask, Tangents, Bitangents FROM MeshGeom " +
                "WHERE MeshId = @id ORDER BY ShapeIdx;";
            cmd.Parameters.AddWithValue("@id", meshId);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                result.Add(new NifShape
                {
                    Name = r.IsDBNull(0) ? "" : r.GetString(0),
                    Positions = ToFloats(r.IsDBNull(1) ? null : (byte[])r[1]),
                    Normals = ToFloats(r.IsDBNull(2) ? null : (byte[])r[2]),
                    Uvs = ToFloats(r.IsDBNull(3) ? null : (byte[])r[3]),
                    Indices = ToInts(r.IsDBNull(4) ? null : (byte[])r[4]),

                    // Carried through the cache rather than recomputed: a diagnostic that goes quiet
                    // after the first view is worse than none at all.
                    MovedByParentNode = !r.IsDBNull(5) && r.GetInt32(5) != 0,
                    DiffuseTexture = r.IsDBNull(6) ? "" : r.GetString(6),
                    NormalTexture = r.IsDBNull(7) ? "" : r.GetString(7),
                    EnvironmentMask = r.IsDBNull(8) ? "" : r.GetString(8),
                    Tangents = ToFloats(r.IsDBNull(9) ? null : (byte[])r[9]),
                    Bitangents = ToFloats(r.IsDBNull(10) ? null : (byte[])r[10]),
                });
            }

            return result;
        }

        private static void Save(SqliteConnection connection, long meshId, IReadOnlyList<NifShape> shapes)
        {
            using var transaction = connection.BeginTransaction();

            // Rewritten wholesale rather than merged: the shape list is a property of the file, and a
            // re-parse that produced fewer shapes must not leave the extra ones behind.
            using (var del = connection.CreateCommand())
            {
                del.Transaction = transaction;
                del.CommandText = "DELETE FROM MeshGeom WHERE MeshId = @id;";
                del.Parameters.AddWithValue("@id", meshId);
                del.ExecuteNonQuery();
            }

            using (var ins = connection.CreateCommand())
            {
                ins.Transaction = transaction;
                ins.CommandText =
                    "INSERT INTO MeshGeom (MeshId, ShapeIdx, Name, VertCount, TriCount, Positions, Normals, Uvs, Indices, MovedByParent, DiffuseTexture, NormalTexture, EnvironmentMask, Tangents, Bitangents) " +
                    "VALUES (@id, @idx, @name, @vc, @tc, @pos, @nrm, @uv, @ind, @moved, @tex, @ntex, @etex, @tan, @bit);";

                var pId = ins.Parameters.Add(new SqliteParameter("@id", meshId));
                var pIdx = ins.Parameters.Add(new SqliteParameter("@idx", 0));
                var pName = ins.Parameters.Add(new SqliteParameter("@name", ""));
                var pVc = ins.Parameters.Add(new SqliteParameter("@vc", 0));
                var pTc = ins.Parameters.Add(new SqliteParameter("@tc", 0));
                var pPos = ins.Parameters.Add(new SqliteParameter("@pos", Array.Empty<byte>()));
                var pNrm = ins.Parameters.Add(new SqliteParameter("@nrm", DBNull.Value));
                var pUv = ins.Parameters.Add(new SqliteParameter("@uv", DBNull.Value));
                var pInd = ins.Parameters.Add(new SqliteParameter("@ind", Array.Empty<byte>()));
                var pMoved = ins.Parameters.Add(new SqliteParameter("@moved", 0));
                var pTex = ins.Parameters.Add(new SqliteParameter("@tex", ""));
                var pNTex = ins.Parameters.Add(new SqliteParameter("@ntex", ""));
                var pETex = ins.Parameters.Add(new SqliteParameter("@etex", ""));
                var pTan = ins.Parameters.Add(new SqliteParameter("@tan", DBNull.Value));
                var pBit = ins.Parameters.Add(new SqliteParameter("@bit", DBNull.Value));

                for (int i = 0; i < shapes.Count; i++)
                {
                    var s = shapes[i];
                    pIdx.Value = i;
                    pName.Value = s.Name ?? "";
                    pVc.Value = s.VertexCount;
                    pTc.Value = s.TriangleCount;
                    pPos.Value = ToBytes(s.Positions);
                    pNrm.Value = s.Normals.Length > 0 ? ToBytes(s.Normals) : (object)DBNull.Value;
                    pUv.Value = s.Uvs.Length > 0 ? ToBytes(s.Uvs) : (object)DBNull.Value;
                    pInd.Value = ToBytes(s.Indices);
                    pMoved.Value = s.MovedByParentNode ? 1 : 0;
                    pTex.Value = s.DiffuseTexture ?? "";
                    pNTex.Value = s.NormalTexture ?? "";
                    pETex.Value = s.EnvironmentMask ?? "";

                    // Both or neither: a tangent without its bitangent is not a frame.
                    bool frame = s.HasTangentFrame;
                    pTan.Value = frame ? ToBytes(s.Tangents) : (object)DBNull.Value;
                    pBit.Value = frame ? ToBytes(s.Bitangents) : (object)DBNull.Value;
                    ins.ExecuteNonQuery();
                }
            }

            using (var state = connection.CreateCommand())
            {
                state.Transaction = transaction;
                state.CommandText = "UPDATE Mesh SET GeomState = @state WHERE MeshId = @id;";
                state.Parameters.AddWithValue("@state", (int)MeshGeomState.Parsed);
                state.Parameters.AddWithValue("@id", meshId);
                state.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        private static void SetState(SqliteConnection connection, long meshId, MeshGeomState state)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Mesh SET GeomState = @state WHERE MeshId = @id;";
            cmd.Parameters.AddWithValue("@state", (int)state);
            cmd.Parameters.AddWithValue("@id", meshId);
            cmd.ExecuteNonQuery();
        }

        // ---- BLOB encoding ----
        //
        // Raw little-endian, no header and no compression. The arrays are already the most compact
        // honest form of the data, and a length prefix would duplicate what the BLOB's own length says.
        // Buffer.BlockCopy rather than a loop of BitConverter calls: this runs over arrays of a hundred
        // thousand floats.
        //
        // A length that is not a whole number of elements means the BLOB was written by something other
        // than this code, or truncated. Returning empty beats handing a renderer two thirds of a vertex.

        internal static byte[] ToBytes(float[] values)
        {
            var bytes = new byte[values.Length * sizeof(float)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        internal static byte[] ToBytes(int[] values)
        {
            var bytes = new byte[values.Length * sizeof(int)];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        internal static float[] ToFloats(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
                return Array.Empty<float>();

            var values = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }

        internal static int[] ToInts(byte[]? bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length % sizeof(int) != 0)
                return Array.Empty<int>();

            var values = new int[bytes.Length / sizeof(int)];
            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
            return values;
        }
    }
}
