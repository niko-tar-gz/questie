using System.IO.Compression;
using System.Security.Cryptography;
using Qt.Quick;

namespace questie.Services
{
    [QmlElement(Name = "Firmware", Singleton = true)]
    public class FirmwareService : ObservableObject
    {
        private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        private bool _busy;
        private double _progress = -1;
        private string _status = "idle.";
        private string _log = "";
        private CancellationTokenSource? _cts;

        public bool Busy { get => _busy; private set => Set(ref _busy, value); }
        // 0..1 while downloading, -1 when indeterminate
        public double Progress { get => _progress; private set => Set(ref _progress, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public string Log { get => _log; private set => Set(ref _log, value); }

        static FirmwareService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Questie/1.0");
        }

        public void ClearLog() => Log = "";

        public void ClearCache(string serial, string adbPath, string fastbootPath)
        {
            if (Busy) return;
            StartOperation("clearing cache...");
            AppendLog("clearing the cache partition");
            Task.Run(() => RunGuarded(() => WipeDeviceAsync(serial, adbPath, fastbootPath, false, true, Cts().Token)));
        }

        public void WipeDevice(string serial, string adbPath, string fastbootPath)
        {
            if (Busy) return;
            StartOperation("wiping device...");
            AppendLog("wiping the headset");
            Task.Run(() => RunGuarded(() => WipeDeviceAsync(serial, adbPath, fastbootPath, true, true, Cts().Token)));
        }

        private async Task WipeDeviceAsync(
            string serial, string adb, string fastboot, bool eraseUserdata, bool eraseCache, CancellationToken ct)
        {
            if (await DeviceTools.FastbootDeviceSerialAsync(fastboot, ct).ConfigureAwait(false) is null)
            {
                Status = "rebooting to fastboot...";
                var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";
                AppendLog("rebooting to fastboot...");
                await ToolRunner.RunAsync(adb, $"{serialArg}reboot bootloader", 60000, AppendOutput, ct).ConfigureAwait(false);
                if (!await DeviceTools.WaitForFastbootAsync(fastboot, ct).ConfigureAwait(false))
                    throw new TimeoutException("timed out while waiting for device. hold vol- and power to put the headset in fastboot.");
            }
            else
            {
                AppendLog("headset is already in fastboot mode.");
            }

            if (eraseUserdata)
            {
                Status = "erasing userdata...";
                var u = await ToolRunner.RunAsync(fastboot, "erase userdata", 300000, AppendOutput, ct).ConfigureAwait(false);
                if (u.ExitCode != 0) throw new InvalidOperationException("userdata wipe failed!");
                AppendLog("userdata erased.");
            }
            if (eraseCache)
            {
                Status = "erasing cache...";
                var c = await ToolRunner.RunAsync(fastboot, "erase cache", 120000, AppendOutput, ct).ConfigureAwait(false);
                AppendLog(c.ExitCode == 0
                    ? "cache erased."
                    : "fastboot erase cache failed, this is fine i think");
                if (c.ExitCode != 0 && eraseUserdata)
                    throw new InvalidOperationException("cache erase failed after userdata was already erased");
            }

            Status = "rebooting to system...";
            AppendLog("rebooting to system...");
            await ToolRunner.RunAsync(fastboot, "reboot", 60000, AppendOutput, ct).ConfigureAwait(false);
            Status = eraseUserdata ? "wipe complete!" : "cache cleared.";
            AppendLog("done.");
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { }
            AppendLog("cancellation requested.");
        }

        // fallback
        public string FetchPage(string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd("Questie/1.0");
                using var resp = Http.Send(req, HttpCompletionOption.ResponseContentRead);
                resp.EnsureSuccessStatusCode();
                return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"failed to download {url}: {ex.Message}", ex);
            }
        }

