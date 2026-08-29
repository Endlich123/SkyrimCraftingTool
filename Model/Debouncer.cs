namespace SkyrimCraftingTool.Model
{
    /// <summary>
    /// Coalesces a burst of calls into a single delayed action - each new call cancels the
    /// previous pending one and restarts the timer.
    /// <para>
    /// One instance is shared per ViewModel for autosave (see the <c>_saveDebouncer</c> fields in
    /// MainContentVM / PresetsConfigVM / EnchantmentMenuVM). Because a single instance only ever
    /// keeps ONE pending action, changing several DIFFERENT save targets within the debounce
    /// window means only the last one is actually persisted. Any bulk / multi-target path
    /// (multi-select apply, preset bulk apply) MUST therefore persist directly and awaited
    /// (MainContentVM.PersistFieldAsync, PresetsConfigVM.SavePresetImmediate) instead of relying
    /// on this.
    /// </para>
    /// </summary>
    public sealed class Debouncer
    {
        private readonly object _gate = new();
        // Serializes the scheduled run against an explicit FlushAsync so the same save can't run twice
        // concurrently (SQLite would throw "database is locked").
        private readonly SemaphoreSlim _runLock = new(1, 1);

        private CancellationTokenSource? _cts;
        private Func<CancellationToken, Task>? _pending;

        public void Debounce(int delayMs, Action<CancellationToken> action)
            => DebounceAsync(delayMs, ct => { action(ct); return Task.CompletedTask; });

        // Awaits the async action so exceptions thrown after the first 'await' inside it are
        // caught here too - a plain Action<CancellationToken> with an async lambda fires the
        // work "async void", which lets exceptions escape uncaught.
        public void DebounceAsync(int delayMs, Func<CancellationToken, Task> asyncAction)
        {
            CancellationTokenSource newCts;
            lock (_gate)
            {
                _cts?.Cancel();
                newCts = new CancellationTokenSource();
                _cts = newCts;
                _pending = asyncAction;
            }
            var token = newCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token);
                    if (token.IsCancellationRequested) return;

                    await _runLock.WaitAsync(token);
                    try
                    {
                        if (token.IsCancellationRequested) return;
                        await asyncAction(token);
                        lock (_gate)
                        {
                            if (ReferenceEquals(_pending, asyncAction))
                                _pending = null;
                        }
                    }
                    finally
                    {
                        _runLock.Release();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppLogger.LogError("Debounced action failed", ex);
                }
            }, token);
        }

        /// <summary>
        /// Runs any still-pending debounced action immediately (skipping the remaining delay) and
        /// waits for it. No-op if nothing is pending. Called on app shutdown so an edit made within
        /// the last debounce window isn't lost.
        /// </summary>
        public async Task FlushAsync()
        {
            Func<CancellationToken, Task>? pending;
            lock (_gate)
            {
                _cts?.Cancel();
                pending = _pending;
                _pending = null;
            }

            if (pending == null) return;

            await _runLock.WaitAsync();
            try
            {
                await pending(CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Debounced flush failed", ex);
            }
            finally
            {
                _runLock.Release();
            }
        }
    }
}
