---
title: Local HTTP API
description: The local HTTP API of HASS.Agent .NET10 - off by default, the notification endpoint on port 5115, the API key, and the Windows Firewall rule.
---

# Local HTTP API

A small HTTP server in the app for the integration's Local HTTP API mode: notifications only, for a setup with neither MQTT nor the HA API. **It is off by default**: nothing listens on the port and there is no API key until you turn it on. MQTT and the HA API do not use it.

## Turning it on

On the **General** page, under **Local HTTP API**, tick **Turn on the local HTTP API** and save. The app then listens on the bind address and port set there (`0.0.0.0` and `5115` by default), makes an API key and shows it with a **Copy key** button; the LAN addresses Home Assistant can reach it at are listed above it, and the tray menu gets a **Copy URL** item. Copy the key into the integration's Local HTTP API setup in Home Assistant.

Turning it off stops the server and drops the key. Turning it on again makes a new key, which has to go into Home Assistant again: remove the PC from the integration and add it once more with the new key.

!!! note "Upgrading from 10.9.2 or earlier"

    Until 10.9.3 the server was always on and every install had a key. An install from before keeps it on, with its key, so a PC set up in Home Assistant with the Local HTTP API goes on working. Turn it off on the General page when nothing uses it; only a new install starts with it off.

**Endpoints:**

| Method | Path | Auth | Description |
|--------|------|:----:|-------------|
| `GET` | `/info` | | Device info and capabilities |
| `POST` | `/notify` | Bearer | Send a notification |

`POST /notify` needs the key in the `Authorization: Bearer <key>` header, and obeys the **Notifications** switch on the Capabilities page like the other two connections: while that is off it answers `403` with `notifications_disabled`, and `GET /info` reports `notifications: false`.

```powershell
# Test from the local machine
Invoke-RestMethod http://localhost:5115/info

# Test notification with API key
$headers = @{ Authorization = "Bearer YOUR_API_KEY_HERE" }
$body = @{ message = "Test"; title = "Hello" } | ConvertTo-Json
Invoke-RestMethod http://localhost:5115/notify -Method Post -Body $body -ContentType "application/json" -Headers $headers
```

!!! note

    `GET /info` is intentionally unauthenticated so the Home Assistant config flow can validate the connection. It only returns the device name, serial number and capability flags.

The settings, with the key while there is one, are stored in:

```text
C:\ProgramData\HASS.Agent.NET10\settings.json
```

## Windows Firewall

!!! note

    If you used the installer, the firewall rule is already configured. This section is only needed for manual (non-installer) setups. The rule only matters while the API is on: with it off nothing listens on the port.

The local HTTP API listens on TCP port `5115` when it is on. If you run the app without the installer, allow Home Assistant to reach it from your local network:

```powershell
New-NetFirewallRule `
  -DisplayName "HASS.Agent .NET10 Local API" `
  -Direction Inbound `
  -Action Allow `
  -Protocol TCP `
  -LocalPort 5115 `
  -Profile Private
```

Keep your Windows network profile set to **Private** for your home LAN. Avoid opening this port on Public networks.
