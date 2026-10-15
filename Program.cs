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
        internal static void Main(string[] args)
        {

            Qml.LoadFromRootModule("Main");
            Qml.WaitForExit();
        }
    }
}
