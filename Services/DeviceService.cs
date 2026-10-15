using System.Reflection;
using Qt.Quick;

namespace questie.Services
{
    [QmlElement(Name = "Device", Singleton = true)]
    public class DeviceService : ObservableObject
    {
        private static readonly Dictionary<string, string> CodenameToName = new(StringComparer.OrdinalIgnoreCase)
        {
            ["monterey"] = "quest 1",
            ["hollywood"] = "quest 2",
            ["eureka"] = "quest 3",
            ["panther"] = "quest 3s",
            ["seacliff"] = "quest pro",
            ["stella"] = "quest 4",
            ["seiku"] = "quest emulator (how????)",
        };

        private static readonly Dictionary<string, string> CodenameToArchive = new(StringComparer.OrdinalIgnoreCase)
        {
            ["monterey"] = "https://cocaine.trade/Quest_firmware",
            ["hollywood"] = "https://cocaine.trade/Quest_2_firmware",
            ["seacliff"] = "https://cocaine.trade/Quest_Pro_firmware",
            ["eureka"] = "https://cocaine.trade/Quest_3_firmware",
            ["panther"] = "https://cocaine.trade/Quest_3S_firmware",
        };

        private bool _connected;
        private string _headsetName = "no headset detected";
        private string _codename = "none";
        private string _firmwareIncremental = "";
        private string _firmwareVersion = "";
        private string _serial = "";
        private bool _serialRevealed;
        private string _lockState = "unknown";
        private string _headsetImage = "";
        private string _status = "welcome! connect a headset, then press refresh.";
        private bool _busy;
        private string _adbPath = ResolveTool("adb");
        private string _fastbootPath = ResolveTool("fastboot");

        private static string ResolveTool(string name)
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
                foreach (var candidate in new[]
                         {
                             Path.Combine(baseDir, "adb", exe),
                             Path.Combine(baseDir, exe),
                         })
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch { }
            return OperatingSystem.IsWindows() ? name + ".exe" : name;
        }

