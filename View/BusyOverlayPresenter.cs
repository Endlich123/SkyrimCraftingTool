using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.View
{
    // The loading overlay for work that blocks the main UI thread (docs/Performance-Plan.md, P-P1,
    // "Weg 1"): a borderless, half-transparent window laid exactly over the main window's content,
    // living on a THREAD OF ITS OWN.
    //
    // Why a thread of its own: in WPF the thread that loads a tab is the thread that draws the window.
    // An overlay inside the main window was switched on before the load, but only drawn once that
    // thread got round to it - 1-2 s late, together with the result, or not at all; the wait cursor
    // (set by Windows directly) came alone and it felt like a crash (2026-10-13). This window is drawn
    // by its own thread, which is never busy: it is on screen at once, and its progress bar keeps
    // moving while the main thread is blocked.
    //
    // Started once, with the main window, and kept: the thread and the window are built in advance and
    // only shown and hidden. Creating them on demand would cost exactly the moment they are needed for.
    //
    // NOTHING HERE TOUCHES THE APP'S RESOURCES from the overlay thread. Application resources belong
    // to the main thread (there is an implicit TextBlock style in Styles/Controls.xaml), so every
    // element gets an explicit, empty Style - which stops the implicit lookup - and the theme colours
    // are read on the main thread and handed over as plain Color values.
    public sealed class BusyOverlayPresenter : IBusyPresenter
    {
        private readonly Window _main;
        private readonly Dispatcher _overlay;

        // Built on the overlay thread, only ever touched there.
        private Window _window = null!;
        private TextBlock _text = null!;
        private Rectangle _dim = null!;
        private Border _box = null!;
        private ProgressBar _bar = null!;
        private bool _ownerSet;

        private BusyOverlayPresenter(Window main, Dispatcher overlay)
        {
            _main = main;
            _overlay = overlay;
        }

        // Called on the main thread once the main window exists.
        public static BusyOverlayPresenter Start(Window main)
        {
            Dispatcher? overlay = null;
            using var ready = new ManualResetEventSlim();

            var thread = new Thread(() =>
            {
                overlay = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,   // never keeps the process alive after the main window closes
                Name = "Loading overlay",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();

            var presenter = new BusyOverlayPresenter(main, overlay!);
            overlay!.Invoke(presenter.Build);
            main.Closed += (_, _) => overlay.BeginInvokeShutdown(DispatcherPriority.Normal);
            return presenter;
        }

        // ---- IBusyPresenter, called on the main thread ----

        public bool Show(string message)
        {
            if (!_main.IsVisible || _main.WindowState == WindowState.Minimized) return false;
            if (_main.Content is not FrameworkElement root || root.ActualWidth <= 0) return false;

            // Where, in screen pixels: the main window's content area.
            var topLeft = root.PointToScreen(new Point(0, 0));
            var bottomRight = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));
            var ownerHwnd = new WindowInteropHelper(_main).Handle;
            var colours = new Colours(
                ColourOf("ColorBackgroundBase", Colors.Black),
                ColourOf("ColorBackgroundControl", Colors.DimGray),
                ColourOf("ColorBorder", Colors.Gray),
                ColourOf("ColorTextPrimary", Colors.White),
                ColourOf("ColorAccent", Colors.SteelBlue));

            // Wait until the overlay window is up, but keep THIS thread's messages flowing meanwhile:
            // putting up a window owned by this thread's window can need an answer from this thread,
            // and a plain blocking wait could then hang until its timeout.
            var mainDispatcher = _main.Dispatcher;
            var frame = new DispatcherFrame();
            void Done() => mainDispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => frame.Continue = false));

            _overlay.BeginInvoke(new Action(() =>
            {
                try
                {
                    Present(message, topLeft, bottomRight, ownerHwnd, colours);
                    // ContextIdle on the overlay thread: after its layout and render pass.
                    _overlay.BeginInvoke(DispatcherPriority.ContextIdle, new Action(Done));
                }
                catch (Exception ex)
                {
                    AppLogger.LogError("Loading overlay window: show failed", ex);
                    Done();
                }
            }));

            var timeout = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Send,
                (s, _) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; }, mainDispatcher);
            timeout.Start();

            try
            {
                Dispatcher.PushFrame(frame);
            }
            catch (InvalidOperationException)
            {
                // Called where the main dispatcher is suspended (inside a layout pass): no waiting
                // possible. The window is on its way regardless; report it as not shown so the caller
                // uses the in-window overlay, and take this one down again.
                timeout.Stop();
                Hide();
                return false;
            }
            finally
            {
                timeout.Stop();
            }

            return true;
        }

        public void SetMessage(string message)
            => _overlay.BeginInvoke(new Action(() => _text.Text = message));

        public void Hide()
            => _overlay.BeginInvoke(new Action(() => { if (_window.IsVisible) _window.Hide(); }));

        // ---- overlay thread ----

        private void Build()
        {
            _dim = new Rectangle { Style = new Style(typeof(Rectangle)), Opacity = 0.6 };

            _text = new TextBlock
            {
                Style = new Style(typeof(TextBlock)),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 10),
            };

            _bar = new ProgressBar
            {
                Style = new Style(typeof(ProgressBar)),
                IsIndeterminate = true,
                Height = 6,
                BorderThickness = new Thickness(0),
            };

            var panel = new StackPanel { Style = new Style(typeof(StackPanel)), MinWidth = 280 };
            panel.Children.Add(_text);
            panel.Children.Add(_bar);

            _box = new Border
            {
                Style = new Style(typeof(Border)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 14, 24, 14),
                Child = panel,
            };

            var grid = new Grid { Style = new Style(typeof(Grid)) };
            grid.Children.Add(_dim);
            grid.Children.Add(_box);

            _window = new Window
            {
                Style = new Style(typeof(Window)),
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,   // never takes focus or activation from the main window
                Topmost = false,         // above its owner only, not above other programs
                Cursor = Cursors.Wait,
                Title = "Loading",
                Content = grid,
            };
            new WindowInteropHelper(_window).EnsureHandle();
        }

        private void Present(string message, Point topLeftPx, Point bottomRightPx, IntPtr ownerHwnd, Colours c)
        {
            if (!_ownerSet && ownerHwnd != IntPtr.Zero)
            {
                // Owned by the main window: stays above it, minimizes with it, never above other apps.
                new WindowInteropHelper(_window).Owner = ownerHwnd;
                _ownerSet = true;
            }

            _dim.Fill = Frozen(c.Base);
            _box.Background = Frozen(c.Control);
            _box.BorderBrush = Frozen(c.Border);
            _text.Foreground = Frozen(c.Text);
            _bar.Foreground = Frozen(c.Accent);
            _bar.Background = Frozen(c.Base);
            _text.Text = message;

            _window.Show();

            // Placed in physical pixels, after Show: exact on any DPI, and WPF takes its own Left/Top/
            // Width/Height from the move that follows.
            var hwnd = new WindowInteropHelper(_window).Handle;
            SetWindowPos(hwnd, IntPtr.Zero,
                (int)Math.Round(topLeftPx.X), (int)Math.Round(topLeftPx.Y),
                (int)Math.Round(bottomRightPx.X - topLeftPx.X), (int)Math.Round(bottomRightPx.Y - topLeftPx.Y),
                SWP_NOZORDER | SWP_NOACTIVATE);
        }

        private Color ColourOf(string key, Color fallback)
            => _main.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

        private static SolidColorBrush Frozen(Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            return brush;
        }

        private readonly record struct Colours(Color Base, Color Control, Color Border, Color Text, Color Accent);

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    }
}
