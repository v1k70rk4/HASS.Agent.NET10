---
title: HA API (WebSocket)
description: HASS.Agent .NET10 over the Home Assistant WebSocket API - no broker, works from away, with the events the app fires and the commands the integration sends.
---

# HA API (WebSocket)

The app connects directly to Home Assistant's WebSocket API with a long-lived access token: no broker, and it works from away through Nabu Casa or your own reverse proxy. It is used on its own, or as the automatic failover when MQTT drops. [Connection modes](index.md) has the trade-offs, and how the PC gets [a Home Assistant user of its own](index.md#a-home-assistant-user-for-the-pc) so that the token on it is not an administrator's.

**Test connection** on the **HA API** page says what it found: the Home Assistant version and the integration's version, or that the integration is missing or too old.

## Events and commands

In HA API mode the app communicates through Home Assistant's event bus instead of MQTT topics.

With the integration 10.9.1 or newer the app sends its events with the integration's `hass_agent/fire` command and receives its commands with `hass_agent/subscribe`, which only carries the commands for its own serial number. Both work with a token of a user who is not an administrator. With an older integration it falls back to `fire_event` and `subscribe_events`, which need an administrator's token.

Events fired by the app:

```text
hass_agent_device_update          # discovery + capabilities (tray app)
hass_agent_service_update         # Windows service status + capabilities
hass_agent_update_state           # available app update (drives the update entity)
hass_agent_sensor_update          # sensor values
hass_agent_media_update           # media player state
hass_agent_media_thumbnail        # media thumbnail (base64)
hass_agent_notification_action    # notification button press
hass_agent_hotkey                 # hotkey press (10.9.0+)
```

Commands sent by the integration to the app:

```json
{
  "serial_number": "agent-serial",
  "command_type": "notification | media_command | button_command | update_install",
  "target": "app | service",
  "payload": { }
}
```

`target` names which side should act on a button command, the tray app or the Windows service, the same choice MQTT makes by picking a topic. It is optional: without it each side falls back to deciding for itself, which is how integrations older than 10.6.5 behave.

All events and commands are targeted by `serial_number`, so renaming the device in Home Assistant does not break routing.

## After a Home Assistant restart

What the app sends once on connect (its discovery, the service status, the update state) could reach Home Assistant before the integration listens, and the HA API keeps nothing for latecomers. The app therefore sends these again when the integration asks (integration 10.9.0+), and the tray app sends its discovery again every ten minutes on its own. The Danger Zone's **Republish discovery** does the same by hand.
