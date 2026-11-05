using System;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    // CanExecuteChanged hangs on CommandManager.RequerySuggested: WPF asks every bound canExecute
    // again after keyboard/mouse input and focus changes. A state that changes WITHOUT input (an
    // async job finishing, a rebuild, an import) only shows on the next input - call
    // RaiseCanExecuteChanged there, or bind IsEnabled to a property that raises PropertyChanged
    // (what the Reset buttons do).

    // -----------------------------
    // Parameterloser RelayCommand
    // -----------------------------
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();

        // Without a canExecute there is nothing to ask again - no subscription at all.
        public event EventHandler? CanExecuteChanged
        {
            add { if (_canExecute != null) CommandManager.RequerySuggested += value; }
            remove { if (_canExecute != null) CommandManager.RequerySuggested -= value; }
        }

        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }

    // -----------------------------
    // Generischer RelayCommand<T>
    // -----------------------------
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        private readonly Func<T?, bool>? _canExecute;

        public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            if (_canExecute == null)
                return true;

            return parameter is T t ? _canExecute(t) : _canExecute(default);
        }

        public void Execute(object? parameter)
        {
            if (parameter is T t)
                _execute(t);
            else
                _execute(default);
        }

        public event EventHandler? CanExecuteChanged
        {
            add { if (_canExecute != null) CommandManager.RequerySuggested += value; }
            remove { if (_canExecute != null) CommandManager.RequerySuggested -= value; }
        }

        public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }
}
