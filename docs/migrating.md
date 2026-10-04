# Coming from HASS.Agent

[← Back to the README](https://github.com/v1k70rk4/HASS.Agent.NET10#readme)

This page is for people who run the classic HASS.Agent, the [LAB02 original](https://github.com/LAB02-Research/HASS.Agent) or its [community continuation](https://github.com/hass-agent/HASS.Agent), and are thinking about HASS.Agent .NET10.

HASS.Agent .NET10 began as a fork of the classic client and is a separate program now. It follows the same idea, but it shares neither the settings nor the Home Assistant entities with it, so switching is a fresh setup and not an upgrade. The classic client is maintained by its community; if it does what you need, there is nothing wrong with staying.

## What is the same

- The purpose: Home Assistant notifications on the PC, the PC as a media player, sensors, commands, a web page in a popup window, and a service for what should work with nobody logged in.
- MQTT as the main transport, and a companion integration installed through HACS.

## What is different

| | Classic HASS.Agent | HASS.Agent .NET10 |
|---|---|---|
| **Entities in Home Assistant** | Sensors and commands arrive through Home Assistant's MQTT discovery; the integration adds notifications and the media player. | One integration creates every entity from what the agent advertises, and removes what you switch off. |
| **Connection** | MQTT is needed for the sensors and commands. | MQTT, or the Home Assistant WebSocket API with no broker at all, or both with automatic failover. See [Connection Modes](connection.md#connection-modes). |
| **Notifications** | Windows toast notifications, with images and input. | A tray balloon or the app's own popup with action buttons. No images and no text input. |
| **Quick Actions** | A hotkey opens a window that controls Home Assistant entities. | Not there. The nearest things are [hotkeys](features.md#hotkeys) that arrive in Home Assistant as events (10.9.0 beta) and the [dashboard popup](features.md#dashboard-popup). |
| **Sensors** | Each sensor is added one by one from a long list of types. | A fixed set of [built-in sensors](sensors.md#built-in-sensors) you switch on or off, plus [custom sensors](sensors.md#custom-sensors): a process, a service, a drive, an attribute of a built-in sensor, the output of a command or script, a LibreHardwareMonitor value (10.9.0 beta). |
| **Commands** | Each command is added from a list of types. | A fixed set of [system commands](commands.md#system-commands), plus [custom commands](commands.md#custom-commands): a program, a PowerShell script, key presses, a link, a popup window. |
| **Service** | The separate Satellite Service. | The same executable installed as a Windows service. |
| **Runtime** | .NET 8 (the LAB02 original: .NET 6). | .NET 10, shipped with the program, nothing to install beside it. |

Before you switch, check that the sensors and commands you rely on have a counterpart in the pages linked above. The classic client has sensor and command types this one does not have.

## What does not carry over

- **Settings.** Nothing is imported from the classic client. Sensors, commands and connection settings are set up again.
- **Entities.** The PC appears in Home Assistant as a new device with new entities. Automations, scripts and dashboards that name the old entities have to be pointed at the new ones, and the history stays with the old entities.
- **The integration.** Both integrations use the `hass_agent` domain, so Home Assistant can hold only one of them, and this one does not talk to the classic client: a classic client is not added, and a notice under **Settings → Repairs** says why. With several PCs on one Home Assistant, switch them together. A PC left on the classic client keeps its MQTT sensors and commands, but loses its notifications and its media player, which need the classic integration.

## Switching, step by step

1. **Take stock.** Note the sensors, commands and Quick Actions you use, and the automations and dashboards that refer to their entities.
2. **On the PC, uninstall the classic client** and its Satellite Service. Do not run the two clients side by side on one PC.
3. **In Home Assistant, remove the old device.** Under **Settings → Devices & services → MQTT**, open the PC's device and delete it; that also clears its retained discovery messages on the broker. Then remove the classic HASS.Agent integration entry, and remove the classic integration in HACS.
4. **Install this integration and the app** as in the [Quick Start](https://github.com/v1k70rk4/HASS.Agent.NET10#quick-start), connect over MQTT or the HA API, and add the PC when it turns up under **Discovered**.
5. **Set up sensors and commands.** Switch on the built-in sensors you want on the **Sensors** page and the commands on the **Capabilities** page, then add your custom ones. The editors have a **Test** button that shows what a sensor would report or runs a command right away.
6. **Point automations and dashboards at the new entities.**
7. **Check for leftovers.** The [Danger Zone](features.md#danger-zone)'s MQTT maintenance lists the retained HASS.Agent messages on the broker and deletes the ones you select.

## Going back

Uninstall HASS.Agent .NET10, remove its device and this integration from Home Assistant, then install the classic client and its integration again. For the LAB02-era client the integration of this project still has its last compatible version, **v3.0.2**, on the [`legacy` branch](https://github.com/v1k70rk4/HASS.Agent.NET10-Integration/tree/legacy); it is no longer maintained.
