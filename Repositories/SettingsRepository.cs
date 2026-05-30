using System.IO;
using System.Text.Json;
using FreeWPFShell.Models;

namespace FreeWPFShell.Repositories
{
    public class SettingsRepository
    {
        private readonly string _filePath;
        private static readonly JsonSerializerOptions s_writeIndented = new() { WriteIndented = true };

        // 缓存已加载的设置，避免重复读磁盘
        private AppSettings? _cached;
        private DateTime _lastModified = DateTime.MinValue;

        public SettingsRepository()
        {
            _filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "settings.json");
        }

        public AppSettings Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return _cached ??= new AppSettings();
                var lastWrite = File.GetLastWriteTime(_filePath);
                if (_cached != null && lastWrite == _lastModified) return _cached;
                string json = File.ReadAllText(_filePath);
                _cached = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                _lastModified = lastWrite;
                return _cached;
            }
            catch { return _cached ??= new AppSettings(); }
        }

        public void Save(AppSettings settings)
        {
            _cached = settings;
            File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, s_writeIndented));
            _lastModified = File.GetLastWriteTime(_filePath);
        }
    }
}
