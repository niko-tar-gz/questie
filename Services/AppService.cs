using Qt.Quick;

namespace questie.Services
{
    [QmlElement(Name = "App", Singleton = true)]
    public class AppService : ObservableObject
    {
        public string Version => Program.Version;
    }
}
