---
title: Sensor attributes
description: The attributes of the built-in sensors of HASS.Agent .NET10 - network adapters, displays, GPU adapters, drives, event log entries, sleep blockers - and how any of them becomes a Home Assistant entity of its own.
---

# Sensor attributes

Some sensors have a simple primary state but expose richer details as attributes. Any of these can be extracted into a separate Home Assistant entity with the `built_in_attribute` custom sensor type (see [Custom sensors](custom.md)), or with the **+** button next to the sensor's name on the Built-in sensors tab, which offers every attribute path the PC has.

| Sensor | Attributes |
|--------|-----------|
| **LAN IP** | `addresses[N].adapter`, `addresses[N].description`, `addresses[N].address` |
| **Active displays** | `displays[N].name`, `displays[N].primary`, `displays[N].width`, `displays[N].height`, `displays[N].x`, `displays[N].y` |
| **Recent Event Log errors** | `window_minutes`, `events[N].log`, `events[N].provider`, `events[N].event_id`, `events[N].level`, `events[N].created_at` |
| **Last shutdown reason** | `reason`, `event_id`, `created_at`, `message` |
| **Last wake reason** | `source`, `kind` (`modern_standby`, `sleep`, `hibernate`, `fast_startup`), `created_at`, `duration_seconds`, `sleep_entered`, `detail` |
| **Sleep blocked** | `system_required`, `display_required`, `away_mode_required`, `primary_blocker`, `blockers[N].category`, `blockers[N].type`, `blockers[N].name`, `blockers[N].reason`, `blockers[N].blocking` |
| **GPU usage** | `name` (the GPU the main value follows), `engines.<type>` (e.g. `engines.3d`, `engines.videodecode`), `npu_usage`, `memory_dedicated_gb`, `memory_shared_gb` (and the same in `_mb`), `adapters[N].name`, `adapters[N].memory_gb`, `adapters[N].usage`, `adapters[N].engines.<type>`, `adapters[N].memory_dedicated_gb`, `adapters[N].memory_shared_gb` (and `_mb`) |
| **Memory usage** | `total_gb`, `used_gb`, `available_gb` |
| **System drive free % / (GB)** | `drive`, `total_gb`, `used_gb`, `free_gb` |
| **Drive space** | `system` (the system drive's letter, the one the value is about), and per drive by its letter: `<letter>.label`, `<letter>.file_system`, `<letter>.removable`, `<letter>.total_gb`, `<letter>.used_gb`, `<letter>.free_gb`, `<letter>.used_percent`, `<letter>.free_percent` (e.g. `D.free_gb`) |
| **Drive health** | `temperature` (the system drive's), `drives[N].name`, `drives[N].health`, `drives[N].bus`, `drives[N].media`, `drives[N].temperature`, `drives[N].temperature_max`, `drives[N].power_on_hours`, `drives[N].wear`, `drives[N].system` |
| **Audio sessions** | `apps[N].app`, `apps[N].volume`, `apps[N].muted`, `apps[N].active` |
| **Camera in use** / **Microphone in use** | `apps[N]` |

**Drive health** is what Windows keeps about the drive (the Storage Management API, read by the service). The value is Windows' verdict from the SMART data, the worst among the drives: `healthy`, `warning`, `unhealthy`, `unknown`. NVMe drives report the temperature and its highest value, a SATA drive the temperature and the power-on hours when its controller passes them on, USB enclosures nothing; a field the drive does not report is left out. The system drive comes first.

**GPU usage with more than one GPU** (an integrated one and a graphics card, for example): the sensor's value and the top-level `engines`, `memory_dedicated_mb` and `memory_shared_mb` follow the busiest GPU at each reading, so they can move from one GPU to the other. For one GPU of your choice use its own values under `adapters[N]` (10.10.0+): `adapters[1].usage`, `adapters[1].engines.3d`, `adapters[1].memory_dedicated_mb`. `adapters[N].name` tells which is which; the order is the one Windows lists the display adapters in, and does not change between readings.

**LAN IP** is the address of the adapter Windows routes through, the one with the default route (10.10.0+). `addresses[N]` lists every connected adapter, that one first, then the ones with a gateway, then virtual adapters such as VirtualBox, Hyper-V, WSL or Docker. Before 10.10.0 it was simply the first adapter Windows listed, often a virtual one.

## From the attribute list to an entity

The **+** button next to a sensor's name (Built-in sensors tab) and the editor's list offer every attribute path this PC has: `adapters[1]`, `displays[1]`, `drives[1]` and so on for every item the sensor last reported, not only `[0]`. A sensor made from the list gets its unit (°C for a temperature, h for hours, GB or MB for memory, % for a load), and with it a device class in Home Assistant, so a °C sensor shows as °F where that is the custom. The second item's sensor gets a 2 after its name. The box of a role that cannot read the sensor is greyed.

A custom attribute sensor on a faster profile than its built-in sensor refreshes the shared read at its own pace, while the built-in sensor keeps showing Home Assistant its value and attributes at its own: an hourly GPU usage stays hourly in the recorder even with one adapter's load wanted every second. [Polling profiles](profiles.md) has the details.
