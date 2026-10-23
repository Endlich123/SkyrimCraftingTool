using System;
using System.Windows;
using System.Windows.Threading;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.Services
{
    // The loading overlay (docs/Performance-Plan.md, P-P1): while a slow step runs, the window says so
    // and clicks do not land anywhere.
    //
    // ONE service for the whole shell, bound by MainWindow's overlay, so every tab looks the same while
    // it loads rather than each inventing its own.
    //
    // THE UI THREAD IS BLOCKED while the work runs - the work itself (reading NPCs, building thousands
    // of rows) still happens on it. An overlay drawn by that same thread came 1-2 s late or not at all
    // (2026-10-13), whatever Run waited for first. So Run hands the visible part to an IBusyPresenter:
    // in the app a window on a thread of its own (View/BusyOverlayPresenter), laid over the main
    // window. It is on screen before the work starts and keeps being drawn - with a moving bar - while
    // the main thread is blocked. The in-window overlay still stands meanwhile, invisible, to keep
    // clicks off the main window. Without a presenter (tests, minimized window) Run falls back to the
    // in-window overlay and a wait for frames (AfterTwoFrames).
    //
    // ALWAYS OFF AGAIN: the switch-off is queued in a finally, so a step that throws does not leave an
    // overlay that locks the whole program. It is queued at ContextIdle, after the new content has been
    // laid out and drawn - otherwise the overlay would vanish a moment before the result appears.
    //
    // NO TIME THRESHOLD, unlike the first plan ("only above ~200 ms"): with the work on the UI thread
    // there is no moment to decide that later - the overlay has to be drawn before the work starts or
    // not at all. Instead the callers only use it where something actually gets loaded (first visit,
    // a list after a scan), never for a tab that is already filled.
    public sealed class BusyService : ViewModelBase
    {
        // The one the shell binds to.
        public static BusyService Instance { get; } = new(() => Application.Current?.Dispatcher);

        private readonly Func<Dispatcher?> _dispatcher;
        private int _depth;   // Runs begun and not yet ended - the overlay stays while any is open

        // How Run waits for the overlay to be on screen before starting the work - AfterTwoFrames in
        // the app. The tests pass "right away": a window-less test thread draws no frames.
        private readonly Action<Dispatcher, Action> _whenDrawn;

        // The dispatcher is looked up per call: the instance is created before the Application is.
        internal BusyService(Func<Dispatcher?> dispatcher, Action<Dispatcher, Action>? whenDrawn = null)
        {
            _dispatcher = dispatcher;
            _whenDrawn = whenDrawn ?? AfterTwoFrames;
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(ShowsOwnPanel));
            }
        }

        private string _message = "";
        public string Message
        {
            get => _message;
            private set
            {
                if (!SetProperty(ref _message, value)) return;
                if (_presenterShown && value.Length > 0)
                {
                    try { _presenter?.SetMessage(value); }
                    catch (Exception ex) { AppLogger.LogError("Loading overlay window: message update failed", ex); }
                }
            }
        }

        public static void Run(string message, Action work) => Instance.RunCore(message, work);

        internal void RunCore(string message, Action work)
        {
            // Called from INSIDE running work (a sub-tab loading as its tab opens): run it in place.
            // The overlay is up, and the outer Run switches it off.
            if (_inWork)
            {
                Message = message;
                work();
                return;
            }

            var dispatcher = _dispatcher();

            // No dispatcher (tests, or before the app is up), or called from another thread: nothing to
            // draw an overlay on in time. Run straight away, with the state still correct around it.
            if (dispatcher == null || !dispatcher.CheckAccess())
            {
                Begin(message);
                try { Execute(work); }
                finally { End(); }
                return;
            }

            // Queued, also when another Run is still waiting: the queue keeps them in click order, so
            // two quick clicks end on the tab clicked last. Each Begin has its End; the overlay goes
            // when the last one is done.
            Begin(message);

            void Queue() =>
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    try
                    {
                        Execute(work);
                    }
                    finally
                    {
                        // Even when the work throws - the exception still goes its usual way (the
                        // app's unhandled-exception handler), but the overlay does not stay behind.
                        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(End));
                    }
                }));

            // The overlay window on its own thread (Weg 1, 2026-10-13): it is on screen once Show
            // returns and keeps being drawn while the work blocks this thread - so the work can be
            // queued straight away. Already up for an earlier Run: just queue.
            // _presenterShowing: Show waits for the window while letting this thread's messages through,
            // and a second Run arriving in that moment must not start a second Show.
            if (_presenterShown || _presenterShowing || TryShowPresenter(message))
            {
                Queue();
                return;
            }

            // No such window (none attached, main window minimized): the in-window overlay, with
            // the wait for frames. Less reliable - see AfterTwoFrames - but the work must run.
            _whenDrawn(dispatcher, Queue);
        }

        // ---- The overlay window on its own thread, for Run ----

        private IBusyPresenter? _presenter;
        private bool _presenterShown;
        private bool _presenterShowing;

        // Called once by the app (MainWindow) with the window-on-its-own-thread presenter.
        public static void AttachPresenter(IBusyPresenter presenter) => Instance._presenter = presenter;

        internal void AttachPresenterCore(IBusyPresenter presenter) => _presenter = presenter;

        private bool TryShowPresenter(string message)
        {
            if (_presenter == null) return false;

            bool shown;
            _presenterShowing = true;
            try { shown = _presenter.Show(message); }
            catch (Exception ex)
            {
                // An overlay that fails must never stop the load it was meant to announce.
                AppLogger.LogError("Loading overlay window could not be shown", ex);
                shown = false;
            }
            finally { _presenterShowing = false; }

            if (!shown) return false;
            _presenterShown = true;
            OnPropertyChanged(nameof(ShowsOwnPanel));
            return true;
        }

        private void HidePresenter()
        {
            if (!_presenterShown) return;
            _presenterShown = false;
            try { _presenter?.Hide(); }
            catch (Exception ex) { AppLogger.LogError("Loading overlay window could not be hidden", ex); }
            OnPropertyChanged(nameof(ShowsOwnPanel));
        }

        // Whether the in-window overlay draws its own dimmed panel. Not while the overlay window is up:
        // it would only appear at the very end, under the window, as a flash. The in-window overlay
        // still stands then (IsBusy) - invisible, but it keeps the clicks off the main window.
        public bool ShowsOwnPanel => IsBusy && !_presenterShown;

        // Calls `then` once the overlay has really been put on screen.
        //
        // The first version only queued the work at Background priority, trusting that the render
        // pass (a higher priority) would come first. It does not always: WPF paces its frames, and
        // when one was drawn a moment ago the next is scheduled by a timer - not yet in the queue. The
        // work then ran first, and the overlay appeared only together with the result, 1-2 s late,
        // which felt like a hang (reported 2026-10-13). For "All keywords" it was worse still: the
        // expensive part there is laying out thousands of chips, which happened in the SAME pass that
        // would have drawn the overlay.
        //
        // So: wait for CompositionTarget.Rendering, which fires right before each frame, and start
        // after the SECOND one - by then a frame containing the overlay has been handed over for
        // display. Two frames are ~30 ms. A timer stands behind it in case no frame comes (no visible
        // window): the work must start either way.
        private static void AfterTwoFrames(Dispatcher dispatcher, Action then)
        {
            int frames = 0;
            bool started = false;
            EventHandler? onRendering = null;
            DispatcherTimer? fallback = null;

            void Start()
            {
                if (started) return;
                started = true;
                System.Windows.Media.CompositionTarget.Rendering -= onRendering;
                fallback?.Stop();
                then();
            }

            onRendering = (_, _) => { if (++frames >= 2) Start(); };
            System.Windows.Media.CompositionTarget.Rendering += onRendering;

            fallback = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal,
                (_, _) => Start(), dispatcher);
            fallback.Start();
        }

        // ---- Work that runs OFF the UI thread (the scan) ----
        //
        // Here the UI thread stays free, so the overlay can show what Run cannot: a moving progress
        // bar, the current step, and the seconds so far. Used as a scope, because such work is an
        // async method with several steps, not one Action:
        //
        //     using var busy = BusyService.Show("Scanning…");
        //     ... await Task.Run(...) ... BusyService.SetMessage("Scanning: writing the item database…");
        //     busy.Dispose();   // early, before the result dialog - Dispose twice is harmless
        //
        // The using also covers the exception path: the overlay never outlives the work.
        //
        // blocksUiThread: for async work whose loop still runs ON the UI thread between its awaits
        // (preset patch, "Reset all items"). The in-window panel is never drawn then - reported
        // 2026-10-22, no loading screen at all - so the overlay window on its own thread goes up, as
        // for Run. Its text follows SetMessage, which is how such a loop reports "n / total".
        public static IDisposable Show(string message, bool blocksUiThread = false)
            => Instance.ShowCore(message, blocksUiThread);

        // The current step, from any thread - the scan's steps run on a worker.
        public static void SetMessage(string message) => Instance.SetMessageCore(message);

        internal IDisposable ShowCore(string message, bool blocksUiThread = false)
        {
            Begin(message);
            _scopes++;
            IsAnimated = true;
            StartClock();

            var dispatcher = _dispatcher();
            if (blocksUiThread && !_presenterShown && !_presenterShowing
                && dispatcher != null && dispatcher.CheckAccess())
                TryShowPresenter(message);

            return new Scope(this);
        }

        internal void SetMessageCore(string message)
        {
            var dispatcher = _dispatcher();
            if (dispatcher == null || dispatcher.CheckAccess()) Message = message;
            else dispatcher.BeginInvoke(new Action(() => { if (_depth > 0) Message = message; }));
        }

        private sealed class Scope : IDisposable
        {
            private BusyService? _owner;
            public Scope(BusyService owner) => _owner = owner;

            public void Dispose()
            {
                var owner = _owner;
                if (owner == null) return;
                _owner = null;
                owner.EndScope();
            }
        }

        private void EndScope()
        {
            if (--_scopes == 0)
            {
                IsAnimated = false;
                StopClock();
            }
            End();
        }

        private int _scopes;

        // True while a Show scope is open: the UI thread is free, so the progress bar really moves.
        private bool _isAnimated;
        public bool IsAnimated
        {
            get => _isAnimated;
            private set => SetProperty(ref _isAnimated, value);
        }

        // "12 s" - so a long scan visibly goes on. Ticked by a DispatcherTimer, which needs the free UI
        // thread that Show is for; without a dispatcher (tests) it stays empty.
        private string _elapsedText = "";
        public string ElapsedText
        {
            get => _elapsedText;
            private set => SetProperty(ref _elapsedText, value);
        }

        private DispatcherTimer? _clock;
        private DateTime _clockStart;

        private void StartClock()
        {
            if (_clock != null) return;
            var dispatcher = _dispatcher();
            if (dispatcher == null || !dispatcher.CheckAccess()) return;

            _clockStart = DateTime.Now;
            ElapsedText = "0 s";
            _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal,
                (_, _) => ElapsedText = $"{(int)(DateTime.Now - _clockStart).TotalSeconds} s", dispatcher);
            _clock.Start();
        }

        private void StopClock()
        {
            _clock?.Stop();
            _clock = null;
            ElapsedText = "";
        }

        private bool _inWork;

        private void Execute(Action work)
        {
            _inWork = true;
            try { work(); }
            finally { _inWork = false; }
        }

        private void Begin(string message)
        {
            _depth++;
            Message = message;
            IsBusy = true;
        }

        private void End()
        {
            if (--_depth > 0) return;
            _depth = 0;
            HidePresenter();
            IsBusy = false;
            Message = "";
        }
    }
}
