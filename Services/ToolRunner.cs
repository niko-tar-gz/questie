using System.Diagnostics;
using System.Text;

namespace questie.Services
{
    internal sealed record CommandResult(int ExitCode, string Output);

    internal static class ToolRunner
    {
        public static async Task<CommandResult> RunAsync(
            string tool, string args, int timeoutMs,
            Action<string>? onOutput, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            var sb = new StringBuilder();
            try
            {
                using var proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    lock (sb) sb.AppendLine(e.Data);
                    try { onOutput?.Invoke(e.Data); } catch { }
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is null) return;
                    lock (sb) sb.AppendLine(e.Data);
                    try { onOutput?.Invoke(e.Data); } catch { }
                };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                try
                {
                    await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw new TimeoutException($"timed out after {timeoutMs / 1000}s: {tool} {args}");
                }

                string output;
                lock (sb) output = sb.ToString();
                return new CommandResult(proc.ExitCode, output);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException($"could not start '{tool}'. is it installed and on path?", ex);
            }
        }

        public static string Quote(string path) => "\"" + path.Replace("\"", "") + "\"";
    }
}
