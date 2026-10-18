---
title: The installer and winget
description: The HASS.Agent .NET10 setup package, its options, how winget installs and upgrades it, what an upgrade and an uninstall do.
---

# The installer and winget

The setup package is built with [Inno Setup](https://jrsoftware.org/isinfo.php) and signed; it is also what winget installs and upgrades.

## winget

```powershell
winget install v1k70rk4.HASSAgentNET10
winget upgrade v1k70rk4.HASSAgentNET10
```

winget runs the installer without its wizard pages, showing only the progress, so the options below keep their defaults on a first install (start on login on, the rest off) and their earlier choices on an upgrade. Add `--interactive` to go through the wizard.

## Options

- **Desktop icon** (optional)
- **Start automatically on login** (default: enabled, sets a registry Run key)
- **Install system service** (optional, see [Windows service](../features/windows-service.md))
- **Clean install**: removes the existing settings, API key and log files, for a fresh start
- **Who may use it** (first install only): *Only me* or *Every user of this PC*, see [Who may use it on this PC](../features/users.md)

The installer also sets up a **Windows Firewall** rule (Private profile, TCP port 5115) for the [local HTTP API](../connection/local-api.md), so no manual firewall setup is needed when you turn that on; it is off by default.

## Upgrades and uninstall

During an upgrade the installer stops the running tray app, stops the system service if installed, replaces the files, reinstalls and starts the service, and restarts the tray app. Updates can also be installed from Home Assistant, silently with the service: [Updates from Home Assistant](../features/updates.md).

On uninstall the firewall rule is removed, and you are asked whether the settings, with the tokens and passwords in them, should go too.

## Without the installer

The zip on the releases page holds the same `HASS.Agent.NET10.exe`: start it and it runs from wherever it is, with its settings in `C:\ProgramData\HASS.Agent.NET10`. Set the firewall rule yourself if you turn on the local HTTP API ([Windows Firewall](../connection/local-api.md#windows-firewall)), and install the service from the **Service** page.
