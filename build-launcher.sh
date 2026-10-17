#!/usr/bin/env bash
# Builds the questie app and wraps it into a single-file launcher (dist/questie[.exe]).
# The launcher embeds the whole app and unpacks it on first run / version change.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$root"

config="${CONFIGURATION:-Release}"

host_rid() {
  case "$(uname -s)" in
    Linux)
      case "$(uname -m)" in
        aarch64|arm64) echo "linux-arm64" ;;
        *) echo "linux-x64" ;;
      esac ;;
    Darwin) echo "osx-arm64" ;;
    *) echo "linux-x64" ;;
  esac
}

rid="${RID:-$(host_rid)}"

# the app version is the single source of truth, taken from Program.cs
version="$(sed -nE 's/.*const[[:space:]]+string[[:space:]]+Version[[:space:]]*=[[:space:]]*"([^"]+)".*/\1/p' Program.cs | head -n1)"
if [ -z "$version" ]; then
  echo "error: could not read the app version from Program.cs" >&2
  exit 1
fi

echo "==> questie $version  ($config / $rid)"

# on linux/macos Qt Bridge needs an external Qt install, pointed at by QTDIR
qtargs=()
if [ -n "${QTDIR:-}" ]; then qtargs+=("-p:QtDir=$QTDIR"); fi

# 1. build the app (Qt Bridge deploys Qt into the same output directory)
dotnet build questie.csproj -c "$config" ${qtargs[@]+"${qtargs[@]}"}

appout="bin/$config/net10.0"
[ -d "$appout" ] || { echo "error: app output not found: $appout" >&2; exit 1; }

# 2. stage the payload, dropping build-only files
stage="obj/launcher-payload"
launcherout="obj/launcher-out"
payload="obj/payload.zip"
rm -rf "$stage" "$launcherout"
rm -f "$payload"
mkdir -p "$stage"
cp -a "$appout/." "$stage/"
find "$stage" -type f \( -name '*.pdb' -o -name 'qtdeploy.txt' \) -delete

# 3. zip the payload
command -v zip >/dev/null 2>&1 || { echo "error: 'zip' is required to build the payload" >&2; exit 1; }
( cd "$stage" && zip -q -r -9 "$root/$payload" . )
echo "    payload: $(du -h "$payload" | cut -f1)"

# 4. build the launcher (embeds the payload and produces a single file)
dotnet publish launcher/QuestieLauncher.csproj -c "$config" -r "$rid" --self-contained false \
  -o "$launcherout" -p:AppVersion="$version" -p:PayloadZip="$root/$payload"

# 5. publish the single-file launcher
mkdir -p dist
exe="questie"
case "$rid" in win-*) exe="questie.exe" ;; esac
cp "$launcherout/$exe" "dist/$exe"
chmod +x "dist/$exe"

echo "==> wrote dist/$exe (app version $version)"
