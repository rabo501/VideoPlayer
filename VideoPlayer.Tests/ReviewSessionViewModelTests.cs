using System;
using System.IO;
using VideoPlayer.ViewModels;
using Xunit;

namespace VideoPlayer.Tests
{
    public sealed class ReviewSessionViewModelTests : IDisposable
    {
        private readonly string _tempDirectory =
            Path.Combine(Path.GetTempPath(), "VideoPlayer_ReviewSession_" + Guid.NewGuid().ToString("N"));

        public ReviewSessionViewModelTests()
        {
            Directory.CreateDirectory(_tempDirectory);
        }

        [Fact]
        public void SaveAndLoad_RestoresPlaybackViewAndClipState()
        {
            string source = Path.Combine(_tempDirectory, "recording.mp4");
            File.WriteAllText(source, "test");

            var viewModel = new ReviewSessionViewModel();
            Assert.False(viewModel.IsReady);
            viewModel.AttachRepository(new ReviewRepository(_tempDirectory));
            Assert.True(viewModel.IsReady);

            var expected = new ReviewSession
            {
                Position = 125.5,
                Speed = 0.5,
                Paused = true,
                Zoom = 1.25,
                PanX = -0.2,
                PanY = 0.3,
                Panscan = 0.4,
                ClipStart = 10,
                ClipEnd = 20
            };
            expected.Clips.Add(new ClipRange { Name = "Fragment 1", Start = 10, End = 20 });

            viewModel.Save(source, expected);
            ReviewSession? actual = viewModel.Load(source);

            Assert.NotNull(actual);
            Assert.Equal(expected.Position, actual!.Position);
            Assert.Equal(expected.Speed, actual.Speed);
            Assert.Equal(expected.Paused, actual.Paused);
            Assert.Equal(expected.Zoom, actual.Zoom);
            Assert.Equal(expected.PanX, actual.PanX);
            Assert.Equal(expected.PanY, actual.PanY);
            Assert.Equal(expected.Panscan, actual.Panscan);
            Assert.Equal(expected.ClipStart, actual.ClipStart);
            Assert.Equal(expected.ClipEnd, actual.ClipEnd);
            Assert.Single(actual.Clips);
            Assert.Equal("Fragment 1", actual.Clips[0].Name);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDirectory))
                    Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of temporary SQLite files on Windows.
            }
        }
    }
}