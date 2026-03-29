# Release & Distribution

This document covers the release process for all distribution channels.

## Triggering a release

All builds are triggered by pushing a `v*` tag:

```bash
# 1. Bump version in src/RailReader2/RailReader2.csproj
# 2. Update docs/index.html softwareVersion in JSON-LD
# 3. Commit and tag
git add src/RailReader2/RailReader2.csproj docs/index.html
git commit -m "chore: bump version to X.Y.Z"
git tag vX.Y.Z
git push && git push origin vX.Y.Z
```

The release workflow (`.github/workflows/release.yml`) runs five jobs:

| Job | Runner | Output | Destination |
|-----|--------|--------|-------------|
| `build-linux` | ubuntu-24.04 | `railreader2-linux-x86_64.AppImage` | GitHub Release |
| `build-windows` | windows-latest | `railreader2-setup-x64.exe` | GitHub Release |
| `build-msix` | windows-latest | `railreader2-win-x64.msix` | CI artifact only (not in release) |
| `build-cli-linux` | ubuntu-24.04 | `railreader2-cli-linux-x64` | GitHub Release |
| `build-cli-windows` | windows-latest | `RailReader2.Cli.exe` | GitHub Release |

The GitHub Release is created automatically with the AppImage, Inno Setup installer, and CLI binaries. The MSIX artifact is available in the Actions run for manual Store submission.

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
