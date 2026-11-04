---
title: Coming from HASS.Agent
description: "Coming from HASS.Agent: what is the same in HASS.Agent .NET10, what is different, which settings it takes over, what does not carry over, and how to switch step by step."
---

# Coming from HASS.Agent

This page is for people who run the classic HASS.Agent, the [LAB02 original](https://github.com/LAB02-Research/HASS.Agent) or its [community continuation](https://github.com/hass-agent/HASS.Agent), and are thinking about HASS.Agent .NET10.

HASS.Agent .NET10 began as a fork of the classic client and is a separate program now. It follows the same idea, but it does not share the Home Assistant entities with it, so switching is not an upgrade; the settings of the classic client it can take over. The classic client is maintained by its community; if it does what you need, there is nothing wrong with staying.

## What is the same

- The purpose: Home Assistant notifications on the PC, the PC as a media player, sensors, commands, a web page in a popup window, and a service for what should work with nobody logged in.
- MQTT as the main transport, and a companion integration installed through the Marketplace (HACS in Home Assistant before 2026.11).

## What is different

| | Classic HASS.Agent | HASS.Agent .NET10 |
|---|---|---|
| **Entities in Home Assistant** | Sensors and commands arrive through Home Assistant's MQTT discovery; the integration adds notifications and the media player. | One integration creates every entity from what the agent advertises, and removes what you switch off. |
| **Connection** | MQTT is needed for the sensors and commands. | MQTT, or the Home Assistant WebSocket API with no broker at all, or both with automatic failover. See [Connection modes](../connection/index.md). |
| **Notifications** | Windows toast notifications, with images and input. | From 10.9.0 the same: Windows notifications with pictures, buttons and text fields. The app's own always-visible window is there as the other style, for what has to be seen during Do not disturb. See [Notifications](../features/notifications.md). |
| **Quick Actions** | A hotkey opens a window that controls Home Assistant entities. | Not there. The nearest things are [hotkeys](../features/hotkeys.md) that arrive in Home Assistant as events (from 10.9.0) and the [dashboard popup](../features/dashboard-popup.md). |
| **Sensors** | Each sensor is added one by one from a long list of types. | A fixed set of [built-in sensors](../sensors/index.md) you switch on or off, plus [custom sensors](../sensors/custom.md): a process, a service, a drive, an attribute of a built-in sensor, the output of a command or script, a LibreHardwareMonitor value (from 10.9.0). |
| **Commands** | Each command is added from a list of types. | A fixed set of [system commands](../commands/index.md), plus [custom commands](../commands/custom.md): a program, a PowerShell script, key presses, a link, a popup window. |
| **Service** | The separate Satellite Service. | The same executable installed as a Windows service. |
| **Runtime** | .NET 8 (the LAB02 original: .NET 6). | .NET 10, shipped with the program, nothing to install beside it. |

Before you switch, check that the sensors and commands you rely on have a counterpart in the pages linked above. The classic client has sensor and command types this one does not have.

## What comes over

When the app finds the classic client's settings (for the Windows user it runs as) and has no connection set up yet, it offers to take them over the first time its window opens. Later the **Import from HASS.Agent** button in the [Danger Zone](../features/danger-zone.md), under **Backup / restore**, does the same. The classic install is only read, nothing is changed there.

- **The connection:** the MQTT broker, port, TLS, user name and password, and the Home Assistant address and token for the [HA API](../connection/ha-api.md), stored protected like every secret here. The classic client's token is as a rule an administrator's: once the PC is connected, the app offers to swap it for a user of the PC's own.
- **The device name.**
- **Sensors** with a built-in counterpart are switched on: CPU, memory and GPU load, active window, last boot, logged-in user(s), session state, webcam and microphone in use, monitor power, volume and audio, battery, network, storage, displays, Windows updates, last active. *Process active*, *Service state* and *PowerShell* sensors become [custom sensors](../sensors/custom.md), with their update interval turned into the nearest polling profile.
- **Commands:** lock, sleep, hibernate, log off, shutdown, restart, monitor sleep and the volume commands switch on the [system commands](../commands/index.md). *Custom* and *PowerShell* commands, *Key* and *Multiple keys*, *Launch URL* and *WebView* become [custom commands](../commands/custom.md), and the media play/pause, next and previous commands become key presses. A custom command that needs cmd (its own commands such as `start`, pipes, redirection) still runs through `cmd /C`, as it did.
- **The Satellite Service's** sensors and commands come over as well, set to run in the service. What needs the user's desktop (key presses, addresses, windows) goes to the tray app instead.

What has no counterpart is listed by name at the end: Quick Actions, WMI and performance counter sensors, named-window, Bluetooth, location and printer sensors, setting the volume or the audio device, switching desktops, and the like. MQTT over WebSocket and certificate files do not come over either.

## What does not carry over

- **Entities.** The PC appears in Home Assistant as a new device with new entities. Automations, scripts and dashboards that name the old entities have to be pointed at the new ones, and the history stays with the old entities.
- **The integration.** Both integrations use the `hass_agent` domain, so Home Assistant can hold only one of them, and this one does not talk to the classic client: a classic client is not added, and a notice under **Settings → Repairs** says why. With several PCs on one Home Assistant, switch them together. A PC left on the classic client keeps its MQTT sensors and commands, but loses its notifications and its media player, which need the classic integration.

## Switching, step by step

1. **Take stock.** Note the automations and dashboards that refer to the classic client's entities, and the Quick Actions you use.
2. **Install the app** as in [Get started](index.md), while the classic client is still on the PC, and take over its settings when the app offers it (see [What comes over](#what-comes-over)).
3. **On the PC, uninstall the classic client** and its Satellite Service. Do not run the two clients side by side on one PC.
4. **In Home Assistant, remove the old device.** Under **Settings → Devices & services → MQTT**, open the PC's device and delete it; that also clears its retained discovery messages on the broker. Then remove the classic HASS.Agent integration entry, and remove the classic integration in the Marketplace (in HACS before 2026.11).
5. **Install this integration** as in [Get started](index.md).
6. **Add the PC in Home Assistant.** On MQTT it turns up under **Settings → Devices & services → Discovered**; over the HA API add it by hand (**Add integration → HASS.Agent → HA API (WebSocket)**). The summary at the end of the import says which.
7. **Check sensors and commands.** What came over is on the **Sensors** and **Capabilities** pages; add what the import listed as not taken over, if this app has something close to it. The editors have a **Test** button that shows what a sensor would report or runs a command right away.
8. **Point automations and dashboards at the new entities.**
9. **Check for leftovers.** The [Danger Zone](../features/danger-zone.md)'s MQTT maintenance lists the retained HASS.Agent messages on the broker and deletes the ones you select.

## Going back

Uninstall HASS.Agent .NET10, remove its device and this integration from Home Assistant, then install the classic client and its integration again. For the LAB02-era client the integration of this project still has its last compatible version, **v3.0.2**, on the [`legacy` branch](https://github.com/v1k70rk4/HASS.Agent.NET10-Integration/tree/legacy); it is no longer maintained.
