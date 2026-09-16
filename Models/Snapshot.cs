using System;

namespace xBackup.Models
{
    public enum SnapshotStatus
    {
        Running,
        Complete,
        Failed
    }

    public class Snapshot
    {
        public int Id { get; set; }
        public DateTime SnapshotDate { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public SnapshotStatus Status { get; set; }
    }
}
