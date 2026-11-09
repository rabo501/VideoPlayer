using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VideoPlayer
{
    /// <summary>
    /// Stores application history and timeline markers in a portable SQLite database.
    /// The database is placed beside the application executable (or in the supplied directory).
    /// </summary>
    internal sealed class HistoryService
    {
        private readonly string _directory;
        private readonly string _databasePath;
        private readonly string _connectionString;

        public HistoryService(string? directory = null)
        {
            _directory = directory ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(_directory);
            _databasePath = Path.Combine(_directory, "VideoPlayer.db");
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            bool databaseAlreadyExisted = File.Exists(_databasePath);
            InitializeDatabase();
            if (!databaseAlreadyExisted)
                ImportLegacyJson();
        }

        public AppHistoryData Load()
        {
            var data = new AppHistoryData();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Kind, Path FROM RecentItems ORDER BY Kind, Position;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string kind = reader.GetString(0);
                string path = reader.GetString(1);
                if (!File.Exists(path)) continue;
                if (kind == "file" && data.RecentFiles.Count < 10 && !data.RecentFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
                    data.RecentFiles.Add(path);
                else if (kind == "screenshot" && data.RecentScreenshots.Count < 30 && !data.RecentScreenshots.Contains(path, StringComparer.OrdinalIgnoreCase))
                    data.RecentScreenshots.Add(path);
            }
            return data;
        }

        public void Save(AppHistoryData data)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM RecentItems;";
                delete.ExecuteNonQuery();
            }

            InsertRecentItems(connection, transaction, "file", data.RecentFiles.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(10));
            InsertRecentItems(connection, transaction, "screenshot", data.RecentScreenshots.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(30));
            transaction.Commit();
        }

        public List<TimelineMarker> LoadMarkers(string sourcePath)
        {
            var markers = new List<TimelineMarker>();
            string key = NormalizePath(sourcePath);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT Id, Seconds, Description, VehicleMake FROM TimelineMarkers
                                    WHERE SourcePath = $sourcePath ORDER BY Seconds;";
            command.Parameters.AddWithValue("$sourcePath", key);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out Guid id)) id = Guid.NewGuid();
                markers.Add(new TimelineMarker
                {
                    Id = id,
                    Seconds = reader.GetDouble(1),
                    Description = reader.GetString(2),
                    VehicleMake = reader.GetString(3)
                });
            }
            return markers;
        }

        public void SaveMarkers(string sourcePath, IEnumerable<TimelineMarker> markers)
        {
            string key = NormalizePath(sourcePath);
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM TimelineMarkers WHERE SourcePath = $sourcePath;";
                delete.Parameters.AddWithValue("$sourcePath", key);
                delete.ExecuteNonQuery();
            }

            foreach (TimelineMarker marker in markers
                .Where(m => double.IsFinite(m.Seconds) && m.Seconds >= 0)
                .OrderBy(m => m.Seconds))
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = @"INSERT INTO TimelineMarkers (Id, SourcePath, Seconds, Description, VehicleMake)
                                       VALUES ($id, $sourcePath, $seconds, $description, $vehicleMake);";
                insert.Parameters.AddWithValue("$id", marker.Id.ToString("D"));
                insert.Parameters.AddWithValue("$sourcePath", key);
                insert.Parameters.AddWithValue("$seconds", marker.Seconds);
                insert.Parameters.AddWithValue("$description", marker.Description ?? string.Empty);
                insert.Parameters.AddWithValue("$vehicleMake", marker.VehicleMake ?? string.Empty);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        private void InitializeDatabase()
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS RecentItems (
                    Kind TEXT NOT NULL,
                    Path TEXT NOT NULL,
                    Position INTEGER NOT NULL,
                    PRIMARY KEY (Kind, Path)
                );
                CREATE INDEX IF NOT EXISTS IX_RecentItems_KindPosition ON RecentItems (Kind, Position);
                CREATE TABLE IF NOT EXISTS TimelineMarkers (
                    Id TEXT NOT NULL,
                    SourcePath TEXT NOT NULL,
                    Seconds REAL NOT NULL,
                    Description TEXT NOT NULL DEFAULT '',
                    VehicleMake TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY (SourcePath, Id)
                );
                CREATE INDEX IF NOT EXISTS IX_TimelineMarkers_SourceSeconds ON TimelineMarkers (SourcePath, Seconds);
            ";
            command.ExecuteNonQuery();

            // Add the new field to existing portable databases without losing saved markers.
            bool hasVehicleMake = false;
            using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "PRAGMA table_info(TimelineMarkers);";
                using var reader = columns.ExecuteReader();
                while (reader.Read())
                    if (string.Equals(reader.GetString(1), "VehicleMake", StringComparison.OrdinalIgnoreCase))
                        hasVehicleMake = true;
            }
            if (!hasVehicleMake)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE TimelineMarkers ADD COLUMN VehicleMake TEXT NOT NULL DEFAULT '';"; 
                alter.ExecuteNonQuery();
            }
        }

        private void InsertRecentItems(SqliteConnection connection, SqliteTransaction transaction, string kind, IEnumerable<string> paths)
        {
            int position = 0;
            foreach (string path in paths)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR REPLACE INTO RecentItems (Kind, Path, Position) VALUES ($kind, $path, $position);";
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$position", position++);
                command.ExecuteNonQuery();
            }
        }

        private void ImportLegacyJson()
        {
            // One-time import of the previous AppData JSON files, so an update does not lose history.
            string legacyDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoPlayer");
            string historyPath = Path.Combine(legacyDirectory, "history.json");
            string markersPath = Path.Combine(legacyDirectory, "timeline-markers.json");

            if (File.Exists(historyPath))
            {
                try
                {
                    var history = JsonSerializer.Deserialize<AppHistoryData>(File.ReadAllText(historyPath));
                    if (history != null) Save(history);
                }
                catch { /* Preserve the database and continue if an old JSON file is invalid. */ }
            }

            if (File.Exists(markersPath))
            {
                try
                {
                    var db = JsonSerializer.Deserialize<MarkerDatabase>(File.ReadAllText(markersPath));
                    if (db != null)
                    {
                        foreach (var pair in db.Files)
                        {
                            using var connection = OpenConnection();
                            using var transaction = connection.BeginTransaction();
                            foreach (TimelineMarker marker in pair.Value ?? new List<TimelineMarker>())
                            {
                                if (!double.IsFinite(marker.Seconds) || marker.Seconds < 0) continue;
                                using var command = connection.CreateCommand();
                                command.Transaction = transaction;
                                command.CommandText = @"INSERT OR REPLACE INTO TimelineMarkers (Id, SourcePath, Seconds, Description, VehicleMake)
                                                        VALUES ($id, $sourcePath, $seconds, $description, $vehicleMake);";
                                command.Parameters.AddWithValue("$id", marker.Id.ToString("D"));
                                command.Parameters.AddWithValue("$sourcePath", NormalizePath(pair.Key));
                                command.Parameters.AddWithValue("$seconds", marker.Seconds);
                                command.Parameters.AddWithValue("$description", marker.Description ?? string.Empty);
                                command.Parameters.AddWithValue("$vehicleMake", marker.VehicleMake ?? string.Empty);
                                command.ExecuteNonQuery();
                            }
                            transaction.Commit();
                        }
                    }
                }
                catch { /* Do not prevent startup because an old marker file cannot be imported. */ }
            }
        }

        private static string NormalizePath(string path) => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
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
        public string VehicleMake { get; set; } = "";
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
