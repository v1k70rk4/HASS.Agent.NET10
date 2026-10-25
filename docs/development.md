---
title: Development
description: Building HASS.Agent .NET10 from source - the .NET 10 SDK, the installer, the tests, GitHub Actions, a minimal development setup, and how the website is built.
---

# Building and development

Sending a pull request? [CONTRIBUTING.md](https://github.com/v1k70rk4/HASS.Agent.NET10/blob/main/CONTRIBUTING.md) says what it needs.

## Build from source

Install the .NET 10 SDK named in `global.json`, that exact version (`dotnet --list-sdks` shows what you have), then publish:

```powershell
dotnet publish .\src\HASS.Agent.NET10\HASS.Agent.NET10.csproj `
    -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The output goes to:

```text
src\HASS.Agent.NET10\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\HASS.Agent.NET10.exe
```

The publish is self-contained: the .NET runtime of that SDK goes into the exe, and Windows Update does not patch it there. That is why `global.json` pins the SDK: the release is built on a known runtime, CI uses the same one, and Dependabot opens a pull request for each new .NET 10 SDK (a new major version is left out: moving to it is a change of its own). When a new SDK brings .NET security fixes, merging it is the cue for a release built on it, whose notes name the CVEs; nothing does that by itself. See [Security and privacy](getting-started/security.md#security-fixes-from-net-and-the-libraries).

To build the installer, also install [Inno Setup](https://jrsoftware.org/isinfo.php) and compile `installer\HASS.Agent.NET10.iss`.

`build-exe.ps1` wraps the common cases: on its own it produces a standalone `.exe` and offers to swap it into the installed copy; `-Tag` builds the signed installer and zip with the CI file names (see [Security and privacy](getting-started/security.md); it needs the maintainer's certificate, so this is a release tool rather than a build step).

## Tests

`tests/HASS.Agent.NET10.Tests` holds the unit tests (xUnit v3). They cover the logic that has an input and an output: version comparison and the choice of release asset, the release notes shown in the update prompt, notification payloads (buttons, text fields, durations), key combinations and hotkeys, popup window sizes, LibreHardwareMonitor readings, sensor profiles and the publish pace, settings normalization, the local API key check and the signature check of update installers. What needs Windows itself (displays, audio devices, the tray icon, the windows) is tested by hand on real PCs.

```powershell
dotnet test --project tests/HASS.Agent.NET10.Tests -c Release
```

`global.json` selects the Microsoft.Testing.Platform runner that xUnit v3 uses. The installer signature tests need a signed release installer, which is too big for the repository. CI downloads the installer of the latest release; locally, set `HASS_AGENT_SIGNED_INSTALLER` to one (for example `artifacts\installer\HASS.Agent.NET10-Setup-<version>.exe` after `build-exe.ps1 -Tag`), otherwise those tests are skipped.

Every bug fix comes with a test that fails without the fix, so the same bug cannot come back unnoticed.

## GitHub Actions

The repository's Windows workflow:

- the unit tests (a release build waits for them)
- `dotnet restore` + `dotnet build -c Release`
- self-contained `win-x64` publish
- Inno Setup installer build
- downloadable artifacts from manual workflow runs: `HASS.Agent.NET10-win-x64` and `HASS.Agent.NET10-Setup`
- release assets on `v*` tags, attached to a draft release (the signed files replace them before it is published): `HASS.Agent.NET10-Setup-<version>.exe` and `HASS.Agent.NET10-win-x64-<version>.zip`

Beside it: CodeQL on every change, the OpenSSF Scorecard, the Sigstore signing of a published release, and the website build below.

## Minimal development setup

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

The technical developer notes: [src/HASS.Agent.NET10/README.md](https://github.com/v1k70rk4/HASS.Agent.NET10/blob/main/src/HASS.Agent.NET10/README.md).

## The website

This site is built by [MkDocs](https://www.mkdocs.org/) with the [Material](https://squidfunk.github.io/mkdocs-material/) theme from the markdown files under `docs/`; `mkdocs.yml` holds the navigation, `website/hooks.py` pulls in the changelog and the integration's README. A push to `main` that touches them deploys it through GitHub Pages. To see it on your PC:

```powershell
python -m venv .venv
.venv\Scripts\pip install -r website\requirements.txt
.venv\Scripts\mkdocs serve
```

A new feature gets a page, or a section of one, under `docs/`, and one line in the README's feature table.
