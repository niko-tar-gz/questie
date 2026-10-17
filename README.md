<div align="center">

<img src="Resources/logo.png" width="150" height="150" alt="questie Logo" />

# questie

### a multitool for quest headsets

<img src="Resources/screenshot.png" alt="questie Logo" />


</div>

# ✨ so, what does it do?
questie allows you to upgade and downgrade firmware, simple as that.
i will probably add more things that it can do at some point (e.g root, bootloader unlock) but for now, only firmware swapping

# 🛠 how to build
you need to set up [qtbridge-csharp](https://github.com/qt/qtbridge-csharp)
then just build with `dotnet build`

# 📦 building a release
`build-launcher.ps1` (windows) and `build-launcher.sh` (linux) build the app
*and* the launcher, then pack the app together with its qt and adb runtimes into
a single self-extracting executable in `dist/`.

```bash
# windows
./build-launcher.ps1

# linux (also builds the launcher for linux-x64/linux-arm64)
./build-launcher.sh
```

the version is read from `Program.cs` (`Program.Version`), so there is a single
place to bump it. on first launch the launcher unpacks the app to
`%LOCALAPPDATA%\questie\app` (windows) or `~/.local/share/questie/app` (linux)
and writes an `app.ver` file next to the app. it only unpacks again when that
version changes, or when `app.ver` is missing.

> the packed app still needs the .net 10 runtime installed. this is a
> limitation of the qt bridge host, which always loads the machine-wide runtime.

the launcher source lives in [`launcher/`](launcher).