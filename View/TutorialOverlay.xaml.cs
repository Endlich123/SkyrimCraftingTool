using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SkyrimCraftingTool.View
{
    // Plays one TutorialTour over the main window: dims everything, cuts out the step's target,
    // and puts the explanation next to it.
    //
    // It lives in the main window's own visual tree, not in a window of its own like the loading
    // overlay (BusyOverlayPresenter): that one exists because the UI thread is blocked while it shows.
    // During a tour the thread is free, and being in the same tree is what lets the cut-out follow the
    // target through every scroll and resize - LayoutUpdated below.
    public partial class TutorialOverlay : UserControl
    {
        private const double HolePadding = 4;
        private const double Gap = 12;
        private const double EdgeMargin = 10;

        private TutorialTour? _tour;
        private ITutorialHost? _host;
        private int _index;
        private int _direction = 1;
        private bool _stopped;
        private FrameworkElement? _target;
        private Window? _window;

        // Expanders the tour opened. Closed again at the end, so the item view looks the way the
        // user left it.
        private readonly List<Expander> _openedExpanders = new();

        private Rect _lastHole = Rect.Empty;
        private Size _lastOverlay;
        private Size _lastCallout;

        public TutorialOverlay()
        {
            InitializeComponent();
        }

        public bool IsRunning => _tour != null;

        public void Start(TutorialTour tour, object? hostContext)
        {
            if (tour.Steps.Count == 0) return;
            if (IsRunning) End();

            _tour = tour;
            _host = hostContext as ITutorialHost;
            _stopped = false;
            _direction = 1;
            _window = Window.GetWindow(this);

            Visibility = Visibility.Visible;
            LayoutUpdated += OnLayoutUpdated;
            if (_window != null) _window.PreviewKeyDown += OnWindowKeyDown;

            ShowStep(0);
        }

        public void End()
        {
            LayoutUpdated -= OnLayoutUpdated;
            if (_window != null) _window.PreviewKeyDown -= OnWindowKeyDown;

            foreach (var expander in _openedExpanders)
                expander.IsExpanded = false;
            _openedExpanders.Clear();

            _tour = null;
            _host = null;
            _target = null;
            _window = null;
            _lastHole = Rect.Empty;
            Visibility = Visibility.Collapsed;
        }

        // ---- steps ----

        private void ShowStep(int index)
        {
            if (_tour == null) return;

            while (index >= 0 && index < _tour.Steps.Count)
            {
                var step = _tour.Steps[index];

                if (!string.IsNullOrWhiteSpace(step.Prepare) && !Prepare(step.Prepare!))
                {
                    ShowStopped(step.PrepareFailedText
                                ?? "This part of the tour needs data to point at. Run Scan/Rescan in the Items tab first, then start the tour again.");
                    return;
                }

                OpenExpanders(step.Expand);
                var target = string.IsNullOrWhiteSpace(step.Target) ? null : Find(step.Target!);

                if (target == null && step.SkipIfMissing && !string.IsNullOrWhiteSpace(step.Target))
                {
                    index += _direction;
                    continue;
                }

                _index = index;
                _target = target;
                if (target != null)
                {
                    // The top of it into view: a whole section is often taller than the pane, and
                    // asking for all of it scrolls to wherever WPF likes.
                    target.BringIntoView(new Rect(0, 0, target.ActualWidth, Math.Min(target.ActualHeight, 240)));
                }

                Fill(step);
                ForceReposition();
                return;
            }

            // Ran off either end while skipping: forward means done, backward means stay at the start.
            if (index >= _tour.Steps.Count)
            {
                End();
                return;
            }

            _direction = 1;
            ShowStep(0);
        }

        private bool Prepare(string action)
        {
            try
            {
                var done = _host?.PrepareTutorialStep(action) ?? false;
                // The view behind a freshly selected item is built by the next layout pass; without it
                // the target the step points at does not exist yet.
                _window?.UpdateLayout();
                return done;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Tutorial: preparing '{action}' failed", ex);
                return false;
            }
        }

        private void OpenExpanders(List<string>? ids)
        {
            if (ids == null) return;

            foreach (var id in ids)
            {
                if (Find(id) is Expander { IsExpanded: false } expander)
                {
                    expander.IsExpanded = true;
                    _openedExpanders.Add(expander);
                    _window?.UpdateLayout();
                }
            }
        }

        private void Fill(TutorialStep step)
        {
            var count = _tour!.Steps.Count;

            TitleText.Text = step.Title;
            BodyText.Text = step.Text;
            Counter.Text = $"{_index + 1} / {count}";

            var hasMore = !string.IsNullOrWhiteSpace(step.More);
            MoreExpander.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
            MoreExpander.IsExpanded = false;
            MoreText.Text = step.More ?? "";

            SkipButton.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Visible;
            BackButton.IsEnabled = _index > 0;
            NextButton.Content = _index == count - 1 ? "Finish" : "Next";
        }

        private void ShowStopped(string text)
        {
            _stopped = true;
            _target = null;

            TitleText.Text = _tour?.Title ?? "Tour";
            BodyText.Text = text;
            Counter.Text = "";
            MoreExpander.Visibility = Visibility.Collapsed;
            SkipButton.Visibility = Visibility.Collapsed;
            BackButton.Visibility = Visibility.Collapsed;
            NextButton.Content = "Close";

            ForceReposition();
        }

        // ---- buttons ----

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_tour == null) return;
            if (_stopped || _index >= _tour.Steps.Count - 1) { End(); return; }

            _direction = 1;
            ShowStep(_index + 1);
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_tour == null || _index == 0) return;

            _direction = -1;
            ShowStep(_index - 1);
        }

        private void Skip_Click(object sender, RoutedEventArgs e) => End();

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;

            End();
            e.Handled = true;
        }

        // ---- finding the target ----

        // The first VISIBLE element carrying this id. Visible matters: the same template is
        // instantiated for armor and weapon blocks, and the hidden one would put the cut-out at 0,0.
        private FrameworkElement? Find(string id)
        {
            if (_window == null) return null;

            var stack = new Stack<DependencyObject>();
            stack.Push(_window);

            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (ReferenceEquals(node, this)) continue;

                if (node is FrameworkElement fe)
                {
                    if (!fe.IsVisible) continue;   // nothing under a hidden element is on screen either
                    if (string.Equals(Tutorial.GetId(fe), id, StringComparison.Ordinal) && fe.ActualWidth > 0)
                        return fe;
                }

                var children = VisualTreeHelper.GetChildrenCount(node);
                for (int i = children - 1; i >= 0; i--)
                    stack.Push(VisualTreeHelper.GetChild(node, i));
            }

            return null;
        }

        // ---- layout ----

        private void ForceReposition()
        {
            _lastHole = Rect.Empty;
            _lastOverlay = default;
            _lastCallout = default;
            UpdateLayout();
            Reposition();
        }

        private void OnLayoutUpdated(object? sender, EventArgs e) => Reposition();

        private void Reposition()
        {
            if (_tour == null || ActualWidth <= 0 || ActualHeight <= 0) return;

            var overlay = new Size(ActualWidth, ActualHeight);
            var hole = HoleRect();

            Callout.Measure(new Size(Callout.Width, double.PositiveInfinity));
            var callout = Callout.DesiredSize;

            // LayoutUpdated fires for every layout pass anywhere, including the ones this method
            // causes; only move things when something actually moved.
            if (Same(hole, _lastHole) && overlay == _lastOverlay && callout == _lastCallout) return;
            _lastHole = hole;
            _lastOverlay = overlay;
            _lastCallout = callout;

            var full = new RectangleGeometry(new Rect(overlay));
            if (hole.IsEmpty)
            {
                Dim.Data = full;
                Frame.Visibility = Visibility.Collapsed;
            }
            else
            {
                Dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hole, 4, 4));
                Frame.Visibility = Visibility.Visible;
                Frame.Width = hole.Width;
                Frame.Height = hole.Height;
                Canvas.SetLeft(Frame, hole.X);
                Canvas.SetTop(Frame, hole.Y);
            }

            var at = PlaceCallout(hole, callout, overlay);
            Canvas.SetLeft(Callout, at.X);
            Canvas.SetTop(Callout, at.Y);
        }

        // The target's bounds in overlay coordinates, cut down to what its scroll viewers actually
        // show - a field scrolled half out of the pane gets a hole round the visible half only.
        private Rect HoleRect()
        {
            if (_target == null || !_target.IsVisible || PresentationSource.FromVisual(_target) == null)
                return Rect.Empty;

            try
            {
                var rect = _target.TransformToVisual(this).TransformBounds(new Rect(_target.RenderSize));

                for (var p = VisualTreeHelper.GetParent(_target); p != null; p = VisualTreeHelper.GetParent(p))
                {
                    if (p is ScrollViewer sv && sv.IsVisible)
                    {
                        var view = sv.TransformToVisual(this).TransformBounds(new Rect(sv.RenderSize));
                        rect.Intersect(view);
                        if (rect.IsEmpty) return Rect.Empty;
                    }
                }

                rect.Inflate(HolePadding, HolePadding);
                rect.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
                return rect;
            }
            catch (InvalidOperationException)
            {
                // Not a descendant of the same window any more (its tab was swapped out).
                return Rect.Empty;
            }
        }

        // Right of the target, else left, below, above; if none of those has room (a target that
        // fills most of the window, like the whole detail pane) the callout sits in its bottom-right
        // corner, over the target. Without a target: in the middle.
        private static Point PlaceCallout(Rect hole, Size c, Size o)
        {
            if (hole.IsEmpty)
                return Clamp(new Point((o.Width - c.Width) / 2, (o.Height - c.Height) / 2), c, o);

            double alignedTop = Math.Max(EdgeMargin, Math.Min(hole.Top, o.Height - c.Height - EdgeMargin));
            double alignedLeft = Math.Max(EdgeMargin, Math.Min(hole.Left, o.Width - c.Width - EdgeMargin));

            var candidates = new[]
            {
                new Point(hole.Right + Gap, alignedTop),
                new Point(hole.Left - Gap - c.Width, alignedTop),
                new Point(alignedLeft, hole.Bottom + Gap),
                new Point(alignedLeft, hole.Top - Gap - c.Height),
            };

            foreach (var p in candidates)
            {
                if (p.X >= EdgeMargin && p.X + c.Width <= o.Width - EdgeMargin &&
                    p.Y >= EdgeMargin && p.Y + c.Height <= o.Height - EdgeMargin)
                    return p;
            }

            var inside = new Point(Math.Min(hole.Right, o.Width) - c.Width - Gap,
                                   Math.Min(hole.Bottom, o.Height) - c.Height - Gap);
            return Clamp(inside, c, o);
        }

        private static Point Clamp(Point p, Size c, Size o)
            => new(Math.Max(EdgeMargin, Math.Min(p.X, o.Width - c.Width - EdgeMargin)),
                   Math.Max(EdgeMargin, Math.Min(p.Y, o.Height - c.Height - EdgeMargin)));

        private static bool Same(Rect a, Rect b)
        {
            if (a.IsEmpty || b.IsEmpty) return a.IsEmpty && b.IsEmpty;
            return Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 &&
                   Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;
        }
    }
}
