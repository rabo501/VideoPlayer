using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace VideoPlayer
{
    /// <summary>
    /// Local archive of investigation cases. Original recordings are never copied or modified;
    /// only their absolute paths are linked to a case in the local SQLite database.
    /// </summary>
    internal sealed class CaseRepository
    {
        private readonly string _connectionString;

        public CaseRepository()
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(AppContext.BaseDirectory, "VideoPlayer.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();

            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Cases (
                    Id TEXT NOT NULL PRIMARY KEY,
                    CaseNumber TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    CaseTitle TEXT NOT NULL DEFAULT '',
                    Location TEXT NOT NULL DEFAULT '',
                    Notes TEXT NOT NULL DEFAULT '',
                    CreatedLocal TEXT NOT NULL,
                    UpdatedLocal TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS CaseSources (
                    CaseId TEXT NOT NULL,
                    SourcePath TEXT NOT NULL,
                    AddedLocal TEXT NOT NULL,
                    PRIMARY KEY (CaseId, SourcePath),
                    FOREIGN KEY (CaseId) REFERENCES Cases(Id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_CaseSources_SourcePath ON CaseSources(SourcePath);";
            command.ExecuteNonQuery();
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        public List<CaseRecord> LoadAll()
        {
            var result = new List<CaseRecord>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT c.Id, c.CaseNumber, c.CaseTitle, c.Location, c.Notes,
                       c.CreatedLocal, c.UpdatedLocal, COUNT(s.SourcePath)
                FROM Cases c
                LEFT JOIN CaseSources s ON s.CaseId = c.Id
                GROUP BY c.Id
                ORDER BY c.UpdatedLocal DESC, c.CaseNumber COLLATE NOCASE;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out Guid id)) continue;
                result.Add(new CaseRecord
                {
                    Id = id,
                    CaseNumber = reader.GetString(1),
                    Title = reader.GetString(2),
                    Location = reader.GetString(3),
                    Notes = reader.GetString(4),
                    CreatedLocal = ParseLocal(reader.GetString(5)),
                    UpdatedLocal = ParseLocal(reader.GetString(6)),
                    SourceCount = reader.GetInt32(7)
                });
            }
            return result;
        }

        public CaseRecord Save(ReportMetadata metadata, string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(metadata.CaseNumber))
                throw new InvalidOperationException("Podaj numer sprawy przed zapisaniem jej w archiwum.");
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("Nie znaleziono bieżącego nagrania. Sprawa nie została zapisana.");

            string caseNumber = metadata.CaseNumber.Trim();
            string sourceKey = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar);
            DateTime now = DateTime.Now;
            using var connection = Open();
            using var transaction = connection.BeginTransaction();

            Guid id;
            string created;
            using (var find = connection.CreateCommand())
            {
                find.Transaction = transaction;
                find.CommandText = "SELECT Id, CreatedLocal FROM Cases WHERE CaseNumber = $number COLLATE NOCASE;";
                find.Parameters.AddWithValue("$number", caseNumber);
                using var reader = find.ExecuteReader();
                if (reader.Read())
                {
                    if (!Guid.TryParse(reader.GetString(0), out id)) id = Guid.NewGuid();
                    created = reader.GetString(1);
                }
                else
                {
                    id = Guid.NewGuid();
                    created = now.ToString("yyyy-MM-dd HH:mm:ss");
                }
            }

            using (var save = connection.CreateCommand())
            {
                save.Transaction = transaction;
                save.CommandText = @"
                    INSERT INTO Cases (Id, CaseNumber, CaseTitle, Location, Notes, CreatedLocal, UpdatedLocal)
                    VALUES ($id, $number, $title, $location, $notes, $created, $updated)
                    ON CONFLICT(CaseNumber) DO UPDATE SET
                        CaseTitle = excluded.CaseTitle,
                        Location = excluded.Location,
                        Notes = excluded.Notes,
                        UpdatedLocal = excluded.UpdatedLocal;";
                save.Parameters.AddWithValue("$id", id.ToString("D"));
                save.Parameters.AddWithValue("$number", caseNumber);
                save.Parameters.AddWithValue("$title", metadata.CaseTitle?.Trim() ?? string.Empty);
                save.Parameters.AddWithValue("$location", metadata.Location?.Trim() ?? string.Empty);
                save.Parameters.AddWithValue("$notes", metadata.Notes?.Trim() ?? string.Empty);
                save.Parameters.AddWithValue("$created", created);
                save.Parameters.AddWithValue("$updated", now.ToString("yyyy-MM-dd HH:mm:ss"));
                save.ExecuteNonQuery();
            }

            // Resolve the canonical ID in case the unique case number already existed.
            using (var resolve = connection.CreateCommand())
            {
                resolve.Transaction = transaction;
                resolve.CommandText = "SELECT Id FROM Cases WHERE CaseNumber = $number COLLATE NOCASE;";
                resolve.Parameters.AddWithValue("$number", caseNumber);
                if (Guid.TryParse(Convert.ToString(resolve.ExecuteScalar()), out Guid savedId)) id = savedId;
            }

            string? existingSourcePath = null;
            using (var findSource = connection.CreateCommand())
            {
                findSource.Transaction = transaction;
                findSource.CommandText = "SELECT SourcePath FROM CaseSources WHERE CaseId = $id;";
                findSource.Parameters.AddWithValue("$id", id.ToString("D"));
                using var reader = findSource.ExecuteReader();
                while (reader.Read())
                {
                    string candidate = reader.GetString(0);
                    if (string.Equals(candidate, sourceKey, StringComparison.OrdinalIgnoreCase))
                    {
                        existingSourcePath = candidate;
                        break;
                    }
                }
            }

            if (existingSourcePath == null)
            {
                using var link = connection.CreateCommand();
                link.Transaction = transaction;
                link.CommandText = @"INSERT INTO CaseSources (CaseId, SourcePath, AddedLocal)
                                     VALUES ($id, $source, $added);";
                link.Parameters.AddWithValue("$id", id.ToString("D"));
                link.Parameters.AddWithValue("$source", sourceKey);
                link.Parameters.AddWithValue("$added", now.ToString("yyyy-MM-dd HH:mm:ss"));
                link.ExecuteNonQuery();
            }
            else if (!string.Equals(existingSourcePath, sourceKey, StringComparison.Ordinal))
            {
                // Refresh legacy uppercase paths while retaining the original display casing.
                using var updateSource = connection.CreateCommand();
                updateSource.Transaction = transaction;
                updateSource.CommandText = @"UPDATE CaseSources SET SourcePath = $source
                                             WHERE CaseId = $id AND SourcePath = $old;";
                updateSource.Parameters.AddWithValue("$source", sourceKey);
                updateSource.Parameters.AddWithValue("$id", id.ToString("D"));
                updateSource.Parameters.AddWithValue("$old", existingSourcePath);
                updateSource.ExecuteNonQuery();
            }

            transaction.Commit();
            return Load(id) ?? throw new InvalidOperationException("Zapisano sprawę, ale nie udało się jej ponownie odczytać.");
        }

        public CaseRecord? Load(Guid id)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT c.Id, c.CaseNumber, c.CaseTitle, c.Location, c.Notes,
                       c.CreatedLocal, c.UpdatedLocal, COUNT(s.SourcePath)
                FROM Cases c LEFT JOIN CaseSources s ON s.CaseId = c.Id
                WHERE c.Id = $id GROUP BY c.Id;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            if (!Guid.TryParse(reader.GetString(0), out Guid savedId)) return null;
            return new CaseRecord
            {
                Id = savedId,
                CaseNumber = reader.GetString(1),
                Title = reader.GetString(2),
                Location = reader.GetString(3),
                Notes = reader.GetString(4),
                CreatedLocal = ParseLocal(reader.GetString(5)),
                UpdatedLocal = ParseLocal(reader.GetString(6)),
                SourceCount = reader.GetInt32(7)
            };
        }

        public List<string> LoadSources(Guid caseId)
        {
            var sources = new List<string>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT SourcePath FROM CaseSources
                                    WHERE CaseId = $id ORDER BY AddedLocal, SourcePath;";
            command.Parameters.AddWithValue("$id", caseId.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                sources.Add(reader.GetString(0));
            return sources;
        }

        public bool Delete(Guid caseId)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var links = connection.CreateCommand())
            {
                links.Transaction = transaction;
                links.CommandText = "DELETE FROM CaseSources WHERE CaseId = $id;";
                links.Parameters.AddWithValue("$id", caseId.ToString("D"));
                links.ExecuteNonQuery();
            }
            int deleted;
            using (var item = connection.CreateCommand())
            {
                item.Transaction = transaction;
                item.CommandText = "DELETE FROM Cases WHERE Id = $id;";
                item.Parameters.AddWithValue("$id", caseId.ToString("D"));
                deleted = item.ExecuteNonQuery();
            }
            transaction.Commit();
            return deleted > 0;
        }

        private static DateTime ParseLocal(string value) =>
            DateTime.TryParse(value, out DateTime parsed) ? parsed : DateTime.MinValue;
    }

    internal sealed class CaseRecord
    {
        public Guid Id { get; set; }
        public string CaseNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string Location { get; set; } = "";
        public string Notes { get; set; } = "";
        public DateTime CreatedLocal { get; set; }
        public DateTime UpdatedLocal { get; set; }
        public int SourceCount { get; set; }

        public override string ToString()
        {
            string title = string.IsNullOrWhiteSpace(Title) ? "" : " — " + Title;
            return $"{CaseNumber}{title}  |  nagrania: {SourceCount}";
        }
    }
}
