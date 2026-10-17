---
title: Polling profiles
description: How often HASS.Agent .NET10 reads and sends its sensors - the push, fast, normal, hourly and startup profiles, their intervals, the profile per sensor, and what fast sensors mean for Home Assistant's recorder.
---

# Polling profiles

Sensors are refreshed by profile instead of one global interval. The intervals are set on the **Capabilities** page, and each built-in sensor follows one of them:

| Profile | Default | What it is for |
|---------|---------|-------------|
| **push** | on change | Event-driven: reports within about 600 ms of a change (monitor power, session lock, power source, volume, mute, audio device). A poll still runs as a safety net. *Last wake reason* follows the event log and is published with the next fast cycle. |
| **fast** | 10 s | CPU, memory, the active window and the like. Down to 5 seconds on the Capabilities page; 1–4 seconds after the **Fast sensors below 5 seconds** switch on the [Danger Zone](../features/danger-zone.md) page (10.9.3+). |
| **normal** | 60 s | Drives, battery, network, Bluetooth, the GPU and so on. |
| **hourly** | 3600 s | Windows Update, the Event Log. |
| **startup** | once | Boot time, last shutdown reason. |

The minimum is 10 seconds for the normal and hourly profiles.

## The profile per sensor

> From 10.9.3.

The **Profile** column of the Built-in sensors tab is a choice: fast, normal or hourly for every sensor that is polled, so GPU usage can go to fast on a gaming PC and CPU usage to normal on a server. Push-driven and startup sensors keep theirs. The three intervals stay global; the choice is which one a sensor follows. [Built-in sensors](index.md) lists the defaults.

A custom [attribute sensor](custom.md#built_in_attribute) has a profile of its own. On a faster profile than its built-in sensor it refreshes the shared read at its own pace, while the built-in sensor still shows Home Assistant its value and attributes at its own: an hourly GPU usage stays hourly in the recorder even with one adapter's load wanted every second.

## Push events and the rest

A push event (a volume change, a lock, a media event) refreshes the push-driven sensors only; the clock-driven ones keep their own pace (10.9.3+). So an evening of media does not turn the uptime or the GPU into fast sensors.

## What fast means for Home Assistant

Every cycle sends the whole sensor state, and Home Assistant's recorder writes a row whenever a value or an attribute changes: a value that changes every second (CPU usage) lands in the recorder every second, on each role that sends it. That is why the fast profile below 5 seconds sits behind the Danger Zone switch, why Uptime is a normal sensor (its value changes with every tick), and why a sensor ticked for both the tray app and the service is worth a thought: both send it.

Command sensors have a timeout of about 10 seconds per run, so they belong on the normal or hourly profile, not on fast.
