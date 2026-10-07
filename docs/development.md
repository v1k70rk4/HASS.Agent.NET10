# Building and development

[← Back to the README](https://github.com/v1k70rk4/HASS.Agent.NET10#readme)

## Build from Source

Install the .NET 10 SDK, then publish:

```powershell
dotnet publish .\src\HASS.Agent.NET10\HASS.Agent.NET10.csproj `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The output goes to:

```text
src\HASS.Agent.NET10\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\HASS.Agent.NET10.exe
```

To build the installer, also install [Inno Setup](https://jrsoftware.org/isinfo.php) and compile `installer\HASS.Agent.NET10.iss`.

`build-exe.ps1` wraps the common cases: on its own it produces a standalone `.exe` and offers to swap it into the installed copy; `-Tag` builds the signed installer and zip with the CI file names (see [Code Signing](https://github.com/v1k70rk4/HASS.Agent.NET10#code-signing) — it needs the maintainer's certificate, so this is a release tool rather than a build step).

## Tests

`tests/HASS.Agent.NET10.Tests` holds the unit tests (xUnit v3). They cover the logic that has an input and an output: version comparison and the choice of release asset, the release notes shown in the update prompt, notification payloads (buttons, text fields, durations), key combinations and hotkeys, popup window sizes, LibreHardwareMonitor readings, settings normalization, the local API key check and the signature check of update installers. What needs Windows itself (displays, audio devices, the tray icon, the windows) is tested by hand on real PCs.

```powershell
dotnet test --project tests/HASS.Agent.NET10.Tests -c Release
```

`global.json` selects the Microsoft.Testing.Platform runner that xUnit v3 uses. The installer signature tests need a signed release installer, which is too big for the repository. CI downloads the installer of the latest release; locally, set `HASS_AGENT_SIGNED_INSTALLER` to one (for example `artifacts\installer\HASS.Agent.NET10-Setup-<version>.exe` after `build-exe.ps1 -Tag`), otherwise those tests are skipped.

Every bug fix comes with a test that fails without the fix, so the same bug cannot come back unnoticed.

## GitHub Actions

This repository includes a Windows GitHub Actions workflow:

- the unit tests (a release build waits for them)
- `dotnet restore` + `dotnet build -c Release`
- self-contained `win-x64` publish
- Inno Setup installer build
- downloadable artifacts from manual workflow runs:
  - `HASS.Agent.NET10-win-x64`
  - `HASS.Agent.NET10-Setup`
- release assets on `v*` tags, attached to a draft release (the signed files replace them before it is published, see [Code Signing](https://github.com/v1k70rk4/HASS.Agent.NET10#code-signing)):
  - `HASS.Agent.NET10-Setup-<version>.exe`
  - `HASS.Agent.NET10-win-x64-<version>.zip`

## Minimal Development Setup

You do not need Visual Studio for this project.

Required:

- .NET 10 SDK for Windows x64
- PowerShell

Optional:

- Visual Studio Code
- C# Dev Kit extension

Useful commands:

```powershell
dotnet nuget list source
dotnet --list-sdks
dotnet build .\src\HASS.Agent.NET10\HASS.Agent.NET10.csproj -c Release
dotnet run --project .\src\HASS.Agent.NET10\HASS.Agent.NET10.csproj -c Release
```

If `dotnet nuget list source` says `No sources found`, add the official NuGet feed:

```powershell
dotnet nuget add source https://api.nuget.org/v3/index.json --name nuget.org
```

The technical developer notes:
[src/HASS.Agent.NET10/README.md](../src/HASS.Agent.NET10/README.md)
