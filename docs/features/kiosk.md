---
title: Kiosk mode
description: HASS.Agent .NET10 on a kiosk PC, where the sensors need the tray app but the person in front of the screen must not open, change or close it.
---

# Kiosk mode

> From 10.11.0.

Some sensors need the tray app (the session, the screen, the media player, the notifications), and on a PC set up as a kiosk the person in front of the screen should not be able to open its window, change its settings or close it. Kiosk mode keeps the app running for them with nothing to touch:

- The users ticked in **Danger Zone → Kiosk mode** keep the full app, and so does every administrator of the PC. Whoever turns it on is ticked too.
- Everyone else gets only the icon in the tray, with *Kiosk mode* as its tooltip: no window, no menu, no **Exit**. Notifications, hotkeys and the pages Home Assistant opens with the WebView command work as before.
- The app starts when a kiosk user logs on, and comes back when it is closed: at once when it closes or crashes, within a minute when it is ended in Task Manager.
- The kiosk users can read the settings but not change, replace or delete them, and they cannot add files to the settings folder. Their app logs to a file of their own, `%LOCALAPPDATA%\HASS.Agent.NET10\hass-agent-net10.log`, since the shared log is in that folder.

There is no password. One would not stop anyone who can close the app or edit its files; Windows' own file permissions do.

Turning it on, changing who keeps the full app and turning it off need an administrator (UAC). From the command line, as administrator:

```powershell
HASS.Agent.NET10.exe --kiosk on USER [USER ...]
HASS.Agent.NET10.exe --kiosk off
```

A user from a domain or Entra ID is given as `DOMAIN\user`; the administrators are added by themselves. The exit code is 1 when the change could not be made.

## How it works

- Who keeps the full app is in `kiosk.json`, next to the program in `Program Files`, where only administrators can write, so a kiosk user cannot take themselves off the list. Without the file kiosk mode is off.
- The settings folder gets the rule of [Who may use it on this PC](users.md), with one difference: only the users who keep the full app may change what is in it. The users who may use HASS.Agent at all stay the same; an update keeps the rule.
- A scheduled task, `HASS.Agent.NET10\Kiosk`, created by the administrator, runs for every user who logs on. It starts the app only for a kiosk user, and only when it is not running yet. It runs at logon, when the app writes to the event log that it closed, when Windows writes that it crashed, and once a minute.
- The Windows event log (Application, source `HASS.Agent.NET10`) has an entry when kiosk mode is turned on or off, when the app starts for a kiosk user, when it is closed, and when it is started again after that.

An update stops the task while it replaces the files, and turns it back on at the end. Uninstalling turns kiosk mode off: the task goes, and the users get back what they could do in the settings folder.
