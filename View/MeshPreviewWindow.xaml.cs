using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.View
{
    // The mesh preview window.
    //
    // The mouse handling lives here rather than in the view model on purpose: it is pixels and capture,
    // which are facts about a control, and the view model takes the result as "turn by this much". That
    // keeps the orbit arithmetic testable without a window.
    public partial class MeshPreviewWindow : Window
    {
        private readonly MeshPreviewVM _vm;

        private Point _lastPosition;
        private bool _dragging;

        // Redraws at full quality a moment after the model stops moving.
        //
        // A BUTTON RELEASE IS NOT THE ONLY WAY TO STOP MOVING: the wheel has no "up", so zooming without
        // this would ask for an oversampled frame at every notch and turn a scroll into a slideshow. One
        // timer, restarted by any interaction, covers both and needs no special case for either.
        //
        // It lives here rather than in the view model so the view model stays free of a dispatcher - the
        // same reason the mouse arithmetic is here.
        private readonly System.Windows.Threading.DispatcherTimer _settle = new()
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };

        public MeshPreviewWindow(MeshPreviewVM vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            _settle.Tick += (_, _) => { _settle.Stop(); _vm.SetDragging(false); };
            Closed += (_, _) => _settle.Stop();
        }

        // Called by every interaction that moves the view. Marks it as moving and pushes the full-quality
        // redraw out to 180 ms after the last one.
        private void Interacting()
        {
            _vm.SetDragging(true);
            _settle.Stop();
            _settle.Start();
        }

        // The one call site a caller needs. Takes the ids rather than geometry so the window owns the
        // loading - which may parse a 3,6 MB file - instead of the caller having to know that.
        public static void Show(Window? owner, long meshId, string label,
                                MeshGeometryStore? store = null, MeshLocator? locator = null)
        {
            var vm = new MeshPreviewVM(store ?? new MeshGeometryStore(), locator ?? MeshLocator.Build(includeTextures: true));
            vm.Load(meshId, label);

            var window = new MeshPreviewWindow(vm) { Owner = owner };
            window.Show();
        }

        // The form a caller should normally use: hand over every mesh the item could be shown as, so the
        // window can offer the choice. An armor genuinely has several - race variants, and a male and a
        // female slot per addon - and picking one silently is what showed bare vanilla hands for
        // female-only mods.
        public static void Show(Window? owner, IReadOnlyList<MeshHit> variants, string label,
                                MeshGeometryStore? store = null, MeshLocator? locator = null)
        {
            var vm = new MeshPreviewVM(store ?? new MeshGeometryStore(), locator ?? MeshLocator.Build(includeTextures: true));
            vm.LoadVariants(variants, label);

            var window = new MeshPreviewWindow(vm) { Owner = owner };
            window.Show();
        }

        private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _lastPosition = e.GetPosition(Viewport);
            _dragging = true;
            Viewport.CaptureMouse();

            // Drop the oversampling for the duration: four times the pixels mid-turn is not worth it,
            // and a moving image is the one place a stepped edge does not show.
            Interacting();
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;

            var position = e.GetPosition(Viewport);

            // Dragging right turns the model to the right, dragging up tips its top toward the viewer -
            // hence the inverted Y. Anything else feels like the mouse is fighting back.
            Interacting();
            _vm.Orbit(position.X - _lastPosition.X, -(position.Y - _lastPosition.Y));

            _lastPosition = position;
        }

        private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            Viewport.ReleaseMouseCapture();
            Interacting();
        }

        private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            Interacting();
            _vm.Zoom(e.Delta / 120.0);
        }

        // The renderer draws into exactly as many pixels as the control occupies, so it has to be told
        // the size. In DEVICE pixels, not WPF's device-independent ones: on a scaled display the two
        // differ by the scale factor, and rasterising at the smaller number would show as a soft image.
        private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                        ?? Matrix.Identity;

            _vm.SetViewportSize(
                (int)Math.Round(e.NewSize.Width * scale.M11),
                (int)Math.Round(e.NewSize.Height * scale.M22),
                scale.M11);
        }
    }
}
