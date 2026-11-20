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
            if (databaseAlreadyExisted)
                BackupBeforeSchemaMigrationIfNeeded();
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
            command.CommandText = @"SELECT Id, Seconds, Description, VehicleMake, EndSeconds, MotionX, MotionY, MotionWidth, MotionHeight FROM TimelineMarkers
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
                    VehicleMake = reader.GetString(3),
                    EndSeconds = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    MotionX = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                    MotionY = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    MotionWidth = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    MotionHeight = reader.IsDBNull(8) ? null : reader.GetDouble(8)
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
                insert.CommandText = @"INSERT INTO TimelineMarkers (Id, SourcePath, Seconds, Description, VehicleMake, EndSeconds, MotionX, MotionY, MotionWidth, MotionHeight)
                                       VALUES ($id, $sourcePath, $seconds, $description, $vehicleMake, $endSeconds, $motionX, $motionY, $motionWidth, $motionHeight);";
                insert.Parameters.AddWithValue("$id", marker.Id.ToString("D"));
                insert.Parameters.AddWithValue("$sourcePath", key);
                insert.Parameters.AddWithValue("$seconds", marker.Seconds);
                insert.Parameters.AddWithValue("$description", marker.Description ?? string.Empty);
                insert.Parameters.AddWithValue("$vehicleMake", marker.VehicleMake ?? string.Empty);
                insert.Parameters.AddWithValue("$endSeconds", (object?)marker.EndSeconds ?? DBNull.Value);
                insert.Parameters.AddWithValue("$motionX", (object?)marker.MotionX ?? DBNull.Value);
                insert.Parameters.AddWithValue("$motionY", (object?)marker.MotionY ?? DBNull.Value);
                insert.Parameters.AddWithValue("$motionWidth", (object?)marker.MotionWidth ?? DBNull.Value);
                insert.Parameters.AddWithValue("$motionHeight", (object?)marker.MotionHeight ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        public List<PlateSearchResult> SearchMarkers(string plateNumber, string vehicleMake, string sourcePath)
        {
            var results = new List<PlateSearchResult>();
            string plate = NormalizePlateSearch(plateNumber);
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            var filters = new List<string> { "Description LIKE 'Tablica rejestracyjna:%'" };
            if (plate.Length > 0)
            {
                filters.Add("REPLACE(REPLACE(REPLACE(UPPER(Description), ' ', ''), '-', ''), ':', '') LIKE $plate");
                command.Parameters.AddWithValue("$plate", "%" + plate + "%");
            }
            if (!string.IsNullOrWhiteSpace(vehicleMake))
            {
                filters.Add("instr(lower(VehicleMake), lower($make)) > 0");
                command.Parameters.AddWithValue("$make", vehicleMake.Trim());
            }
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                filters.Add("instr(lower(SourcePath), lower($source)) > 0");
                command.Parameters.AddWithValue("$source", sourcePath.Trim());
            }
            command.CommandText = @"SELECT Id, SourcePath, Seconds, Description, VehicleMake
                                    FROM TimelineMarkers " +
                (filters.Count > 0 ? "WHERE " + string.Join(" AND ", filters) : "") +
                " ORDER BY SourcePath, Seconds LIMIT 1000;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                Guid.TryParse(reader.GetString(0), out Guid id);
                results.Add(new PlateSearchResult
                {
                    Id = id,
                    SourcePath = reader.GetString(1),
                    Seconds = reader.GetDouble(2),
                    Description = reader.GetString(3),
                    VehicleMake = reader.GetString(4)
                });
            }
            return results;
        }

        private static string NormalizePlateSearch(string value) =>
            new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Creates a consistent SQLite backup before changing an existing database schema.
        /// SQLite's backup API includes committed WAL data, unlike copying the .db file directly.
        /// </summary>
        private void BackupBeforeSchemaMigrationIfNeeded()
        {
            using var source = OpenConnection();
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var table = source.CreateCommand())
            {
                table.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='TimelineMarkers' LIMIT 1;";
                if (table.ExecuteScalar() == null)
                    return;
            }

            using (var columns = source.CreateCommand())
            {
                columns.CommandText = "PRAGMA table_info(TimelineMarkers);";
                using var reader = columns.ExecuteReader();
                while (reader.Read())
                    existingColumns.Add(reader.GetString(1));
            }

            string[] requiredColumns = { "VehicleMake", "EndSeconds", "MotionX", "MotionY", "MotionWidth", "MotionHeight" };
            if (requiredColumns.All(existingColumns.Contains))
                return;

            string backupDirectory = Path.Combine(_directory, "backups");
            Directory.CreateDirectory(backupDirectory);
            string backupPath = Path.Combine(
                backupDirectory,
                $"VideoPlayer_pre-migration_{DateTime.Now:yyyyMMdd_HHmmss_fff}.db");

            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());
            destination.Open();
            source.BackupDatabase(destination);
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
                    EndSeconds REAL NULL,
                    PRIMARY KEY (SourcePath, Id)
                );
                CREATE INDEX IF NOT EXISTS IX_TimelineMarkers_SourceSeconds ON TimelineMarkers (SourcePath, Seconds);
                CREATE TABLE IF NOT EXISTS ExportHistory (
                    Id TEXT PRIMARY KEY,
                    SourcePath TEXT NOT NULL,
                    OutputPath TEXT NOT NULL,
                    Operation TEXT NOT NULL,
                    StartSeconds REAL NULL,
                    EndSeconds REAL NULL,
                    CreatedLocal TEXT NOT NULL,
                    Details TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS IX_ExportHistory_SourceCreated ON ExportHistory (SourcePath, CreatedLocal);
            ";
            command.ExecuteNonQuery();

            // Add the new field to existing portable databases without losing saved markers.
            bool hasVehicleMake = false;
            bool hasEndSeconds = false;
            bool hasMotionX = false, hasMotionY = false, hasMotionWidth = false, hasMotionHeight = false;
            using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "PRAGMA table_info(TimelineMarkers);";
                using var reader = columns.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), "VehicleMake", StringComparison.OrdinalIgnoreCase))
                        hasVehicleMake = true;
                    if (string.Equals(reader.GetString(1), "EndSeconds", StringComparison.OrdinalIgnoreCase))
                        hasEndSeconds = true;
                    if (string.Equals(reader.GetString(1), "MotionX", StringComparison.OrdinalIgnoreCase)) hasMotionX = true;
                    if (string.Equals(reader.GetString(1), "MotionY", StringComparison.OrdinalIgnoreCase)) hasMotionY = true;
                    if (string.Equals(reader.GetString(1), "MotionWidth", StringComparison.OrdinalIgnoreCase)) hasMotionWidth = true;
                    if (string.Equals(reader.GetString(1), "MotionHeight", StringComparison.OrdinalIgnoreCase)) hasMotionHeight = true;
                }
            }
            if (!hasVehicleMake)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE TimelineMarkers ADD COLUMN VehicleMake TEXT NOT NULL DEFAULT '';"; 
                alter.ExecuteNonQuery();
            }
            if (!hasEndSeconds)
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE TimelineMarkers ADD COLUMN EndSeconds REAL NULL;";
                alter.ExecuteNonQuery();
            }
            foreach (var column in new[] { ("MotionX", hasMotionX), ("MotionY", hasMotionY), ("MotionWidth", hasMotionWidth), ("MotionHeight", hasMotionHeight) })
            {
                if (column.Item2) continue;
                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE TimelineMarkers ADD COLUMN {column.Item1} REAL NULL;";
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
                                command.CommandText = @"INSERT OR REPLACE INTO TimelineMarkers (Id, SourcePath, Seconds, Description, VehicleMake, EndSeconds, MotionX, MotionY, MotionWidth, MotionHeight)
                                                        VALUES ($id, $sourcePath, $seconds, $description, $vehicleMake, $endSeconds, $motionX, $motionY, $motionWidth, $motionHeight);";
                                command.Parameters.AddWithValue("$id", marker.Id.ToString("D"));
                                command.Parameters.AddWithValue("$sourcePath", NormalizePath(pair.Key));
                                command.Parameters.AddWithValue("$seconds", marker.Seconds);
                                command.Parameters.AddWithValue("$description", marker.Description ?? string.Empty);
                                command.Parameters.AddWithValue("$vehicleMake", marker.VehicleMake ?? string.Empty);
                                command.Parameters.AddWithValue("$endSeconds", (object?)marker.EndSeconds ?? DBNull.Value);
                                command.Parameters.AddWithValue("$motionX", (object?)marker.MotionX ?? DBNull.Value);
                                command.Parameters.AddWithValue("$motionY", (object?)marker.MotionY ?? DBNull.Value);
                                command.Parameters.AddWithValue("$motionWidth", (object?)marker.MotionWidth ?? DBNull.Value);
                                command.Parameters.AddWithValue("$motionHeight", (object?)marker.MotionHeight ?? DBNull.Value);
                                command.ExecuteNonQuery();
                            }
                            transaction.Commit();
                        }
                    }
                }
                catch { /* Do not prevent startup because an old marker file cannot be imported. */ }
            }
        }

        public void AddExport(ExportHistoryItem item)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT INTO ExportHistory
                (Id, SourcePath, OutputPath, Operation, StartSeconds, EndSeconds, CreatedLocal, Details)
                VALUES ($id, $source, $output, $operation, $start, $end, $created, $details);";
            command.Parameters.AddWithValue("$id", item.Id.ToString("D"));
            command.Parameters.AddWithValue("$source", NormalizePath(item.SourcePath));
            command.Parameters.AddWithValue("$output", item.OutputPath ?? string.Empty);
            command.Parameters.AddWithValue("$operation", item.Operation ?? string.Empty);
            command.Parameters.AddWithValue("$start", (object?)item.StartSeconds ?? DBNull.Value);
            command.Parameters.AddWithValue("$end", (object?)item.EndSeconds ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", item.CreatedLocal.ToString("yyyy-MM-dd HH:mm:ss"));
            command.Parameters.AddWithValue("$details", item.Details ?? string.Empty);
            command.ExecuteNonQuery();
        }

        public List<ExportHistoryItem> LoadExports(string sourcePath)
        {
            var items = new List<ExportHistoryItem>();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT Id, OutputPath, Operation, StartSeconds, EndSeconds, CreatedLocal, Details
                                    FROM ExportHistory WHERE SourcePath=$source ORDER BY CreatedLocal;";
            command.Parameters.AddWithValue("$source", NormalizePath(sourcePath));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out Guid id)) id = Guid.NewGuid();
                items.Add(new ExportHistoryItem
                {
                    Id = id,
                    SourcePath = sourcePath,
                    OutputPath = reader.GetString(1),
                    Operation = reader.GetString(2),
                    StartSeconds = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    EndSeconds = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    CreatedLocal = DateTime.TryParse(reader.GetString(5), out var created) ? created : DateTime.MinValue,
                    Details = reader.GetString(6)
                });
            }
            return items;
        }

        private static string NormalizePath(string path) => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
    }

    internal sealed class ExportHistoryItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string SourcePath { get; set; } = "";
        public string OutputPath { get; set; } = "";
        public string Operation { get; set; } = "";
        public double? StartSeconds { get; set; }
        public double? EndSeconds { get; set; }
        public DateTime CreatedLocal { get; set; } = DateTime.Now;
        public string Details { get; set; } = "";
    }

    internal sealed class PlateSearchResult
    {
        public Guid Id { get; set; }
        public string SourcePath { get; set; } = "";
        public double Seconds { get; set; }
        public string Description { get; set; } = "";
        public string VehicleMake { get; set; } = "";
        public string PlateNumber
        {
            get
            {
                const string prefix = "Tablica rejestracyjna: ";
                if (!Description.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
                string value = Description.Substring(prefix.Length);
                int end = value.IndexOf(" (", StringComparison.Ordinal);
                return (end >= 0 ? value.Substring(0, end) : value).Trim();
            }
        }
        public string TimeText => TimeSpan.FromSeconds(Math.Max(0, Seconds)).ToString(@"hh\:mm\:ss\.fff");
        public override string ToString() =>
            $"{PlateNumber} | {(string.IsNullOrWhiteSpace(VehicleMake) ? "marka niepodana" : VehicleMake)} | {TimeText} | {Path.GetFileName(SourcePath)}";
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
        public double? EndSeconds { get; set; }
        public double? MotionX { get; set; }
        public double? MotionY { get; set; }
        public double? MotionWidth { get; set; }
        public double? MotionHeight { get; set; }

        // Event type is derived from existing marker data; no database migration is required.
        public string EventType =>
            Description.StartsWith("Tablica rejestracyjna:", StringComparison.OrdinalIgnoreCase)
                ? "Tablica"
                : EndSeconds.HasValue && Description.StartsWith("Ruch", StringComparison.OrdinalIgnoreCase)
                    ? "Ruch"
                    : "Ręczne";

        // Runtime-only relation calculated by the event center from nearby motion intervals.
        public int RelatedMotionCount { get; set; }

        public override string ToString()
        {
            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, Seconds));
            string make = string.IsNullOrWhiteSpace(VehicleMake) ? "" : " | Marka: " + VehicleMake;
            string range = "";
            if (EndSeconds is double end && end > Seconds)
            {
                TimeSpan endTime = TimeSpan.FromSeconds(end);
                range = $" – {(int)endTime.TotalHours:00}:{endTime.Minutes:00}:{endTime.Seconds:00}";
            }
            string related = RelatedMotionCount > 0 ? $" | Powiązany ruch: {RelatedMotionCount}" : "";
            return $"[{EventType}] {(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}{range} — {Description}{make}{related}";
        }
    }

    internal sealed class MarkerDatabase
    {
        public Dictionary<string, List<TimelineMarker>> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
