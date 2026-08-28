using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows.Services;

public sealed class SearchIndexStore
{
    private const int QueryLimit = 500;
    private readonly string _connectionString;
    private readonly object _gate = new();
    private bool _initialized;

    public SearchIndexStore()
    {
        var directory = Path.Combine(ConfigStore.PortableRoot, "data", "cache");
        var path = Path.Combine(directory, $"{Environment.MachineName.ToUpperInvariant()}.sqlite");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public IReadOnlyList<SearchResult> Search(string query)
    {
        var match = ToFtsMatch(query);
        if (string.IsNullOrWhiteSpace(match))
        {
            return [];
        }

        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT r.name, r.extension, r.path, r.type, r.size, r.modified, r.is_folder
                    FROM result_fts f
                    INNER JOIN results r ON r.result_key = f.result_key
                    WHERE result_fts MATCH $match
                    ORDER BY r.last_used DESC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$match", match);
                command.Parameters.AddWithValue("$limit", QueryLimit);
                using var reader = command.ExecuteReader();
                var results = new List<SearchResult>();
                while (reader.Read())
                {
                    results.Add(new SearchResult
                    {
                        Name = reader.GetString(0),
                        Extension = reader.GetString(1),
                        Path = reader.GetString(2),
                        Type = reader.GetString(3),
                        Size = reader.GetInt64(4),
                        Modified = reader.GetString(5),
                        IsFolder = reader.GetInt64(6) != 0,
                    });
                }
                AppLogger.Info("index", $"query=\"{query}\" results={results.Count}");
                return results;
            }
            catch (Exception ex)
            {
                AppLogger.Error("index", ex, "local cache query failed");
                return [];
            }
        }
    }

    public void Upsert(IEnumerable<SearchResult> source)
    {
        var results = source.ToList();
        if (results.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                foreach (var result in results)
                {
                    var key = HistoryStore.ResultKey(result);
                    using var resultCommand = connection.CreateCommand();
                    resultCommand.Transaction = transaction;
                    resultCommand.CommandText = """
                        INSERT INTO results (result_key, name, extension, path, type, size, modified, is_folder, last_used)
                        VALUES ($key, $name, $extension, $path, $type, $size, $modified, $folder, $lastUsed)
                        ON CONFLICT(result_key) DO UPDATE SET
                          name = excluded.name,
                          extension = excluded.extension,
                          path = excluded.path,
                          type = excluded.type,
                          size = excluded.size,
                          modified = excluded.modified,
                          is_folder = excluded.is_folder,
                          last_used = excluded.last_used;
                        """;
                    resultCommand.Parameters.AddWithValue("$key", key);
                    resultCommand.Parameters.AddWithValue("$name", result.Name);
                    resultCommand.Parameters.AddWithValue("$extension", result.Extension);
                    resultCommand.Parameters.AddWithValue("$path", result.Path);
                    resultCommand.Parameters.AddWithValue("$type", result.Type);
                    resultCommand.Parameters.AddWithValue("$size", result.Size);
                    resultCommand.Parameters.AddWithValue("$modified", result.Modified);
                    resultCommand.Parameters.AddWithValue("$folder", result.IsFolder ? 1 : 0);
                    resultCommand.Parameters.AddWithValue("$lastUsed", DateTime.UtcNow.Ticks);
                    resultCommand.ExecuteNonQuery();

                    using var deleteFts = connection.CreateCommand();
                    deleteFts.Transaction = transaction;
                    deleteFts.CommandText = "DELETE FROM result_fts WHERE result_key = $key;";
                    deleteFts.Parameters.AddWithValue("$key", key);
                    deleteFts.ExecuteNonQuery();

                    using var insertFts = connection.CreateCommand();
                    insertFts.Transaction = transaction;
                    insertFts.CommandText = "INSERT INTO result_fts (result_key, name, path, extension) VALUES ($key, $name, $path, $extension);";
                    insertFts.Parameters.AddWithValue("$key", key);
                    insertFts.Parameters.AddWithValue("$name", result.Name);
                    insertFts.Parameters.AddWithValue("$path", result.Path);
                    insertFts.Parameters.AddWithValue("$extension", result.Extension);
                    insertFts.ExecuteNonQuery();
                }
                transaction.Commit();
                AppLogger.Info("index", $"upserted results={results.Count}");
            }
            catch (Exception ex)
            {
                AppLogger.Error("index", ex, "local cache update failed");
            }
        }
    }

    private SqliteConnection Open()
    {
        var databasePath = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ConfigStore.PortableRoot);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        if (_initialized)
        {
            return connection;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS results (
              result_key TEXT PRIMARY KEY,
              name TEXT NOT NULL,
              extension TEXT NOT NULL,
              path TEXT NOT NULL,
              type TEXT NOT NULL,
              size INTEGER NOT NULL,
              modified TEXT NOT NULL,
              is_folder INTEGER NOT NULL,
              last_used INTEGER NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS result_fts USING fts5(result_key UNINDEXED, name, path, extension, tokenize = 'unicode61');
            """;
        command.ExecuteNonQuery();
        _initialized = true;
        return connection;
    }

    private static string ToFtsMatch(string query)
    {
        var terms = Regex.Matches(query ?? "", "[\\p{L}\\p{N}_]+")
            .Select(match => match.Value)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(term => $"{term}*");
        return string.Join(" AND ", terms);
    }
}
