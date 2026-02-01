# Version Update Guide

This guide explains the **single‑source version process** and how to update versions consistently across the project.

## Single Source of Truth

The canonical version is stored in [version.json](version.json). Everything else is synchronized from this file.

Example:

- `version`: semantic version used for releases (e.g., `0.4.0`)
- `gitHubTag`: optional release tag (e.g., `0.4.0-rc1`)

## How to Update the Version

### Option A — edit the file, then sync

1. Edit [version.json](version.json) and set:
   - `version`
   - `gitHubTag` (optional)
2. Run:
   - `./sync-version.ps1`

### Option B — set version via the script

Run:

- `./sync-version.ps1 -Version "0.4.0" -GitHubTag "0.4.0-rc1"`

This updates [version.json](version.json) and synchronizes all other files.

## What Gets Updated

The sync script updates these locations automatically:

1. [WIMISODriverInjector.csproj](WIMISODriverInjector.csproj) — `<Version>`
2. [build-package.ps1](build-package.ps1) — default `-Version` parameter
3. [installer.iss](installer.iss) — `#define MyAppVersion`
4. [app.manifest](app.manifest) — `assemblyIdentity version` (converted to `x.y.z.0`)
5. [Version.props](Version.props) — generated `AppVersion` (for MSBuild consumption)

## Version Format Notes

- **Semantic version**: `x.y.z` (used in version.json and most tools)
- **Assembly version**: `x.y.z.0` (required by Windows manifest)

The script converts `x.y.z` → `x.y.z.0` automatically for [app.manifest](app.manifest).

## GitHub Releases

Use `version` for the main release version. If you publish pre‑releases, use `gitHubTag` (e.g., `0.4.0-rc1`).

## Troubleshooting

If you see mismatched versions, re-run:

- `./sync-version.ps1`
