#!/usr/bin/env bash
# This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
# If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
# This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.
set -euo pipefail
umask 077

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"

VERSION="$(tr -d ' \t\r\n' < "$SCRIPT_DIR/VERSION")"
if [ -z "$VERSION" ]; then
  echo "::error::VERSION file is empty"
  exit 1
fi

publish_root="$SCRIPT_DIR/build/publish"
release_root="$SCRIPT_DIR/build/release"

rm -rf "$publish_root" "$release_root"
mkdir -p "$publish_root" "$release_root"

rids=(linux-x64 win-x64 osx-arm64)

for rid in "${rids[@]}"; do
  out_dir="$publish_root/$rid"
  dotnet publish "$SCRIPT_DIR/src/Procvd/Procvd.csproj" -c Release -r "$rid" -o "$out_dir"

  if [ "$rid" = "win-x64" ]; then
    src="$out_dir/Procvd.exe"
    dst="$release_root/Procvd-$VERSION-$rid.exe"
  else
    src="$out_dir/Procvd"
    dst="$release_root/Procvd-$VERSION-$rid"
  fi

  if [ ! -f "$src" ]; then
    echo "::error::Missing published binary $src"
    exit 1
  fi

  cp "$src" "$dst"
done
