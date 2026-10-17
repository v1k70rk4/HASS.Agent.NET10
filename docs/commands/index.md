---
title: System commands
description: Control a Windows PC from Home Assistant with HASS.Agent .NET10 - lock, sleep, hibernate, log off, monitor off, volume, shutdown and restart as buttons and as a service call.
---

# System commands

Control the PC from Home Assistant through button entities or the `hass_agent.execute_command` action. Each command has a **Tray** and a **Svc** box on the **Capabilities** page, under **System commands**; a command that is ticked for neither has no button in Home Assistant.

| Command | What it does | Default | Service capable |
|---------|------------|:---:|:-:|
| `lock` | Lock the workstation | on | |
| `sleep` | Suspend | on | |
| `hibernate` | Hibernate | off | |
| `logoff` | Log off the user | off | |
| `monitor_off` | Turn off the monitors | on | |
| `volume_up` | Volume +5 % | on | |
| `volume_down` | Volume -5 % | on | |
| `toggle_mute` | Toggle mute | on | |
| `shutdown` | Shut down, with a delay | on | yes |
| `restart` | Restart, with a delay | on | yes |
| `restart_cancel` | Cancel a pending shutdown or restart | on | yes |

Shutdown and restart take a delay, a force mode and a comment that Windows shows:

```yaml
action: hass_agent.execute_command
data:
  device_name: MY-PC
  command: restart
  force: true
  time: 30
  comment: "Restarted from Home Assistant"
```

Cancel a pending shutdown or restart:

```yaml
action: hass_agent.execute_command
data:
  device_name: MY-PC
  restart_cancel: true
```

When the Windows service is online and handles the command, the integration routes it to the service; otherwise the tray app runs it. Shutdown and restart from the service work with nobody logged in; the rest need the user's session. The [media player](../features/media-player.md) can also turn the PC off, with the same commands, when that is switched on in the integration's options.

Your own commands, as buttons: [Custom commands](custom.md).
