# Changelog

Every release of HASS.Agent .NET10, newest first. The downloads are on the [releases page](https://github.com/v1k70rk4/HASS.Agent.NET10/releases).

## 10.9.1-beta.1

> **Beta.** Out on the beta channel: tick **Beta updates** on the Danger Zone page to be offered it, or take it from the [releases page](https://github.com/v1k70rk4/HASS.Agent.NET10/releases). The current stable release is **10.9.0**, below.

A security release: what CodeRabbit's AI Deep Scan found in the app, fixed. Mostly invisible, except that on a PC with more than one Windows user the app asks once who may use it. Works with the Home Assistant integration **10.6.7** or newer, no new integration needed; the new entities of 10.9.0 need the integration **10.9.0**. Signed release.

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

## 10.9.0

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

## 10.8.0

Works with the Home Assistant integration **10.6.7** or newer. Signed release.

- **Custom commands and custom sensors get an editor window.** Adding one, or double-clicking a row, opens a window with room for a long command line, a description and an example for the chosen type, a *Browse* button for programs and scripts, and a *Test* button: a command runs right away and says what went wrong if it did, a sensor shows the value it would report. For sensors the parameter field also lists what the PC has: the running processes, the installed services, the drives, or the attributes of the built-in sensors. The tables on the settings pages are now just the overview, with the ticks still one click away.
- **Dashboard popup (WebView).** A Home Assistant dashboard, or any web page, in a small window above the tray: set its address and size on the **Capabilities** page, then open it from the tray menu, or with a click on the tray icon if you tick that. It closes when you click elsewhere, and nothing is loaded while it is closed. There is a matching custom command type, *Popup window (WebView)*, that shows a page in a window of its own, for example a camera when the doorbell rings. Both use the WebView2 runtime that ships with Windows 11; the login is kept per Windows user. Off until an address is set.
- **Two new custom command types: *Key press* and *Open address*.** A custom command can now press keys (`win+r`, `ctrl+shift+esc`, `alt+tab`, media and browser keys, or several combinations in a row) or open a link in the default browser, without a script written for it. Both show up in Home Assistant as buttons like any other custom command, and both run in the tray app, since they act on the desktop of the logged-in user. The key names are listed under [Custom Commands](docs/commands.md#custom-commands).

## 10.7.3

Works with the Home Assistant integration **10.6.7** or newer; the *Check for updates* button and the single update entity need integration **10.7.3**. Signed release.

- **A *Check for updates* button in Home Assistant** (integration 10.7.3+). It sits next to the update entity and makes the agent ask GitHub right away, so a fresh release shows up in Home Assistant without waiting for the six-hourly check or opening the About page. Thanks to [@Taomyn](https://github.com/Taomyn) for the idea.
- **One update entity, on both transports.** Over MQTT the update entity used to come from Home Assistant's own MQTT discovery, and over the HA API from the integration - so a PC that switched transports ended up with two, one of them always unavailable. With integration 10.7.3 or newer the integration builds the entity on MQTT as well, and the agent removes its discovered one (it announces the change on `hass.agent/integration/{id}`, retained; an older integration says nothing there and keeps getting the discovered entity as before). The entity now follows the device rather than the tray app, so it stays available, and installable, while only the service runs.
- **Fixed: installing an update from Home Assistant now works with nobody logged in.** The Install button's request was only picked up by the tray app, which handed the actual install to the Windows service. On a PC that was switched on but not logged in there was no tray app, so the request went unanswered, with nothing in any log. The service now takes the request itself when no tray app is running, and it keeps checking for new releases meanwhile, so the update entity in Home Assistant stays current on a PC nobody is logged in to. When a tray app runs, it handles the request as before, since it also has to bring itself back after the install. Thanks to [@Taomyn](https://github.com/Taomyn) for the report.

## 10.7.2

Works with the Home Assistant integration **10.6.7** or newer (no integration change in this release). Signed release.

- **Fixed: a large album cover no longer takes the MQTT connection down.** The media player published the artwork exactly as Windows handed it over, with no resizing and no size cap. Mosquitto 2.1 (Home Assistant add-on 7.0.0 and newer) rejects any packet over 2 MB by default, and a full-size cover from a browser can exceed that: the broker dropped the connection, the agent reconnected and sent the same picture again, and every entity flapped between `unavailable` and its state for as long as the track played. Artwork over 128 KB or 512 px is now scaled down to 512 px and re-encoded as JPEG before publishing (a few dozen KB; smaller artwork goes out unchanged), the agent honours the packet size the broker advertises on connect, and anything still over the limit is skipped with a log line. The same cap applies on the HA API transport. Thanks to [@j0k34](https://github.com/j0k34) for the precise report.

## 10.7.1

Works with the Home Assistant integration **10.6.7** or newer; the **new sensors need integration 10.7.0** to show up in Home Assistant. Signed release, like every release since 10.6.8 (see [Code signing](README.md#code-signing)).

10.7.1 replaces 10.7.0, which was withdrawn a few hours after it went out; everything below is new compared to 10.6.9.

**Nothing changes for an existing installation.** Every sensor you have stays exactly as you set it. The new sensors arrive switched off, and the "off by default" list below only applies to a fresh install.

- **Five new built-in sensors**, all switched off until you enable them on the *Sensors* page:
  - **GPU usage** — GPU load the way Task Manager shows it, from Windows' own counters, so it works the same on Intel, AMD and NVIDIA without any vendor tool. Attributes: load per engine (`3d`, `videodecode`, `videoencode`, …), NPU load, dedicated / shared memory in use, and the installed adapters with their real memory size (WMI stops at 4 GB). Temperature, clock and fan speed are vendor specific and not part of it — use a [custom sensor](docs/sensors.md#custom-sensors) for those.
  - **Sleep blocked** — `on` while something keeps the machine (or its display) awake: a video playing in the browser, a download, a driver. The attributes name who: `primary_blocker`, and the full `blockers` list with category, type, name, reason and whether it really blocks sleep. Service only — Windows shows the holders to administrators alone.
  - **Last wake reason** — what woke the machine last (`Input Keyboard`, `Power Button`, `Lid`, a device, a wake timer…) with the time, how long it was away and whether it really slept. Works on Modern Standby laptops and on desktops with classic sleep / hibernate. Event driven: the new value is in Home Assistant seconds after the wake, without polling.
  - **Camera in use** / **Microphone in use** — `on` while an app is using the camera or the microphone, with the apps listed in the `apps` attribute. Handy for an "in a meeting" light. Tray app only (Windows keeps this per user).
- **Sensors that are off by default.** Not every sensor is for everyone, so a fresh install no longer creates all of them in Home Assistant: besides the five new ones, *VPN connected*, *RDP sessions*, *Recent Event Log errors*, *Last shutdown reason* and *Clipboard text available* now start switched off. Existing installations are not touched — a setting you already have is never changed by an update.
- **A sensor that is switched off costs nothing.** The more expensive reads (the power request list, the GPU counters, the wake event subscription) only run for a sensor that is enabled, or that a custom attribute sensor is built on.
- **The log file no longer grows forever.** It was never trimmed and could reach hundreds of megabytes after a few months. The log now starts a new file every day (and within a day once it passes 10 MB); older files are kept next to it as `hass-agent-net10-<date>.log` and **removed after 7 days**, or sooner if together they pass 100 MB. The oversized file an earlier version left behind is cleaned up on the first start.
- **Sensors arrive faster after startup.** The Windows Update check takes 5–30 seconds and used to hold back *every* sensor for that long — at startup and again once an hour. It now runs on its own in the background: the first sensor values reach Home Assistant in about a second, and *Windows Update pending* follows when its search is done.
- **Attribute sensors get readable names.** A custom sensor created from a built-in sensor's attribute (the **+** on the *Built-in sensors* tab) was named after the raw attribute key — `Last shutdown reason: reason`. The attributes now have proper names in English and Hungarian, and the generated name follows the *Home Assistant language* setting, since it becomes the entity name there. Sensors you already created keep the name they have; rename them on the *Custom sensors* tab if you like.
- **Fixed: a manual update check now reaches Home Assistant.** The agent asks GitHub at startup and every six hours, and only those checks were reported to Home Assistant. The *Check for updates* button on the About page asked GitHub on its own and told nobody - so when a release came out between two checks, the app knew about it while Home Assistant kept showing "up to date" until the next scheduled check or a restart of the tray app. The result of a manual check is now reported the same way. Thanks to [@Taomyn](https://github.com/Taomyn) for the report.
- **Fixed: crash on exit.** Closing the tray app (also when an update closes it) could end in an unhandled exception, recorded by Windows as an application error: the media session monitor was stopped from two places at once during shutdown. Nothing was lost, since the app was closing anyway, but it no longer happens.
- **Fixed: starting a service that is already running is no longer an error**, and neither is stopping one that is not. The message Windows gives in those cases (and any other `sc` error) is also readable now on a localized Windows: it was decoded with the wrong code page, so every accented letter came out as `�`.
- **Fixed: a refused MQTT login was logged as `MQTT connected.`** A broker that rejects the credentials answers the connection attempt instead of failing it, and the agent took that answer for success — so the log showed a connection that never existed, and the HA API failover kept flapping between the two transports. A refused connection is now a failed one: it is logged with the broker's reason, and the failover stays on the HA API until the broker really accepts the login.

## 10.6.9

Requires the Home Assistant integration **10.6.7** or newer (no integration change in this release).

- **Installing an update from Home Assistant now works on the HA API (WebSocket) transport with the service installed.** Pressing *Install* started the relaunch watchdog (the brief console window) and posted the "update started" notification, but the actual install command for the service was sent to the MQTT service topic — which, without a broker, was silently dropped. So nothing was installed and the version never changed. The command now travels over the HA API as well, and the service runs the silent install exactly as it does over MQTT.
- Without the service, the installer started from Home Assistant is launched detached from the tray app instead of as its child process, so the installer's own close-the-running-app step can no longer take the installer down with it.

## 10.6.8

Requires the Home Assistant integration **10.6.7** or newer (no integration change in this release).

- **Signed releases.** The installer, its uninstaller and the executable are signed with a Certum code signing certificate issued to *Open Source Developer Viktor Révész*, so Windows no longer shows them as coming from an unknown publisher. See [Code signing](README.md#code-signing) for how to verify a download.
- **The `rdp_sessions` sensor now counts Remote Desktop sessions.** It always reported `0`: the session's client protocol type was read as a 4-byte integer, but Windows returns a 2-byte value, so the protocol type of every session came back unreadable and no session was ever counted as RDP. Thanks to [@ThorgarIV](https://github.com/ThorgarIV) for the report and the fix.
- **Factory reset now actually resets.** Since 10.6.4 the settings file has had a backup and a device-id sidecar so a damaged file cannot lose your configuration — but the *Factory reset* button only deleted the settings file, and the next start quietly restored everything (connection, credentials, serial number) from those. It now removes the backup, the sidecar and any legacy settings the migration would have picked up, so the device really starts over.
- **MQTT drops are logged, with the reason, and the reconnect backs off.** When the broker closed a session right after connecting (a rejected login, a user or ACL removed from Mosquitto, a duplicate client ID) the log showed nothing but `Connecting…` / `MQTT connected.` repeating at full speed. The agent now logs the broker's verdict for a refused connection and the disconnect reason for a dropped one, and waits 5 → 60 s between attempts when sessions keep dying, with a hint about the usual causes.
- **Release notes in the app.** The About page has a *Release notes* button for the installed version, and the update prompt now shows what changed in the new release before asking to download it.
- **Support the project.** A *Buy me a coffee* button on the About page links to [Ko-fi](https://ko-fi.com/v1k70rk4).
- The GitHub links in the app point at the renamed repository (`HASS.Agent.NET10`) instead of relying on the redirect from the old name.

Thanks to [@ThorgarIV](https://github.com/ThorgarIV) for the RDP report and fix, and to [@phuzzyday](https://github.com/phuzzyday) for the migration write-up that surfaced the reset and logging problems.

## 10.6.7

Requires the Home Assistant integration **10.6.7** or newer.

- **The update entity now works on the HA API (WebSocket) transport.** It was built from Home Assistant's own MQTT discovery, so without a broker there was no update entity and no Install button — on the very transport people choose precisely because Home Assistant is not on their local network. The agent now reports the available release over the WebSocket as well, and installs it when asked, so updating from Home Assistant works with or without MQTT.

## 10.6.6

Requires the Home Assistant integration **10.6.6** or newer.

- **Closing the tray app no longer takes the whole device offline.** With the Windows service installed, everything in Home Assistant turned unavailable the moment the tray app was closed or you logged out — even though the service was still running and reporting CPU, memory and disk. Only the tray app ever published the device's availability, so leaving declared the *device* dead rather than just itself. The tray app and the service are now treated as two independent providers: the device stays reachable while either is running, and each entity follows whichever side actually feeds it. Entities that only the tray app can provide (media player, active window, notifications) go **unavailable** rather than disappearing, and come straight back when it starts again.
- **A stopped provider no longer deletes its entities.** The service used to report an empty capability list while offline, so Home Assistant removed its sensors instead of greying them out. It now always reports what it handles, and reports separately whether it is running — so its entities stay put and simply show as unavailable. Turning a capability off in the settings still removes those entities, as it should.

Thanks to [@Taomyn](https://github.com/Taomyn) for the report.

## 10.6.5

Requires the Home Assistant integration **10.6.5** or newer when using the HA API (WebSocket) transport.

- **Custom commands now work over the Home Assistant API (WebSocket) transport.** Pressing a custom command button did nothing when running without MQTT — the log only showed `Unsupported app WebSocket command received: <id>`. Built-in commands were unaffected. Each transport carried its own copy of the "what does this button mean" logic, and only the MQTT one knew about custom commands; all transports now share a single implementation, so they cannot drift apart again.
- **Interactive sensors no longer flicker between a value and blank.** With system sensors enabled for both the tray app and the Windows service, both published a full snapshot to the same topic — but the service cannot see the desktop, so it sent *active window*, *active process*, *foreground app*, *active displays*, *audio output* and *user present* as empty, blanking out what the tray app had just reported, a couple of times a minute. The service now omits what it cannot measure instead of reporting it as empty, so those sensors keep the tray app's values.
- **The system service is now a first-class citizen on the HA API transport.** It had no channel of its own there — unlike MQTT, where the app and the service each advertise what they can handle and Home Assistant merges the two. Over the WebSocket the service announced itself *as the app*, advertising the tray app's commands and then refusing them, so with the tray app closed only commands enabled for both sides happened to work, and every button press left a stray warning in the log. The service now has its own channel, and Home Assistant names the side a command is meant for — so each press runs exactly once, on the right side, whether the tray app is running or not.

Thanks to [@CookSleep](https://github.com/CookSleep) for the report — including the root cause and a suggested fix.

## 10.6.4

Both fixes address the update problems reported in #22.

- **Updates now start on battery.** The one-shot tasks that run an update carried Task Scheduler's default battery conditions ("start only on AC power", "stop when switching to battery"), so on a notebook running on battery the update never started — the tasks just sat *Queued*. The tasks are now created from an explicit definition that allows battery power, and they still remove themselves afterwards.
- **"Clean install" is never remembered and never runs silently.** The installer remembers task selections from previous runs, so ticking *Clean install* once made **every later update** on that machine silently repeat it — wiping the settings and the device id, which is why the PC kept reappearing in Home Assistant as a new device. The tick now always starts unchecked on an upgrade, and an unattended (silent) update can never wipe settings at all.

Thanks to [@AdmiralRaccoon](https://github.com/AdmiralRaccoon) for the report and for methodically confirming both causes — registry value and Task Scheduler conditions included.

## 10.6.3

- **Settings are far harder to lose when an update closes the app.** They were written by truncating the file first and then writing it, so a badly timed force-close could leave it empty. Settings are now written atomically and the previous version is kept as a backup, which is restored automatically if the main file ever turns up missing or unreadable.
- **A lost configuration no longer creates a duplicate device in Home Assistant.** The device serial is what identifies the PC, and it used to live only in the settings file — so losing that file minted a new serial and the machine reappeared as a brand new device (with the old entities left behind on the broker). The serial is now mirrored next to the settings and reused. A deliberate *Clean install* still gives a fresh identity, as it should.
- **One-shot update tasks clean themselves up.** The scheduled tasks used to run an update were left behind in Task Scheduler, where they piled up and invited being run by hand. They now delete themselves after running, and leftovers from earlier versions are removed on start.

## 10.6.2

- Fixed a **`NullReferenceException` in the sensor loop** that left the device permanently **unavailable** in Home Assistant. The Windows service runs with interactive metrics disabled, and those fields fell back to the previous snapshot — which doesn't exist yet on the first read after the service (re)loads, so the very first read threw before a snapshot could be stored, and every cycle after it repeated the same failure. Affected setups where the **service** publishes system sensors; the tray app was unaffected.
- **Entities no longer disappear** from Home Assistant when the app shuts down cleanly. A graceful exit also published an empty capability list, which Home Assistant reads as "this device has nothing left" and deletes the entities. Now only the availability state goes offline, so the entities stay and show as **unavailable** until the device is back — the same as after a crash or network loss. (Turning off both MQTT and the HA API in settings still removes them on purpose.)
- **Sensor reads are now fault-isolated**: a failing metric read falls back to its previous value instead of aborting the whole cycle, and the log names the exact read that failed — so one misbehaving read can no longer take the device offline, and diagnosing one is much quicker.

Thanks to [@Taomyn](https://github.com/Taomyn) for the detailed reports and for testing the beta builds — these were tracked down entirely from his logs and feedback.

## 10.6.1

- Fixed a **`NullReferenceException` in the sensor loop** that could make the device go (and stay) **unavailable** in Home Assistant, typically after startup or resume from sleep. A transient network adapter with a null name/description (common with VPN/virtual adapters mid-initialization) threw inside the network reads, aborting every sensor cycle. The network reads are now null-safe and fault-isolated.
- Fixed **GitHub update-check `403 (rate limit exceeded)`**: the update state was queried on every reconnect, which — with frequent reconnects and several devices behind one IP — exhausted the unauthenticated GitHub API limit. The result is now cached/throttled (at most once per hour outside the 6-hour poll).

## 10.6.0

Stable release of the custom commands & command sensors line.

- **Custom command buttons** — run your own programs or PowerShell/pwsh scripts from Home Assistant. You define what runs; Home Assistant only triggers a command by its id (it can't send arbitrary code). Also handles a full command line typed into the command field (e.g. `taskkill /F /IM app.exe /T`).
- **Command sensors** — a custom sensor whose value is the output of a program or PowerShell script (e.g. GPU temperature via `nvidia-smi`), with an optional **unit** that makes it a numeric `measurement` (graphs & statistics in Home Assistant).
- **Fixed in-app updates** (from the About page) aborting: the installer closes the running app with `taskkill /… /T`, which also killed the installer when it was launched as a child of the app. It now runs detached, so it survives, installs, and relaunches the app. Updating from Home Assistant was unaffected.
- **Update the Home Assistant integration too** — it's now in the **HACS default store**, so no custom repository is needed (search "HASS.Agent").

## 10.5.0

- Fixed a freeze that could stop all reporting when the monitor powered off: multiple audio components held separate WASAPI COM instances and deadlocked during an audio device change (e.g. HDMI audio disappearing). Audio access is now a single, serialized endpoint, and the device-change handler no longer does COM work inside the notification callback.
- The system sensor loop now survives a transient read error instead of stopping, and logs the full stack trace if one occurs.

## 10.4.0 (Yanked)

- Added **event-driven (push) sensor updates**: monitor power state, session lock/unlock, AC/battery power source, and audio (volume, mute, output device, microphone mute) now report to Home Assistant within ~600 ms of changing instead of waiting for the next poll. Rapid changes (e.g. dragging the volume slider) are debounced.
- Added **enum sensor states**: `monitor_power_state`, `power_status`, and `session_state` are now `enum` sensors, so Home Assistant knows their possible values (selectable in automations; `dimmed` is a first-class monitor state).
- Added **device availability**: the device publishes an MQTT availability topic with a Last Will, and a heartbeat over the HA API transport. On a clean shutdown — or a crash / network loss — the entities turn **unavailable** in Home Assistant instead of keeping stale values.
- The **Sensors** page now shows a **Push** profile for push-driven sensors.
- Moved the Bluetooth sensor from hourly to normal polling.

## 10.3.0

- Added one-click updates from Home Assistant: the update entity's **Install** button downloads and installs the new version on the PC — fully silent when the system service is installed, with a UAC prompt otherwise.
- Added persistent notifications to Home Assistant for update progress: started, completed (with version), no installer, or failure.
- Added the opt-in **Danger Zone** tab: maintenance and diagnostics tools behind a checkbox on the General page.
- Added MQTT maintenance: list and delete retained HASS.Agent messages on the broker (per device or all devices).
- Added one-click discovery republish on the active connection (MQTT or HA API).
- Added a live debug log viewer with filtering and a runtime verbose (DEBUG) logging toggle.
- Added a live MQTT monitor for the `hass.agent/#` topics with payload preview.
- Added settings backup/restore to a portable JSON file (machine-bound secrets excluded).
- Added factory reset with double confirmation and automatic app restart.
- Added an MQTT connection test button on the MQTT page (matches the HA API page).
- Added a beta update channel: opt in to receive GitHub pre-releases from the update checker.
- Pre-release versions (`10.3.0-beta.1`) are now handled correctly by the version comparison and shown in the UI.
- The General page warning now also shows when the system service is installed but stopped.
- Fixed input fields overflowing on small window sizes across the General, MQTT, and HA API pages.

## 10.2.0

- Fixed default language set to Hungarian on non-Hungarian systems; the app now auto-detects the OS language and defaults to English.
- Fixed clean install not removing legacy `HASS.Agent.Companion` directories, causing old settings to migrate back.
- Fixed tray icon missing in standalone single-file publish by embedding the icon as an assembly resource.
- Removed the "MQTT not configured" warning from the General page when HA API is enabled.

## 10.1.0

- Added Home Assistant WebSocket API transport for MQTT failover and MQTT-free remote control.
- Added HA API settings, connection testing, HTTP warning tooltip, and setup status banners.
- Added HA API cross-check for the installed HASS.Agent Home Assistant integration version.
- Added Home Assistant update entity publishing and About-page update checks.
- Added multi-value built-in sensor attributes and one-click custom sensor generation.
- Added per-sensor polling profiles: fast, normal, hourly, and startup.
- Added custom sensor value testing without blocking the settings UI.
- Improved Windows Update pending detection and release lookup.
- Improved service/MQTT setup warnings, About page actions, and tray service labeling.
- Switched MQTT topic routing and HA API command targeting to `serial_number` so device renames do not break commands.

## 10.0.0

- Rebuilt the companion client as a modern `.NET 10` Windows app.
- Added a Windows tray app for interactive user-session features.
- Added a Windows service for system-level features that should work without a logged-in user.
- Renamed the modern client to **HASS.Agent .NET10** so it is clearly separate from the legacy app.
- Moved shared settings/logs to `C:\ProgramData\HASS.Agent.NET10`.
- Added MQTT discovery and dynamic Home Assistant entities.
- Added a role matrix so features can be handled by `Service`, `Tray app`, or both.
- Added a configurable sensor catalog and custom sensors.
- Added service-aware shutdown/restart/restart-cancel support.
- Added a new Windows 11-style icon.
