# Security Policy

## Supported versions

| Version | Security fixes |
|---------|----------------|
| Latest stable release ([Releases](https://github.com/v1k70rk4/HASS.Agent.NET10/releases/latest)) | Yes |
| The beta running at the time, if there is one | Yes |
| Older versions | No, please update first |

Fixes come out as a new release, signed like every other one.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Report it privately instead:

**[Report a vulnerability](https://github.com/v1k70rk4/HASS.Agent.NET10/security/advisories/new)** (the *Security* tab of this repository, *Report a vulnerability*).

Only you and the maintainer see the report. It helps to include:

- the version of HASS.Agent .NET10, and of the integration if it is involved,
- the transport (MQTT, HA API, local HTTP API),
- what someone could do with it, and under which conditions,
- the steps to reproduce it.

You get an answer within a few days. Once it is fixed, the release notes mention it and thank you by name, unless you would rather not be named. Where it is warranted, a GitHub security advisory (and a CVE) is published with the fix.

## What counts

Anything that lets someone do more than the person who set up the PC intended, for example:

- the **local HTTP API** (port 5115 by default) and its API key,
- the **commands** the app runs: system commands, custom commands, key presses, scripts, popup windows,
- the **credentials** the app keeps (MQTT login, Home Assistant token, API key), stored with Windows DPAPI,
- the **update path**: the update check, the download and the installer started from Home Assistant or from the About page,
- the **notifications**: their text, buttons, text fields and the pictures fetched for them,
- the **Windows service** and the rights it runs with.

Not in scope: problems in Home Assistant, the MQTT broker or Windows themselves; something that needs an administrator account on the PC already; a custom command or script doing what it was set up to do. The Home Assistant integration has its own [security policy](https://github.com/v1k70rk4/HASS.Agent.NET10-Integration/security/policy); a report sent to either repository reaches the same person.
