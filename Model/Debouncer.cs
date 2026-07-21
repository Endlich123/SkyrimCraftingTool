namespace SkyrimCraftingTool.Model
{
    public sealed class Debouncer
    {
        private CancellationTokenSource _cts;

        public void Debounce(int delayMs, Action<CancellationToken> action)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token);
                    if (!token.IsCancellationRequested)
                        action(token);
                }
                catch (TaskCanceledException) { }
            }, token);
        }
    }

}