        private static bool PartitionFlag(string? flags, string name)
            => (flags ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));

        private void LogErasePlan(bool eraseUserdata, bool eraseCache)
        {
            if (!eraseUserdata && !eraseCache)
            {
                AppendLog("no partitions will be erased.");
                return;
            }
            var parts = eraseUserdata ? "userdata" : "";
            if (eraseCache) parts += (parts.Length > 0 ? " + " : "") + "cache";
            AppendLog($"will erase {parts} before installing.");
        }

        public void FlashOta(string url, string incremental, string sha256, string serial, string adbPath, string fastbootPath, string eraseFlags)
        {
            if (Busy) return;
            var eraseUserdata = PartitionFlag(eraseFlags, "userdata");
            var eraseCache = PartitionFlag(eraseFlags, "cache");
            StartOperation($"preparing ota {incremental}...");
            LogErasePlan(eraseUserdata, eraseCache);
            var args = (url, incremental, sha256, serial, adbPath, fastbootPath, eraseUserdata, eraseCache);
            Task.Run(() => RunGuarded(() => FlashOtaAsync(args, Cts().Token)));
        }

        public void FlashFactory(string url, string serial, string adbPath, string fastbootPath, string eraseFlags)
        {
            if (Busy) return;
            var eraseUserdata = PartitionFlag(eraseFlags, "userdata");
            var eraseCache = PartitionFlag(eraseFlags, "cache");
            StartOperation("preparing factory fw...");
            LogErasePlan(eraseUserdata, eraseCache);
            var args = (url, serial, adbPath, fastbootPath, eraseUserdata, eraseCache);
            Task.Run(() => RunGuarded(() => FlashFactoryAsync(args, Cts().Token)));
        }

        private CancellationTokenSource Cts()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            return _cts;
        }

        private void StartOperation(string status)
        {
            Busy = true;
            Progress = -1;
            Status = status;
            Log = "";
            AppendLog(status);
        }

        private async Task RunGuarded(Func<Task> work)
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Status = "cancelled.";
                AppendLog("operation cancelled.");
            }
            catch (Exception ex)
            {
                Status = "failed: " + ex.Message;
                AppendLog("error: " + ex.Message);
            }
            finally
            {
                Busy = false;
                Progress = -1;
            }
        }

        private void AppendLog(string line)
        {
            var next = string.IsNullOrEmpty(Log) ? line : Log + "\n" + line;
            if (next.Length > 30000) next = next[^30000..];
            Log = next;
        }

        private void AppendOutput(string line) => AppendLog("  " + line);

        private static string TempDir(params string[] parts)
        {
            var dir = Path.Combine(new[] { Path.GetTempPath(), "questie" }.Concat(parts).ToArray());
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string FileNameFromUrl(string url)
        {
            try
            {
                var name = Path.GetFileName(new Uri(url).LocalPath);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            return "firmware_" + Guid.NewGuid().ToString("N")[..8] + ".zip";
        }

        private async Task<string> DownloadAsync(string url, string destDir, string? expectedSha256, CancellationToken ct)
        {
            try { url = new Uri(url).AbsoluteUri; } catch { }
            var dest = Path.Combine(destDir, FileNameFromUrl(url));
            if (File.Exists(dest))
            {
                try { File.Delete(dest); } catch { }
            }

            Status = "downloading " + Path.GetFileName(dest) + "...";
            AppendLog("downloading: " + url);
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;

            await using var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(
                dest, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true);
            using var sha = SHA256.Create();
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await net.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, buffer, 0);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                Progress = total is > 0 ? (double)done / total.Value : -1;
            }
            sha.TransformFinalBlock(buffer, 0, 0);
            await file.FlushAsync(ct).ConfigureAwait(false);
            Progress = -1;
            AppendLog($"downloaded {done} bytes to {dest}.");

            var actual = Convert.ToHexString(sha.Hash ?? []).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                Status = "verifying hash...";
                AppendLog("verifying hash...");
                if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"hash mismatch. expected {expectedSha256}, got {actual}");
                AppendLog("hash ok.");
            }
            return dest;
        }

        private async Task RebootToSideloadAsync(string adb, string fastboot, string? serial, CancellationToken ct)
        {
            var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";
            Status = "rebooting to sideload...";
            AppendLog($"rebooting to sideload (adb {serialArg}reboot sideload)...");
            var reboot = await ToolRunner.RunAsync(adb, $"{serialArg}reboot sideload", 60000, AppendOutput, ct).ConfigureAwait(false);
            var saysRoot = reboot.Output.Contains("root", StringComparison.OrdinalIgnoreCase);

            if (reboot.ExitCode == 0 && !saysRoot)
                return;

            if (saysRoot)
            {
                AppendLog("i will shoot the meta engineer who decided this");
                Status = "rebooting to fastboot...";
                var toBootloader = await ToolRunner.RunAsync(adb, $"{serialArg}reboot bootloader", 60000, AppendOutput, ct).ConfigureAwait(false);
                if (toBootloader.ExitCode != 0)
                    throw new InvalidOperationException("could not reboot to fastboot!");
                if (!await DeviceTools.WaitForFastbootAsync(fastboot, ct).ConfigureAwait(false))
                    throw new TimeoutException("timed out while waiting for device. hold vol- and power to put the headset in fastboot.");
                await FastbootOemRebootSideloadAsync(fastboot, ct).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("adb reboot sideload failed. see log.");
        }

        // god these function names fucking suck
        private async Task FastbootOemRebootSideloadAsync(string fastboot, CancellationToken ct)
        {
            Status = "entering sideload mode...";
            AppendLog("entering sideload mode...");
            var oem = await ToolRunner.RunAsync(fastboot, "oem reboot-sideload", 120000, AppendOutput, ct).ConfigureAwait(false);
            var oemText = oem.Output.ToLowerInvariant();
            var accepted = oem.ExitCode == 0
                && !oemText.Contains("unknown")
                && !oemText.Contains("usage")
                && !oemText.Contains("fail");
            if (!accepted)
                throw new InvalidOperationException("reboot request was rejected");
            AppendLog("works");
        }

        private async Task FlashOtaAsync(
            (string url, string incremental, string sha256, string serial, string adb, string fastboot, bool eraseUserdata, bool eraseCache) a,
            CancellationToken ct)
        {
            var zip = await DownloadAsync(a.url, TempDir("ota"), a.sha256, ct).ConfigureAwait(false);

            var mode = await DeviceTools.DetectModeAsync(a.adb, a.fastboot, ct).ConfigureAwait(false);
            var inFastboot = mode == "fastboot";

            if (a.eraseUserdata || a.eraseCache)
            {
                await ErasePartitionsBeforeInstallAsync(a.adb, a.fastboot, a.serial,
                    a.eraseUserdata, a.eraseCache, inFastboot, ct).ConfigureAwait(false);
                inFastboot = true;
            }

            if (inFastboot)
            {
                AppendLog("headset is in fastboot mode!");
                await FastbootOemRebootSideloadAsync(a.fastboot, ct).ConfigureAwait(false);
            }
            else
            {
                await RebootToSideloadAsync(a.adb, a.fastboot, a.serial, ct).ConfigureAwait(false);
            }

            Status = "waiting for sideload mode...";
            AppendLog("waiting for the headset to enter sideload mode...");
            var inSideload = await DeviceTools.WaitForSideloadAsync(a.adb, ct).ConfigureAwait(false);
            if (!inSideload)
                throw new TimeoutException(
                    "the headset never entered sideload mode. " +
                    "check the headset screen.");

            var devices = await DeviceTools.AdbDevicesAsync(a.adb, ct).ConfigureAwait(false);
            var sideloadSerial = devices.FirstOrDefault(d => d.State is "sideload" or "recovery")?.Serial;
            var serialArg = string.IsNullOrWhiteSpace(sideloadSerial) ? "" : $"-s {sideloadSerial} ";

            Status = "sideloading update (this takes a while)...";
            AppendLog($"running adb sideload {serialArg}... DO NOT UNPLUG THE HEADSET!!!");
            var side = await ToolRunner.RunAsync(a.adb, $"{serialArg}sideload {ToolRunner.Quote(zip)}", 1800_000, TrackSideloadProgress, ct).ConfigureAwait(false);
            if (side.ExitCode != 0) throw new InvalidOperationException("adb sideload failed. see log.");
            AppendLog("sideload complete.");

            Status = "ota install finished.";
            AppendLog("done.");
        }

        private async Task ErasePartitionsBeforeInstallAsync(
            string adb, string fastboot, string? serial,
            bool eraseUserdata, bool eraseCache, bool alreadyInFastboot, CancellationToken ct)
        {
            Status = "erasing before install...";
            AppendLog("erase requested!");

            if (!alreadyInFastboot)
            {
                var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";
                AppendLog("rebooting to fastboot to erase before installing...");
                var toBootloader = await ToolRunner.RunAsync(adb, $"{serialArg}reboot bootloader", 60000, AppendOutput, ct).ConfigureAwait(false);
                if (toBootloader.ExitCode != 0)
                    throw new InvalidOperationException("adb reboot bootloader failed!");
                if (!await DeviceTools.WaitForFastbootAsync(fastboot, ct).ConfigureAwait(false))
                    throw new TimeoutException("no fastboot device appeared. check the headset.");
            }

            await FastbootEraseAsync(fastboot, eraseUserdata, eraseCache, ct).ConfigureAwait(false);

            AppendLog("erase done; staying in fastboot to install the update.");
        }

        private void TrackSideloadProgress(string line)
        {
            AppendOutput(line);
            var m = System.Text.RegularExpressions.Regex.Match(line, @"\(~(?<pct>\d{1,3})%\)");
            if (!m.Success) return;
            if (int.TryParse(m.Groups["pct"].Value, out var pct) && pct is >= 0 and <= 100)
                Progress = pct / 100.0;
        }

        private async Task FlashFactoryAsync(
            (string url, string serial, string adb, string fastboot, bool eraseUserdata, bool eraseCache) a,
            CancellationToken ct)
        {
            var zip = await DownloadAsync(a.url, TempDir("factory"), null, ct).ConfigureAwait(false);

            Status = "unpacking factory firmware...";
            var dir = TempDir("factory", "unpacked_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            AppendLog("extracting to " + dir);
            ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);

            var system = FindImage(dir, "system.img");
            var boot = FindImage(dir, "boot.img");
            var modem = FindImage(dir, "modem.img");
            if (system == null || boot == null || modem == null)
                throw new InvalidOperationException(
                    "factory zip did not contain system.img, boot.img and modem.img. this is bad. ");
            AppendLog("found:\n  " + system + "\n  " + boot + "\n  " + modem);

            Status = "rebooting to bootloader...";
            var serialArg = string.IsNullOrWhiteSpace(a.serial) ? "" : $"-s {a.serial} ";
            if (await DeviceTools.FastbootDeviceSerialAsync(a.fastboot, ct).ConfigureAwait(false) is not null)
            {
                AppendLog("device is already in fastboot");
            }
            else
            {
                var reboot = await ToolRunner.RunAsync(a.adb, $"{serialArg}reboot bootloader", 60000, AppendOutput, ct).ConfigureAwait(false);
                if (reboot.ExitCode != 0) throw new InvalidOperationException("adb reboot bootloader failed.");
            }

            Status = "waiting for fastboot...";
            if (!await DeviceTools.WaitForFastbootAsync(a.fastboot, ct).ConfigureAwait(false))
                throw new TimeoutException("no fastboot device appeared. put theheadset on.");

            await FastbootFlashAsync(a.fastboot, "system", system, ct).ConfigureAwait(false);
            await FastbootFlashAsync(a.fastboot, "boot", boot, ct).ConfigureAwait(false);
            await FastbootFlashAsync(a.fastboot, "modem", modem, ct).ConfigureAwait(false);

            if (a.eraseUserdata || a.eraseCache)
                await FastbootEraseAsync(a.fastboot, a.eraseUserdata, a.eraseCache, ct).ConfigureAwait(false);

            Status = "rebooting to system...";
            await ToolRunner.RunAsync(a.fastboot, "reboot", 60000, AppendOutput, ct).ConfigureAwait(false);
            Status = "flash finished.";
            AppendLog("done.");
        }

        private static string? FindImage(string dir, string name)
        {
            try
            {
                return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return null; }
        }

        private async Task FastbootEraseAsync(string fastboot, bool eraseUserdata, bool eraseCache, CancellationToken ct)
        {
            if (eraseUserdata)
            {
                Status = "erasing userdata...";
                var u = await ToolRunner.RunAsync(fastboot, "erase userdata", 300000, AppendOutput, ct).ConfigureAwait(false);
                AppendLog(u.ExitCode == 0 ? "userdata erased." : "fastboot erase userdata failed.");
            }
            if (eraseCache)
            {
                Status = "erasing cache...";
                var c = await ToolRunner.RunAsync(fastboot, "erase cache", 120000, AppendOutput, ct).ConfigureAwait(false);
                AppendLog(c.ExitCode == 0 ? "cache erased." : "cache ersae failed, this is probably fine");
            }
        }

        private async Task FastbootFlashAsync(string fastboot, string partition, string image, CancellationToken ct)
        {
            Status = $"flashing {partition}... (DO NOT UNPLUG)";
            AppendLog($"fastboot flash {partition} {image}");
            var r = await ToolRunner.RunAsync(fastboot, $"flash {partition} {ToolRunner.Quote(image)}", 900000, AppendOutput, ct).ConfigureAwait(false);
            if (r.ExitCode != 0) throw new InvalidOperationException($"fastboot flash {partition} failed. probably not the best idea to reboot right now.");
            AppendLog($"{partition} flashed.");
        }

        private static async Task WaitForFastbootAsync(string fastboot, CancellationToken ct)
        {
            if (!await DeviceTools.WaitForFastbootAsync(fastboot, ct).ConfigureAwait(false))
                throw new TimeoutException("no fastboot device appeared. hold vol down and power to do it manually");
        }
    }
}
