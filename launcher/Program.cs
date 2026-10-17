using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;

namespace QuestieLauncher;

internal static class Program
{
    private const string PayloadResource = "payload.zip";

    private static int Main(string[] args)
    {
        var appDir = ResolveAppDir();
        var exeName = OperatingSystem.IsWindows() ? "questie.exe" : "questie";
        var exePath = Path.Combine(appDir, exeName);
        var versionFile = Path.Combine(appDir, "app.ver");

        // app.ver lives inside the app folder. if it is missing, or it records a
        // different version than the one embedded in this launcher, unpack again.
        var installed = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : "";
        if (installed != BuildInfo.Version || !File.Exists(exePath))
            Extract(appDir, versionFile);

        var startInfo = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = appDir,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        Process.Start(startInfo);
        return 0;
    }

    private static string ResolveAppDir()
    {
        string dataRoot;
        if (OperatingSystem.IsWindows())
        {
            dataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            dataRoot = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : xdg;
        }

        return Path.Combine(dataRoot, "questie", "app");
    }

    private static void Extract(string appDir, string versionFile)
    {
        if (Directory.Exists(appDir))
            Directory.Delete(appDir, recursive: true);
        Directory.CreateDirectory(appDir);

        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException($"embedded resource '{PayloadResource}' not found");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
            while (relative.StartsWith("./", StringComparison.Ordinal))
                relative = relative[2..];
            if (relative.Length == 0 || relative == ".")
                continue;

            var target = Path.Combine(appDir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (relative.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }

        // zip does not carry the unix execute bit, so restore it on linux/macos
        if (!OperatingSystem.IsWindows())
            MarkExecutable(appDir);

        File.WriteAllText(versionFile, BuildInfo.Version);
    }

    [UnsupportedOSPlatform("windows")]
    private static void MarkExecutable(string appDir)
    {
        SetExecutable(Path.Combine(appDir, "questie"));

        var adbDir = Path.Combine(appDir, "adb");
        if (!Directory.Exists(adbDir))
            return;

        foreach (var file in Directory.EnumerateFiles(adbDir, "*", SearchOption.AllDirectories))
            SetExecutable(file);
    }

    [UnsupportedOSPlatform("windows")]
    private static void SetExecutable(string path)
    {
        if (!File.Exists(path))
            return;

        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
