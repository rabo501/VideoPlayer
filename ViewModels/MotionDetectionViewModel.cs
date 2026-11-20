using System;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VideoPlayer.ViewModels
{
    internal sealed partial class MotionDetectionViewModel : ObservableObject, IDisposable
    {
        private CancellationTokenSource? _cts;

        public MotionDetectionService Service { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStart))]
        private bool _isRunning;

        [ObservableProperty]
        private double _progressPercent;

        [ObservableProperty]
        private string _progressText = string.Empty;

        public bool CanStart => !IsRunning;

        public CancellationToken BeginRun()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            IsRunning = true;
            ProgressPercent = 0;
            ProgressText = "Przygotowanie analizy…";
            return _cts.Token;
        }

        public bool IsCurrentToken(CancellationToken token) =>
            _cts != null && _cts.Token == token;

        public void CompleteRun()
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }

        [RelayCommand(CanExecute = nameof(IsRunning))]
        public void Cancel()
        {
            _cts?.Cancel();
            ProgressText = "Anulowanie analizy ruchu…";
            CancelCommand.NotifyCanExecuteChanged();
        }

        partial void OnIsRunningChanged(bool value)
        {
            CancelCommand.NotifyCanExecuteChanged();
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }
}
