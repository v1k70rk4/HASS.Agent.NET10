---
title: 面向 Home Assistant 联动的现代化 .NET10 Windows 客户端
description: 免费的 Home Assistant 联动 Windows 的应用，提供简体中文用户界面。发送通知、媒体播放控制、状态上报、命令行指令和一键更新客户端，支持通过 MQTT 或 WebSocket API 连接。
lang: zh-Hans
hide:
  - navigation
  - toc
---

<div class="hero" markdown>

![HASS.Agent .NET10 图标](../images/hass_agent_companion_modern_icon.png){ .logo }

<p class="eyebrow">.NET 10 · WINDOWS 10 / 11 · 支持简体中文</p>

# HASS.Agent .NET10

<p class="tag">现代、轻量的 Home Assistant 联动 Windows 客户端：发送通知、媒体播放控制、状态上报、命令行指令和一键更新客户端。</p>

[:material-download: 下载](https://github.com/v1k70rk4/HASS.Agent.NET10/releases/latest){ .md-button .md-button--primary }
[:material-rocket-launch: 快速入门（英文）](../getting-started/index.md){ .md-button }
[:material-github: GitHub](https://github.com/v1k70rk4/HASS.Agent.NET10){ .md-button }

或 `winget install v1k70rk4.HASSAgentNET10`

</div>

## 功能一览

在 Home Assistant 中查看和控制 Windows 设备。客户端在托盘运行，也可通过安装 Windows 服务以处理无用户登录时也需要运行的功能。

<div class="grid cards" markdown>

-   :material-bell-ring:{ .lg .middle } **通知**

    ---

    Home Assistant 的通知可以以 Windows 原生通知的形式发布到设备，也可显示在独立、始终可见的窗口中。支持图片、按钮和文字互动，动作和内容可传回 Home Assistant。

-   :material-music:{ .lg .middle } **媒体播放**

    ---

    设备中播放媒体的状态会同步到 Home Assistant 中。可控制设备播放、暂停、切歌、进度条、音量，可显示标题、封面。即使设备静音，也可临时调整音量使用户听到 TTS 语音播报。

-   :material-chart-line:{ .lg .middle } **传感器**

    ---

    内置有丰富的系统状态传感器，也可添加自定义进程、服务、磁盘状态、脚本输出，甚至外部 LibreHardwareMonitor 的数值。

-   :material-lightning-bolt:{ .lg .middle } **指令**

    ---

    锁屏、睡眠、关机、重启，以及可自定义的按钮：程序、PowerShell 脚本、按键、链接。可接入自动化或仪表盘使用。

-   :material-dock-window:{ .lg .middle } **仪表盘**

    ---

    点击托盘图标（需勾选 **点击托盘图标以打开弹窗**），Home Assistant 仪表盘即可显示在托盘上方弹出的小窗口中。也可自定义任意页面将其显示在此独立窗口里，比如门铃响起时弹出摄像头画面。

-   :material-keyboard:{ .lg .middle } **组合键**

    ---

    设置一组按键并添加定义，它就会成为 Home Assistant 中的一个事件，用键盘即可触发任意自动化。

-   :material-update:{ .lg .middle } **在 Home Assistant 中更新**

    ---

    update 实体提供 **安装** 按钮。在设备安装系统服务后，点击 Home Assistant 对应实体的安装按钮，客户端更新就会在后台静默完成，且只运行带有发布者签名的安装包。

-   :material-shield-check:{ .lg .middle } **安全**

    ---

    设备与Home Assistant连接，无需管理员账号下的令牌。它使用自行创建的、非管理员的 Home Assistant 用户。客户端每个版本都经过签名，不含任何遥测。

</div>

## 支持简体中文

应用界面提供简体中文。在中文环境下的 Windows 会自动以中文启动，也可以在 **常规** 页面的 **语言(language)** 中手动指定。另外Home Assistant 中实体名称的语言也可以单独设置（**HA集成使用的语言**）。

<figure class="shot" markdown>
![HASS.Agent .NET10 的“常规设置”页面（简体中文）](../images/ui-general-zh.png){ width="700" }
<figcaption>简体中文的“常规”页面</figcaption>
</figure>

## 三步完成安装

系统要求：x64架构，Windows 11 或 Windows 10（2004 或更高版本）

Home Assistant：2026.6 或更高版本

连接：MQTT 代理服务器 **或** Home Assistant 的 WebSocket API。 使用 WebSocket API 可通过 Nabu Casa 等服务在公网远程访问实例。

1. **集成。** 在 Home Assistant 的 **设置** 中打开 **Marketplace**（2026.11 之前的 Home Assistant 中是 HACS），搜索 **HASS.Agent** 集成，下载并重启 Home Assistant。

    [![在 Home Assistant 的 Marketplace 中打开此集成。](https://my.home-assistant.io/badges/hacs_repository.svg)](https://my.home-assistant.io/redirect/hacs_repository/?owner=v1k70rk4&repository=HASS.Agent.NET10-Integration&category=integration)

2. **Windows 应用。** 使用 winget 安装，或从[发布页面](https://github.com/v1k70rk4/HASS.Agent.NET10/releases/latest)下载已签名的安装包：

    ```powershell
    winget install v1k70rk4.HASSAgentNET10
    ```

    应用常驻系统托盘，双击图标即可打开设置。

3. **连接。** 在 **MQTT** 页面填写代理服务器地址和登录信息，或者在 **HA API** 页面填写 Home Assistant 地址和长期访问令牌。使用 MQTT 时，电脑会自动出现在 Home Assistant 的 **设备与服务** 页面的已发现设备中。使用 HA API 时，第一台电脑需要通过添加集成来加入（**HASS.Agent → HA API (WebSocket)**），之后的设备则会被 Home Assistant 自动发现。

## 更多内容

详细文档为英文：

- [Get started](../getting-started/index.md)：详细的安装步骤
- [Features](../features/index.md)：全部功能，附截图
- [Connection modes](../connection/index.md)：MQTT、HA API，或两者并用
- [Coming from HASS.Agent](../getting-started/migrating.md)：从原版 HASS.Agent 迁移
- [Translating](../translating.md)：帮助改进翻译

本应用免费、开源（MIT 许可证），与 Home Assistant 项目没有关联。
