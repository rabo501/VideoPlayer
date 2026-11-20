using VideoPlayer.ViewModels;
using Xunit;

namespace VideoPlayer.Tests
{
    public sealed class PlateOcrViewModelTests
    {
        [Fact]
        public void EditingPlateText_ResetsConfirmationAndUpdatesActionStates()
        {
            using var vm = new PlateOcrViewModel();
            vm.IsActive = true;
            vm.HasSourceFile = true;
            vm.SetRectifiedCrop(new PlatePixels(20, 10, new byte[20 * 10 * 4]));
            vm.PlateText = "WA12345";
            vm.IsResultConfirmed = true;

            Assert.True(vm.CanSaveEvent);
            Assert.True(vm.CanSearchInRecording);

            vm.PlateText = "WA12346";

            Assert.False(vm.IsResultConfirmed);
            Assert.False(vm.CanSaveEvent);
            Assert.False(vm.CanSearchInRecording);
        }

        [Fact]
        public void SelectingCandidate_UpdatesPlateTextAndClearsPreviousConfirmation()
        {
            using var vm = new PlateOcrViewModel();
            vm.Candidates.Add("KR55555");
            vm.PlateText = "KR11111";
            vm.IsResultConfirmed = true;

            vm.SelectedCandidate = "KR55555";

            Assert.Equal("KR55555", vm.PlateText);
            Assert.False(vm.IsResultConfirmed);
        }

        [Fact]
        public void CanSearchInRecording_IsDisabledForDahuaLiveFrame()
        {
            using var vm = new PlateOcrViewModel();
            vm.IsActive = true;
            vm.HasSourceFile = true;
            vm.IsFromDahua = true;
            vm.SetRectifiedCrop(new PlatePixels(20, 10, new byte[20 * 10 * 4]));
            vm.PlateText = "GD99999";
            vm.IsResultConfirmed = true;

            Assert.True(vm.CanSaveEvent);
            Assert.False(vm.CanSearchInRecording);
        }
    }
}