        public bool Connected { get => _connected; private set => Set(ref _connected, value); }
        public string HeadsetName { get => _headsetName; private set => Set(ref _headsetName, value); }
        public string Codename { get => _codename; private set => Set(ref _codename, value); }
        public string FirmwareIncremental { get => _firmwareIncremental; private set => Set(ref _firmwareIncremental, value); }
        public string FirmwareVersion { get => _firmwareVersion; private set => Set(ref _firmwareVersion, value); }
        public string Serial { get => _serial; private set => Set(ref _serial, value); }
        public bool SerialRevealed { get => _serialRevealed; set => Set(ref _serialRevealed, value); }
        // "locked", "unlocked" or "unknown"
        public string LockState { get => _lockState; private set => Set(ref _lockState, value); }
        public string HeadsetImage { get => _headsetImage; private set => Set(ref _headsetImage, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public bool Busy { get => _busy; private set => Set(ref _busy, value); }
        public string AdbPath { get => _adbPath; set => Set(ref _adbPath, string.IsNullOrWhiteSpace(value) ? ResolveTool("adb") : value.Trim()); }
        public string FastbootPath { get => _fastbootPath; set => Set(ref _fastbootPath, string.IsNullOrWhiteSpace(value) ? ResolveTool("fastboot") : value.Trim()); }
        public string ArchiveUrl
        {
            get => CodenameToArchive.TryGetValue(Codename, out var url) ? url : "";
        }

        public string AppLogo => HeadsetImageProvider.GetLogoUrl();

        public void Refresh()
        {
            if (Busy) return;
            Busy = true;
            Status = "detecting headset...";
            var adb = AdbPath;
            var fastboot = FastbootPath;
            Task.Run(async () =>
            {
                try
                {
                    await DetectAsync(adb, fastboot, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Connected = false;
                    Status = "detection failed: " + ex.Message;
                }
                finally
                {
                    Busy = false;
                }
            });
        }

        public void Reboot(string mode)
        {
            if (Busy) return;
            Busy = true;
            Status = "rebooting...";
            var adb = AdbPath;
            var serial = Serial;
            var arg = mode == "bootloader" ? "reboot bootloader" : "reboot";
            Task.Run(async () =>
            {
                try
                {
                    var prefix = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";
                    var r = await ToolRunner.RunAsync(adb, prefix + arg, 60000, null, CancellationToken.None).ConfigureAwait(false);
                    Status = r.ExitCode == 0 ? "reboot command sent!" : "reboot failed!";
                }
                catch (Exception ex)
                {
                    Status = "reboot failed: " + ex.Message;
                }
                finally
                {
                    Busy = false;
                }
            });
        }

        private async Task DetectAsync(string adb, string fastboot, CancellationToken ct)
        {
            string? serial = null;
            try
            {
                var devices = await ToolRunner.RunAsync(adb, "devices -l", 15000, null, ct).ConfigureAwait(false);
                serial = ParseSerial(devices.Output);
            }
            catch (Exception ex)
            {
                SetDisconnected("adb not available: " + ex.Message);
                return;
            }

            if (serial is null)
            {
                // no adb device, maybe device is in fastboot mode?
                var fbSerial = await DeviceTools.FastbootDeviceSerialAsync(fastboot, ct).ConfigureAwait(false);
                if (fbSerial is not null)
                {
                    var vars = await DeviceTools.FastbootGetvarAllAsync(fastboot, ct).ConfigureAwait(false);
                    var fbProps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (vars.TryGetValue("product", out var product) && product.Length > 0)
                    {
                        fbProps["ro.product.device"] = product;
                        fbProps["ro.product.name"] = product;
                        fbProps["ro.product.model"] = product;
                    }
                    var fbCodename = ResolveCodename(fbProps);
                    Connected = true;
                    HeadsetName = CodenameToName.TryGetValue(fbCodename, out var fbName) ? fbName + " (fastboot)" : "a headset in fastboot mode";
                    Codename = fbCodename;
                    FirmwareIncremental = "";
                    FirmwareVersion = "";
                    Serial = vars.TryGetValue("serialno", out var sn) ? sn.Trim() : "";
                    SerialRevealed = false;
                    LockState = ResolveFastbootLockState(vars);
                    if (LockState == "unknown")
                        LockState = await QueryFastbootLockStateAsync(fastboot, ct).ConfigureAwait(false);
                    UpdateImage();
                    Notify(nameof(ArchiveUrl));
                    Status = "found headset in fastboot! ";
                }
                else
                {
                    SetDisconnected("no device found.");
                }
                return;
            }

            Serial = serial;
            SerialRevealed = false;
            Connected = true;

            var props = await ReadPropsAsync(adb, serial, ct).ConfigureAwait(false);
            var codename = ResolveCodename(props);
            Codename = codename;
            HeadsetName = CodenameToName.TryGetValue(codename, out var name) ? name : "unknown headset";
            FirmwareIncremental = props.TryGetValue("ro.build.version.incremental", out var inc) ? inc.Trim() : "";
            var display = props.TryGetValue("ro.build.display.id", out var d) ? d.Trim() : "";
            FirmwareVersion = display.Length > 0 ? display : FirmwareIncremental;
            LockState = ResolveLockState(props);
            UpdateImage();
            Notify(nameof(ArchiveUrl));
            Status = $"connected: {HeadsetName} ({Codename}).";
        }

        private void SetDisconnected(string status)
        {
            Connected = false;
            HeadsetName = "no headset detected";
            Codename = "none";
            FirmwareIncremental = "";
            FirmwareVersion = "";
            Serial = "";
            SerialRevealed = false;
            LockState = "unknown";
            UpdateImage();
            Notify(nameof(ArchiveUrl));
            Status = status;
        }

        private static string? ParseSerial(string output)
        {
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1].Equals("device", StringComparison.OrdinalIgnoreCase))
                    return parts[0];
            }
            return null;
        }

