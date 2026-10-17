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
            Http.DefaultRequestHeaders.UserAgent.ParseAdd($"Questie/{Program.Version}");
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

        public void SkipNux(string serial, string adbPath, string lockState)
        {
            if (Busy) return;
            if (BlockedByLock(lockState, "skip nux")) return;
            StartOperation("skipping nux...");
            AppendLog("skipping nux");
            Task.Run(() => RunGuarded(() => SkipNuxAsync(serial, adbPath, Cts().Token)));
        }

        public void SkipNuxOlderFw(string serial, string adbPath, string lockState)
        {
            if (Busy) return;
            if (BlockedByLock(lockState, "skip nux")) return;
            StartOperation("skipping nux (older fw)...");
            AppendLog("skipping nux (older fw)");
            Task.Run(() => RunGuarded(() => SkipNuxOlderFwAsync(serial, adbPath, Cts().Token)));
        }

        // quest 1 only: fastboot oem commands that let adbd run as root on retail builds.
        public void UnlockRoot(string serial, string adbPath, string fastbootPath)
        {
            if (Busy) return;
            StartOperation("unlocking adb root...");
            AppendLog("quest 1: allowing adb root on retail");
            Task.Run(() => RunGuarded(() => FastbootOemAsync(serial, adbPath, fastbootPath,
            [
                "oem set-appended-cmdline androidboot.adb.rootable=1",
                "oem set-enable-adb-on-retail 1",
            ], "adb root unlocked!", Cts().Token)));
        }

        public void LockRoot(string serial, string adbPath, string fastbootPath)
        {
            if (Busy) return;
            StartOperation("locking adb root...");
            AppendLog("quest 1: disallowing adb root on retail");
            Task.Run(() => RunGuarded(() => FastbootOemAsync(serial, adbPath, fastbootPath,
            [
                "oem set-appended-cmdline",
                "oem set-enable-adb-on-retail 0",
            ], "adb root locked!", Cts().Token)));
        }

        private async Task FastbootOemAsync(
            string serial, string adb, string fastboot, string[] commands, string doneStatus, CancellationToken ct)
        {
            await EnsureFastbootAsync(serial, adb, fastboot, ct).ConfigureAwait(false);

            foreach (var cmd in commands)
            {
                Status = $"running fastboot {cmd}...";
                AppendLog($"fastboot {cmd}");
                var r = await ToolRunner.RunAsync(fastboot, cmd, 60000, AppendOutput, ct).ConfigureAwait(false);
                if (r.ExitCode != 0)
                    throw new InvalidOperationException($"fastboot {cmd} failed. see log.");
            }

            Status = doneStatus;
            AppendLog("done.");
        }

        // reboots to bootloader and waits when the headset isn't already in fastboot mode.
        private async Task EnsureFastbootAsync(string? serial, string adb, string fastboot, CancellationToken ct)
        {
            if (await DeviceTools.FastbootDeviceSerialAsync(fastboot, ct).ConfigureAwait(false) is not null)
            {
                AppendLog("headset is already in fastboot mode.");
                return;
            }

            Status = "rebooting to fastboot...";
            var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";
            AppendLog("rebooting to fastboot...");
            await ToolRunner.RunAsync(adb, $"{serialArg}reboot bootloader", 60000, AppendOutput, ct).ConfigureAwait(false);
            if (!await DeviceTools.WaitForFastbootAsync(fastboot, ct).ConfigureAwait(false))
                throw new TimeoutException("timed out while waiting for device. hold vol- and power to put the headset in fastboot.");
        }

        // all of these need adb root, which only works on unlocked devices
        private bool BlockedByLock(string lockState, string action)
        {
            if (string.Equals(lockState, "unlocked", StringComparison.OrdinalIgnoreCase)) return false;
            Status = $"{action} needs an unlocked bootloader";
            AppendLog($"the bootloader is {(string.IsNullOrWhiteSpace(lockState) ? "unknown" : lockState)}, adb root won't work while it's locked.");
            return true;
        }

        private async Task EnsureAdbRootAsync(string serial, string adb, CancellationToken ct)
        {
            var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";

            if (await DeviceTools.IsAdbRootAsync(adb, serial, ct).ConfigureAwait(false))
            {
                AppendLog("adbd is already running as root");
                return;
            }

            Status = "running adb root...";
            AppendLog("running adb root...");
            var root = await ToolRunner.RunAsync(adb, $"{serialArg}root", 60000, AppendOutput, ct).ConfigureAwait(false);
            if (root.ExitCode != 0 || root.Output.Contains("cannot run as root", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("adb root failed. the headset must be running a debuggable build (and be unlocked).");

            Status = "waiting for adb to reconnect...";
            if (!await DeviceTools.WaitForDeviceAsync(adb, ct, 60).ConfigureAwait(false))
                throw new TimeoutException("the headset did not come back after adb root.");
        }

        private async Task SkipNuxAsync(string serial, string adb, CancellationToken ct)
        {
            var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";
            await EnsureAdbRootAsync(serial, adb, ct).ConfigureAwait(false);

            Status = "sending skip nux command...";
            const string skipCmd =
                "shell am startservice -a nux.ota.SKIP_NUX " +
                "-n com.oculus.nux.ota/com.oculus.nux.ota.NuxOtaIntentService --ez REBOOT true";
            AppendLog("sending skip nux intent...");
            var skip = await ToolRunner.RunAsync(adb, $"{serialArg}{skipCmd}", 60000, AppendOutput, ct).ConfigureAwait(false);

            var serviceMissing = skip.Output.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || skip.Output.Contains("no service started", StringComparison.OrdinalIgnoreCase);

            if (serviceMissing)
            {
                AppendLog("the nux ota service wasn't found, trying method 2");
                Status = "applying nux skip flags...";
                const string flagsCmd =
                    "shell \"settings put global device_provisioned 1; " +
                    "settings put secure user_setup_complete 1; " +
                    "settings put secure finished_monterey_nux 1; " +
                    "settings put global finished_monterey_nux 1; " +
                    "settings put secure dnux_complete 1; " +
                    "settings put global dnux_complete 1; " +
                    "svc power reboot\"";
                var flags = await ToolRunner.RunAsync(adb, $"{serialArg}{flagsCmd}", 60000, AppendOutput, ct).ConfigureAwait(false);
                if (flags.ExitCode != 0)
                    AppendLog($"settings command exited with code {flags.ExitCode}.");
            }
            else if (skip.ExitCode != 0)
            {
                throw new InvalidOperationException("could not send the skip nux command. see log.");
            }

            Status = "nux skip sent! the headset will reboot.";
            AppendLog("done.");
        }

        private async Task SkipNuxOlderFwAsync(string serial, string adb, CancellationToken ct)
        {
            var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $"-s {serial} ";
            await EnsureAdbRootAsync(serial, adb, ct).ConfigureAwait(false);

            Status = "writing nux identity prefs...";
            AppendLog("writing nux identity prefs...");

            const string xmlFormat =
                "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\" ?>\\n" +
                "<map>\\n" +
                "<boolean name=\"login_status\" value=\"true\" />\\n" +
                "<boolean name=\"login_status_meta\" value=\"true\" />\\n" +
                "<boolean name=\"nux_complete\" value=\"true\" />\\n" +
                "<boolean name=\"vr_ready\" value=\"true\" />\\n" +
                "<boolean name=\"headset_claimed_by_user\" value=\"true\" />\\n" +
                "<string name=\"nux_status\">NUX_COMPLETE</string>\\n" +
                "<string name=\"nux_flow\">TWILIGHT</string>\\n" +
                "</map>\\n";

            var script = $"D=/data/user_de/0/com.oculus.companion.server/shared_prefs; F=$D/com.oculus.companion.identity.xml; printf '{xmlFormat}' > $F; chown $(stat -c %u:%g $D) $F; restorecon $F; reboot";

            var escaped = script.Replace("\"", "\\\"");
            var r = await ToolRunner.RunAsync(adb, $"{serialArg}shell \"{escaped}\"", 60000, AppendOutput, ct).ConfigureAwait(false);
            if (r.ExitCode != 0)
                AppendLog($"command exited with code {r.ExitCode}.");

            Status = "nux skip (older fw) sent! the headset will reboot.";
            AppendLog("done.");
        }

        private async Task WipeDeviceAsync(
            string serial, string adb, string fastboot, bool eraseUserdata, bool eraseCache, CancellationToken ct)
        {
            await EnsureFastbootAsync(serial, adb, fastboot, ct).ConfigureAwait(false);

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
                req.Headers.UserAgent.ParseAdd($"Questie/{Program.Version}");
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
            if (eraseCache) parts += $"{(parts.Length > 0 ? " + " : "")}cache";
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
                Status = $"failed: {ex.Message}";
                AppendLog($"error: {ex.Message}");
            }
            finally
            {
                Busy = false;
                Progress = -1;
            }
        }

        private void AppendLog(string line)
        {
            var next = string.IsNullOrEmpty(Log) ? line : $"{Log}\n{line}";
            if (next.Length > 30000) next = next[^30000..];
            Log = next;
        }

        private void AppendOutput(string line) => AppendLog($"  {line}");

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
            return $"firmware_{Guid.NewGuid().ToString("N")[..8]}.zip";
        }

        private async Task<string> DownloadAsync(string url, string destDir, string? expectedSha256, CancellationToken ct)
        {
            try { url = new Uri(url).AbsoluteUri; } catch { }
            var dest = Path.Combine(destDir, FileNameFromUrl(url));
            if (File.Exists(dest))
            {
                try { File.Delete(dest); } catch { }
            }

            Status = $"downloading {Path.GetFileName(dest)}...";
            AppendLog($"downloading: {url}");
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
                await EnsureFastbootAsync(serial, adb, fastboot, ct).ConfigureAwait(false);

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
            var dir = TempDir("factory", $"unpacked_{DateTime.Now.ToString("yyyyMMdd_HHmmss")}");
            AppendLog($"extracting to {dir}");
            ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);

            var system = FindImage(dir, "system.img");
            var boot = FindImage(dir, "boot.img");
            var modem = FindImage(dir, "modem.img");
            if (system == null || boot == null || modem == null)
                throw new InvalidOperationException(
                    "factory zip did not contain system.img, boot.img and modem.img. this is bad. ");
            AppendLog($"found:\n  {system}\n  {boot}\n  {modem}");

            await EnsureFastbootAsync(a.serial, a.adb, a.fastboot, ct).ConfigureAwait(false);

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
    }
}
