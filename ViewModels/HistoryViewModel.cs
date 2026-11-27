using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VideoPlayer.ViewModels
{
    internal sealed record RecentMediaEntry(string FileName, string FullPath);

    internal sealed partial class HistoryViewModel : ObservableObject
    {
        private HistoryService? _historyService;

        public HistoryViewModel(HistoryService? historyService = null)
        {
            _historyService = historyService;
        }

        public ObservableCollection<RecentMediaEntry> RecentFiles { get; } = new();
        public ObservableCollection<RecentMediaEntry> RecentScreenshots { get; } = new();
        public bool IsReady => _historyService != null;

        [ObservableProperty]
        private RecentMediaEntry? _selectedRecentFile;

        [ObservableProperty]
        private RecentMediaEntry? _selectedRecentScreenshot;

        public void AttachService(HistoryService historyService)
        {
            _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
            Load();
        }

        public void Load()
        {
            if (_historyService == null) return;
            AppHistoryData data = _historyService.Load();
            ReplaceCollection(RecentFiles, data.RecentFiles, 10);
            ReplaceCollection(RecentScreenshots, data.RecentScreenshots, 30);
            SelectedRecentFile = RecentFiles.FirstOrDefault();
            SelectedRecentScreenshot = RecentScreenshots.FirstOrDefault();
        }

        public string? AddRecentFile(string path)
        {
            InsertUnique(RecentFiles, path, 10);
            SelectedRecentFile = RecentFiles.FirstOrDefault();
            return TrySave();
        }

        public string? AddRecentScreenshot(string path)
        {
            InsertUnique(RecentScreenshots, path, 30);
            SelectedRecentScreenshot = RecentScreenshots.FirstOrDefault();
            return TrySave();
        }

        public List<PlateSearchResult> SearchMarkers(string plate, string make, string source)
        {
            if (_historyService == null) throw new InvalidOperationException("Historia nie została jeszcze zainicjalizowana.");
            return _historyService.SearchMarkers(plate, make, source);
        }

        public List<ExportHistoryItem> LoadExports(string sourcePath)
        {
            if (_historyService == null) throw new InvalidOperationException("Historia nie została jeszcze zainicjalizowana.");
            return _historyService.LoadExports(sourcePath);
        }

        public string? AddExport(ExportHistoryItem item)
        {
            if (_historyService == null) return "Historia nie została jeszcze zainicjalizowana.";
            try { _historyService.AddExport(item); return null; }
            catch (Exception ex) { return ex.Message; }
        }

        private string? TrySave()
        {
            if (_historyService == null) return null;
            try
            {
                _historyService.Save(new AppHistoryData
                {
                    RecentFiles = RecentFiles.Select(x => x.FullPath).ToList(),
                    RecentScreenshots = RecentScreenshots.Select(x => x.FullPath).ToList()
                });
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static void ReplaceCollection(ObservableCollection<RecentMediaEntry> target, IEnumerable<string> paths, int limit)
        {
            target.Clear();
            foreach (string path in paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(limit))
                target.Add(new RecentMediaEntry(Path.GetFileName(path), path));
        }

        private static void InsertUnique(ObservableCollection<RecentMediaEntry> target, string path, int limit)
        {
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (string.Equals(target[i].FullPath, path, StringComparison.OrdinalIgnoreCase))
                    target.RemoveAt(i);
            }
            target.Insert(0, new RecentMediaEntry(Path.GetFileName(path), path));
            while (target.Count > limit)
                target.RemoveAt(target.Count - 1);
        }
    }
}
