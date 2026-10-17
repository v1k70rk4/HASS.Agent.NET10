---
title: Local HTTP API
description: The local HTTP API of HASS.Agent .NET10 - the notification endpoint on port 5115, the API key, and the Windows Firewall rule.
---

# Local HTTP API

The app runs a lightweight HTTP server on port `5115`. The integration's Local HTTP API mode uses it for notifications, and it serves the device info.

**Endpoints:**

| Method | Path | Auth | Description |
|--------|------|:----:|-------------|
| `GET` | `/info` | | Device info and capabilities |
| `POST` | `/notify` | Bearer | Send a notification |

The `POST /notify` endpoint requires an API key in the `Authorization: Bearer <key>` header. The key is generated on the first launch and shown on the **General** page under **Network**, with a **Copy key** button; the bind address and the port are set there too. Copy it from there when setting up the Local HTTP API integration in Home Assistant.

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

The settings and the API key are stored in:

```text
C:\ProgramData\HASS.Agent.NET10\settings.json
```

## Windows Firewall

!!! note

    If you used the installer, the firewall rule is already configured. This section is only needed for manual (non-installer) setups.

The local HTTP API listens on TCP port `5115`. If you run the app without the installer, allow Home Assistant to reach it from your local network:

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
