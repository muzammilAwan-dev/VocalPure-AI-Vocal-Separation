using System;
using System.Windows.Input;

namespace VocalPure.ViewModels
{
    /// <summary>
    /// Generic RelayCommand implementation supporting both parameterized and parameterless commands.
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action? _execute;
        private readonly Action<object?>? _executeWithParam;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            if (canExecute != null)
                _canExecute = _ => canExecute();
        }

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _executeWithParam = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter)
        {
            return _canExecute?.Invoke(parameter) ?? true;
        }

        public void Execute(object? parameter)
        {
            if (_execute != null)
                _execute();
            else
                _executeWithParam?.Invoke(parameter);
        }

        public void RaiseCanExecuteChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Generic RelayCommand with strongly-typed parameter.
    /// </summary>
    public sealed class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        private readonly Predicate<T?>? _canExecute;

        public RelayCommand(Action<T?> execute, Predicate<T?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter)
        {
            if (parameter is T tParam)
                return _canExecute?.Invoke(tParam) ?? true;
            if (parameter == null)
                return _canExecute?.Invoke(default) ?? true;
            return false;
        }

        public void Execute(object? parameter)
        {
            if (parameter is T tParam)
                _execute(tParam);
            else
                _execute(default);
        }

        public void RaiseCanExecuteChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
