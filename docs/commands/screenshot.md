---
title: Screenshot
description: A picture of the Windows PC's screen in Home Assistant when you ask for one - a button and an image entity, off until you turn it on at the PC.
---

# Screenshot

A button in Home Assistant that makes the PC take a picture of its screen, and an image entity that shows the last one. It is for a wall panel or a kiosk PC you want to look at from somewhere else, or for an automation that wants to see what the screen shows.

The PC takes a picture only when it is asked to. It never sends one on its own, and there is no interval to set in the app.

Needs the app and the Home Assistant integration **10.14.0** or newer.

## Turning it on

It is off until you turn it on, at the PC: tick **Take a screenshot** in the **Tray** box on the **Capabilities** page, under **System commands**. Home Assistant cannot switch it on.

Think before you do. A screenshot is whatever is on the screen at that moment, a password manager or a private message included, and whoever may press the button in Home Assistant may look. On a PC people work on, leave it off; on a panel that shows a dashboard all day there is little to give away.

The Windows service has no box for it: it has no desktop to take a picture of.

## In Home Assistant

The PC's device gets two entities:

| Entity | What it is |
|--------|-----------|
| **Take a screenshot** (button) | Asks the PC for a picture now |
| **Screenshot** (image) | The last picture, with the time it was taken as its state |

Both go again when the command is unticked on the PC.

For a picture at regular times, press the button from an automation:

```yaml
automation:
  - alias: A picture of the hallway panel every five minutes
    triggers:
      - trigger: time_pattern
        minutes: "/5"
    actions:
      - action: button.press
        target:
          entity_id: button.hallway_panel_take_a_screenshot
```

On a dashboard, a picture entity card shows it:

```yaml
type: picture-entity
entity: image.hallway_panel_screenshot
```

## What the picture is

- A JPEG of every monitor in one picture, made no wider than 1920 pixels.
- At most 512 KB, or less when the broker allows less. A picture that is larger is made again at a lower quality, then smaller, until it fits.
- At most one a second, however often the button is pressed.

## What is kept of it

- On MQTT the picture is not a retained message: the broker hands it on and keeps nothing.
- Over the HA API it goes to the PC's own image entity and not on Home Assistant's event bus, where everything that listens to all events would get it.
- Home Assistant keeps the last picture in memory only. After a restart of Home Assistant the image is empty until the next one is asked for.

## When there is no picture

- **Nobody is signed in, or the tray app is not running**: the button and the image are unavailable.
- **The screen is locked, or a UAC prompt is up**: Windows does not let the screen be read then. Nothing is sent and the app's log says so.
- **Protected video** shows as a black area, as in any screenshot.
