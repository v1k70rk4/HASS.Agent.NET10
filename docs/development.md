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

## GitHub Actions

This repository includes a Windows GitHub Actions workflow:

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
