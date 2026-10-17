---
title: Display and audio
description: The screen of a Windows PC as a light in Home Assistant, the default audio output and input device as selects, and the volume of a single app, with HASS.Agent .NET10.
---

# Display and audio

> From 10.9.0. The Home Assistant side needs the integration 10.9.0 or newer.

These come with sensors of the **Sensors** page: switch the sensor on and the matching entity appears in Home Assistant.

## The display as a light

Turn on the **Display brightness** sensor (tray app, off by default) and the PC gets a *Display* light in Home Assistant: its brightness slider sets the screen brightness, off switches the monitor off, on wakes it. It works with what Windows itself offers: the built-in panel of a laptop, and external monitors that speak DDC/CI. With no adjustable display (many TVs, some docks) the light is a plain on/off one, without the slider.

## The default audio device as a select

The **Audio output device** sensor comes with a select that makes another playback device the default, for "switch to the headset" or "sound to the TV" automations. The **Audio input device** sensor (off by default) does the same for the recording device.

## Per-app volume

Turn on the **Audio sessions** sensor and the apps in the Windows volume mixer show up as its attributes (name, volume, muted, playing); the state is how many apps play right now. The integration's `hass_agent.set_app_volume` service sets the volume or mutes one of them:

```yaml
action: hass_agent.set_app_volume
data:
  device_name: MY-PC
  app: spotify
  volume: 30
```

The *Audio sessions* sensor lists the apps with their volumes as attributes; an attribute sensor on one of them gives the app its own entity ([Custom sensors](../sensors/custom.md)).

## Volume and mute

The PC's own volume is on the [media player](media-player.md), with the **Volume** and **Muted** sensors next to it, and the `volume_up`, `volume_down` and `toggle_mute` [system commands](../commands/index.md) as buttons. The **Microphone muted** sensor tells about the default recording device.
