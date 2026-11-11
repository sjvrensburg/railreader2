# Release & Distribution

This document covers the release process for all distribution channels.

## Triggering a release

All builds are triggered by pushing a `v*` tag:

```bash
# 1. Bump version in src/RailReader2/RailReader2.csproj
# 2. Update docs/index.html softwareVersion in JSON-LD
# 3. Add a <release> entry to assets/railreader2.metainfo.xml (AppStream requires this to stay
#    current — it is NOT rewritten by CI, unlike the MSIX manifest and Inno installer version)
# 4. Commit and tag
git add src/RailReader2/RailReader2.csproj docs/index.html assets/railreader2.metainfo.xml
git commit -m "chore: bump version to X.Y.Z"
git tag vX.Y.Z
git push && git push origin vX.Y.Z
```

The release workflow (`.github/workflows/release.yml`) runs five jobs:

| Job | Runner | Output | Destination |
|-----|--------|--------|-------------|
| `build-linux` | ubuntu-24.04 | `railreader2-x86_64.AppImage` (+ `.zsync` for AppImageUpdate), `railreader2-linux-x64.tar.gz` | GitHub Release |
| `build-windows` | windows-latest | `railreader2-setup-x64.exe` | GitHub Release |
| `build-msix` | windows-latest | `railreader2-win-x64.msix` | CI artifact only (not in release) |
| `build-cli-linux` | ubuntu-24.04 | `railreader2-cli-linux-x64.tar.gz` | GitHub Release |
| `build-cli-windows` | windows-latest | `railreader2-cli-win-x64.zip` | GitHub Release |

The GitHub Release is created automatically with the AppImage, Inno Setup installer, and CLI binaries, plus the signed `update-manifest.json` / `update-manifest.json.sig` that in-app updates read (see below). The MSIX artifact is available in the Actions run for manual Store submission.

## In-app updates

The app reads `https://github.com/sjvrensburg/railreader2/releases/latest/download/update-manifest.json` and its detached signature. The `release` job writes the manifest with `scripts/write-update-manifest.py` (version, release URL, and name/URL/size/SHA-256 of the AppImage, Inno installer and tarball), signs its exact bytes with the `UPDATE_SIGNING_KEY` Actions secret (ECDSA P-256, `openssl dgst -sha256 -sign`), and verifies the signature against the public key committed at `src/RailReader2.Shared/Updates/update-signing-key.pub.pem`, which is built into the app. **A missing secret or a key mismatch fails the release**: `releases/latest` without a valid manifest would break update checks for every installed copy.

- **One-time setup:** `scripts/generate-update-signing-key.sh` creates the key, stores a backup (default `~/.config/railreader2-release/update-signing-key.pem`), sets the secret with `gh`, and writes the public key. Commit the public key. Keep the backup safe: installed copies trust only the key they were built with, so a lost key means one release that users must install by hand.
- **Asset names are part of the contract** (`railreader2-x86_64.AppImage`, `railreader2-setup-x64.exe`, `railreader2-linux-x64.tar.gz`); renaming one breaks updates for older builds as well as the AppImage zsync string.
- **What each channel does:** AppImage replaces its own file; Inno runs the new installer with `/VERYSILENT … /WAITPID=<pid> [/RELAUNCH=1]` after the app exits (`installer/railreader2.iss` `[Code]` waits for the PID and relaunches unelevated); the tarball (CI writes an `install-kind` marker into it) and dev builds point at the release page; the Store build never updates itself and opens its Store page. `RR_DISABLE_UPDATES=1` turns it all off (for repackagers).
- **Testing:** `railreader2-cli update check|download` runs the same check/verify/download code headlessly (`--feed`, `--public-key`). `installer/test-silent-update.ps1` (CI `installer` job, Windows) builds two installers around a placeholder exe and checks `/WAITPID`, the in-place upgrade, the recorded version and `/RELAUNCH=1`. In the app, `RR_UPDATE_FEED=<url prefix or local directory>` and `RR_UPDATE_PUBLIC_KEY_FILE=<pem>` point the app at a test feed signed with a test key; asset URLs may be `http://127.0.0.1…` for a local server.

## GitHub Release (Linux + Windows)

Fully automated. The `release` job downloads the AppImage and Inno Setup installer artifacts and creates a GitHub Release with auto-generated release notes.

No manual steps required.

## Microsoft Store (Windows)

The Store submission is currently a manual process. The CI builds the unsigned MSIX; you download it and upload to Partner Center.

### Store release workflow

1. **Wait for CI to finish** — after pushing the tag, wait for the `build-msix` job to complete:
   ```bash
   gh run list --workflow=release.yml --limit=1
   ```

2. **Download the MSIX artifact** from the completed workflow run:
   ```bash
   # Find the run ID
   gh run list --workflow=release.yml --limit=1 --json databaseId --jq '.[0].databaseId'

   # Download the artifact
   gh run download <RUN_ID> --name windows-msix --dir /tmp/msix
   ```
   The file is at `/tmp/msix/railreader2-win-x64.msix`.

3. **Submit to Partner Center**:
   - Go to https://partner.microsoft.com/dashboard
   - Navigate to **Apps and Games** > **RailReader2**
   - Click **Update** (or **Start your submission** for the first time)
   - In the **Packages** section, upload `railreader2-win-x64.msix`
   - Review the listing details (description, screenshots, etc.)
   - Submit for certification

4. **Certification** takes a few hours. Microsoft re-signs the package with their certificate. Once approved, the update goes live on the Store.

### Partner Center account

- **Account**: sjansevanrensburg@outlook.com
- **Publisher display name**: Probity Data Analytics
- **Package identity**: `ProbityDataAnalytics.5701382B25AF5`
- **Publisher ID**: `CN=1760E4F3-7B38-4A64-8D2D-B4F7703D7D10`
- **Privacy policy URL**: https://sjvrensburg.github.io/railreader2/privacy.html

### MSIX manifest

The manifest at `msix/Package.appxmanifest` declares:
- App identity matching the Partner Center registration
- PDF file type association
- `runFullTrust` capability (required for .NET desktop apps)
- Visual assets for Store tiles

The CI automatically updates the manifest version from the git tag before building the MSIX.

### Future automation

Automated Store submission via the Partner Center API requires an Azure AD tenant with an app registration linked to the Partner Center account. This can be set up later if the release cadence justifies it. The CI already produces the MSIX artifact — the only manual step is the upload to Partner Center.
