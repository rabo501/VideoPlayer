using System;
using System.Collections.Generic;
using System.IO;
using VideoPlayer.ViewModels;
using Xunit;

namespace VideoPlayer.Tests
{
    public sealed class CaseArchiveAndHistoryTests : IDisposable
    {
        private readonly string _tempDir;

        public CaseArchiveAndHistoryTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "VideoPlayer_Stage5_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of temp SQLite files on Windows.
            }
        }

        private string CreateDummyFile(string name)
        {
            string path = Path.Combine(_tempDir, name);
            File.WriteAllText(path, "dummy");
            return path;
        }


        [Fact]
        public async Task CaseArchiveViewModel_GeneratesCsvForAllLinkedSourcesWithoutActivePlayerFile()
        {
            var repository = new CaseRepository(_tempDir);
            var history = new HistoryService(_tempDir);
            string first = CreateDummyFile("evidence_camera_1.mp4");
            string second = CreateDummyFile("evidence_camera_2.mp4");

            CaseRecord saved = repository.Save(new ReportMetadata
            {
                CaseNumber = "MVVM-CASE-5",
                CaseTitle = "Raport wielonagraniowy"
            }, first);
            saved = repository.Save(new ReportMetadata
            {
                CaseNumber = "MVVM-CASE-5",
                CaseTitle = "Raport wielonagraniowy"
            }, second);

            var reportService = new ReportService(new ExportService(new FfmpegService(_tempDir)));
            var caseReportService = new VideoPlayer.Services.CaseReportService(
                repository, history, new MediaInfoService(Path.Combine(_tempDir, "missing-ffmpeg.exe")), reportService);
            var vm = new CaseArchiveViewModel();
            vm.AttachRepository(repository);
            vm.AttachReportService(caseReportService);

            string output = Path.Combine(_tempDir, "case-report.csv");
            string result = await vm.GenerateCaseReportAsync(
                saved.Id, pdf: false, output, includeThumbnails: false, progress: null, CancellationToken.None);

            Assert.True(File.Exists(output));
            string csv = File.ReadAllText(output);
            Assert.Contains(first, csv);
            Assert.Contains(second, csv);
            Assert.Contains("MVVM-CASE-5", csv);
            Assert.Contains("2 nagrania", result);
        }

        [Fact]
        public void CaseRepository_SaveLinkMultipleSourcesReloadAndDelete_PreservesSourceFiles()
        {
            var repo = new CaseRepository(_tempDir);
            string clip1 = CreateDummyFile("clip1.mp4");
            string clip2 = CreateDummyFile("clip2.mp4");

            CaseRecord firstSave = repo.Save(new ReportMetadata
            {
                CaseNumber = "RSD-101/2026",
                CaseTitle = "Kolizja na skrzyżowaniu",
                Location = "ul. Główna 12",
                Notes = "Kamera wjazdowa"
            }, clip1);

            Assert.Equal("RSD-101/2026", firstSave.CaseNumber);
            Assert.Equal(1, firstSave.SourceCount);

            CaseRecord secondSave = repo.Save(new ReportMetadata
            {
                CaseNumber = "rsd-101/2026",
                CaseTitle = "Kolizja na skrzyżowaniu (uzupełnienie)",
                Location = "ul. Główna 12",
                Notes = "Dwie kamery"
            }, clip2);

            Assert.Equal(firstSave.Id, secondSave.Id);
            Assert.Equal(2, secondSave.SourceCount);

            List<string> sources = repo.LoadSources(firstSave.Id);
            Assert.Equal(2, sources.Count);

            Assert.True(repo.Delete(firstSave.Id));
            Assert.Null(repo.Load(firstSave.Id));
            Assert.Empty(repo.LoadAll());
            Assert.True(File.Exists(clip1));
            Assert.True(File.Exists(clip2));
        }

        [Fact]
        public void CaseArchiveViewModel_SelectionMetadataClearAndDelete_Lifecycle()
        {
            var repo = new CaseRepository(_tempDir);
            var vm = new CaseArchiveViewModel();
            vm.AttachRepository(repo);

            var missingResult = vm.SaveCurrentCase(Path.Combine(_tempDir, "nonexistent.mp4"));
            Assert.False(missingResult.Success);
            Assert.Contains("Najpierw otwórz nagranie", vm.StatusText);

            string clip = CreateDummyFile("evidence.mp4");
            vm.CaseNumber = "SP-42";
            vm.CaseTitle = "Wykroczenie drogowe";
            vm.Location = "DK-7";
            vm.Notes = "Widoczna tablica";

            var savedResult = vm.SaveCurrentCase(clip);
            Assert.True(savedResult.Success);
            Assert.NotNull(savedResult.Saved);
            Assert.Single(vm.Cases);
            Assert.Equal(savedResult.Saved!.Id, vm.SelectedCase?.Id);

            vm.ClearSelectionCommand.Execute(null);
            Assert.Null(vm.SelectedCase);
            Assert.Equal(string.Empty, vm.CaseNumber);
            Assert.Equal(string.Empty, vm.CaseTitle);
            Assert.Equal(string.Empty, vm.Location);
            Assert.Equal(string.Empty, vm.Notes);

            vm.SelectedCase = vm.Cases[0];
            Assert.Equal("SP-42", vm.CaseNumber);
            Assert.Equal("Wykroczenie drogowe", vm.CaseTitle);
            Assert.Equal("DK-7", vm.Location);
            Assert.Equal("Widoczna tablica", vm.Notes);

            var deletedResult = vm.DeleteSelectedCase();
            Assert.True(deletedResult.Deleted);
            Assert.Empty(vm.Cases);
            Assert.Equal(string.Empty, vm.CaseNumber);
        }

        [Fact]
        public void HistoryViewModel_DeduplicatesCapsAndFiltersMissingOnReload()
        {
            var service = new HistoryService(_tempDir);
            var vm = new HistoryViewModel();
            vm.AttachService(service);

            var files = new List<string>();
            for (int i = 0; i < 12; i++)
            {
                string f = CreateDummyFile($"video_{i:00}.mp4");
                files.Add(f);
                Assert.Null(vm.AddRecentFile(f));
            }

            Assert.Equal(10, vm.RecentFiles.Count);
            Assert.Equal(Path.GetFileName(files[11]), vm.RecentFiles[0].FileName);

            // Re-adding an existing item promotes it to the top without duplicating.
            Assert.Null(vm.AddRecentFile(files[5]));
            Assert.Equal(10, vm.RecentFiles.Count);
            Assert.Equal(Path.GetFileName(files[5]), vm.RecentFiles[0].FileName);

            for (int i = 0; i < 32; i++)
            {
                string shot = CreateDummyFile($"shot_{i:00}.jpg");
                Assert.Null(vm.AddRecentScreenshot(shot));
            }

            Assert.Equal(30, vm.RecentScreenshots.Count);

            // Delete the promoted file and verify Load filters missing entries out.
            File.Delete(files[5]);
            var reloaded = new HistoryViewModel();
            reloaded.AttachService(new HistoryService(_tempDir));
            Assert.Equal(9, reloaded.RecentFiles.Count);
            Assert.DoesNotContain(reloaded.RecentFiles, item => item.FullPath == files[5]);
        }

        [Fact]
        public void DahuaViewModel_ArchiveAndDownloadState_UpdatesComputedProperties()
        {
            using var vm = new DahuaViewModel();
            var changed = new HashSet<string>(StringComparer.Ordinal);
            vm.PropertyChanged += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.PropertyName))
                    changed.Add(args.PropertyName);
            };

            Assert.True(vm.IsStreamOverlayVisible);
            Assert.True(vm.IsArchiveInputsEnabled);
            Assert.False(vm.IsDownloadStatusVisible);

            vm.IsDownloadBusy = true;
            Assert.False(vm.IsArchiveInputsEnabled);
            Assert.True(vm.IsDownloadStatusVisible);
            Assert.Contains(nameof(DahuaViewModel.IsArchiveInputsEnabled), changed);
            Assert.Contains(nameof(DahuaViewModel.IsDownloadStatusVisible), changed);

            vm.IsDownloadBusy = false;
            Assert.True(vm.IsArchiveInputsEnabled);
            Assert.False(vm.IsDownloadStatusVisible);

            vm.IsArchiveSearching = true;
            Assert.False(vm.IsArchiveInputsEnabled);
            Assert.True(vm.IsDownloadStatusVisible);

            vm.IsArchiveSearching = false;
            vm.DownloadStatusText = "Znaleziono 3 nagrań.";
            Assert.True(vm.IsArchiveInputsEnabled);
            Assert.True(vm.IsDownloadStatusVisible);

            vm.IsStreamOverlayVisible = false;
            Assert.False(vm.IsStreamOverlayVisible);
        }
    }
}
