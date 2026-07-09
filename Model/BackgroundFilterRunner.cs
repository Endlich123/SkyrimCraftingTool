namespace SkyrimCraftingTool.Model
{
    public sealed class BackgroundFilterRunner<TInput, TResult>
    {
        private CancellationTokenSource _cts;

        public void Run(
            TInput input,
            Func<TInput, CancellationToken, TResult> backgroundWork,
            Action<TResult> uiApply)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(() =>
            {
                try
                {
                    var result = backgroundWork(input, token);
                    if (token.IsCancellationRequested)
                        return;

                    System.Windows.Application.Current.Dispatcher.Invoke(() => uiApply(result));
                }
                catch (OperationCanceledException) { }
            }, token);
        }
    }

}
