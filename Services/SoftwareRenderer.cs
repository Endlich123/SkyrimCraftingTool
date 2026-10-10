using System;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    // One surface to draw: geometry plus the maps that belong to it. The caller assembles these from a
    // NifShape and its decoded textures; the renderer never touches a file or a database.
    public sealed class RenderSurface
    {
        public float[] Positions = Array.Empty<float>();
        public float[] Normals = Array.Empty<float>();
        public float[] Uvs = Array.Empty<float>();
        public float[] Tangents = Array.Empty<float>();
        public float[] Bitangents = Array.Empty<float>();
        public int[] Indices = Array.Empty<int>();

        // Decoded BGRA. Diffuse supplies colour, Normal supplies per-pixel relief AND the specular mask
        // in its alpha, Environment is the second opinion on where the surface is shiny.
        public DdsImage? Diffuse;
        public DdsImage? Normal;
        public DdsImage? Environment;

        // A _msn.dds holds MODEL-space normals, which are not a perturbation of the surface normal and
        // must not be run through the tangent frame. Those files are rejected rather than misapplied.
        public bool NormalIsModelSpace;

        public bool HasTangentFrame =>
            Normals.Length > 0 && Tangents.Length == Normals.Length && Bitangents.Length == Normals.Length;
    }

    // What the finished image is written into. Plain BGRA so a WriteableBitmap can take it directly.
    public sealed class RenderTarget
    {
        public int Width;
        public int Height;
        public byte[] Pixels = Array.Empty<byte>();

        // Diagnostics, because "the preview looks wrong" needs to be answerable. Counted during the
        // draw, where the information exists, rather than guessed at afterwards.
        public int TrianglesSubmitted;
        public int TrianglesDrawn;
        public int PixelsShaded;
        public int SurfacesWithoutTangentFrame;
        public int SurfacesWithDerivedFrame;
    }

    // A rasteriser, written out rather than handed to Direct3D.
    //
    // WHY THIS EXISTS AT ALL. The preview ran on WPF's Viewport3D, which is fixed-function: it has no
    // shader stage, so a normal map cannot be applied per pixel. Most of what makes a Skyrim armour look
    // like anything lives in that map - the diffuse is often nearly flat, and in the latex case measured
    // at luma 0,1 of 255, where no amount of light can help. The workaround in place bakes the relief
    // into the diffuse once at load time with a fixed light, which paints the structure on: it does not
    // move when the model turns, and that is exactly what reads as cheap.
    //
    // WHY NOT D3D11. It works - a probe got a D3D11 device onto a D3D9Ex shared surface in this process,
    // all seven steps - but the route is interop: a hand-written vtable for IDirect3D9Ex, where one
    // missing entry crashed the test host with an access violation rather than failing, and where
    // CreateDeviceEx refused four of five plausible parameter sets. That is a lot of untestable surface
    // on the user's GPU driver for a window that inspects armour meshes.
    //
    // WHAT THIS BUYS INSTEAD: every pixel is decided by code in this file, so the whole renderer is a
    // pure function of its inputs - deterministic, no device, no driver, and therefore testable in the
    // same suite as everything else. The cost is CPU time, which measured at a few tens of milliseconds
    // for an armour at preview size, against a 16 ms budget nobody is holding this window to.
    public static class SoftwareRenderer
    {
        // Where the camera is and what it looks at. Kept separate from WPF's PerspectiveCamera so the
        // renderer can be exercised without a dispatcher.
        public sealed record Camera(
            double EyeX, double EyeY, double EyeZ,
            double TargetX, double TargetY, double TargetZ,
            double FieldOfViewDegrees = 45);

        // Mirrors MeshPreviewBuilder.LightingOptions so the two paths obey the same switches, and adds
        // the one the fixed-function path could not offer.
        public sealed record Lighting(
            bool Headlight = true,
            bool FixedLights = true,
            double Brightness = 1.0,
            bool Shine = true,
            bool NormalMapping = true)
        {
            public static readonly Lighting Default = new();
        }

        // THE LIGHT BUDGET. Ambient plus every directional a surface can face at once has to land near
        // 1,0, because the diffuse texture is MULTIPLIED by it: go past 1 and the texture is scaled into
        // white, which destroys detail exactly as thoroughly as leaving it black.
        //
        // The Viewport3D path used values almost twice these, and they were not wrong there - they were
        // measured, against a near-black fraction, and they fixed a real complaint. What they were
        // actually compensating for was the SSE normal decode being wrong: normals read as signed bytes
        // point the wrong way for every component above 127, so most of a model failed to catch any lamp
        // and the answer looked like "not enough light". With the decode fixed the same lamps reach far
        // more of the surface, and carrying the old levels over turned a thief armour's brown leather
        // into chrome. Compensation for a bug does not survive the bug being fixed.
        //
        // A surface facing the camera gets ambient + headlight + most of the key: 0,34 + 0,42 + ~0,3.
        private const double AmbientLevel = 0.34;
        private const double AmbientNoDirectional = 0.70;
        private const double HeadlightLevel = 0.42;
        private const double KeyLevel = 0.40;
        private const double FillLevel = 0.22;

        // Specular ADDS rather than multiplies, so it has its own budget: about 0,4 at a full highlight
        // across all lamps. It is what makes a near-black diffuse readable at all - the latex case, where
        // the texture measures luma 0,1 of 255 and no multiplier can lift it - and what turns leather
        // into metal when overdone.
        //
        // Power 6 rather than something tighter: at power 30 the highlight was too small to register on
        // cloth or latex, which is the case this is for.
        private const double ShineStrength = 0.32;
        private const double ShinePower = 6;

        // The floor under the specular mask. A raw mask makes things WORSE - the latex suit fell from 57
        // to 36 of 255 - because the game multiplies it by a specular strength and adds a cubemap
        // reflection on top, and with neither of those the mask only ever subtracts.
        private const double ShineMaskFloor = 0.5;

        // Draws the scene, optionally oversampled.
        //
        // SUPERSAMPLING IS WHAT TAKES THE STAIRCASE OFF THE SILHOUETTE. A rasteriser decides each pixel
        // by a single point test, so an edge crossing it is either in or out and a diagonal comes out as
        // steps. Drawing at twice the size and averaging four samples down gives the edge pixels their
        // intermediate values - the one artefact that bilinear texture filtering cannot touch, because
        // it is about geometry rather than texture.
        //
        // It costs four times the pixels, so the caller passes 1 while the model is being dragged and 2
        // once it comes to rest. Measured on a thief armour at 900×900: 27 ms against 95 ms at normal
        // framing. The still image is the one anybody looks at.
        public static RenderTarget Render(
            IReadOnlyList<RenderSurface> surfaces, Camera camera, Lighting lighting, int width, int height,
            int supersample = 1)
        {
            int scale = Math.Clamp(supersample, 1, 3);

            if (scale > 1)
            {
                var large = Render(surfaces, camera, lighting, Math.Max(1, width) * scale, Math.Max(1, height) * scale);
                return Downsample(large, Math.Max(1, width), Math.Max(1, height), scale);
            }

            var target = new RenderTarget { Width = Math.Max(1, width), Height = Math.Max(1, height) };
            target.Pixels = new byte[target.Width * target.Height * 4];

            if (surfaces is null || surfaces.Count == 0) return target;

            double distance = EyeDistance(camera);
            var view = ViewMatrix(camera);
            var projection = ProjectionMatrix(camera, target.Width / (double)target.Height,
                                              NearPlane(distance), FarPlane(distance));

            // Per pixel, not per triangle: the nearest surface wins wherever triangles overlap, which is
            // most of an armour with layered pieces. Initialised to +infinity so anything draws over it.
            var depth = new float[target.Width * target.Height];
            for (int i = 0; i < depth.Length; i++) depth[i] = float.PositiveInfinity;

            // The direction the eye looks, for the headlight and for every specular highlight.
            var viewDir = Normalise(camera.TargetX - camera.EyeX,
                                    camera.TargetY - camera.EyeY,
                                    camera.TargetZ - camera.EyeZ);
            if (viewDir.Length < 1e-9) viewDir = new Vec3(0, 1, 0);

            // Projection times view, in that order: a point goes through the view first, so the view sits
            // on the right. Written the other way round nothing is projected at all - every triangle
            // lands behind the camera and the image comes out empty.
            var viewProjection = Multiply(projection, view);

            // Each surface is transformed ONCE, here, and the result is shared by every band below. The
            // first version of this did the transform inside the band and was no faster than single
            // threaded - 142 ms became 167 - because an armour covers a fraction of the screen, so
            // repeating 23.000 vertex transforms sixteen times cost more than the pixels saved.
            var prepared = new PreparedSurface[surfaces.Count];
            for (int i = 0; i < surfaces.Count; i++)
            {
                prepared[i] = Prepare(surfaces[i], viewProjection, lighting, target);
                if (prepared[i] is not null) target.TrianglesSubmitted += surfaces[i].Indices.Length / 3;
            }

            // Then split into horizontal bands and give each one a thread. A band owns its rows outright,
            // so no two threads ever write the same pixel or the same depth entry and no lock is needed.
            //
            // THE OUTPUT DOES NOT DEPEND ON THE THREAD COUNT: a band draws the rows it owns and nothing
            // else, so the image is identical to the single-threaded one down to the byte. That is what
            // keeps the renderer testable - a parallel version whose result shifted with the machine it
            // ran on would not be worth having here.
            int bands = Math.Clamp(Environment.ProcessorCount, 1, 16);
            int rowsPerBand = Math.Max(1, (target.Height + bands - 1) / bands);
            var shadedPerBand = new int[bands];
            var drawnPerBand = new int[bands];

            System.Threading.Tasks.Parallel.For(0, bands, band =>
            {
                int fromRow = band * rowsPerBand;
                int toRow = Math.Min(target.Height, fromRow + rowsPerBand) - 1;
                if (fromRow > toRow) return;

                // Counted per band and summed afterwards. A shared counter would be a race, and the
                // band-local numbers are the ones that add up to the truth.
                var counts = new BandCounts();

                for (int i = 0; i < prepared.Length; i++)
                    if (prepared[i] is not null)
                        DrawBand(surfaces[i], prepared[i]!, viewDir, lighting, target, depth, fromRow, toRow, counts);

                shadedPerBand[band] = counts.PixelsShaded;
                drawnPerBand[band] = counts.TrianglesTouched;
            });

            foreach (int n in shadedPerBand) target.PixelsShaded += n;

            // A triangle spanning two bands is touched by both, so this is an upper bound on how many
            // reached the screen rather than a count of distinct triangles. Named accordingly.
            foreach (int n in drawnPerBand) target.TrianglesDrawn += n;

            return target;
        }

        private sealed class BandCounts
        {
            public int PixelsShaded;
            public int TrianglesTouched;
        }

        // Box filter, scale × scale samples per output pixel.
        //
        // ALPHA IS AVERAGED WITH THE REST and the colour is weighted BY it, because the background is
        // transparent: an edge pixel covered by one sample of four is a quarter-opaque pixel of the
        // model's colour, not a pixel three-quarters mixed with black. Averaging the colour flat would
        // draw a dark fringe all the way round every silhouette.
        private static RenderTarget Downsample(RenderTarget source, int width, int height, int scale)
        {
            var target = new RenderTarget
            {
                Width = width,
                Height = height,
                Pixels = new byte[width * height * 4],

                TrianglesSubmitted = source.TrianglesSubmitted,
                TrianglesDrawn = source.TrianglesDrawn,
                PixelsShaded = source.PixelsShaded,
                SurfacesWithoutTangentFrame = source.SurfacesWithoutTangentFrame,
                SurfacesWithDerivedFrame = source.SurfacesWithDerivedFrame,
            };

            int samples = scale * scale;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    double b = 0, g = 0, r = 0, a = 0;

                    for (int sy = 0; sy < scale; sy++)
                    {
                        int row = (y * scale + sy) * source.Width;
                        for (int sx = 0; sx < scale; sx++)
                        {
                            int at = (row + x * scale + sx) * 4;
                            double weight = source.Pixels[at + 3] / 255.0;

                            b += source.Pixels[at] * weight;
                            g += source.Pixels[at + 1] * weight;
                            r += source.Pixels[at + 2] * weight;
                            a += source.Pixels[at + 3];
                        }
                    }

                    int to = (y * width + x) * 4;
                    double coverage = a / (255.0 * samples);

                    if (coverage > 1e-6)
                    {
                        // Divided by the COVERAGE, not by the sample count: this undoes the weighting
                        // above and leaves the model's own colour, with the transparency carrying how
                        // much of the pixel it covers.
                        target.Pixels[to + 0] = (byte)Math.Clamp(Math.Round(b / (coverage * samples)), 0, 255);
                        target.Pixels[to + 1] = (byte)Math.Clamp(Math.Round(g / (coverage * samples)), 0, 255);
                        target.Pixels[to + 2] = (byte)Math.Clamp(Math.Round(r / (coverage * samples)), 0, 255);
                    }

                    target.Pixels[to + 3] = (byte)Math.Clamp(Math.Round(a / samples), 0, 255);
                }
            }

            return target;
        }

        // A surface with its vertices already in clip space, plus the decisions that are the same for
        // every band.
        private sealed class PreparedSurface
        {
            public Vec4[] Clip = Array.Empty<Vec4>();
            public Vec3[] World = Array.Empty<Vec3>();
            public bool PerPixelNormals;
            public bool StoredFrame;
        }

        private static PreparedSurface? Prepare(
            RenderSurface surface, Mat4 viewProjection, Lighting lighting, RenderTarget target)
        {
            int vertexCount = surface.Positions.Length / 3;
            if (vertexCount == 0 || surface.Indices.Length < 3) return null;

            var result = new PreparedSurface
            {
                Clip = new Vec4[vertexCount],
                World = new Vec3[vertexCount],
                PerPixelNormals = lighting.NormalMapping
                               && surface.Normal is not null
                               && !surface.NormalIsModelSpace
                               && surface.Uvs.Length / 2 == vertexCount,
                StoredFrame = surface.HasTangentFrame,
            };

            // A frame is needed to apply a normal map, and 27 % of shapes in the load order simply do not
            // carry one - the TANGENTS bit in their vertex descriptor is clear. Deriving one from the
            // triangle's own UV gradient covers them: it is what the frame means geometrically, so the
            // result matches up to a sign, and it keeps the map working rather than silently dropping it
            // for a quarter of the meshes.
            if (!result.StoredFrame) target.SurfacesWithoutTangentFrame++;
            if (!result.StoredFrame && result.PerPixelNormals) target.SurfacesWithDerivedFrame++;

            for (int v = 0; v < vertexCount; v++)
            {
                double x = surface.Positions[v * 3], y = surface.Positions[v * 3 + 1], z = surface.Positions[v * 3 + 2];
                result.World[v] = new Vec3(x, y, z);
                result.Clip[v] = Transform(viewProjection, x, y, z, 1);
            }

            return result;
        }

        private static void DrawBand(
            RenderSurface surface, PreparedSurface prepared, Vec3 viewDir, Lighting lighting,
            RenderTarget target, float[] depth, int fromRow, int toRow, BandCounts counts)
        {
            var clip = prepared.Clip;
            var world = prepared.World;
            int vertexCount = clip.Length;
            bool stored = prepared.StoredFrame;
            bool perPixelNormals = prepared.PerPixelNormals;

            // Hoisted: allocating these inside the triangle loop is what overflowed the stack in the
            // first version of this (CA2014 warned about exactly that).
            var polygon = new Vertex[8];
            var clipped = new Vertex[8];

            for (int t = 0; t + 2 < surface.Indices.Length; t += 3)
            {
                int i0 = surface.Indices[t], i1 = surface.Indices[t + 1], i2 = surface.Indices[t + 2];
                if ((uint)i0 >= vertexCount || (uint)i1 >= vertexCount || (uint)i2 >= vertexCount) continue;

                // Reject against this band's rows BEFORE building vertices or clipping. Those are the
                // steps that made the first banded version slower than no threads at all, and most
                // triangles miss most bands. Only triangles fully in front of the camera can be rejected
                // this cheaply; one crossing the near plane has no screen position until it is clipped,
                // so it falls through to the slow path.
                if (clip[i0].W > 0 && clip[i1].W > 0 && clip[i2].W > 0)
                {
                    double y0 = ScreenY(clip[i0], target.Height);
                    double y1 = ScreenY(clip[i1], target.Height);
                    double y2 = ScreenY(clip[i2], target.Height);

                    if (Math.Min(y0, Math.Min(y1, y2)) > toRow + 1) continue;
                    if (Math.Max(y0, Math.Max(y1, y2)) < fromRow - 1) continue;
                }

                // The flat normal, needed both as a fallback shading normal and to derive a tangent
                // frame for a shape that stores none.
                var face = Cross(Subtract(world[i1], world[i0]), Subtract(world[i2], world[i0]));
                if (face.Length > 1e-12) face = face.Normalised();

                polygon[0] = MakeVertex(surface, i0, clip[i0], world[i0], face, stored);
                polygon[1] = MakeVertex(surface, i1, clip[i1], world[i1], face, stored);
                polygon[2] = MakeVertex(surface, i2, clip[i2], world[i2], face, stored);

                // A triangle crossing the near plane has a vertex with w <= 0, and dividing by that
                // sends it to the wrong side of the screen as a huge smear. Clipping against w first is
                // what keeps a camera inside a model from painting over everything.
                int count = ClipAgainstNearPlane(polygon, 3, clipped);
                if (count < 3) continue;

                if (!stored && perPixelNormals)
                    DeriveTangentFrame(clipped, count, face);

                // Fan: the clip produces a convex polygon, so any one vertex works as the hub.
                for (int k = 1; k + 1 < count; k++)
                {
                    if (RasteriseTriangle(clipped[0], clipped[k], clipped[k + 1],
                                          surface, viewDir, lighting, perPixelNormals, target, depth,
                                          fromRow, toRow, counts))
                        counts.TrianglesTouched++;
                }
            }
        }

        private static double ScreenY(Vec4 clip, int height) => (0.5 - clip.Y / clip.W * 0.5) * height;

        // One vertex as the rasteriser wants it: clip-space position plus everything that interpolates.
        private struct Vertex
        {
            public Vec4 Clip;
            public Vec3 World;
            public Vec3 Normal;
            public Vec3 Tangent;
            public Vec3 Bitangent;
            public double U, V;
        }

        private static Vertex MakeVertex(
            RenderSurface surface, int index, Vec4 clip, Vec3 world, Vec3 face, bool stored)
        {
            var vertex = new Vertex { Clip = clip, World = world };

            // An empty normal collection is not an error: 720 of 11.393 shapes carry none. The face
            // normal stands in, which is what a flat-shaded surface would use anyway.
            if (surface.Normals.Length >= (index + 1) * 3)
            {
                var n = new Vec3(surface.Normals[index * 3], surface.Normals[index * 3 + 1], surface.Normals[index * 3 + 2]);
                vertex.Normal = n.Length > 1e-6 ? n.Normalised() : face;
            }
            else
            {
                vertex.Normal = face;
            }

            if (stored && surface.Tangents.Length >= (index + 1) * 3)
            {
                vertex.Tangent = new Vec3(surface.Tangents[index * 3], surface.Tangents[index * 3 + 1], surface.Tangents[index * 3 + 2]);
                vertex.Bitangent = new Vec3(surface.Bitangents[index * 3], surface.Bitangents[index * 3 + 1], surface.Bitangents[index * 3 + 2]);
            }

            if (surface.Uvs.Length >= (index + 1) * 2)
            {
                vertex.U = surface.Uvs[index * 2];
                vertex.V = surface.Uvs[index * 2 + 1];
            }

            return vertex;
        }

        // The tangent frame for a shape that stores none, from the triangle's own UV gradient. This is
        // the definition of the frame rather than an approximation of it: the tangent is the direction in
        // which U grows across the surface.
        private static void DeriveTangentFrame(Vertex[] polygon, int count, Vec3 face)
        {
            var e1 = Subtract(polygon[1].World, polygon[0].World);
            var e2 = Subtract(polygon[2 % count].World, polygon[0].World);

            double du1 = polygon[1].U - polygon[0].U, dv1 = polygon[1].V - polygon[0].V;
            double du2 = polygon[2 % count].U - polygon[0].U, dv2 = polygon[2 % count].V - polygon[0].V;

            double determinant = du1 * dv2 - du2 * dv1;

            // A degenerate UV triangle - both edges mapped to the same line in texture space - has no
            // tangent at all. Any vector perpendicular to the normal is as good as another there.
            Vec3 tangent;
            if (Math.Abs(determinant) < 1e-12)
            {
                tangent = Perpendicular(face);
            }
            else
            {
                double inverse = 1.0 / determinant;
                tangent = new Vec3(
                    inverse * (dv2 * e1.X - dv1 * e2.X),
                    inverse * (dv2 * e1.Y - dv1 * e2.Y),
                    inverse * (dv2 * e1.Z - dv1 * e2.Z));
                if (tangent.Length < 1e-12) tangent = Perpendicular(face);
            }

            for (int i = 0; i < count; i++)
            {
                polygon[i].Tangent = tangent;
                polygon[i].Bitangent = Cross(polygon[i].Normal, tangent);
            }
        }

        // Clips a polygon against w > epsilon. Interpolates every attribute along the cut edge, because
        // a new vertex needs a UV and a frame as much as a position.
        private static int ClipAgainstNearPlane(Vertex[] input, int count, Vertex[] output)
        {
            const double Epsilon = 1e-5;
            int written = 0;

            for (int i = 0; i < count; i++)
            {
                var current = input[i];
                var next = input[(i + 1) % count];

                bool currentIn = current.Clip.W > Epsilon;
                bool nextIn = next.Clip.W > Epsilon;

                if (currentIn) output[written++] = current;

                if (currentIn != nextIn)
                {
                    double t = (Epsilon - current.Clip.W) / (next.Clip.W - current.Clip.W);
                    output[written++] = Lerp(current, next, t);
                }
            }

            return written;
        }

        private static Vertex Lerp(Vertex a, Vertex b, double t) => new()
        {
            Clip = new Vec4(a.Clip.X + (b.Clip.X - a.Clip.X) * t,
                            a.Clip.Y + (b.Clip.Y - a.Clip.Y) * t,
                            a.Clip.Z + (b.Clip.Z - a.Clip.Z) * t,
                            a.Clip.W + (b.Clip.W - a.Clip.W) * t),
            World = Mix(a.World, b.World, t),
            Normal = Mix(a.Normal, b.Normal, t),
            Tangent = Mix(a.Tangent, b.Tangent, t),
            Bitangent = Mix(a.Bitangent, b.Bitangent, t),
            U = a.U + (b.U - a.U) * t,
            V = a.V + (b.V - a.V) * t,
        };

        // Returns whether this triangle reached the screen at all - which is deliberately a property of
        // the triangle and not of the band, so every band computes the same answer and the count can be
        // taken from any one of them. "Did it put a pixel in MY rows" would be a different number per
        // thread and would not add up to anything meaningful.
        private static bool RasteriseTriangle(
            Vertex a, Vertex b, Vertex c, RenderSurface surface, Vec3 viewDir,
            Lighting lighting, bool perPixelNormals, RenderTarget target, float[] depth,
            int fromRow, int toRow, BandCounts counts)
        {
            // To screen space. 1/w is kept per vertex: every attribute has to be interpolated in
            // PERSPECTIVE, meaning attribute/w is interpolated linearly and divided by the interpolated
            // 1/w at the end. Interpolating the attribute directly is the classic wrong way and shows as
            // textures that swim and bend across a surface seen at an angle.
            double wa = 1.0 / a.Clip.W, wb = 1.0 / b.Clip.W, wc = 1.0 / c.Clip.W;

            double ax = (a.Clip.X * wa * 0.5 + 0.5) * target.Width;
            double ay = (0.5 - a.Clip.Y * wa * 0.5) * target.Height;
            double bx = (b.Clip.X * wb * 0.5 + 0.5) * target.Width;
            double by = (0.5 - b.Clip.Y * wb * 0.5) * target.Height;
            double cx = (c.Clip.X * wc * 0.5 + 0.5) * target.Width;
            double cy = (0.5 - c.Clip.Y * wc * 0.5) * target.Height;

            double area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);

            // NO BACKFACE CULLING, deliberately. NIF winding is not guaranteed consistent, and the
            // Viewport3D path needed both Material and BackMaterial for the same reason - cull by
            // winding and half an armour disappears. Instead the shading normal is flipped to face the
            // eye further down, which is what BackMaterial amounted to.
            if (Math.Abs(area) < 1e-12) return false;

            int minX = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
            int maxX = Math.Min(target.Width - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
            int maxY = Math.Min(target.Height - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            if (minX > maxX || minY > maxY) return false;

            // On screen, so it counts. Narrowing to this band happens after the decision, so the count
            // stays the same whichever band is asking.
            int bandMinY = Math.Max(minY, fromRow);
            int bandMaxY = Math.Min(maxY, toRow);
            if (bandMinY > bandMaxY) return true;

            minY = bandMinY;
            maxY = bandMaxY;

            double inverseArea = 1.0 / area;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    double px = x + 0.5, py = y + 0.5;

                    // Barycentric weights from signed sub-triangle areas.
                    double w0 = ((bx - px) * (cy - py) - (cx - px) * (by - py)) * inverseArea;
                    double w1 = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) * inverseArea;
                    double w2 = 1.0 - w0 - w1;

                    const double Edge = -1e-9;
                    if (w0 < Edge || w1 < Edge || w2 < Edge) continue;

                    double oneOverW = w0 * wa + w1 * wb + w2 * wc;
                    if (oneOverW <= 0) continue;

                    // Depth as 1/w: larger means nearer, and it interpolates linearly in screen space,
                    // which is the whole reason graphics hardware stores it this way.
                    float z = (float)(1.0 / oneOverW);
                    int pixel = y * target.Width + x;
                    if (z >= depth[pixel]) continue;

                    double w = 1.0 / oneOverW;
                    double pa = w0 * wa * w, pb = w1 * wb * w, pc = w2 * wc * w;

                    double u = a.U * pa + b.U * pb + c.U * pc;
                    double v = a.V * pa + b.V * pb + c.V * pc;

                    var normal = Mix3(a.Normal, b.Normal, c.Normal, pa, pb, pc);
                    if (normal.Length < 1e-9) continue;
                    normal = normal.Normalised();

                    if (perPixelNormals)
                    {
                        var tangent = Mix3(a.Tangent, b.Tangent, c.Tangent, pa, pb, pc);
                        var bitangent = Mix3(a.Bitangent, b.Bitangent, c.Bitangent, pa, pb, pc);
                        normal = ApplyNormalMap(surface.Normal!, u, v, normal, tangent, bitangent);
                    }

                    // Facing the eye, whichever way the triangle was wound. See the culling note above.
                    if (Dot(normal, viewDir) > 0) normal = normal.Negated();

                    var position = Mix3(a.World, b.World, c.World, pa, pb, pc);
                    Shade(surface, lighting, u, v, normal, position, viewDir, target.Pixels, pixel * 4);

                    depth[pixel] = z;
                    counts.PixelsShaded++;
                }
            }

            return true;
        }

        // Perturbs the surface normal by the normal map, in tangent space.
        //
        // The frame is orthogonalised here rather than trusted as stored. Measured over the load order,
        // 98,9 % of SSE vertices have a tangent perpendicular to their normal and 1,1 % do not - meshes
        // exist whose stored frame is genuinely skewed, with N·T as high as 0,375 - and a skewed frame
        // tilts the mapped normal in a direction the artist never intended. Gram-Schmidt costs a dot
        // product and makes the bad cases behave.
        private static Vec3 ApplyNormalMap(DdsImage map, double u, double v, Vec3 normal, Vec3 tangent, Vec3 bitangent)
        {
            if (tangent.Length < 1e-9) return normal;

            // Remove the component along the normal, then rebuild the bitangent from the corrected pair
            // while keeping its handedness - mirrored UVs are common and flipping them inverts the relief.
            var t = Subtract(tangent, Scale(normal, Dot(normal, tangent)));
            if (t.Length < 1e-9) return normal;
            t = t.Normalised();

            var b = Cross(normal, t);
            if (Dot(b, bitangent) < 0) b = b.Negated();

            var sample = Sample(map, u, v);

            // Tangent-space normal maps store a direction over 0..255 per channel. Channels that decode
            // to zero mean the pixel carries no direction at all, which some maps do in unused regions.
            double nx = sample.R / 255.0 * 2.0 - 1.0;
            double ny = sample.G / 255.0 * 2.0 - 1.0;
            double nz = sample.B / 255.0 * 2.0 - 1.0;

            // Z is the component along the surface normal and is positive by construction. A map stored
            // without it - or one whose blue channel is empty - is reconstructed from the other two.
            if (nz <= 0.01)
            {
                double squared = 1.0 - nx * nx - ny * ny;
                nz = squared > 0 ? Math.Sqrt(squared) : 0;
            }

            var mapped = new Vec3(
                t.X * nx + b.X * ny + normal.X * nz,
                t.Y * nx + b.Y * ny + normal.Y * nz,
                t.Z * nx + b.Z * ny + normal.Z * nz);

            return mapped.Length < 1e-9 ? normal : mapped.Normalised();
        }

        private static void Shade(
            RenderSurface surface, Lighting lighting, double u, double v,
            Vec3 normal, Vec3 position, Vec3 viewDir, byte[] pixels, int at)
        {
            double scale = Math.Clamp(lighting.Brightness, 0.1, 3.0);

            // Base colour. A shape with no diffuse map is drawn mid-grey rather than skipped: a missing
            // texture is a thing the user needs to SEE in the shape list, not a hole in the model.
            double red = 0.72, green = 0.72, blue = 0.72;
            if (surface.Diffuse is not null)
            {
                var sample = Sample(surface.Diffuse, u, v);
                red = sample.R / 255.0;
                green = sample.G / 255.0;
                blue = sample.B / 255.0;
            }

            bool anyDirectional = lighting.Headlight || lighting.FixedLights;
            double ambient = (anyDirectional ? AmbientLevel : AmbientNoDirectional) * scale;

            double diffuse = 0, specular = 0;

            if (lighting.Headlight)
                Accumulate(Negated(viewDir), HeadlightLevel, normal, viewDir, lighting, ref diffuse, ref specular);

            if (lighting.FixedLights)
            {
                // The same two directions the Viewport3D path used, negated: WPF's DirectionalLight takes
                // the direction light TRAVELS, and the maths below wants the direction toward the lamp.
                Accumulate(Normalise(1, 1.5, 2), KeyLevel, normal, viewDir, lighting, ref diffuse, ref specular);
                Accumulate(Normalise(-1, -1.5, -1), FillLevel, normal, viewDir, lighting, ref diffuse, ref specular);
            }

            diffuse *= scale;

            if (specular > 0)
            {
                // Where the surface is allowed to shine. The normal map's ALPHA is Skyrim's specular
                // mask, present on 97,3 % of shapes against 57,0 % for a separate _m.dds; neither covers
                // both metal and latex alone, so the stronger of the two wins. The floor is there because
                // a raw mask only subtracts - see ShineMaskFloor.
                double mask = 1.0;
                if (surface.Normal is not null || surface.Environment is not null)
                {
                    double fromNormal = surface.Normal is not null ? Sample(surface.Normal, u, v).A / 255.0 : 0;
                    double fromEnvironment = surface.Environment is not null ? Luma(Sample(surface.Environment, u, v)) : 0;
                    mask = ShineMaskFloor + (1 - ShineMaskFloor) * Math.Max(fromNormal, fromEnvironment);
                }

                specular *= scale * mask;
            }

            // Diffuse MULTIPLIES the texture and specular ADDS to it. That is why a near-black diffuse
            // cannot be rescued by any light setting but does respond to shine.
            pixels[at + 0] = Clamp255((blue * (ambient + diffuse) + specular) * 255.0);
            pixels[at + 1] = Clamp255((green * (ambient + diffuse) + specular) * 255.0);
            pixels[at + 2] = Clamp255((red * (ambient + diffuse) + specular) * 255.0);
            pixels[at + 3] = 255;
        }

        private static void Accumulate(
            Vec3 toLight, double level, Vec3 normal, Vec3 viewDir, Lighting lighting,
            ref double diffuse, ref double specular)
        {
            double lambert = Dot(normal, toLight);
            if (lambert <= 0) return;

            diffuse += lambert * level;

            if (!lighting.Shine) return;

            // Blinn-Phong: the half vector between light and eye, which is cheaper than reflecting and
            // behaves better at grazing angles.
            var half = Add(toLight, Negated(viewDir));
            if (half.Length < 1e-9) return;
            half = half.Normalised();

            double highlight = Dot(normal, half);
            if (highlight > 0)
                specular += Math.Pow(highlight, ShinePower) * level * ShineStrength;
        }

        // BILINEAR sampling, with the two conventions a Skyrim texture needs: V runs DOWN in a NIF and up
        // in an image, and UVs routinely leave 0..1 and rely on the wrap.
        //
        // WHY NOT NEAREST, which is what this started as: a preview magnifies. The diffuse is decoded to
        // a mip of at most 1024 across and then stretched over an armour that fills the window, so one
        // texel covers several pixels and nearest sampling draws it as a square. That reads as "the
        // renderer is crude" rather than "the texture ran out", and it is the one artefact that survived
        // every other fix.
        //
        // The wrap is applied per CORNER rather than once to the centre, so a sample straddling the edge
        // of the texture blends with the far side instead of folding back on itself.
        private static (byte B, byte G, byte R, byte A) Sample(DdsImage image, double u, double v)
        {
            if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length < 4) return (128, 128, 128, 255);

            // Minus half a texel: a texel's colour belongs at its CENTRE, and sampling without this
            // shifts the whole texture by half a texel and makes the interpolation lopsided.
            double x = Wrap(u) * image.Width - 0.5;
            double y = Wrap(v) * image.Height - 0.5;

            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;

            int x1 = WrapIndex(x0 + 1, image.Width), y1 = WrapIndex(y0 + 1, image.Height);
            x0 = WrapIndex(x0, image.Width);
            y0 = WrapIndex(y0, image.Height);

            int row0 = y0 * image.Width, row1 = y1 * image.Width;
            int a = (row0 + x0) * 4, b = (row0 + x1) * 4;
            int c = (row1 + x0) * 4, d = (row1 + x1) * 4;

            if (d + 3 >= image.Pixels.Length || a + 3 >= image.Pixels.Length) return (128, 128, 128, 255);

            byte Mix(int channel)
            {
                double top = image.Pixels[a + channel] + (image.Pixels[b + channel] - image.Pixels[a + channel]) * fx;
                double bottom = image.Pixels[c + channel] + (image.Pixels[d + channel] - image.Pixels[c + channel]) * fx;
                return (byte)Math.Clamp(Math.Round(top + (bottom - top) * fy, MidpointRounding.AwayFromZero), 0, 255);
            }

            return (Mix(0), Mix(1), Mix(2), Mix(3));
        }

        // Texture coordinates wrap, so index -1 is the last column rather than an error.
        private static int WrapIndex(int index, int size)
        {
            if (size <= 0) return 0;
            index %= size;
            return index < 0 ? index + size : index;
        }

        // Into 0..1 for any input, including negatives. The modulo alone leaves -0,25 negative.
        private static double Wrap(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            value %= 1.0;
            return value < 0 ? value + 1.0 : value;
        }

        private static double Luma((byte B, byte G, byte R, byte A) p)
            => (0.299 * p.R + 0.587 * p.G + 0.114 * p.B) / 255.0;

        private static byte Clamp255(double value)
            => (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

        // ===================================================================
        // camera
        // ===================================================================

        // Near and far from how far the camera stands off, which the caller already derived from the
        // model's size. A fixed pair either clips the model away or wastes the whole depth range, and
        // 1/w depth puts most of its precision near the camera anyway.
        private static double NearPlane(double distance) => Math.Max(distance / 1000.0, 0.01);

        private static double FarPlane(double distance) => Math.Max(distance * 4.0, 10.0);

        private static double EyeDistance(Camera camera)
        {
            double dx = camera.TargetX - camera.EyeX, dy = camera.TargetY - camera.EyeY, dz = camera.TargetZ - camera.EyeZ;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static Mat4 ViewMatrix(Camera camera)
        {
            var forward = Normalise(camera.TargetX - camera.EyeX, camera.TargetY - camera.EyeY, camera.TargetZ - camera.EyeZ);
            if (forward.Length < 1e-9) forward = new Vec3(0, 1, 0);

            // Z-UP. Skyrim's world has Z up, and taking Y - the usual choice elsewhere - lays every model
            // on its side, which looks exactly like a broken mesh.
            var up = new Vec3(0, 0, 1);

            var right = Cross(forward, up);
            if (right.Length < 1e-9)
            {
                // Looking straight up or down: any horizontal direction will do as "right".
                right = new Vec3(1, 0, 0);
            }
            right = right.Normalised();
            var trueUp = Cross(right, forward).Normalised();

            var m = new Mat4();
            m.M[0] = right.X; m.M[4] = right.Y; m.M[8] = right.Z;
            m.M[1] = trueUp.X; m.M[5] = trueUp.Y; m.M[9] = trueUp.Z;
            m.M[2] = forward.X; m.M[6] = forward.Y; m.M[10] = forward.Z;

            m.M[12] = -(right.X * camera.EyeX + right.Y * camera.EyeY + right.Z * camera.EyeZ);
            m.M[13] = -(trueUp.X * camera.EyeX + trueUp.Y * camera.EyeY + trueUp.Z * camera.EyeZ);
            m.M[14] = -(forward.X * camera.EyeX + forward.Y * camera.EyeY + forward.Z * camera.EyeZ);
            m.M[15] = 1;

            return m;
        }

        private static Mat4 ProjectionMatrix(Camera camera, double aspect, double near, double far)
        {
            double fov = Math.Clamp(camera.FieldOfViewDegrees, 1, 179) * Math.PI / 180.0;
            double f = 1.0 / Math.Tan(fov / 2.0);

            var m = new Mat4();
            m.M[0] = f / Math.Max(aspect, 1e-6);
            m.M[5] = f;
            m.M[10] = far / (far - near);
            m.M[11] = 1;
            m.M[14] = -near * far / (far - near);
            return m;
        }

        // ===================================================================
        // small vector and matrix types
        //
        // Written out rather than taken from System.Windows.Media.Media3D: those are structs on a WPF
        // type that drags in a dispatcher-bound assembly, and this file exists precisely so the renderer
        // can run in a test with no UI thread.
        // ===================================================================

        private readonly struct Vec3
        {
            public readonly double X, Y, Z;
            public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
            public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
            public Vec3 Normalised() { double l = Length; return l < 1e-12 ? this : new Vec3(X / l, Y / l, Z / l); }
            public Vec3 Negated() => new(-X, -Y, -Z);
        }

        private readonly struct Vec4
        {
            public readonly double X, Y, Z, W;
            public Vec4(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }
        }

        private sealed class Mat4
        {
            // Column-major, 16 entries, so Transform below reads like the usual row-vector form.
            public readonly double[] M = new double[16];
        }

        private static Vec3 Normalise(double x, double y, double z) => new Vec3(x, y, z).Normalised();
        private static Vec3 Negated(Vec3 v) => v.Negated();
        private static Vec3 Add(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        private static Vec3 Subtract(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        private static Vec3 Scale(Vec3 v, double s) => new(v.X * s, v.Y * s, v.Z * s);
        private static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static Vec3 Cross(Vec3 a, Vec3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        private static Vec3 Mix(Vec3 a, Vec3 b, double t) => new(
            a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

        private static Vec3 Mix3(Vec3 a, Vec3 b, Vec3 c, double wa, double wb, double wc) => new(
            a.X * wa + b.X * wb + c.X * wc,
            a.Y * wa + b.Y * wb + c.Y * wc,
            a.Z * wa + b.Z * wb + c.Z * wc);

        // Any unit vector at right angles to the given one. Picking the axis the vector leans on LEAST is
        // what keeps the cross product from collapsing.
        private static Vec3 Perpendicular(Vec3 v)
        {
            var axis = Math.Abs(v.X) < Math.Abs(v.Y)
                ? (Math.Abs(v.X) < Math.Abs(v.Z) ? new Vec3(1, 0, 0) : new Vec3(0, 0, 1))
                : (Math.Abs(v.Y) < Math.Abs(v.Z) ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1));

            var result = Cross(v, axis);
            return result.Length < 1e-12 ? new Vec3(1, 0, 0) : result.Normalised();
        }

        private static Mat4 Multiply(Mat4 a, Mat4 b)
        {
            var m = new Mat4();
            for (int column = 0; column < 4; column++)
                for (int row = 0; row < 4; row++)
                {
                    double sum = 0;
                    for (int k = 0; k < 4; k++) sum += a.M[k * 4 + row] * b.M[column * 4 + k];
                    m.M[column * 4 + row] = sum;
                }
            return m;
        }

        private static Vec4 Transform(Mat4 m, double x, double y, double z, double w) => new(
            m.M[0] * x + m.M[4] * y + m.M[8] * z + m.M[12] * w,
            m.M[1] * x + m.M[5] * y + m.M[9] * z + m.M[13] * w,
            m.M[2] * x + m.M[6] * y + m.M[10] * z + m.M[14] * w,
            m.M[3] * x + m.M[7] * y + m.M[11] * z + m.M[15] * w);
    }
}
