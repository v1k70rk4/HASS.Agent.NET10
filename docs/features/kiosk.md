---
title: Kiosk mode
description: HASS.Agent .NET10 on a kiosk PC, where the sensors need the tray app but the person in front of the screen must not open, change or close it.
---

# Kiosk mode

> From 10.11.0.

Some sensors need the tray app (the session, the screen, the media player, the notifications), and on a PC set up as a kiosk the person in front of the screen should not be able to open its window, change its settings or close it. Kiosk mode keeps the app running for them with nothing to touch:

- The users ticked in **Danger Zone → Kiosk mode** are the kiosk users. You cannot tick yourself, and a note under the list says so: ticking yourself would lock your own app. So kiosk mode is set from the app of a user who is not to be locked.
- An administrator can be ticked too (from 10.11.0-beta.3), for a kiosk PC where the account in front of the screen is an administrator. The window asks first, because for an administrator kiosk mode is a cover, not a lock: it hides the window and the menu, but they can take themselves out of the group, change the folder's permissions or end the task.
- A kiosk user gets only the icon in the tray, with *Kiosk mode* as its tooltip: no window, no menu, no **Exit**. Notifications, hotkeys and the pages Home Assistant opens with the WebView command work as before.
- The app starts when a kiosk user logs on, and comes back when it is closed: at once when it closes or crashes, within a minute when it is ended in Task Manager.
- The kiosk users can read the settings but not change, replace or delete them, and they cannot add files to the settings folder. Their app logs to a file of their own, `%LOCALAPPDATA%\HASS.Agent.NET10\hass-agent-net10.log`, since the shared log is in that folder.
- Everyone else keeps the full app, as before.

There is no password. One would not stop anyone who can close the app or edit its files; Windows' own file permissions do.

Turning it on, changing who the kiosk users are and turning it off need an administrator (UAC). From the command line, as administrator:

```powershell
HASS.Agent.NET10.exe --kiosk on USER [USER ...]
HASS.Agent.NET10.exe --kiosk off
```

A user from a domain or Entra ID is given as `DOMAIN\user`. The exit code is 1 when the change could not be made. The command line does not ask about an administrator the way the window does.

## The HASS.Agent Kiosk group

The kiosk users are the members of a local group, **HASS.Agent Kiosk**, made when kiosk mode is first turned on and removed when it is turned off. An administrator can also add or remove members with Windows' own tools, or from a domain's group policy, for a row of kiosk PCs. The group alone decides: whoever is in it gets the kiosk app, an administrator too (the group's deny on the settings folder holds for them as well, also when elevated). Like any group membership, it counts from the user's next logon: a user just ticked gets kiosk mode when they log on again, and one just unticked gets the full app back then.

## How it works

- The settings folder keeps the rule of [Who may use it on this PC](users.md), with a deny for the kiosk group on top: on the folder and on everything in it, nothing may be created, written or deleted. A deny goes before every allow, so it holds whatever else lets a kiosk user in. An update keeps it.
- A scheduled task, `HASS.Agent.NET10\Kiosk`, created by the administrator, runs for the kiosk group, in the session of each member who is logged on. It starts the app when it is not running there yet. It runs at logon, when the app writes to the event log that it closed, when Windows writes that it crashed, and once a minute.
- The Windows event log (Application, source `HASS.Agent.NET10`) has an entry when kiosk mode is turned on or off, when the app starts for a kiosk user, when it is closed, and when it is started again after that.

An update stops the task while it replaces the files, and turns it back on at the end. Uninstalling turns kiosk mode off: the task and the group go, and the users get back what they could do in the settings folder.
