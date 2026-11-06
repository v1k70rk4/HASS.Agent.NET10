# Copilot code review instructions

- Start review comments with a short, one-sentence summary of the suggested fix.
- Do not comment on code style, formatting or naming. The build treats warnings as errors and CodeQL scans every pull request; what they let through is fine.
- Flag comments that over-explain straightforward code, narrate the obvious, or read like AI commentary (multi-sentence justifications for a single line).
- A pull request with a dependency version bump should only contain changes required for the version bump. If the PR includes other changes, request that they are removed from the PR. Dependabot PRs are identifiable by "Dependabot commands and options" in their description.
- The version numbers (`<Version>` in `src/HASS.Agent.NET10/HASS.Agent.NET10.csproj`, `MyAppVersion` in `installer/HASS.Agent.NET10.iss`, the badge and status line of `README.md`) and the changelog (`CHANGELOG.md`, the `## What Changed` section of `README.md`) change only in a release pull request. In any other pull request, ask for them to be left alone.
- Text a user sees comes from the language files under `src/HASS.Agent.NET10/Localization/` (`en.json`, `hu.json` and the others), under the same key in every file; `LocalizationTests` fail when a language lacks a key. A language the author does not speak may carry the English text, with a note in the PR. In a translation PR, check keys and `{0}`-style placeholders against `en.json`; grammar and tone in that language are the translator's.
- A bug fix comes with a test that fails without the fix; new logic with an input and an output gets tests in the same pull request. What only Windows itself can show (windows, the tray icon, audio and display devices, the service) is tested by hand: the PR should say what was tried and on which Windows. Ask for the test, or for that note, when it is missing.
- A new feature is documented under `docs/` (its own page or section) with one line in the README's feature table. The docs are built with MkDocs in `--strict` mode: a relative link that does not resolve, or a new page missing from the `nav` of `mkdocs.yml`, fails the build. The `<a id="...">` anchors in the README keep old links alive; do not let a change remove them.
- Security is the first thing to look at. The service runs as SYSTEM and the tray app as the signed-in user; the settings live under `%ProgramData%\HASS.Agent.NET10` behind an ACL, secrets are protected with DPAPI (`Security/ProtectedSecretStore.cs`). Anything that comes from Home Assistant, the MQTT broker, the local HTTP API, the command line of another process, or files in a folder every user of the PC may write (the classic client's folders included) is untrusted. Look for: a path a user controls reaching a file the service writes or reads as SYSTEM (symbolic links, junctions, planted files), a command or argument built from an untrusted string, a user of the PC getting more through the service than they have themselves, a secret written to the log or to a file every user may read. `HardeningTests`, `SecurityTests`, `SettingsAccessTests` and `ServicePolicyTests` show the bar.
- There is no PR template. Check that the description says what the change does and why, and for a bug, how to reproduce it.
- CodeRabbit reviews every pull request too. Do not repeat its comments; a point it already made and the author answered needs no second thread.

# GitHub Copilot & Claude Code Instructions

This repository is HASS.Agent .NET10, a Windows client for Home Assistant: one executable that runs as a tray app (WinForms) for the signed-in user and, installed as a Windows service, as SYSTEM for sensors and commands that need it. It talks to Home Assistant over MQTT and the Home Assistant API, and has a small local HTTP API. The Home Assistant side is a separate repository, `HASS.Agent.NET10-Integration`.

## Git Commit Guidelines

- **Do NOT amend, squash, or rebase commits that have already been pushed to the PR branch after the PR is opened.** Reviewers need to follow the commit history, as well as see what changed since their last review. Pull requests are squash-merged at the end.

## Pull Requests

- One change per pull request, against `main`. Say what it changes and why; for a bug, how to reproduce it; for what was tested by hand, what and on which Windows.
- Leave the version numbers and the changelog alone; they are updated when a release is made.

## Development Commands

- The .NET SDK named in `global.json` is required at that exact version (`dotnet --list-sdks`).
- Build: `dotnet build src/HASS.Agent.NET10/HASS.Agent.NET10.csproj -c Release`. Warnings are errors.
- Tests: `dotnet test --project tests/HASS.Agent.NET10.Tests -c Release` (xunit v3). They run on Windows and need no installed app, no service and no Home Assistant.
- Docs: `python -m venv .venv`, `.venv\Scripts\pip install -r website\requirements.txt`, `.venv\Scripts\mkdocs build --strict`. See `docs/development.md`.

## Code

- C# with `Nullable` enabled and `LangVersion` latest on `net10.0-windows`. Do not suggest workarounds for older language or runtime versions.
- Where things are: `Tray/` (the tray app and its windows), `SystemService/` (the service), `Mqtt/` and `Http/` (the connections), `SystemStatus/` (sensors), `SystemCommands/` (commands), `Configuration/` (settings, the classic client import), `Security/`, `Localization/`, `Media/`, `Networking/`, `Runtime/`, `Logging/`.
- Comments explain why, not what: the non-obvious constraint, the surprising behaviour, the workaround. No comment that restates the next line, no section dividers, no comment that justifies a change by what the code looked like before.
- Keep try blocks small and catch what the call can throw. A catch-all is for the places where the app must keep running whatever happens (the service loop, a sensor read, a window the user closed while something was loading); it logs what it caught.
- UI work is done on the UI thread (`BeginInvoke` on the tray context); never block it with `.Result` or `.Wait()`. Long work is async with a `CancellationToken` that the shutdown cancels.
- Win32 handles and COM objects are disposed; a native call is checked for its failure value.
- Tests are named as sentences with underscores, say what the behaviour is rather than which method is called (`A_zoom_is_there_again_for_the_same_address_only`), use a temporary folder of their own (`Directory.CreateTempSubdirectory`) and clean it up, and never touch the installed app's folders or the registry of the PC.

## Documentation and texts

- The README is short: installation, the feature table, links. Details live under `docs/`, one folder per area. The changelog is `CHANGELOG.md`, newest first, and the README repeats only the latest release.
- Texts the user sees, the docs and the changelog are plain English, written for the person using the app, not for the developer. Short sentences, no marketing, no AI-sounding filler.
