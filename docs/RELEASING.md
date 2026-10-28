# Releasing DeskMaster

## Make a release

1. Update `<Version>` in `Directory.Build.props` (the same number as the tag, without the `v`) and write the notes for this version in `docs/release-notes.md`.
2. Commit, then tag and push:

   ```bash
   git tag v1.0.0
   git push origin HEAD --tags
   ```

3. The **Release** workflow (`.github/workflows/release.yml`) then runs the tests and builds installers with [Velopack](https://velopack.io):

   | OS | Files |
   |---|---|
   | Windows x64 | `DeskMaster-win-Setup.exe` (installer), `DeskMaster-win-Portable.zip` |
   | Linux x64 | `DeskMaster.AppImage` |
   | macOS (Apple silicon) | `DeskMaster-osx-Setup.pkg`, `DeskMaster-osx-Portable.zip` |

   It uploads them, together with the update feeds (`releases.*.json`), to one GitHub release.

Installed copies check that feed when they start (Settings → *Check for new versions*), offer the update in a pop-up, and restart into it. Only the changes are downloaded (delta updates).

The app reads the repository address from `<RepositoryUrl>` in `Directory.Build.props`, so it must point at the real GitHub repository.

## Build an installer locally (Windows)

```powershell
dotnet tool install -g vpk
dotnet publish src/DeskMaster.App -c Release -r win-x64 --self-contained -o artifacts/vpk/publish
dotnet publish src/DeskMaster.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o artifacts/vpk/publish
vpk pack -u DeskMaster -v 1.0.0 -p artifacts/vpk/publish -e DeskMaster.exe --packTitle DeskMaster -i src/DeskMaster.App/Assets/deskmaster.ico -o artifacts/vpk/releases
```

## Code signing

Unsigned installers work, but Windows SmartScreen and macOS Gatekeeper show a warning. To sign:

### Windows

Get a code-signing certificate. Free options for open source:
- [SignPath Foundation](https://signpath.org) signs open-source projects for free.
- [Azure Trusted Signing](https://learn.microsoft.com/azure/trusted-signing/) costs about $10 a month.

With a `.pfx` certificate, add two repository secrets (Settings → Secrets and variables → Actions):

| Secret | Value |
|---|---|
| `WINDOWS_CERT_PFX_BASE64` | the `.pfx` file as Base64 (`[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`) |
| `WINDOWS_CERT_PASSWORD` | its password |

The workflow signs automatically when these secrets exist. For Azure Trusted Signing, replace `--signParams` with `--azureTrustedSignFile` (see `vpk pack --help`).

### macOS

You need an Apple Developer account ($99 a year). Import the *Developer ID Application* and *Developer ID Installer* certificates into the runner's keychain, then add these options to the `vpk pack` step:

```
--signAppIdentity "Developer ID Application: Your Name (TEAMID)"
--signInstallIdentity "Developer ID Installer: Your Name (TEAMID)"
--notaryProfile "your-notary-profile"
```

## Portable builds without an installer

`./build/publish.ps1` or `./build/publish.sh` creates self-contained single-file builds in `artifacts/`. Those copies don't update themselves.
