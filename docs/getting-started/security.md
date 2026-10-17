---
title: Security and privacy
description: How HASS.Agent .NET10 keeps the PC and the Home Assistant token safe, how its releases are signed and checked, and what it sends where.
---

# Security and privacy

An app that can run commands on your PC and holds your Home Assistant token should not need blind trust. This page collects what the app does about it; version 10.9.1 was a release of its own for most of it.

## On the PC

- **Who may use it on this PC.** The settings, with the token, the MQTT login and the commands, can be limited to the Windows users you choose; a first install asks. [Who may use it on this PC](../features/users.md)
- **SYSTEM only with approval.** The Windows service runs a custom command or a command sensor only after an administrator has approved exactly what it runs. [Commands the service runs](../features/windows-service.md#commands-the-service-runs)
- **Home Assistant never sends code.** It only sends the id of a command you set up; what the command runs is defined on the PC. [Custom commands](../commands/custom.md)
- **Windows tools are started by their full path**, and the settings folder cannot be taken over by another user of the PC.

## In Home Assistant

- **No administrator's token.** Over the HA API the PC uses a Home Assistant user of its own, who is not an administrator, and gets only its own commands. The app creates that user with one click. [A Home Assistant user for the PC](../connection/index.md#a-home-assistant-user-for-the-pc)
- **An address without a scheme gets https** unless it points into the home network, so a token never leaves the house unencrypted by accident.
- **The MQTT login is the key to the PC.** Whoever can log in to the broker can send it commands; use a login of its own and keep it to yourself. [MQTT](../connection/mqtt.md)

## Signed releases

Starting with 10.6.8, the release assets (`HASS.Agent.NET10-Setup-<version>.exe`, its uninstaller, and `HASS.Agent.NET10.exe` in the zip) are signed with a Certum *Open Source* code signing certificate:

| | |
|---|---|
| Issued to | `Open Source Developer Viktor Révész` |
| Issued by | `Certum Code Signing 2021 CA` |
| SHA-1 thumbprint | `0C09522639318AF1069507568599DFC5B7F86EE4` |
| Timestamp | `time.certum.pl` (the signature stays valid after the certificate expires) |

To check a download, open its **Properties → Digital Signatures** tab, or with the Windows SDK:

```powershell
signtool verify /pa /v HASS.Agent.NET10-Setup-10.9.2.exe
```

Signing happens on the maintainer's machine through SimplySign, unlocked with a one-time code; the private key lives in Certum's cloud HSM and is never exported. The GitHub Actions build itself is unsigned, the signed files replace its assets on the release. A certificate this new has little SmartScreen reputation yet, so Windows may still show a *"Windows protected your PC"* prompt for a while; the publisher name on that prompt is what confirms the file is genuine.

From 10.9.0 on, every release also carries a [Sigstore](https://www.sigstore.dev/) signature per asset (`<asset>.sigstore.json`). A workflow of the repository makes it once the release is out: it checks that the file carries the Certum signature above, then signs it with the workflow's GitHub identity, without any key to keep. It shows that the file is the one the repository published, and it can be checked on any system with [cosign](https://docs.sigstore.dev/cosign/system_config/installation/):

```bash
cosign verify-blob HASS.Agent.NET10-Setup-<version>.exe \
  --bundle HASS.Agent.NET10-Setup-<version>.exe.sigstore.json \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity-regexp '^https://github\.com/v1k70rk4/HASS\.Agent\.NET10/\.github/workflows/sign-release\.yml@'
```

The built-in updater runs a downloaded installer only when it carries the publisher's valid Authenticode signature; anything else is deleted and the update stops, with the reason in the log and in Home Assistant. [Updates from Home Assistant](../features/updates.md)

## Reviewed

CodeQL scans every change, the app, the integration and the release flow have had security reviews, the project carries the [OpenSSF Best Practices](https://www.bestpractices.dev/projects/15316) passing badge and an [OpenSSF Scorecard](https://scorecard.dev/viewer/?uri=github.com/v1k70rk4/HASS.Agent.NET10) that checks it on every change. A vulnerability is reported privately, as the [security policy](https://github.com/v1k70rk4/HASS.Agent.NET10/security/policy) says, not in an issue.

## Privacy

The program does not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. HASS.Agent .NET10 communicates only with:

- the **Home Assistant instance and/or MQTT broker configured by the user**, to provide its core functionality,
- the **GitHub API** (`api.github.com` / `github.com`), to check for application updates and download release assets.

No telemetry, analytics or usage data is collected or transmitted.
