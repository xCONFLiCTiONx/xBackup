namespace xBackup.Models
{
    public class BackupFile
    {
        public int Id { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string NormalizedPath { get; set; } = string.Empty;
    }
}
