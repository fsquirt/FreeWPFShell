namespace FreeWPFShell.Models
{
    public class RemoteFile
    {
        public string Icon { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Perms { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public string FullName { get; set; } = string.Empty;
    }
}