        private static async Task<Dictionary<string, string>> ReadPropsAsync(string adb, string serial, CancellationToken ct)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] keys =
            [
                "ro.product.device", "ro.product.name", "ro.build.product", "ro.product.model",
                "ro.build.fingerprint", "ro.build.version.incremental", "ro.build.display.id",
                "ro.boot.flash.locked", "ro.boot.vbmeta.device_state", "ro.boot.verifiedbootstate",
                "ro.serialno",
            ];
            foreach (var key in keys)
            {
                try
                {
                    var r = await ToolRunner.RunAsync(adb, $"-s {serial} shell getprop {key}", 15000, null, ct).ConfigureAwait(false);
                    var val = r.Output.Trim();
                    if (val.Length > 0) result[key] = val;
                }
                catch { }
            }
            return result;
        }

        internal static string ResolveCodename(Dictionary<string, string> props)
        {
            string[] candidates =
            [
                props.TryGetValue("ro.product.device", out var a) ? a : "",
                props.TryGetValue("ro.product.name", out var b) ? b : "",
                props.TryGetValue("ro.build.product", out var c) ? c : "",
                props.TryGetValue("ro.build.fingerprint", out var d) ? d : "",
                props.TryGetValue("ro.product.model", out var e) ? e : "",
            ];
            var meow = string.Join(" ", candidates).ToLowerInvariant();
            foreach (var code in new[] { "monterey", "hollywood", "eureka", "panther", "seacliff", "stella", "seiku" })
                if (meow.Contains(code)) return code;

            // fallback
            if (meow.Contains("quest pro")) return "seacliff";
            if (meow.Contains("quest 3s") || meow.Contains("quest3s")) return "panther";
            if (meow.Contains("quest 3")) return "eureka";
            if (meow.Contains("quest 2")) return "hollywood";
            if (meow.Contains("quest")) return "monterey";
            return "unknown";
        }

        private static string ResolveLockState(Dictionary<string, string> props)
        {
            if (props.TryGetValue("ro.boot.flash.locked", out var v))
            {
                if (v.Trim() == "1") return "locked";
                if (v.Trim() == "0") return "unlocked";
            }
            if (props.TryGetValue("ro.boot.vbmeta.device_state", out var s))
            {
                if (s.Contains("locked", StringComparison.OrdinalIgnoreCase)) return "locked";
                if (s.Contains("unlocked", StringComparison.OrdinalIgnoreCase)) return "unlocked";
            }
            return "unknown";
        }

        private static string ResolveFastbootLockState(Dictionary<string, string> vars)
        {
            if (vars.TryGetValue("unlocked", out var u))
            {
                var v = u.Trim().ToLowerInvariant();
                if (v is "yes" or "true" or "unlocked") return "unlocked";
                if (v is "no" or "false" or "locked") return "locked";
            }
            return "unknown";
        }

        private static async Task<string> QueryFastbootLockStateAsync(string fastboot, CancellationToken ct)
        {
            try
            {
                var r = await ToolRunner.RunAsync(fastboot, "getvar unlocked", 15000, null, ct).ConfigureAwait(false);
                var o = r.Output.ToLowerInvariant();
                if (o.Contains("unlocked: yes")) return "unlocked";
                if (o.Contains("unlocked: no")) return "locked";
            }
            catch { }
            return "unknown";
        }

        private void UpdateImage()
        {
            try { HeadsetImage = HeadsetImageProvider.GetImageUrl(Codename); }
            catch { HeadsetImage = ""; }
        }
    }

    internal static class HeadsetImageProvider
    {
        private static readonly string[] Known = ["monterey", "hollywood", "eureka", "panther", "seacliff", "stella", "seiku"];

        public static string GetImageUrl(string codename)
        {
            if (string.Equals(codename, "none", StringComparison.OrdinalIgnoreCase))
                return Extract("none", fallbackToUnknown: false);
            var code = Known.Contains(codename, StringComparer.OrdinalIgnoreCase) ? codename.ToLowerInvariant() : "unknown";
            return Extract(code, fallbackToUnknown: true);
        }

        public static string GetLogoUrl() => Extract("logo", fallbackToUnknown: false);

        private static string Extract(string name, bool fallbackToUnknown)
        {
            var dir = Path.Combine(Path.GetTempPath(), "questie", "img");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, name + ".png");
            if (!new FileInfo(file).Exists || new FileInfo(file).Length == 0)
            {
                var asm = Assembly.GetExecutingAssembly();
                var resource = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(name + ".png", StringComparison.OrdinalIgnoreCase));
                if (resource is null && fallbackToUnknown)
                    resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("unknown.png", StringComparison.OrdinalIgnoreCase));
                if (resource is null) return "";
                using var stream = asm.GetManifestResourceStream(resource);
                if (stream is null) return "";
                using var outFile = File.Create(file);
                stream.CopyTo(outFile);
            }
            return new Uri(file).AbsoluteUri;
        }
    }
}
