using System.Diagnostics;

namespace questie.Services
{
    internal sealed record AdbDevice(string Serial, string State);

    internal static class DeviceTools
    {
        private static readonly char[] Split = { ' ', '\t' };

        public static async Task<string> DetectModeAsync(string adb, string fastboot, CancellationToken ct)
        {
            if (await FastbootDeviceSerialAsync(fastboot, ct).ConfigureAwait(false) is not null)
                return "fastboot";
            if ((await AdbDevicesAsync(adb, ct).ConfigureAwait(false)).Count > 0)
                return "adb";
            return "none";
        }

        public static async Task<List<AdbDevice>> AdbDevicesAsync(string adb, CancellationToken ct)
        {
            var list = new List<AdbDevice>();
            try
            {
                var r = await ToolRunner.RunAsync(adb, "devices", 15000, null, ct).ConfigureAwait(false);
                foreach (var raw in r.Output.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var parts = line.Split(Split, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        list.Add(new AdbDevice(parts[0], parts[1]));
                }
            }
            catch { }
            return list;
        }

        public static async Task<bool> IsAdbRootAsync(string adb, string? serial, CancellationToken ct)
        {
            try
            {
                var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";
                var r = await ToolRunner.RunAsync(adb, $"{serialArg}shell id -u", 15000, null, ct).ConfigureAwait(false);
                return r.ExitCode == 0 && r.Output.Trim() == "0";
            }
            catch { return false; }
        }

        public static async Task<string?> FastbootDeviceSerialAsync(string fastboot, CancellationToken ct)
        {
            try
            {
                var r = await ToolRunner.RunAsync(fastboot, "devices", 15000, null, ct).ConfigureAwait(false);
                foreach (var raw in r.Output.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || !line.Contains("fastboot")) continue;
                    var parts = line.Split(Split, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0) return parts[0];
                }
            }
            catch { }
            return null;
        }

        public static async Task<Dictionary<string, string>> FastbootGetvarAllAsync(string fastboot, CancellationToken ct)
        {
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var r = await ToolRunner.RunAsync(fastboot, "getvar all", 30000, null, ct).ConfigureAwait(false);
                foreach (var raw in r.Output.Split('\n'))
                {
                    var line = raw.Trim();
                    const string prefix = "(bootloader)";
                    if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        line = line[prefix.Length..].Trim();
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var key = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    if (key.Length > 0 && !vars.ContainsKey(key))
                        vars[key] = value;
                }
            }
            catch { }
            return vars;
        }

        public static async Task<bool> WaitForSideloadAsync(string adb, CancellationToken ct, int timeoutSeconds = 120)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var devices = await AdbDevicesAsync(adb, ct).ConfigureAwait(false);
                    if (devices.Any(d => d.State is "sideload" or "recovery")) return true;
                }
                catch { }
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }

        public static async Task<bool> WaitForDeviceAsync(string adb, CancellationToken ct, int timeoutSeconds = 240)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var devices = await AdbDevicesAsync(adb, ct).ConfigureAwait(false);
                    if (devices.Any(d => d.State is "device" or "recovery")) return true;
                }
                catch { }
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }

        public static async Task<bool> WaitForFastbootAsync(string fastboot, CancellationToken ct, int timeoutSeconds = 60)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (await FastbootDeviceSerialAsync(fastboot, ct).ConfigureAwait(false) is not null) return true;
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
        }
    }
}
