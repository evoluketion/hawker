#!/usr/bin/env bash
# Builds self-contained Hawker packages for Windows and Linux into artifacts/.
#
#   build/package.sh                 # both platforms
#   build/package.sh win-x64         # one platform
#   SUMATRA_VERSION=3.6.1 build/package.sh
#   HAWKER_VERSION=0.2.0 build/package.sh  # override the version in Directory.Build.props (releases do this)
#
# Needs: the .NET 10 SDK, curl, unzip, zip and tar. Both packages can be built on either OS.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
artifacts="$root/artifacts"
sumatra_version="${SUMATRA_VERSION:-3.6.1}"
rids=("${@:-win-x64 linux-x64}")
# shellcheck disable=SC2206
rids=(${rids[@]})
version="${HAWKER_VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props")}"

mkdir -p "$artifacts"

for rid in "${rids[@]}"; do
  name="hawker-$version-$rid"
  out="$artifacts/$name"
  echo "==> Publishing $name"
  rm -rf "$out"
  dotnet publish "$root/src/Hawker.Web" -c Release -r "$rid" --self-contained \
    -p:Version="$version" -p:DebugType=None -p:DebugSymbols=false -o "$out" --nologo -v quiet

  # Some native packages ship their own debug symbols.
  find "$out" -name '*.pdb' -delete
  cp "$root/README.md" "$root/THIRD-PARTY-NOTICES.md" "$out/"

  case "$rid" in
    win-*)
      # Batch files need Windows line endings whatever OS builds the package.
      sed 's/\r*$/\r/' "$root/build/scripts/Start Hawker.cmd" > "$out/Start Hawker.cmd"
      echo "==> Adding SumatraPDF $sumatra_version (silent printing on Windows)"
      mkdir -p "$out/tools"
      tmp="$(mktemp -d)"
      curl -fsSL "https://www.sumatrapdfreader.org/dl/rel/$sumatra_version/SumatraPDF-$sumatra_version-64.zip" -o "$tmp/sumatra.zip"
      unzip -q "$tmp/sumatra.zip" -d "$tmp/sumatra"
      cp "$tmp"/sumatra/SumatraPDF*.exe "$out/tools/SumatraPDF.exe"
      rm -rf "$tmp"
      (cd "$artifacts" && rm -f "$name.zip" && zip -qr "$name.zip" "$name")
      echo "    $artifacts/$name.zip"
      ;;
    *)
      cp "$root/build/scripts/start-hawker.sh" "$out/"
      chmod +x "$out/start-hawker.sh" "$out/Hawker.Web"
      tar -C "$artifacts" -czf "$artifacts/$name.tar.gz" "$name"
      echo "    $artifacts/$name.tar.gz"
      ;;
  esac
done
