using Qt.Quick;

namespace questie.Services
{
    [QmlElement(Name = "AppSettings", Singleton = true)]
    public class SettingsService : ObservableObject
    {
        private static readonly string SettingsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "questie");
        private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.ini");

        private bool _skipNuxWarningDontShow;

        public SettingsService()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (var line in File.ReadAllLines(SettingsFile))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2 && parts[0].Trim().Equals("skipNuxWarningDontShow", StringComparison.OrdinalIgnoreCase))
                        _skipNuxWarningDontShow = parts[1].Trim() == "1";
                }
            }
            catch { }
        }

        // the "don't show again" checkbox on the skip-nux warning dialog
        public bool SkipNuxWarningDontShow
        {
            get => _skipNuxWarningDontShow;
            set
            {
                if (Set(ref _skipNuxWarningDontShow, value))
                    Save();
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(SettingsFile, $"skipNuxWarningDontShow={(_skipNuxWarningDontShow ? "1" : "0")}\n");
            }
            catch { }
        }
    }
}
