namespace SkyrimCraftingTool.Services
{
    // Draws the loading overlay for work that BLOCKS the UI thread (BusyService.Run): tab loads,
    // "All keywords". The overlay inside the main window cannot do that reliably - it is drawn by the
    // very thread the work then blocks, and on screen it came 1-2 s late or not at all (2026-10-13).
    // The app's presenter (View/BusyOverlayPresenter) is a window on a thread of its own, which keeps
    // drawing whatever the main thread does.
    //
    // All three are called on the main UI thread.
    public interface IBusyPresenter
    {
        // Puts the overlay up and returns once it is on screen (or a short timeout passed).
        // False when it cannot be shown (no main window, minimized) - the caller then falls back to
        // the in-window overlay.
        bool Show(string message);

        void SetMessage(string message);

        void Hide();
    }
}
