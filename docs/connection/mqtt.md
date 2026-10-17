---
title: MQTT
description: HASS.Agent .NET10 over MQTT - the settings, the topics the app publishes and subscribes to, why the broker login matters, and the malformed packet workaround.
---

# MQTT

The recommended transport: fast, local, with auto-discovery. The **MQTT** page takes the broker's address and port, a login, TLS, and which features go over MQTT (notifications, the media player, the buttons, the sensors), for the tray app and the service each. **Test connection** tells whether the broker answers.

The **Discovery retain flag** keeps the device's discovery message on the broker, so Home Assistant sees the PC right after its own restart, before the app sends it again.

## Topics

Published by the app:

```text
hass.agent/devices/{serialNumber}                   # discovery + capabilities
hass.agent/system/{serialNumber}/state              # service online state
hass.agent/sensors/{serialNumber}/state             # sensor values
hass.agent/update/{serialNumber}/state              # app update state
hass.agent/media_player/{serialNumber}/state        # media player state
hass.agent/notifications/{serialNumber}/actions     # notification action events
hass.agent/hotkeys/{serialNumber}/pressed           # hotkey presses (10.9.0+)
homeassistant/update/{serial}/hass_agent_net10/config  # HA update entity discovery
```

Subscribed by the app:

```text
hass.agent/notifications/{serialNumber}             # incoming notifications
hass.agent/media_player/{serialNumber}/cmd          # media player commands
hass.agent/buttons/{serialNumber}/cmd               # system command buttons
hass.agent/system/{serialNumber}/cmd                # service-routed commands
```

The Danger Zone's [live MQTT monitor](../features/danger-zone.md) shows the traffic on these topics, and its MQTT maintenance deletes the retained messages of a PC that is gone. MQTT messages over 1 MB are ignored.

## Keep the broker login to yourself

MQTT has no idea who sent a message: anything that can log in to the broker can publish to these topics, so it can send this PC notifications, shut it down, or run the commands you set up for it, and read what is typed into a notification. With the Mosquitto add-on that is every login it accepts: Home Assistant users included, and the one a Zigbee bridge or an ESP device uses. Use a login of its own for HASS.Agent, don't put it on devices you don't trust, and change it if it got out. To go further, Mosquitto's ACLs can limit who may write `hass.agent/+/{serialNumber}/cmd` and `hass.agent/notifications/{serialNumber}`. The HA API does not have this problem: there, every PC has its own Home Assistant user and gets only its own commands.

## The broker drops the PC with "malformed packet"?

Mosquitto writes `disconnected: malformed packet` in its log, and the PC reconnects a few seconds later, now and then, always on a larger message. On one PC with a Realtek 2.5G network card (sold as "ASUS 2.5G") HASS.Agent's packets checked out fine as they left the app, and the errors stopped once the card's Large Send Offload was switched off. That is a workaround that helped there, not a proven cause. To try it: in an administrator's PowerShell `Disable-NetAdapterLso -Name Ethernet` (with the adapter's name from `Get-NetAdapter`), or in Device Manager under the adapter's *Advanced* tab, *Large Send Offload v2*. `Enable-NetAdapterLso` turns it back on. When the broker closes the session over a packet, the app's log lists the last packets that went out, with their time, topic and size.
