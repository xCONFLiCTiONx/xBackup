using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using xBackup.Models;

namespace xBackup
{
    public class BackupCatalog : IDisposable
    {
        private readonly SqliteConnection _connection;

        public BackupCatalog(string dbPath)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                ForeignKeys = true
            }.ToString();

            _connection = new SqliteConnection(connectionString);
            _connection.Open();
            InitializeSchema();
        }

        private void InitializeSchema()
        {
            string schema = @"
                CREATE TABLE IF NOT EXISTS Snapshots (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SnapshotDate TEXT NOT NULL,
                    StartedUtc TEXT NOT NULL,
                    CompletedUtc TEXT,
                    Status TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Files (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SourcePath TEXT NOT NULL,
                    NormalizedPath TEXT NOT NULL UNIQUE
                );

                CREATE TABLE IF NOT EXISTS FileVersions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FileId INTEGER NOT NULL,
                    SnapshotId INTEGER NOT NULL,
                    BackupPath TEXT,
                    Size INTEGER,
                    LastWriteUtc TEXT,
                    Hash TEXT,
                    ChangeType TEXT NOT NULL,
                    FOREIGN KEY (FileId) REFERENCES Files(Id),
                    FOREIGN KEY (SnapshotId) REFERENCES Snapshots(Id)
                );

                CREATE INDEX IF NOT EXISTS IDX_Files_NormalizedPath ON Files(NormalizedPath);
                CREATE INDEX IF NOT EXISTS IDX_FileVersions_FileId_SnapshotId ON FileVersions(FileId, SnapshotId);
                CREATE INDEX IF NOT EXISTS IDX_FileVersions_SnapshotId ON FileVersions(SnapshotId);
            ";

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = schema;
                command.ExecuteNonQuery();
            }
        }

        public Snapshot CreateSnapshot(DateTime date)
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = @"
                    INSERT INTO Snapshots (SnapshotDate, StartedUtc, Status)
                    VALUES (@date, @started, @status);
                    SELECT last_insert_rowid();";

                command.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@started", DateTime.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("@status", SnapshotStatus.Running.ToString());

                int id = Convert.ToInt32(command.ExecuteScalar());
                return new Snapshot
                {
                    Id = id,
                    SnapshotDate = date,
                    StartedUtc = DateTime.UtcNow,
                    Status = SnapshotStatus.Running
                };
            }
        }

        public void UpdateSnapshotStatus(int id, SnapshotStatus status)
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = @"
                    UPDATE Snapshots
                    SET Status = @status, CompletedUtc = @completed
                    WHERE Id = @id";

                command.Parameters.AddWithValue("@status", status.ToString());
                command.Parameters.AddWithValue("@completed", status == SnapshotStatus.Complete ? DateTime.UtcNow.ToString("O") : DBNull.Value);
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }
        }

        public void DeleteSnapshot(int id)
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM FileVersions WHERE SnapshotId = @id; DELETE FROM Snapshots WHERE Id = @id;";
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
            }
        }

        public BackupFile GetOrCreateFile(string sourcePath)
        {
            string normalized = NormalizePath(sourcePath);

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT Id, SourcePath FROM Files WHERE NormalizedPath = @norm";
                command.Parameters.AddWithValue("@norm", normalized);

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new BackupFile
                        {
                            Id = reader.GetInt32(0),
                            SourcePath = reader.GetString(1),
                            NormalizedPath = normalized
                        };
                    }
                }
            }

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = @"
                    INSERT INTO Files (SourcePath, NormalizedPath)
                    VALUES (@source, @norm);
                    SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("@source", sourcePath);
                command.Parameters.AddWithValue("@norm", normalized);

                int id = Convert.ToInt32(command.ExecuteScalar());
                return new BackupFile
                {
                    Id = id,
                    SourcePath = sourcePath,
                    NormalizedPath = normalized
                };
            }
        }

        public FileVersion? GetLatestVersion(int fileId, int? maxSnapshotId = null)
        {
            using (var command = _connection.CreateCommand())
            {
                string sql = @"
                    SELECT Id, FileId, SnapshotId, BackupPath, Size, LastWriteUtc, Hash, ChangeType
                    FROM FileVersions
                    WHERE FileId = @fileId";

                if (maxSnapshotId.HasValue)
                {
                    sql += " AND SnapshotId <= @maxSnap";
                }

                sql += " ORDER BY SnapshotId DESC LIMIT 1";

                command.CommandText = sql;
                command.Parameters.AddWithValue("@fileId", fileId);
                if (maxSnapshotId.HasValue)
                {
                    command.Parameters.AddWithValue("@maxSnap", maxSnapshotId.Value);
                }

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new FileVersion
                        {
                            Id = reader.GetInt32(0),
                            FileId = reader.GetInt32(1),
                            SnapshotId = reader.GetInt32(2),
                            BackupPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                            Size = reader.GetInt64(4),
                            LastWriteUtc = DateTime.Parse(reader.GetString(5)),
                            Hash = reader.IsDBNull(6) ? null : reader.GetString(6),
                            ChangeType = Enum.Parse<ChangeType>(reader.GetString(7))
                        };
                    }
                }
            }
            return null;
        }

        public void AddFileVersion(FileVersion version)
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = @"
                    INSERT INTO FileVersions (FileId, SnapshotId, BackupPath, Size, LastWriteUtc, Hash, ChangeType)
                    VALUES (@fileId, @snapId, @path, @size, @write, @hash, @type)";

                command.Parameters.AddWithValue("@fileId", version.FileId);
                command.Parameters.AddWithValue("@snapId", version.SnapshotId);
                command.Parameters.AddWithValue("@path", (object?)version.BackupPath ?? DBNull.Value);
                command.Parameters.AddWithValue("@size", version.Size);
                command.Parameters.AddWithValue("@write", version.LastWriteUtc.ToString("O"));
                command.Parameters.AddWithValue("@hash", (object?)version.Hash ?? DBNull.Value);
                command.Parameters.AddWithValue("@type", version.ChangeType.ToString());

                command.ExecuteNonQuery();
            }
        }

        public List<Snapshot> GetCompletedSnapshots()
        {
            var list = new List<Snapshot>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT Id, SnapshotDate, StartedUtc, CompletedUtc, Status FROM Snapshots WHERE Status = 'Complete' ORDER BY SnapshotDate DESC";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        list.Add(new Snapshot
                        {
                            Id = reader.GetInt32(0),
                            SnapshotDate = DateTime.Parse(reader.GetString(1)),
                            StartedUtc = DateTime.Parse(reader.GetString(2)),
                            CompletedUtc = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)),
                            Status = SnapshotStatus.Complete
                        });
                    }
                }
            }
            return list;
        }

        public List<BackupFile> GetAllFiles()
        {
            var list = new List<BackupFile>();
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT Id, SourcePath, NormalizedPath FROM Files";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        list.Add(new BackupFile
                        {
                            Id = reader.GetInt32(0),
                            SourcePath = reader.GetString(1),
                            NormalizedPath = reader.GetString(2)
                        });
                    }
                }
            }
            return list;
        }

        public string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            try
            {
                // Full path resolution, case-insensitive normalization
                string fullPath = Path.GetFullPath(path);
                return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
            }
            catch
            {
                return path.ToUpperInvariant();
            }
        }

        public Snapshot? GetSnapshotByFolderName(string folderName)
        {
            // folderName is Snapshot_yyyy-MM-dd_HHmmss
            if (folderName.Length < 19 || !folderName.StartsWith("Snapshot_")) return null;

            string datePart = folderName.Substring(9);
            if (!DateTime.TryParseExact(datePart, "yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime date))
            {
                // Try the old format Snapshot_yyyy-MM-dd just in case
                if (!DateTime.TryParseExact(datePart, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date))
                {
                    return null;
                }
            }

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT Id, SnapshotDate, StartedUtc, CompletedUtc, Status FROM Snapshots WHERE SnapshotDate LIKE @datePat AND Status = 'Complete' LIMIT 1";
                command.Parameters.AddWithValue("@datePat", date.ToString("yyyy-MM-dd") + "%");

                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return new Snapshot
                        {
                            Id = reader.GetInt32(0),
                            SnapshotDate = DateTime.Parse(reader.GetString(1)),
                            StartedUtc = DateTime.Parse(reader.GetString(2)),
                            CompletedUtc = reader.IsDBNull(3) ? null : DateTime.Parse(reader.GetString(3)),
                            Status = SnapshotStatus.Complete
                        };
                    }
                }
            }
            return null;
        }
        public void MarkAbandonedSnapshotsFailed()
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "UPDATE Snapshots SET Status = @failed WHERE Status = @running";
                command.Parameters.AddWithValue("@failed", SnapshotStatus.Failed.ToString());
                command.Parameters.AddWithValue("@running", SnapshotStatus.Running.ToString());
                command.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            _connection.Dispose();
        }
    }
}
