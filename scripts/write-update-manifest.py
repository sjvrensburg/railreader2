#!/usr/bin/env python3
"""Write update-manifest.json for a release (read by src/RailReader2/Services/Updates).

Usage: write-update-manifest.py <tag> <assets-dir> <output>

The release workflow then signs the output's exact bytes with the UPDATE_SIGNING_KEY secret
(openssl dgst -sha256 -sign → DER ECDSA P-256), publishing both as release assets. Asset names must
stay stable: the AppImage's zsync update string and old builds' channel keys depend on them.
"""
import hashlib
import json
import os
import sys
from datetime import datetime, timezone

REPO = "sjvrensburg/railreader2"
ASSETS = {
    "appimage": "railreader2-x86_64.AppImage",
    "inno": "railreader2-setup-x64.exe",
    "tarball": "railreader2-linux-x64.tar.gz",
}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    tag, assets_dir, output = sys.argv[1:4]
    version = tag[1:] if tag.startswith("v") else tag
    assets = {}
    for key, name in ASSETS.items():
        path = os.path.join(assets_dir, name)
        if not os.path.isfile(path):
            sys.exit(f"missing release asset: {path}")
        assets[key] = {
            "name": name,
            "url": f"https://github.com/{REPO}/releases/download/{tag}/{name}",
            "size": os.path.getsize(path),
            "sha256": sha256(path),
        }
    manifest = {
        "format_version": 1,
        "version": version,
        "tag": tag,
        "release_url": f"https://github.com/{REPO}/releases/tag/{tag}",
        "published_utc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "assets": assets,
    }
    with open(output, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()
