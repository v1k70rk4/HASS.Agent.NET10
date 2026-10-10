# HASS.Agent .NET10

![Windows](https://img.shields.io/badge/Windows-10%202004%2B%20%7C%2011-0078D4?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![Version](https://img.shields.io/badge/version-10.9.1--beta.5-orange)
![Home Assistant](https://img.shields.io/badge/Home%20Assistant-MQTT%20%7C%20WebSocket%20API-41BDF5?logo=homeassistant&logoColor=white)
![License](https://img.shields.io/badge/license-MIT-blue)
[![OpenSSF Best Practices](https://www.bestpractices.dev/projects/15316/badge)](https://www.bestpractices.dev/projects/15316)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/v1k70rk4/HASS.Agent.NET10/badge)](https://scorecard.dev/viewer/?uri=github.com/v1k70rk4/HASS.Agent.NET10)
[![Website](https://img.shields.io/badge/website-v1k70rk4.github.io-41bdf5?logo=github)](https://v1k70rk4.github.io/HASS.Agent.NET10/)
[![Ko-fi](https://img.shields.io/badge/Ko--fi-support%20the%20project-FF5E5B?logo=kofi&logoColor=white)](https://ko-fi.com/v1k70rk4)

<img src="docs/images/hass_agent_companion_modern_icon.png" align="right" width="128" alt="HASS.Agent .NET10 icon">

A modern Windows companion app for Home Assistant.

🌐 **[Website & screenshots](https://v1k70rk4.github.io/HASS.Agent.NET10/)**

> ⭐ **Enjoying HASS.Agent?** Please star this repo — and the [Home Assistant integration](https://github.com/v1k70rk4/HASS.Agent.NET10-Integration) too. It helps others find the project and keeps it going!
>
> ☕ If HASS.Agent saved you an evening of tinkering, you can [buy me a coffee on Ko-fi](https://ko-fi.com/v1k70rk4). Everything stays free — it just helps cover things like the code signing certificate.

**HASS.Agent .NET10** began as a fork of the classic [HASS.Agent](https://github.com/LAB02-Research/HASS.Agent) and has grown into a project of its own: a lightweight .NET 10 client built for current Windows desktops. The original client was a .NET 6-era application; this one focuses on a smaller, cleaner runtime, Home Assistant integration via MQTT or WebSocket API, Windows 11-friendly UX, and a split tray app/system service model.

It is designed for Windows PCs you want to observe and control from Home Assistant: media playback, notifications, sensors, shutdown/restart, command buttons, and rich machine state.

The .NET10 line starts at **version 10.0.0**. The classic client is a separate program: if you want to stay with it, it is still available from its own project, and the integration keeps a [`legacy` branch](https://github.com/v1k70rk4/HASS.Agent.NET10-Integration/tree/legacy) for it. Thinking about switching? See [Coming from HASS.Agent](docs/migrating.md).

> **Stable:** [10.9.0](https://github.com/v1k70rk4/HASS.Agent.NET10/releases/latest) &nbsp;·&nbsp; **Beta:** [10.9.1-beta.5](https://github.com/v1k70rk4/HASS.Agent.NET10/releases/tag/v10.9.1-beta.5) &nbsp;·&nbsp; [What changed](#what-changed) &nbsp;·&nbsp; [Full changelog](CHANGELOG.md)

---

## Install

### Requirements

- Windows 10 version 2004 / build 19041 or newer
- Windows 11 recommended
- x64 Windows
- Home Assistant with **MQTT broker** (recommended, e.g. Mosquitto) **or HA API** (WebSocket, e.g. via Nabu Casa)
- The companion Home Assistant integration:
  [v1k70rk4/HASS.Agent.NET10-Integration](https://github.com/v1k70rk4/HASS.Agent.NET10-Integration) —
  **version 10.6.7 or newer**; the sensors added in 10.7.1 need integration **10.7.0** to appear in
  Home Assistant (the agent and the integration are released with matching version numbers, so keep
  them in step)

Windows versions older than Windows 10 2004 are intentionally blocked. The app targets `net10.0-windows10.0.19041.0` and uses modern Windows APIs for notifications, media sessions, services, sensors, and desktop state.

If you download a published self-contained build, you do **not** need to install the .NET runtime separately. If you want to build from source, install the **.NET 10 SDK**.

### Quick Start

1. Install the Home Assistant integration, [v1k70rk4/HASS.Agent.NET10-Integration](https://github.com/v1k70rk4/HASS.Agent.NET10-Integration). It is in the **HACS default store**, so no custom repository is needed: open HACS, search for **HASS.Agent** (or use the button), download it and restart Home Assistant.

   [![Open your Home Assistant instance and open a repository inside the Home Assistant Community Store.](https://my.home-assistant.io/badges/hacs_repository.svg)](https://my.home-assistant.io/redirect/hacs_repository/?owner=v1k70rk4&repository=HASS.Agent.NET10-Integration&category=integration)
2. Install the Windows app with winget:

   ```powershell
   winget install v1k70rk4.HASSAgentNET10
   ```

   Or download the signed installer from [Releases](https://github.com/v1k70rk4/HASS.Agent.NET10/releases) (see [Code Signing](#code-signing)), or [build from source](docs/development.md#build-from-source).
3. Run the installer or start `HASS.Agent.NET10.exe` directly.
4. Open the tray icon and go to settings.
5. On the **MQTT** page, enable MQTT and enter your broker address and credentials. Whoever can log in to the broker can send this PC commands, so keep that login to yourself ([more](docs/connection.md#mqtt-topics)).
   Alternatively, on the **HA API** page, enable the WebSocket connection to Home Assistant (useful for remote access via Nabu Casa or when no MQTT broker is available).
6. On the **Capabilities** page, choose which features are handled by the tray app vs. the service.
7. On the **Sensors** page, enable built-in sensors and add custom sensors.
8. Optionally install the Windows service from the **Service** page.
9. On MQTT the PC turns up on its own under **Discovered** in **Settings → Devices & services** of Home Assistant; click **Add** there. Over the HA API the first PC is added by hand, with the button below and then **HA API (WebSocket)**; once the integration has a device, further PCs on the HA API are discovered as well.

   [![Open your Home Assistant instance and start setting up a new integration.](https://my.home-assistant.io/badges/config_flow_start.svg)](https://my.home-assistant.io/redirect/config_flow_start/?domain=hass_agent)

The integration creates the Home Assistant entities from what the agent advertises, so what you switch on or off in the settings appears and disappears there on its own.

### Installer

The setup package is built with [Inno Setup](https://jrsoftware.org/isinfo.php). It is also what winget installs and upgrades (`winget install v1k70rk4.HASSAgentNET10`, `winget upgrade v1k70rk4.HASSAgentNET10`). winget runs it without its wizard pages, showing only the progress, so the options below keep their defaults on a first install (start on login on, the rest off) and their earlier choices on an upgrade; add `--interactive` to go through the wizard. Options during install:

- **Desktop icon** (optional)
- **Start automatically on login** (default: enabled, sets a registry Run key)
- **Install system service** (optional)
- **Clean install** (removes existing settings, API key, and log files — useful for a fresh start)
- **Who may use it** (first install only): *Only me* or *Every user of this PC*, see [Who May Use It on This PC](docs/features.md#who-may-use-it-on-this-pc)

The installer automatically configures a **Windows Firewall** rule (Private profile, TCP port 5115) for the Local HTTP API, so manual firewall setup is not needed.

During upgrades the installer stops the running tray app, stops the system service if installed, replaces the files, reinstalls/starts the service, and restarts the tray app. On uninstall the firewall rule is removed automatically, and you are asked whether the settings (with the tokens and passwords in them) should go too.

---

## Features

| | What it does | Details |
|---|---|---|
| <a id="notifications"></a>**Notifications** | Home Assistant notifications as Windows notifications or in the app's own always-visible window, with pictures, buttons and text fields; what is pressed and typed comes back to Home Assistant as an event. | [Notifications](docs/features.md#notifications) |
| <a id="media-player"></a>**Media player** | The active Windows media session as a `media_player`: title, play / pause, skip, seek, volume, TTS. | [Media Player](docs/features.md#media-player) |
| <a id="sensors"></a><a id="built-in-sensors"></a>**Sensors** | Built-in system sensors (CPU, memory, drives, network, battery, session, GPU, camera and microphone in use, and more), each with its own polling profile. | [Sensors](docs/sensors.md) |
| <a id="custom-sensors"></a>**Custom sensors** | Your own: a process, a service, a drive, an attribute of a built-in sensor, the output of a command or script, a LibreHardwareMonitor value. | [Custom Sensors](docs/sensors.md#custom-sensors) |
| <a id="system-commands"></a>**System commands** | Lock, sleep, monitor off, volume, shutdown, restart as buttons and a service call; Hibernate and Log off (off by default). | [System Commands](docs/commands.md#system-commands) |
| <a id="custom-commands"></a>**Custom commands** | Your own buttons: programs, PowerShell scripts, key presses, links, popup windows. | [Custom Commands](docs/commands.md#custom-commands) |
| **Dashboard popup** | A Home Assistant dashboard, or any web page, in a small window above the tray. | [Dashboard Popup](docs/features.md#dashboard-popup) |
| **Display and audio** | The screen as a light in Home Assistant, the default audio device as a select, the volume of a single app. | [Display and Audio](docs/features.md#display-and-audio) |
| **Hotkeys** | A named key combination becomes an event in Home Assistant, to start automations from the keyboard. | [Hotkeys](docs/features.md#hotkeys) |
| <a id="windows-service"></a>**Windows service** | The same program as a system service, for what should work with nobody logged in. | [Windows Service](docs/features.md#windows-service) |
| <a id="updating-from-home-assistant"></a>**Updates from Home Assistant** | An update entity with a working Install button; silent when the service is installed; only installers signed by the publisher are run. | [Updating](docs/features.md#updating-from-home-assistant) |
| **Who may use it** | The settings, with the Home Assistant token and the commands, for every user of the PC or only for the ones you tick. | [Who May Use It](docs/features.md#who-may-use-it-on-this-pc) |
| <a id="danger-zone"></a>**Danger Zone** | Opt-in toolbox: MQTT cleanup, live monitor, debug log, backup / restore, factory reset, beta updates. | [Danger Zone](docs/features.md#danger-zone) |
| <a id="connection-modes"></a><a id="home-assistant-integration"></a>**Connection** | MQTT, the Home Assistant WebSocket API, or both with automatic failover; a notification-only local HTTP API as a fallback. | [Connection Modes](docs/connection.md#connection-modes) |

<p align="center"><img src="docs/images/ha-device.png" width="800" alt="A Windows PC as a device in Home Assistant: media player, audio output select and command buttons"></p>

<p align="center"><img src="docs/images/ui-general.png" width="700" alt="General settings page"></p>

## Documentation

- [Features in detail](docs/features.md): notifications, media player, dashboard popup, display and audio, hotkeys, Windows service, updates, who may use it, Danger Zone
- [Commands](docs/commands.md): system commands and custom commands, with the key names
- [Sensors](docs/sensors.md): the built-in sensors, their attributes and polling profiles, custom sensors
- <a id="mqtt-topics"></a><a id="ha-api-websocket-events"></a><a id="local-http-api"></a><a id="windows-firewall"></a>[Connecting to Home Assistant](docs/connection.md): connection modes, MQTT topics, HA API events, the local HTTP API, the firewall rule
- <a id="build-from-source"></a><a id="minimal-development-setup"></a>[Building and development](docs/development.md): build from source, GitHub Actions, development setup
- [Coming from HASS.Agent](docs/migrating.md): what is the same, what is different, how to switch
- [Contributing](CONTRIBUTING.md): reporting bugs, sending pull requests
- [Changelog](CHANGELOG.md): every release
- [Home Assistant integration](https://github.com/v1k70rk4/HASS.Agent.NET10-Integration): entities, services, events

---

## Code Signing

Starting with 10.6.8, the release assets (`HASS.Agent.NET10-Setup-<version>.exe`, its uninstaller, and `HASS.Agent.NET10.exe` in the zip) are signed with a Certum *Open Source* code signing certificate:

| | |
|---|---|
| Issued to | `Open Source Developer Viktor Révész` |
| Issued by | `Certum Code Signing 2021 CA` |
| SHA-1 thumbprint | `0C09522639318AF1069507568599DFC5B7F86EE4` |
| Timestamp | `time.certum.pl` (the signature stays valid after the certificate expires) |

To check a download, open its **Properties → Digital Signatures** tab, or from the Windows SDK:

```powershell
signtool verify /pa /v HASS.Agent.NET10-Setup-10.6.8.exe
```

Signing happens on the maintainer's machine through SimplySign, unlocked with a one-time code from the SimplySign app; the private key lives in Certum's cloud HSM and is never exported. The GitHub Actions build itself is unsigned — the signed files replace its assets on the release. A certificate this new has no SmartScreen reputation yet, so Windows may still show a *"Windows protected your PC"* prompt for a while; the publisher name on that prompt is what confirms the file is genuine.

From 10.9.0 on, every release also carries a [Sigstore](https://www.sigstore.dev/) signature per asset (`<asset>.sigstore.json`). A workflow of this repository makes it once the release is out: it checks that the file carries the Certum signature above, then signs it with the workflow's GitHub identity, without any key to keep. It shows that the file is the one this repository published, and it can be checked on any system with [cosign](https://docs.sigstore.dev/cosign/system_config/installation/):

```bash
cosign verify-blob HASS.Agent.NET10-Setup-<version>.exe \
  --bundle HASS.Agent.NET10-Setup-<version>.exe.sigstore.json \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity-regexp '^https://github\.com/v1k70rk4/HASS\.Agent\.NET10/\.github/workflows/sign-release\.yml@'
```

## Privacy Policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

HASS.Agent .NET10 communicates only with:

- the **Home Assistant instance and/or MQTT broker configured by the user**, to provide its core functionality,
- the **GitHub API** (`api.github.com` / `github.com`), to check for application updates and download release assets.

No telemetry, analytics, or usage data is collected or transmitted.

---

## What Changed

### 10.9.1-beta.5

> **Beta.** Out on the beta channel: tick **Beta updates** on the Danger Zone page to be offered it, or take it from the [releases page](https://github.com/v1k70rk4/HASS.Agent.NET10/releases). The current stable release is **10.9.0**, below.

The fifth beta of 10.9.1: fixes from a security review of the app, its installer and the release flow, most of them about other users of the same PC. Nothing changes in how the app is used. Works with the integration **10.6.7** or newer; there is no new integration with this one. Signed release.

**New since beta.4**

- **An update no longer approves new commands for the service.** Updating the app or installing the service approved whatever the settings asked the service to run at that moment. On a PC where every user may use the settings, any of them could put a command there and have it run as SYSTEM after the next update. Now an update keeps only what an administrator approved before, unchanged. Something new is approved only from the prompt after the settings are saved, and only exactly what that prompt showed; the prompt now lists what each command runs. An update from 10.9.0 or older, which had no approvals yet, still approves the current settings once.
- **The settings folder can no longer be taken over.** Every user may create a folder in ProgramData, so another user could have made HASS.Agent's folder before the first install, or made it a link to a folder of their choosing, whose access rule the installer would then have set. Before setting the rule the app now removes such links and takes over what another user owns. Users may change what is in the folder, but no longer delete or replace the folder itself.
- The Windows service downloads an update into a new folder that only administrators can use. On older Windows 10 its temp folder let other users get at the installer between the signature check and the start.
- Setup starts the app as the user who ran it, not with administrator rights.
- An update installer whose signing certificate has been revoked is not run.
- Notification pictures: at most two downloads at a time, none from this PC itself or from 169.254.x.x, redirects checked one by one, nothing over 50 megapixels.
- MQTT messages over 1 MB are ignored.
- A command sensor's command goes to Home Assistant as a hash instead of its text: the device message stays on the broker, where any client can read it.
- The **Test** button of the editor says when only the service is ticked: the test runs as you, the service runs it as SYSTEM.
- Uninstalling asks whether to remove the settings, with the tokens in them.
- [Connecting to Home Assistant](docs/connection.md#mqtt-topics) says why the MQTT login is worth keeping to yourself: whoever can log in to the broker can send the PC commands.

**From beta.4**

- **The HA API no longer needs an administrator's token.** The connection used Home Assistant's `fire_event` and `subscribe_events`, which only an administrator may use, so the token on the PC was one that can change everything in Home Assistant, and a PC that was broken into gave it away. With the integration 10.9.1 or newer the app uses the integration's own commands instead, which work with a token of a Home Assistant user who is not an administrator; an administrator approves such a PC once in Home Assistant. The app also only gets the commands meant for itself now, not those of every PC. An administrator's token keeps working, and with an older integration the app connects as before. **The easy way there:** when the app connects with an administrator's token, its window offers to create a Home Assistant user for this PC (not an administrator, without a password) and switches to its token; the HA API page shows whose the token is and has the same button. How it works, and the way by hand: [A Home Assistant user for the PC](docs/connection.md#a-home-assistant-user-for-the-pc).
- **The offer comes right after an administrator's token is saved**, not only the next time the window opens: a new install, where the token is put in for the first time, is where it helps most.
- **Fixed: after a silent first install (winget, a deployment tool) the app never asked who may use HASS.Agent on this PC.** The installer recorded "every user" as a choice nobody had made. It now leaves the settings open without recording anything, and the app asks the first time its window is opened on a PC with a user who is not an administrator.
- The question who may use HASS.Agent on this PC now names what is at stake: the MQTT password, which opens the whole broker, and what the Home Assistant token can do. When both questions are due, the window asks one per opening: the user of the PC's own first.

**From beta.3**

- **A hotkey is pressed, not typed, and you see at once whether it is free.** In the hotkey editor the combination is pressed in its field (Backspace clears it). The line under it says straight away whether Windows lets HASS.Agent have it, or whether Windows or another program already uses it (the Win+number keys are the taskbar's, for one); before, that only showed in the log. It also says when another hotkey of the list has the same combination, and warns about one modifier alone (ctrl+c, alt+f4), which programs use themselves. A taken combination is kept only when OK is pressed twice; the app then tries it again every minute, so it starts working once the other program lets it go. A combination Windows keeps for itself does not even show up in the field.
- The service no longer restarts its connection after an update. The tray app records the version it now runs in the settings, and the service took that as a change of its own settings.

**From beta.2**

- **A Home Assistant address typed without `http://` or `https://` gets https unless it points into the home network.** It always got plain http before, so an address on the internet (a Nabu Casa one, your own domain) sent the token unencrypted, or did not work at all. A `.local` name, a name without a dot, a private IP address and `localhost` still get http, as Home Assistant answers there by default; for anything else, plain http has to be typed.
- **Limits on what LibreHardwareMonitor sends.** The answer is read up to 4 MB as it arrives, at most 5000 values are kept, and names are cut at 200 characters, so a broken or hostile server cannot make the app hold an unbounded amount of memory. A real one sends a few hundred values in tens of kilobytes.
- Old notification pictures are cleaned up once an hour instead of on every notification with a picture.

**From beta.1**

- **Fixed: a user of the PC could have a command run as SYSTEM.** The settings live in ProgramData, where every user of the PC can write, and the Windows service (SYSTEM) ran the custom commands and command sensors set for it there. The service now runs one only when an administrator has approved it: `service-policy.json` next to the program, which only administrators can write, approves each by its id and what it runs. Installing or updating the app approves what the settings already ask the service to run, so nothing stops working; a command or command sensor added or changed for the service later asks for administrator approval (UAC) when the settings are saved. Until then the service leaves it to the tray app, and the log says which ones. Found by CodeRabbit's AI Deep Scan.
- **Who may use HASS.Agent on this PC.** Its settings, with the Home Assistant token, the MQTT login and the commands it runs, could be read and changed by every user of the PC. They can now be limited to some users: a first install asks (*Only me* or *Every user of this PC*), and on a PC that already has HASS.Agent the app asks once, when it is opened, with the users of the PC to tick; it is also under Danger Zone, *Users of this PC*. Limiting needs an administrator (UAC) and uses a local group, *HASS.Agent Users*, that an administrator can also manage with Windows' own tools; the ticked users get access at once, without logging on again. Someone left out gets a note once, and HASS.Agent does not start for them. On a PC whose users are all administrators the update limits the settings to them by itself, which takes nothing from anyone and keeps out a user added later. On a PC with a user who is not an administrator nothing changes by itself: Home Assistant gets a notification once, and the question comes when someone opens the HASS.Agent window, never at logon.
- **An update installer runs only when it is signed by the publisher.** The app downloaded the installer of a new release and started it without checking it, elevated, and from the service as SYSTEM. It now asks Windows to check the Authenticode signature first (unchanged since signing, a trusted certificate chain, a valid timestamp) and requires the signer to be the publisher of the releases (*Open Source Developer Viktor Révész*, issued by *Certum Code Signing 2021 CA*). Anything else, a damaged download or a release file replaced by someone else, is deleted and the update stops with the reason in the log and in Home Assistant. The publisher is checked by name rather than by certificate, so a renewed certificate keeps updates working.
- **The local HTTP API compares the API key in constant time**, so the time an answer takes says nothing about a guessed key, and it lets nothing in if the key were ever missing from the settings (it is always generated, so this changes nothing in practice).
- **Switched-off sensors stay at home.** A built-in sensor switched off on the Sensors page was only left out of what Home Assistant builds entities from; its value still went to the broker or the HA API with every update, the title of the active window and the logged-in user included. Only the sensors switched on for the tray app or the service are sent now, plus what the display light and the audio selects need.
- **Notifications and media commands over the HA API follow their switches.** With notifications or the media player switched off, MQTT does not even subscribe to them, but over the HA API they were still shown or carried out. They are ignored there too now.
- **Windows tools are started by their full path** (`sc.exe`, `schtasks.exe`, `shutdown.exe`, PowerShell and others). A bare name is looked up in the current folder first, so the app started elevated from a folder someone else can write to could run a planted copy.
- **The update prompt only links to GitHub.** Links inside the release notes are shown as text, and only an https page on github.com is opened.
- **The log cannot be fooled by line breaks** in text that comes from outside (a notification, an MQTT message): the lines after the first are indented, so they never look like a record of their own.
- The release signatures (Sigstore) are only made after every file in the zip has been checked: the app with the Certum signature, the bundled WebView2 and ASP.NET Core libraries with Microsoft's, and nothing else that could run.

### 10.9.0

A bigger release than the step in the version number suggests: notifications rebuilt on Windows notifications, the display as a light, audio device selects, hotkeys, per-app volume, hardware sensors through LibreHardwareMonitor, a new website and documentation, and the fixes of three betas. Works with the Home Assistant integration **10.6.7** or newer; the new entities (display light, audio selects, hotkeys, `set_app_volume`) and the notification fields need the integration **10.9.0**. Signed release.

**Notifications**

- **Notifications as Windows notifications, with pictures and text fields.** A notification from Home Assistant is now a real Windows notification by default: it follows Do not disturb, stays in the notification centre, and can carry a picture (`image`), up to five buttons, and text fields (`inputs`) whose content comes back to Home Assistant with the pressed button. The app's own window stays as the other **Notification style** on the Capabilities page, for a notification that is visible whatever Windows is doing; it shows the same picture and text fields, and it no longer takes the keyboard focus when it appears. A single notification can pick its style with `style: window` or `style: toast`, so the doorbell can always use the window. Until now a notification without buttons was a tray balloon and one with buttons the app's window. The picture can be a web address; with the integration 10.9.0 also a path on Home Assistant or a camera entity. See [Notifications](docs/features.md#notifications).
- **The notification action in Home Assistant has fields of its own** (integration 10.9.0). `image`, `actions`, `inputs`, `style` and `duration` no longer have to be written as YAML inside the action's `data` object: each has an input of its own in the Home Assistant editor. The old form keeps working. See [Notifications](docs/features.md#notifications).
- **The app's own notification window: the app icon in its title bar, and the hint of a text field above the field.** The hint used to be a placeholder inside the field, which disappears as soon as the field has the focus.

**New in Home Assistant** (integration 10.9.0)

- **The display as a light in Home Assistant**. Turn on the new **Display brightness** sensor (tray app, off by default) and the PC gets a *Display* light: its brightness slider sets the screen brightness, off switches the monitor off, on wakes it. It works with what Windows itself offers: the built-in panel of a laptop, and external monitors that speak DDC/CI. With an older integration the sensor does nothing. With no adjustable display (many TVs, some docks) the light is a plain on/off one, without the slider.
- **Choose the audio device from Home Assistant**. The *Audio output device* sensor now comes with a select that makes another playback device the default, for "switch to the headset" or "sound to the TV" automations. The new **Audio input device** sensor (off by default) does the same for the recording device.
- **Hotkeys that reach Home Assistant**. On the Capabilities page you can list key combinations with a name (`ctrl+alt+h` as "Meeting"); pressing one sends an event to Home Assistant, where the device's new *Hotkeys* event entity triggers automations on it. Only the listed combinations are registered with Windows, nothing else that is typed is seen.
- **Per-app volume from Home Assistant**. Turn on the new **Audio sessions** sensor and the apps in the Windows volume mixer show up as its attributes (name, volume, muted, playing); the integration's `hass_agent.set_app_volume` service sets the volume or mutes one of them, for "turn the game down when the doorbell rings". The state is how many apps play right now.
- **Hibernate and Log off commands.** Two more buttons for Home Assistant, off by default on the Capabilities page.
- **Hardware sensors through LibreHardwareMonitor.** A new custom sensor type reads CPU and GPU temperature, fan speed, voltage and load from a running [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (its Remote Web Server). The editor lists every value it reports and fills in the unit; a *Connection...* button sets the address and the login if it asks for one. The agent itself stays free of vendor-specific hardware code. See [Custom Sensors](docs/sensors.md#custom-sensors).

**Updates**

- **The service reports a finished update.** After an update installed with nobody logged in, the "updated from X to Y" notification used to wait for somebody to log in, because only the tray app sent it. The service now sends it when no tray app is running.
- **The update report also arrives after a downgrade.** The installer now tells the service which version it replaced, so the "updated from X to Y" notification is sent when the previous version was older than 10.9.0, or was installed over a newer one.
- **The update prompt of the About page is a proper dialog.** The version found and the installed one on top, the release notes as readable paragraphs under a "What's new" expander (the hard-wrapped lines of the tag message are joined again, long notes cut at a paragraph), a link to the full notes on GitHub, and **Download / Not now** buttons in the app's language; the message box used before got its buttons from Windows, so an English app asked "Igen / Nem" on a Hungarian PC. The dialog after the download asks **Install now / Later** the same way.
- A failed update check (right after boot the service often asks before the network can resolve names) is tried again after a minute, three times at most, instead of waiting six hours. The log line says what failed and that it will try again; the earlier "Unable to publish update state" was misleading, the state did go out.

**Fixes**

- **Fixed: stale data in Home Assistant after its restart, on a PC that only uses the HA API.** What the client sends once on connect (its discovery, the service status, the update state) reached Home Assistant before the integration was listening, and the HA API keeps nothing for latecomers. The device then showed an old version and an unavailable update entity. The client now sends these again when the integration asks (integration 10.9.0), and the tray app sends its discovery again every ten minutes on its own.
- **Fixed: a message could get lost on the HA API when two were sent at the same moment.** Home Assistant wants message ids that grow from one message to the next; two senders could pass each other between taking an id and sending, and the later one was refused with *id_reuse*. Ids are now taken in the order the messages go out.
- **Fixed: the Boot time sensor changed every few seconds.** The service and the tray app both publish it, each computed it from its own clock a few milliseconds apart, and Home Assistant recorded the two values, rounded to different seconds, as a change on every publish. Both now report the boot time the kernel keeps, which is the same in both.
- **Quieter log without an audio output.** A PC with no default audio output (an HDMI output whose screen is off) logged three warnings on every poll. It is now logged once when it starts and once when a device is back.
- When the broker closes the MQTT session over something it was sent (a malformed packet, a protocol error), the log now lists the last packets that went out, with their time, topic and size.
- **With MQTT switched off, the log says what happens.** A failed connection to Home Assistant is logged as what it is (*HA WebSocket connection failed*) and once, not as an MQTT failure followed by a failed failover, and the service no longer warns that it "will stay idle" when it connects over the HA API right after.

**Around the app**

- **A *Star on GitHub* button on the About page**, next to the coffee one. The only place the app asks for it.
- **New website and documentation.** The README is short now and the details live under `docs/`: [Features](docs/features.md), [Commands](docs/commands.md), [Sensors](docs/sensors.md), [Connection](docs/connection.md) and a [Coming from HASS.Agent](docs/migrating.md) page for people who know the classic client. The [website](https://v1k70rk4.github.io/HASS.Agent.NET10/) shows what Home Assistant sees and what the notifications look like.

<details>
<summary><b>10.8.0</b></summary>

### 10.8.0

Works with the Home Assistant integration **10.6.7** or newer. Signed release.

- **Custom commands and custom sensors get an editor window.** Adding one, or double-clicking a row, opens a window with room for a long command line, a description and an example for the chosen type, a *Browse* button for programs and scripts, and a *Test* button: a command runs right away and says what went wrong if it did, a sensor shows the value it would report. For sensors the parameter field also lists what the PC has: the running processes, the installed services, the drives, or the attributes of the built-in sensors. The tables on the settings pages are now just the overview, with the ticks still one click away.
- **Dashboard popup (WebView).** A Home Assistant dashboard, or any web page, in a small window above the tray: set its address and size on the **Capabilities** page, then open it from the tray menu, or with a click on the tray icon if you tick that. It closes when you click elsewhere, and nothing is loaded while it is closed. There is a matching custom command type, *Popup window (WebView)*, that shows a page in a window of its own, for example a camera when the doorbell rings. Both use the WebView2 runtime that ships with Windows 11; the login is kept per Windows user. Off until an address is set.
- **Two new custom command types: *Key press* and *Open address*.** A custom command can now press keys (`win+r`, `ctrl+shift+esc`, `alt+tab`, media and browser keys, or several combinations in a row) or open a link in the default browser, without a script written for it. Both show up in Home Assistant as buttons like any other custom command, and both run in the tray app, since they act on the desktop of the logged-in user. The key names are listed under [Custom Commands](docs/commands.md#custom-commands).

</details>

Older versions are in the [changelog](CHANGELOG.md).

---

## Status

This is a project of its own, not the original LAB02 release line, and it is not affiliated with it. It began as a fork of HASS.Agent and has been developed independently since.

The goal is a focused Windows/Home Assistant companion that keeps the useful HASS.Agent ideas, drops legacy weight, and is built around .NET 10, MQTT or the HA API, and a tray app/service split. The classic client stays with its own project; the integration keeps a [`legacy` branch](https://github.com/v1k70rk4/HASS.Agent.NET10-Integration/tree/legacy) for it.

## License

MIT
