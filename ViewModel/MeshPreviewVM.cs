using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // One shape in the preview, as a row the user can switch off.
    //
    // Switchable because a NIF is several shapes and some of them are in the way: a sword carries
    // blood-decal and glow shapes that sit over the blade, armour carries a skin-shaped underlayer.
    // Measured: 4.427 files hold 11.393 shapes, so roughly two and a half per file, and the extra ones
    // are usually the effects.
    public sealed class MeshShapeVM : ViewModelBase
    {
        private bool _isVisible = true;

        public string Name { get; init; } = "";
        public int VertexCount { get; init; }
        public int TriangleCount { get; init; }
        public bool HasNormals { get; init; }
        public bool HasUvs { get; init; }

        // Whether a parent NiNode moved this shape. Shown because node composition is the likeliest
        // remaining cause of a shape sitting in the wrong place, and a displaced shape is otherwise
        // indistinguishable from an ugly one. See NifShape.MovedByParentNode.
        public bool MovedByParentNode { get; init; }

        // Geometry and maps in the form the rasteriser takes them. Not a WPF type: the renderer is a
        // plain function over arrays so that it can be tested without a window, and this row is where
        // its input is assembled.
        public RenderSurface Surface { get; init; } = new();

        // Does this shape carry a tangent frame of its own? Without one the renderer derives a frame per
        // triangle, which works but is worth being able to see when a surface looks odd.
        public bool HasTangentFrame { get; init; }

        // The diffuse texture this shape names. Its image is null when the file is missing or could not
        // be decoded - the shape then draws grey, which is a visible, explainable state rather than an
        // invisible one.
        public string TexturePath { get; init; } = "";
        public string TextureNote { get; set; } = "";

        public bool HasTexture => Surface.Diffuse is not null;

        public string NormalPath { get; init; } = "";
        public string EnvironmentPath { get; init; } = "";

        public bool IsVisible
        {
            get => _isVisible;
            set { if (SetProperty(ref _isVisible, value)) VisibilityChanged?.Invoke(); }
        }

        public event Action? VisibilityChanged;

        public string Summary =>
            $"{VertexCount:n0} vertices · {TriangleCount:n0} triangles" +
            (HasNormals ? "" : " · no normals") +
            (HasUvs ? "" : " · no UVs") +
            (HasTangentFrame ? "" : " · no tangent frame") +
            (MovedByParentNode ? " · placed by a parent node" : "") +
            (TextureNote.Length > 0 ? " · " + TextureNote : "");
    }

    // The mesh preview: parsed geometry on the left, a Viewport3D scene on the right.
    //
    // WHAT THIS IS NOT: a renderer trying to look like the game. There are no textures, no shaders and
    // no posed skeleton - it answers "is this the shape I think it is", which is the question the item
    // list cannot answer on its own.
    //
    // The scene is rebuilt rather than mutated whenever the camera moves. That sounds wasteful and is
    // not: the geometry objects are frozen and reused, so a rebuild re-points a ModelVisual3D at models
    // that already exist. What it buys is that there is exactly one path from state to scene, instead of
    // a camera that drifts out of step with the controls that moved it.
    public sealed class MeshPreviewVM : ViewModelBase
    {
        private readonly MeshGeometryStore _store;
        private readonly MeshLocator _locator;

        private double _yaw = 25;
        private double _pitch = 15;
        private double _zoom = 1.0;
        private string _status = "";
        private string _title = "";
        private MeshBounds _bounds = new();
        private bool _showTextures = true;
        private bool _headlight = true;
        private bool _fixedLights = true;
        private double _brightness = 1.0;
        private bool _shine = true;
        private bool _normalMapping = true;

        // The drawing area, in device pixels. Set by the view; until it reports a size there is nothing
        // sensible to rasterise into.
        private int _viewportWidth;
        private int _viewportHeight;
        private double _displayScale = 1.0;

        // Twice over in each direction, so four samples per pixel. Enough to take the staircase off a
        // silhouette; 3 costs nine times the pixels and is not visibly better on a model this size.
        private const int Supersample = 2;

        // About 2,6 million samples, which on the machine this was measured on is a settle frame of
        // roughly a quarter of a second. A 1000×650 drawing area fits; a maximised one does not.
        private const long SupersampleBudget = 2_600_000;

        // True while the mouse is dragging the model round. The frame is then drawn without
        // supersampling, because four times the pixels at 22 frames a second is not a trade anyone wants
        // mid-turn - and a moving image is the one place the staircase does not show.
        private bool _dragging;

        // One decode per distinct path, not per shape: a NIF's shapes routinely share a texture, and a
        // 512-px BC7 decode is milliseconds but not free.
        private readonly Dictionary<string, DdsImage?> _textureCache = new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<MeshShapeVM> Shapes { get; } = new();

        private ImageSource? _image;

        // The rendered frame. An image rather than a Viewport3D scene: see the class comment.
        public ImageSource? Image
        {
            get => _image;
            private set => SetProperty(ref _image, value);
        }

        private string _renderNote = "";

        public string RenderNote
        {
            get => _renderNote;
            private set => SetProperty(ref _renderNote, value);
        }

        public ICommand ResetViewCommand { get; }

        // Every mesh this item could be shown as, best first.
        //
        // WHY THIS IS A CHOICE AND NOT A LOOKUP: an armor names addons, several of them is normal, and
        // each addon has a male and a female slot. Measured on a real load order, 146 addons have a
        // vanilla male mesh beside a modded female one, and 22 of those park BARE SKIN in the male slot
        // because the garment has no male version. Picking one and saying nothing is what made a
        // female-only mod show vanilla hands.
        public ObservableCollection<MeshHit> Variants { get; } = new();

        public bool HasVariants => Variants.Count > 1;

        private MeshHit? _selectedVariant;
        public MeshHit? SelectedVariant
        {
            get => _selectedVariant;
            set
            {
                if (!SetProperty(ref _selectedVariant, value)) return;
                if (value != null) Load(value.MeshId, _title);
            }
        }

        public MeshPreviewVM(MeshGeometryStore store, MeshLocator locator)
        {
            _store = store;
            _locator = locator;

            ResetViewCommand = new RelayCommand(() =>
            {
                _yaw = 25;
                _pitch = 15;
                _zoom = 1.0;
                RaiseCamera();
            });
        }

        public string Title
        {
            get => _title;
            private set => SetProperty(ref _title, value);
        }

        // What happened, in one line, whether or not it worked. Never empty after a load: "nothing
        // visible and no explanation" is the one outcome a preview must not produce.
        public string Status
        {
            get => _status;
            private set => SetProperty(ref _status, value);
        }

        public bool HasGeometry => Shapes.Count > 0;

        // Textured or plain grey. Both are worth having: the texture says what the item IS, and grey
        // says what its SHAPE is, which a busy diffuse map can hide completely.
        public bool ShowTextures
        {
            get => _showTextures;
            set { if (SetProperty(ref _showTextures, value)) Rebuild(); }
        }

        // A lamp locked to the viewing direction. On by default, because without it the fixed lights
        // stand still while the camera orbits and half the model goes dark as you turn it - which looks
        // like a bad mesh rather than a badly placed light.
        public bool Headlight
        {
            get => _headlight;
            set { if (SetProperty(ref _headlight, value)) Rebuild(); }
        }

        // The two lamps that stand still in the world. They are what gives the model shape: a headlight
        // alone casts no visible shading, so a curved cuirass and a flat plate look the same.
        public bool FixedLights
        {
            get => _fixedLights;
            set { if (SetProperty(ref _fixedLights, value)) Rebuild(); }
        }

        // Some diffuse maps are genuinely very dark - Daedric armour is nearly black by design - and no
        // arrangement of lamps helps as much as simply turning them up.
        public double Brightness
        {
            get => _brightness;
            set { if (SetProperty(ref _brightness, Math.Clamp(value, 0.2, 2.5))) Rebuild(); }
        }

        // A specular highlight, which is ADDED rather than multiplied by the texture.
        //
        // It is the only thing that gives a black garment back its shape: measured on a real latex
        // bodysuit, the diffuse map has a mean luma of 0,1 of 255, so the model renders as a flat
        // silhouette and no amount of light changes it. On by default for that reason; off for anyone
        // who wants the plain matte look.
        public bool Shine
        {
            get => _shine;
            set { if (SetProperty(ref _shine, value)) Rebuild(); }
        }

        // Per-pixel normal mapping, which is the whole reason this window stopped using Viewport3D.
        //
        // Most of what makes a Skyrim armour look like anything is in the normal map, not the diffuse.
        // The earlier fixed-function path could not light a surface per pixel, so it shaded the relief
        // INTO the texture once with a fixed light: the detail showed, but its shading stayed put when
        // the model turned, which reads as painted-on. Here the surface normal is perturbed per pixel
        // and the highlight moves with the light, as it does in the game.
        //
        // Measured on a thief armour, which is the case the user named: the map changes 360.000 pixels
        // of a close view by a mean of 5,6 of 255 - leather grain, stitch relief and the woven panel
        // that the flat version renders as smooth plastic.
        public bool NormalMapping
        {
            get => _normalMapping;
            set { if (SetProperty(ref _normalMapping, value)) Rebuild(); }
        }

        public bool AnyTexture => Shapes.Any(s => s.HasTexture);

        // How many visible shapes name a texture that could not be loaded. Shown rather than swallowed:
        // a grey patch in an otherwise textured model is a question, and this is the answer.
        public int MissingTextureCount => Shapes.Count(s => s.IsVisible && s.TexturePath.Length > 0 && !s.HasTexture);

        public bool HasMissingTextures => MissingTextureCount > 0;

        // Set when any visible shape was placed by a parent node, so the window can say so once rather
        // than per row. This is the "treat it as a possible error source" half: if the model looks
        // wrong, this says whether node composition had a hand in it.
        public bool AnyShapeMovedByParentNode => Shapes.Any(s => s.IsVisible && s.MovedByParentNode);

        public PerspectiveCamera Camera => MeshPreviewBuilder.BuildCamera(_bounds, _yaw, _pitch, _zoom);

        public string BoundsText => _bounds.IsEmpty
            ? ""
            : $"{_bounds.SizeX:F1} × {_bounds.SizeY:F1} × {_bounds.SizeZ:F1} units";

        // Offers the whole set and loads the best one. The setter on SelectedVariant does the loading, so
        // this must fill the list before assigning.
        public void LoadVariants(IReadOnlyList<MeshHit> variants, string label)
        {
            Variants.Clear();
            foreach (var v in variants) Variants.Add(v);
            OnPropertyChanged(nameof(HasVariants));

            if (Variants.Count == 0)
            {
                Title = label;
                Status = "no mesh is recorded for this item";
                Shapes.Clear();
                Rebuild();
                OnPropertyChanged(nameof(HasGeometry));
                return;
            }

            _title = label;
            SelectedVariant = Variants[0];
        }

        public void Load(long meshId, string label)
        {
            Title = label;
            Shapes.Clear();

            var shapes = _store.GetOrParse(meshId, _locator, out var error);

            if (shapes.Count == 0)
            {
                _bounds = new MeshBounds();
                Status = error.Length > 0 ? error : "this mesh holds no geometry";
                Rebuild();
                return;
            }

            foreach (var shape in shapes)
            {
                var row = new MeshShapeVM
                {
                    Name = shape.Name.Length > 0 ? shape.Name : "(unnamed)",
                    VertexCount = shape.VertexCount,
                    TriangleCount = shape.TriangleCount,
                    HasNormals = shape.Normals.Length > 0,
                    HasUvs = shape.Uvs.Length > 0,
                    MovedByParentNode = shape.MovedByParentNode,
                    HasTangentFrame = shape.HasTangentFrame,
                    TexturePath = shape.DiffuseTexture,
                    NormalPath = shape.NormalTexture,
                    EnvironmentPath = shape.EnvironmentMask,

                    Surface = new RenderSurface
                    {
                        Positions = shape.Positions,
                        Normals = shape.Normals,
                        Uvs = shape.Uvs,
                        Tangents = shape.Tangents,
                        Bitangents = shape.Bitangents,
                        Indices = shape.Indices,

                        // A model-space normal map is not a perturbation of the surface normal, so
                        // running it through the tangent frame would light the model as though its
                        // surface pointed wherever the body part faces - damage, not detail.
                        NormalIsModelSpace = MeshPreviewBuilder.IsModelSpaceNormal(shape.NormalTexture),
                    },
                };

                LoadTextures(row);

                row.VisibilityChanged += OnShapeVisibilityChanged;
                Shapes.Add(row);
            }

            _bounds = MeshPreviewBuilder.Bounds(shapes);

            Status = $"{Shapes.Count} shape(s), {shapes.Sum(s => s.VertexCount):n0} vertices, " +
                     $"{shapes.Sum(s => s.TriangleCount):n0} triangles";

            Rebuild();
            OnPropertyChanged(nameof(HasGeometry));
            OnPropertyChanged(nameof(BoundsText));
            OnPropertyChanged(nameof(AnyTexture));
            OnPropertyChanged(nameof(MissingTextureCount));
            OnPropertyChanged(nameof(HasMissingTextures));
            OnPropertyChanged(nameof(AnyShapeMovedByParentNode));
        }

        // Decoded at 1024 px rather than full size. A diffuse map here is routinely 2048 or 4096 across,
        // so the mip chain already in the file does the downscaling for free and a 4096² BC7 decode is
        // 16 times the work.
        //
        // 1024 RATHER THAN THE 512 THIS SHIPPED WITH: a preview magnifies. An armour filling the window
        // stretches its texture well past one texel per pixel, and at 512 the texels become visible as
        // squares no amount of filtering can invent detail back into.
        private void LoadTextures(MeshShapeVM row)
        {
            row.Surface.Diffuse = Decode(row.TexturePath, 1024);
            row.Surface.Normal = Decode(row.NormalPath, 1024);

            // The environment mask only says which REGIONS reflect, not which texels, so a small mip
            // carries everything that is wanted from it.
            row.Surface.Environment = Decode(row.EnvironmentPath, 256);

            // A shape that names no texture is drawn grey and always was. Measured over a real load
            // order, 7,7 % of shapes are in this state - so "why is this part grey" is a question the
            // window should answer rather than leave to guesswork.
            if (row.TexturePath.Length == 0)
            {
                row.TextureNote = "no texture";
                return;
            }

            if (row.Surface.Diffuse is null)
            {
                row.TextureNote = "texture not loaded";
                return;
            }

            // "I see nothing" deserves an answer. A diffuse map can be essentially black - measured on a
            // real latex bodysuit: mean luma 0,1 of 255, 99,9 % of it below 16 - because the garment's
            // whole appearance in the game comes from the normal and specular maps beside it. With those
            // now actually used this is no longer a dead end, but it still explains a dark model.
            if (MeanLuma(row.Surface.Diffuse) < 8)
                row.TextureNote = "texture is almost black — its look comes from the normal and specular maps";
        }

        private DdsImage? Decode(string path, int maxSize)
        {
            if (path.Length == 0) return null;

            if (_textureCache.TryGetValue(path, out var cached)) return cached;

            if (!_locator.TryReadBytes(path, out var bytes, out _))
            {
                _textureCache[path] = null;
                return null;
            }

            if (!DdsReader.TryDecode(bytes, maxSize, out var image, out var error))
            {
                _textureCache[path] = null;
                AppLogger.LogWarning($"Mesh preview: {path} could not be decoded ({error})");
                return null;
            }

            _textureCache[path] = image;
            return image;
        }

        private static double MeanLuma(DdsImage image)
        {
            long opaque = 0;
            double sum = 0;

            // Transparent texels are not part of what anyone sees, and a mask-heavy texture is mostly
            // transparent - counting them would call nearly every texture black.
            for (int i = 0; i + 3 < image.Pixels.Length; i += 4)
            {
                if (image.Pixels[i + 3] < 8) continue;
                opaque++;
                sum += 0.11 * image.Pixels[i] + 0.59 * image.Pixels[i + 1] + 0.30 * image.Pixels[i + 2];
            }

            return opaque == 0 ? 255 : sum / opaque;
        }

        // How many samples per pixel this frame gets.
        //
        // Not simply "2 when still": the cost is the number of SAMPLES, not of output pixels, and a
        // maximised window zoomed in close is a different proposition from a small one. Measured on a
        // thief armour at 900×900, oversampled: 85 ms at normal framing but 537 ms with the model
        // filling the frame, because every one of those pixels is shaded four times. Past this budget
        // the sharper edge is not worth the wait, so the frame is drawn at one sample and stays slightly
        // stepped - which is the honest trade rather than a half-second pause nobody asked for.
        internal int ChosenSupersample()
        {
            if (_dragging) return 1;

            long samples = (long)_viewportWidth * _viewportHeight * Supersample * Supersample;
            return samples > SupersampleBudget ? 1 : Supersample;
        }

        // Told by the view when the model is being moved, and when it has come to rest. Coming to rest
        // redraws, which is the whole point: the sharp frame is the one that stays on screen.
        public void SetDragging(bool dragging)
        {
            if (_dragging == dragging) return;

            _dragging = dragging;
            if (!dragging) Rebuild();
        }

        // Orbit. Degrees per pixel is a feel decision, not a measurement; pitch is clamped in the camera
        // builder so the view cannot flip through the pole.
        public void Orbit(double deltaX, double deltaY)
        {
            _yaw += deltaX * 0.4;
            _pitch += deltaY * 0.4;
            RaiseCamera();
        }

        // Multiplicative, so a notch feels the same at every distance. Bounded well inside the near and
        // far planes the camera builder derives from the model size.
        public void Zoom(double notches)
        {
            _zoom = Math.Clamp(_zoom * Math.Pow(1.15, notches), 0.05, 40);
            RaiseCamera();
        }

        private void OnShapeVisibilityChanged()
        {
            Rebuild();
            OnPropertyChanged(nameof(AnyShapeMovedByParentNode));
            OnPropertyChanged(nameof(MissingTextureCount));
            OnPropertyChanged(nameof(HasMissingTextures));
        }

        private void RaiseCamera()
        {
            OnPropertyChanged(nameof(Camera));
            Rebuild();
        }

        // The size of the drawing area, in DEVICE pixels, and the display scale it was measured at.
        //
        // BOTH NUMBERS ARE NEEDED, and leaving the second one out is a bug that looks exactly like a bad
        // renderer. The frame is rasterised in device pixels, but WPF lays an Image out in
        // device-INDEPENDENT ones. A bitmap declared at 96 dpi is taken to be one DIP per pixel, so on a
        // display at 150 % every rasterised pixel is blown up to a 1,5-pixel block - visibly chunky -
        // and a third of the image is pushed outside the control. Declaring the bitmap at 96 × scale
        // makes one rasterised pixel land on exactly one screen pixel again.
        public void SetViewportSize(int width, int height, double scale = 1.0)
        {
            // Capped so that maximising the window cannot turn a 20 ms frame into a second. Above this
            // the image is stretched, which on a mesh inspector is invisible next to the wait.
            const int Largest = 1600;

            int w = Math.Clamp(width, 0, Largest);
            int h = Math.Clamp(height, 0, Largest);
            double s = scale > 0.1 ? scale : 1.0;

            if (w == _viewportWidth && h == _viewportHeight && Math.Abs(s - _displayScale) < 1e-6) return;

            _viewportWidth = w;
            _viewportHeight = h;
            _displayScale = s;
            Rebuild();
        }

        // Draws the frame.
        //
        // SYNCHRONOUS, ON THE UI THREAD, and that is a measured decision rather than an oversight: a
        // thief armour of 36.228 triangles takes 17 to 46 ms at normal framing on this machine, and the
        // worst case measured - a close-up filling 900×900 - is about 110 ms. Orbiting at that rate is
        // responsive, and a background thread would buy smoothness at the cost of frames arriving out of
        // order behind the mouse.
        private void Rebuild()
        {
            if (_viewportWidth <= 0 || _viewportHeight <= 0) { Image = null; return; }

            var surfaces = new List<RenderSurface>();
            foreach (var shape in Shapes)
            {
                if (!shape.IsVisible) continue;

                // Textures off means grey, for everything. A per-shape colour was the first idea and was
                // worse: it reads as information the tool does not have, since nothing here knows a
                // material. The renderer draws a surface with no diffuse in exactly that grey, so this
                // is a matter of handing it one or not.
                if (_showTextures)
                {
                    surfaces.Add(shape.Surface);
                }
                else
                {
                    surfaces.Add(new RenderSurface
                    {
                        Positions = shape.Surface.Positions,
                        Normals = shape.Surface.Normals,
                        Uvs = shape.Surface.Uvs,
                        Tangents = shape.Surface.Tangents,
                        Bitangents = shape.Surface.Bitangents,
                        Indices = shape.Surface.Indices,
                    });
                }
            }

            if (surfaces.Count == 0) { Image = null; RenderNote = ""; return; }

            var camera = Camera;
            var eye = camera.Position;
            var target = eye + camera.LookDirection;

            var frame = SoftwareRenderer.Render(
                surfaces,
                new SoftwareRenderer.Camera(eye.X, eye.Y, eye.Z, target.X, target.Y, target.Z, camera.FieldOfView),
                new SoftwareRenderer.Lighting(_headlight, _fixedLights, _brightness, _shine, _normalMapping),
                _viewportWidth, _viewportHeight,
                supersample: ChosenSupersample());

            // 96 x the display scale, so one rasterised pixel is one screen pixel. See SetViewportSize.
            double dpi = 96.0 * _displayScale;
            var bitmap = BitmapSource.Create(frame.Width, frame.Height, dpi, dpi,
                                             PixelFormats.Bgra32, null, frame.Pixels, frame.Width * 4);
            bitmap.Freeze();
            Image = bitmap;

            // Said out loud rather than left to be noticed: a shape with no stored tangent frame gets one
            // derived per triangle, which is close but not what the artist authored.
            RenderNote = frame.SurfacesWithDerivedFrame > 0 && _normalMapping
                ? $"{frame.SurfacesWithDerivedFrame} shape(s) carry no tangent frame — one was derived from their UVs"
                : "";

            OnPropertyChanged(nameof(Image));
        }
    }
}
