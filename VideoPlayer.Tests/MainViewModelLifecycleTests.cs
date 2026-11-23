using System;
using VideoPlayer.ViewModels;
using Xunit;

namespace VideoPlayer.Tests
{
    public sealed class MainViewModelLifecycleTests
    {
        [Fact]
        public void Dispose_CancelsOwnedOperations_AndCanBeCalledMoreThanOnce()
        {
            var main = new MainViewModel();

            var motionToken = main.Motion.BeginRun();
            var exportToken = main.Export.BeginOperation("Eksport testowy");

            Assert.True(main.Motion.IsRunning);
            Assert.True(main.Export.IsBusy);

            main.Dispose();

            Assert.True(motionToken.IsCancellationRequested);
            Assert.True(exportToken.IsCancellationRequested);
            Assert.False(main.Motion.IsRunning);
            Assert.False(main.Export.IsBusy);

            // Zamknięcie okna i późniejsze sprzątanie mogą spotkać się
            // w tym samym cyklu życia; Dispose musi pozostać idempotentne.
            main.Dispose();
        }
    }
}
