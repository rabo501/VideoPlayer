using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace VideoPlayer
{
    internal sealed class ClipRange
    {
        public string Name {get;set;}="Fragment";
        public double Start {get;set;}
        public double End {get;set;}
        public override string ToString() => $"{Name} | {TimeSpan.FromSeconds(Start):g} – {TimeSpan.FromSeconds(End):g}";
    }
    internal sealed class ReviewSession
    {
        public double Position {get;set;}
        public double Speed {get;set;}=1;
        public bool Paused {get;set;}=true;
        public double Zoom {get;set;}
        public double PanX {get;set;}
        public double PanY {get;set;}
        public double Panscan {get;set;}
        public double? ClipStart {get;set;}
        public double? ClipEnd {get;set;}
        public List<ClipRange> Clips {get;set;}=new();
    }
    internal sealed class ReviewRepository
    {
        private readonly string _cs;
        public ReviewRepository(string? directoryPath = null)
        {
            string dataDirectory = directoryPath ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(dataDirectory);
            _cs=new SqliteConnectionStringBuilder {DataSource=Path.Combine(dataDirectory,"VideoPlayer.db")}.ToString();
            using var c=Open(); using var q=c.CreateCommand();
            q.CommandText="CREATE TABLE IF NOT EXISTS ReviewSessions (SourcePath TEXT PRIMARY KEY, Payload TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);";
            q.ExecuteNonQuery();
        }
        private SqliteConnection Open() { var c=new SqliteConnection(_cs);c.Open();return c; }
        private static string Key(string path)=>Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        public ReviewSession? Load(string path)
        {
            using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT Payload FROM ReviewSessions WHERE SourcePath=$p";
            q.Parameters.AddWithValue("$p",Key(path));
            return q.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ReviewSession>(json) : null;
        }
        public void Save(string path,ReviewSession data)
        {
            using var c=Open();using var q=c.CreateCommand();
            q.CommandText="INSERT INTO ReviewSessions VALUES($p,$j,$u) ON CONFLICT(SourcePath) DO UPDATE SET Payload=excluded.Payload, UpdatedUtc=excluded.UpdatedUtc";
            q.Parameters.AddWithValue("$p",Key(path));q.Parameters.AddWithValue("$j",JsonSerializer.Serialize(data));
            q.Parameters.AddWithValue("$u",DateTime.UtcNow.ToString("O"));q.ExecuteNonQuery();
        }
    }
}
