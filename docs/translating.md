---
title: Translating the app
description: How to translate HASS.Agent .NET10 into a new language, try it in the installed app without building it, and send it in.
---

# Translating the app

The texts of the Windows app are in one JSON file per language, under [`src/HASS.Agent.NET10/Localization`](https://github.com/v1k70rk4/HASS.Agent.NET10/tree/main/src/HASS.Agent.NET10/Localization): `en.json` is English, `hu.json` Hungarian. A new language is a new file next to them; nothing else in the code has to change. The app lists it on the General page under **UI language** (and **HA entity language**), and picks it by itself on a Windows set to one of its languages.

## The file

Copy `en.json` and name the copy after the language, for example `zh-Hans.json`. It has two parts:

```json
{
  "language": {
    "code": "zh-Hans",
    "name": "简体中文",
    "cultures": ["zh-Hans"]
  },
  "strings": {
    "Nav.General": "常规",
    "Nav.Mqtt": "MQTT"
  }
}
```

- **`code`**: the language tag, the same as the file name without `.json`.
- **`name`**: the language's name in itself, as the language lists show it.
- **`cultures`**: the Windows display languages this language is chosen for. A culture also covers the ones derived from it: `zh-Hans` covers `zh-CN` and `zh-SG`, `hu` covers `hu-HU`.
- **`strings`**: the English file's keys, with the text in your language. Leave the keys as they are and translate the values only.

Things to keep in a text:

- **Placeholders** like `{0}` and `{1}` are filled in by the app (a name, a number, an error message). Keep each of them, in whatever place the sentence needs. A text whose placeholders differ from the English one, or with a stray `{` or `}`, is not used: what was there before shows instead, the built-in translation for a language the app has, English otherwise.
- **`\n`** is a line break, **`\"`** a quotation mark inside the text.
- **Product and technical names stay**: HASS.Agent, Home Assistant, MQTT, HA API, Windows.
- **Short is better.** Buttons and labels have a fixed width; a text much longer than the English one may be cut off. Sentences on the pages wrap.

While you try it in the app (below), a key you leave out is not a problem: the English text is used for it, so you can work in parts. A file sent in to become part of the app has every key of `en.json`, though; the tests check that (see [Send it in](#send-it-in)).

## Try it in the installed app

No build is needed. Put the file into

```text
%LOCALAPPDATA%\HASS.Agent.NET10\languages\
```

(create the `languages` folder), then quit HASS.Agent from the tray menu and start it again. Your language is in the **UI language** list of the General page; choose it and save. A file for a language the app already has replaces only the texts it contains, which is how a correction can be tried.

The app checks the file when it starts and writes what it could not use into its log (Danger Zone, Debug log, or `C:\ProgramData\HASS.Agent.NET10\hass-agent-net10.log`): a text with other placeholders than the English one or a stray brace, a key English does not have, a file that is not valid JSON. For such a text the app keeps what it had: the built-in translation of a language it has, English otherwise. These files are read by the tray app of your own Windows user only; the Windows service and other users never see them.

## Send it in

Open a pull request that adds your file to `src/HASS.Agent.NET10/Localization/`, or attach the file to an issue or to a post on the [Discussions page](https://github.com/v1k70rk4/HASS.Agent.NET10/discussions) if you prefer not to deal with git. A language that comes into the app this way has every key of `en.json`, translated: the tests check every built-in language against English (the same keys, the same placeholders, a name and at least one culture) and fail on a missing key. A text you are unsure of can stay in English in the file for now; say so, and it gets looked at.

A pull request first gets a review from CodeRabbit, an automated reviewer. On a translation, take its comments as suggestions. It is good at spotting a missing placeholder, a text left in English, or an example that no longer matches the English one. Grammar, and how a sentence sounds in your language, are up to you. Take what helps. Where you don't agree, a short reply on why before you resolve the thread is enough, a line in English is fine: it tells the reviewer it was your choice and not an oversight.

When the app gets new texts later, they show in English in your language until someone translates them; the tests then list the missing keys.

## The Home Assistant side

The names of the entities in Home Assistant come from the integration, which has its own translation files in the Home Assistant format: [`custom_components/hass_agent/translations`](https://github.com/v1k70rk4/HASS.Agent.NET10-Integration/tree/main/custom_components/hass_agent/translations) in the integration's repository. Translating those is a separate, optional step. The installer has its own texts too; those are not translated through these files.
