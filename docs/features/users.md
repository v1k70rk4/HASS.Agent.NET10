---
title: Who may use it on this PC
description: Limiting the HASS.Agent .NET10 settings, with the Home Assistant token and the commands in them, to some of the Windows users of a PC.
---

# Who may use it on this PC

> From 10.9.1.

The settings are shared by every Windows user of the PC, and they hold the Home Assistant token, the MQTT login and the commands the app runs. They can be limited to some users:

- A **first install** asks: *Only me* or *Every user of this PC*. A silent install (winget, or an update started from Home Assistant) takes *Every user*, and the app asks later.
- On a PC that **already has HASS.Agent**, an update does this by itself:
    - If every user of the PC is an administrator, it limits the settings to them. This takes nothing from anyone and keeps out a user added later.
    - If there is a user who is not an administrator, nothing changes. Home Assistant gets a notification once, and the question comes when someone opens the HASS.Agent window, never at logon. Until a choice is made everything keeps working as before.
    - On a PC where someone has logged on with a domain or Entra ID account (a work PC), such a user counts as an administrator when they are in the PC's Administrators group, by their own account or through a domain group such as Domain Admins (from 10.10.1; the domain's groups need a domain controller that answers). One who cannot be shown to be an administrator keeps the update from limiting the settings, and the question comes in the app, where those users are listed as `DOMAIN\user` when Windows can name them. The check through a domain group is made by the update, which runs as SYSTEM or as an administrator; the app of a local account cannot ask the domain, so on such a PC the Home Assistant notice may name a domain administrator as not known to be one. Nothing changes until someone chooses in the app.
- It can be changed any time under **Danger Zone → Users of this PC**.

Limiting needs an administrator (UAC). It uses a local group, **HASS.Agent Users**, that an administrator can also manage with Windows' own tools; the ticked users are also given access by name, so they can go on without logging on again. Administrators and the service keep their access. Someone left out gets a note once, and HASS.Agent does not start for them. Either way the users may change what is in the folder but not delete or replace the folder itself, and before an administrator sets its rule, the app removes any link someone put there and takes over what another user owns.

The folder also holds an empty file named `folder-….anchor`, which only administrators can touch. Leave it there: Windows turns only an empty folder into a link to another folder, and the file is what keeps the settings folder from being emptied and made one by a user of the PC. An update makes a new one and removes the old.

A first install does not take its settings from a settings folder that was already there and that a user who is not an administrator had made. What the folder held stays in it, renamed with `.found-before-install-…` at the end of each name, so an administrator can look at it and put it back.

From the command line (as administrator):

```powershell
HASS.Agent.NET10.exe --settings-access only USER [USER ...]
HASS.Agent.NET10.exe --settings-access everyone
```

A user from a domain or Entra ID is given as `DOMAIN\user`. The exit code is 1 when the change could not be made, also when the folder took the rule but a file in it did not; the message names the file.

## Several users signed in at once

> From 10.13.0.

Every Windows user signed in has a tray app of their own, and the PC is one device in Home Assistant, so one app talks to it and the others wait: the one in the session at the PC's screen, or, on a PC used over Remote Desktop only, the active session's. A switch of users hands the connection over within a few seconds, and the General page of a waiting app says that it waits. Settings saved in a waiting app reach the connected one when it starts next. The Windows service is not part of this: it runs once for the PC.

What is at stake is named in the question itself: the MQTT password opens the whole broker, and the Home Assistant token can do whatever its user can. [Security and privacy](../getting-started/security.md) has the rest.
