using System;

namespace xBackup.Models
{
    public enum ChangeType
    {
        New,
        Modified,
        Deleted
    }

    public class FileVersion
    {
        public int Id { get; set; }
        public int FileId { get; set; }
        public int SnapshotId { get; set; }
        public string? BackupPath { get; set; } // Relative to backup root
        public long Size { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public string? Hash { get; set; }
        public ChangeType ChangeType { get; set; }
    }
}
