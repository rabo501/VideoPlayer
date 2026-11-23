using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VideoPlayer.Services;
using VideoPlayer.ViewModels;
using Xunit;

namespace VideoPlayer.Tests
{
    public sealed class ExportAndSearchTests
    {
        [Theory]
        [InlineData(-5, "0 B")]
        [InlineData(512, "512 B")]
        [InlineData(1024, "1 KB")]
        [InlineData(1536, "1.5 KB")]
        [InlineData(1048576, "1 MB")]
        public void FormatFileSize_FormatsExpectedUnits(long bytes, string expected)
        {
            Assert.Equal(expected, ExportWorkspaceService.FormatFileSize(bytes));
        }

        [Fact]
        public void SanitizeFileName_StripsInvalidCharsAndAppliesFallback()
        {
            Assert.Equal("fragment", ExportWorkspaceService.SanitizeFileName("   ... "));
            string sanitized = ExportWorkspaceService.SanitizeFileName("Brama: wjazd / 01?");
            Assert.DoesNotContain(':', sanitized);
            Assert.DoesNotContain('/', sanitized);
            Assert.DoesNotContain('?', sanitized);
            Assert.True(sanitized.Length <= 70);
        }

        [Fact]
        public void BuildMediaInfoSummary_EstimatesSelectedRangeSize()
        {
            var info = new MediaInfoData
            {
                VideoCodec = "h264",
                Resolution = "1920×1080",
                Fps = "25",
                Bitrate = "4000 kb/s",
                AudioCodec = "aac",
                Duration = 100,
                FileSize = 100 * 1024 * 1024L
            };

            string summary = ExportWorkspaceService.BuildMediaInfoSummary(
                info, @"C:\video\sample.mp4", clipStart: 10, clipEnd: 35, lastExportSummary: "Zapisano: test.mp4");

            Assert.Contains("25 MB", summary);
            Assert.Contains("zaznaczenie (00:00:25)", summary);
            Assert.Contains("Zapisano: test.mp4", summary);
        }

        [Fact]
        public void ExportViewModel_OperationLifecycleAndCancellation()
        {
            using var vm = new ExportViewModel();
            Assert.True(vm.CanStartOperation);
            Assert.False(vm.IsBusy);
            Assert.False(vm.CancelCommand.CanExecute(null));

            CancellationToken ct = vm.BeginOperation("Eksport testowy");
            Assert.True(vm.IsBusy);
            Assert.False(vm.CanStartOperation);
            Assert.True(vm.CancelCommand.CanExecute(null));
            Assert.True(vm.IsCurrentOperationToken(ct));

            vm.ReportProgress(ct, 42.5, "42.5%");
            Assert.Equal(42.5, vm.ProgressPercent);
            Assert.Equal("42.5%", vm.ProgressText);

            vm.CancelCommand.Execute(null);
            Assert.True(ct.IsCancellationRequested);
            Assert.False(vm.CancelCommand.CanExecute(null));

            vm.EndOperation();
            Assert.False(vm.IsBusy);
            Assert.True(vm.CanStartOperation);
        }

        [Fact]
        public void MainViewModel_MotionRunBlocksExportStart()
        {
            using var main = new MainViewModel();
            Assert.True(main.Export.CanStartOperation);

            main.Motion.BeginRun();
            Assert.True(main.Export.IsMotionRunning);
            Assert.False(main.Export.CanStartOperation);

            main.Motion.CompleteRun();
            Assert.False(main.Export.IsMotionRunning);
            Assert.True(main.Export.CanStartOperation);
        }

        [Theory]
        [InlineData("  Duration: 01:02:03.50, start: 0.000000", 3723.5)]
        [InlineData("Duration: N/A", 0)]
        public void MediaInfoService_ParseDuration_ParsesFfmpegHeader(string ffmpegOutput, double expectedSeconds)
        {
            double parsed = MediaInfoService.ParseDuration(ffmpegOutput);
            Assert.Equal(expectedSeconds, parsed, 3);
        }

        [Fact]
        public async Task PlateSearchViewModel_RunsProviderAsyncAndUpdatesResults()
        {
            var sample = new List<PlateSearchResult>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    SourcePath = @"C:\clips\cam1.mp4",
                    Seconds = 12.5,
                    Description = "Tablica rejestracyjna: WA12345 (pewność 96%)",
                    VehicleMake = "Toyota"
                }
            };

            using var vm = new PlateSearchViewModel((plate, make, source) =>
            {
                Assert.Equal("WA", plate);
                Assert.Equal("Toyota", make);
                Assert.Equal("cam1", source);
                return sample;
            });

            vm.PlateFilter = "WA";
            vm.MakeFilter = "Toyota";
            vm.SourceFilter = "cam1";

            await vm.SearchAsync();

            Assert.False(vm.IsSearching);
            Assert.Single(vm.Results);
            Assert.Equal("WA12345", vm.Results[0].PlateNumber);
            Assert.Equal("Znaleziono: 1 zdarzeń.", vm.ResultCountText);
            Assert.Contains("limit listy: 1000", PlateSearchViewModel.FormatResultCount(1000));
        }

        [Fact]
        public void HistoryViewModel_HandlesPlateSearchAndExportHistory()
        {
            string directory = Path.Combine(Path.GetTempPath(), "VideoPlayer_HistoryVm_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string source = Path.Combine(directory, "recording.mp4");
            File.WriteAllBytes(source, Array.Empty<byte>());
            try
            {
                var service = new HistoryService(directory);
                service.SaveMarkers(source, new[]
                {
                    new TimelineMarker
                    {
                        Id = Guid.NewGuid(), Seconds = 18.25,
                        Description = "Tablica rejestracyjna: WA12345 (numer potwierdzony ręcznie)",
                        VehicleMake = "Toyota"
                    }
                });
                var vm = new HistoryViewModel();
                vm.AttachService(service);
                Assert.True(vm.IsReady);
                var results = vm.SearchMarkers("WA12345", "Toyota", "recording");
                Assert.Single(results);
                Assert.Equal("WA12345", results[0].PlateNumber);

                var export = new ExportHistoryItem
                {
                    SourcePath = source, OutputPath = Path.Combine(directory, "clip.mp4"),
                    Operation = "Eksport fragmentu", CreatedLocal = DateTime.Now, Details = "Test"
                };
                Assert.Null(vm.AddExport(export));
                Assert.Single(vm.LoadExports(source));
                Assert.Equal(export.OutputPath, vm.LoadExports(source)[0].OutputPath);
            }
            finally { try { Directory.Delete(directory, true); } catch { } }
        }
    }
}
