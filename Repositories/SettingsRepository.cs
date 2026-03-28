using System.IO;
using System.Text.Json;
using FreeWPFShell.Models;

namespace FreeWPFShell.Repositories
{
    public class SettingsRepository
    {
        private readonly string _filePath;

        public SettingsRepository()
        {
            _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        }

        public AppSettings Load()
        {
            if (!File.Exists(_filePath)) return new AppSettings();
            try
            {
                string json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch { return new AppSettings(); }
        }

        public void Save(AppSettings settings)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, options));
        }
    }
}
