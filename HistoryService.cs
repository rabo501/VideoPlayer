using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VideoPlayer
{
    internal sealed class HistoryService
    {
        private readonly string _directory;
        private string HistoryPath => Path.Combine(_directory, "history.json");
        private string MarkersPath => Path.Combine(_directory, "timeline-markers.json");

        public HistoryService(string? directory = null)
        {
            _directory = directory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoPlayer");
        }

        public AppHistoryData Load()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return new AppHistoryData();
                var data = JsonSerializer.Deserialize<AppHistoryData>(File.ReadAllText(HistoryPath));
                if (data == null) return new AppHistoryData();
                data.RecentFiles = data.RecentFiles.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
                data.RecentScreenshots = data.RecentScreenshots.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
                return data;
            }
            catch { return new AppHistoryData(); }
        }

        public void Save(AppHistoryData data)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(new AppHistoryData
            {
                RecentFiles = data.RecentFiles.Take(10).ToList(),
                RecentScreenshots = data.RecentScreenshots.Take(30).ToList()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        public List<TimelineMarker> LoadMarkers(string sourcePath)
        {
            try
            {
                if (!File.Exists(MarkersPath)) return new List<TimelineMarker>();
                var db = JsonSerializer.Deserialize<MarkerDatabase>(File.ReadAllText(MarkersPath));
                if (db == null) return new List<TimelineMarker>();
                string key = NormalizePath(sourcePath);
                return db.Files.TryGetValue(key, out var markers)
                    ? markers.OrderBy(m => m.Seconds).ToList()
                    : new List<TimelineMarker>();
            }
            catch { return new List<TimelineMarker>(); }
        }

        public void SaveMarkers(string sourcePath, IEnumerable<TimelineMarker> markers)
        {
            Directory.CreateDirectory(_directory);
            MarkerDatabase db;
            try
            {
                db = File.Exists(MarkersPath)
                    ? JsonSerializer.Deserialize<MarkerDatabase>(File.ReadAllText(MarkersPath)) ?? new MarkerDatabase()
                    : new MarkerDatabase();
            }
            catch { db = new MarkerDatabase(); }

            db.Files[NormalizePath(sourcePath)] = markers
                .Where(m => double.IsFinite(m.Seconds) && m.Seconds >= 0)
                .OrderBy(m => m.Seconds).ToList();
            File.WriteAllText(MarkersPath, JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    }

    internal sealed class AppHistoryData
    {
        public List<string> RecentFiles { get; set; } = new();
        public List<string> RecentScreenshots { get; set; } = new();
    }

    internal sealed class TimelineMarker
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public double Seconds { get; set; }
        public string Description { get; set; } = "";
        public override string ToString()
        {
            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, Seconds));
            return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00} — {Description}";
        }
    }

    internal sealed class MarkerDatabase
    {
        public Dictionary<string, List<TimelineMarker>> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
