using Qt.Quick;

[assembly: Qt.Generate(
    MainIncludes = "#include <QtQuickControls2/QQuickStyle>\n#include <QGuiApplication>\n#include <QIcon>\n#include <QCoreApplication>",
    MainStartingUp = "QQuickStyle::setStyle(QStringLiteral(\"Fusion\"));",
    MainBeforeAppExec = "QGuiApplication::setWindowIcon(QIcon(QCoreApplication::applicationDirPath() + QStringLiteral(\"/logo.png\")));",
    Packages = "QuickControls2",
    Libraries = "Qt6::QuickControls2")]

namespace questie
{
    public class Program
    {
        // single source of truth for the app version
        public const string Version = "1.1.1";

        internal static void Main(string[] args)
        {

            Qml.LoadFromRootModule("Main");
            Qml.WaitForExit();
        }
    }
}
