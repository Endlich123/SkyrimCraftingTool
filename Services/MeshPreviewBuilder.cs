using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace SkyrimCraftingTool.Services
{
    // An axis-aligned box around some geometry, plus the two numbers a camera needs: where to look and
    // how far back to stand.
    public sealed class MeshBounds
    {
        public double MinX = double.MaxValue, MaxX = double.MinValue;
        public double MinY = double.MaxValue, MaxY = double.MinValue;
        public double MinZ = double.MaxValue, MaxZ = double.MinValue;

        public bool IsEmpty => MinX > MaxX;

        public double SizeX => IsEmpty ? 0 : MaxX - MinX;
        public double SizeY => IsEmpty ? 0 : MaxY - MinY;
        public double SizeZ => IsEmpty ? 0 : MaxZ - MinZ;

        // The point a camera should aim at. Not the origin: a helmet sits around Z = 125 and a sword
        // around Y = 50, so aiming at the origin would put either off screen.
        public Point3D Centre => IsEmpty
            ? new Point3D(0, 0, 0)
            : new Point3D((MinX + MaxX) / 2, (MinY + MaxY) / 2, (MinZ + MaxZ) / 2);

        // The longest edge. Used as the unit of camera distance, so one gesture feels the same on a ring
        // and on a greatsword - measured extents run from about 4 units to about 230.
        public double LongestEdge => Math.Max(SizeX, Math.Max(SizeY, SizeZ));

        public void Include(double x, double y, double z)
        {
            if (x < MinX) MinX = x;
            if (x > MaxX) MaxX = x;
            if (y < MinY) MinY = y;
            if (y > MaxY) MaxY = y;
            if (z < MinZ) MinZ = z;
            if (z > MaxZ) MaxZ = z;
        }

        public override string ToString() => IsEmpty
            ? "empty"
            : $"X[{MinX:F1}..{MaxX:F1}] Y[{MinY:F1}..{MaxY:F1}] Z[{MinZ:F1}..{MaxZ:F1}]";
    }

    // What the preview needs that is not rasterising: the bounds of a model, the camera that orbits it,
    // and the one filename convention that decides whether a normal map may be used at all.
    //
    // THIS CLASS USED TO BE THE RENDERER. It built a Viewport3D scene - geometry, brushes, lights,
    // materials - and most of it is gone because SoftwareRenderer replaced it. What went with it is
    // worth naming, because the reasons were real and are now handled elsewhere rather than dropped:
    //
    //   - Material AND BackMaterial, because NIF winding is not reliably consistent and a single-sided
    //     material made half an armour vanish. The rasteriser does not cull by winding at all and turns
    //     the shading normal toward the eye instead.
    //
    //   - Leaving an absent normal collection EMPTY so WPF would compute its own - 720 of 11.393 shapes
    //     carry none. The rasteriser falls back to the triangle's own face normal.
    //
    //   - Relief shaded INTO the texture with a fixed light, because a fixed-function pipeline cannot
    //     light a surface per pixel. That is now real per-pixel normal mapping, so the shading moves
    //     with the light instead of staying painted on.
    //
    // Z-up stayed here, in BuildCamera: Skyrim's world has Z up and nearly every graphics convention
    // assumes Y, and getting it wrong lays every model on its side.
    public static class MeshPreviewBuilder
    {
        public static MeshBounds Bounds(IEnumerable<NifShape> shapes)
        {
            var bounds = new MeshBounds();
            foreach (var shape in shapes)
                for (int i = 0; i + 2 < shape.Positions.Length; i += 3)
                    bounds.Include(shape.Positions[i], shape.Positions[i + 1], shape.Positions[i + 2]);
            return bounds;
        }

        // A camera that frames the whole model, whatever its size.
        //
        // The distance is derived from the longest edge rather than fixed, because the meshes this has to
        // show span from a 4-unit ring to a 230-unit greatsword. Factor 2,2 leaves a margin instead of
        // filling the viewport edge to edge, and the 45-degree field of view is WPF's own default.
        public static PerspectiveCamera BuildCamera(MeshBounds bounds, double yawDegrees, double pitchDegrees, double zoom)
        {
            var centre = bounds.Centre;
            double distance = Math.Max(bounds.LongestEdge, 1.0) * 2.2 / Math.Max(zoom, 0.05);

            double yaw = yawDegrees * Math.PI / 180.0;
            double pitch = Math.Clamp(pitchDegrees, -89, 89) * Math.PI / 180.0;

            // Orbit in Skyrim's frame: X right, Y into the screen, Z up. Starting yaw 0 puts the camera
            // on -Y looking toward +Y, which is the direction an armour or weapon mesh faces.
            double horizontal = distance * Math.Cos(pitch);
            var eye = new Point3D(
                centre.X + horizontal * Math.Sin(yaw),
                centre.Y - horizontal * Math.Cos(yaw),
                centre.Z + distance * Math.Sin(pitch));

            return new PerspectiveCamera
            {
                Position = eye,
                LookDirection = centre - eye,

                // Z-up. Without this the model is drawn lying on its side and looks broken.
                UpDirection = new Vector3D(0, 0, 1),

                FieldOfView = 45,
                NearPlaneDistance = Math.Max(distance / 1000.0, 0.01),
                FarPlaneDistance = distance * 10,
            };
        }

        // A model-space normal map means something different in every channel: it is not a perturbation
        // of the surface normal, so running it through a tangent frame lights the model as though its
        // surface pointed wherever the body part faces. The filename is the only marker Skyrim gives.
        public static bool IsModelSpaceNormal(string path)
            => path.EndsWith("_msn.dds", StringComparison.OrdinalIgnoreCase);
    }
}
