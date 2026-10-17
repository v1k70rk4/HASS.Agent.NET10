"""MkDocs hooks of the website.

Two things the markdown files cannot do on their own:

* ``changelog.md`` shows the repository's ``CHANGELOG.md``, so the changelog is written once.
* The Integration pages show sections of the integration's README, which lives in the other repository
  (``<!-- integration-readme: Entities -->`` in a page is replaced with that section). The README is read
  from a checkout next to this one when there is one, otherwise it is downloaded from GitHub.
"""

from __future__ import annotations

import logging
import os
import re
import urllib.request
from pathlib import Path

log = logging.getLogger("mkdocs.hooks.hass_agent")

ROOT = Path(__file__).resolve().parent.parent
AGENT_REPO = "https://github.com/v1k70rk4/HASS.Agent.NET10"
INTEGRATION_REPO = "https://github.com/v1k70rk4/HASS.Agent.NET10-Integration"
INTEGRATION_README_URL = "https://raw.githubusercontent.com/v1k70rk4/HASS.Agent.NET10-Integration/main/README.md"

# Where the sections of the old one-file pages went, for links that still name them (the integration's README,
# older release notes, issues). The key is the old path under docs/ with its anchor, the value the new page.
LEGACY_PAGES = {
    "features.md": "features/index.md",
    "features.md#notifications": "features/notifications.md",
    "features.md#pictures-text-fields-and-more": "features/notifications.md#pictures-text-fields-and-more",
    "features.md#notification-style": "features/notifications.md#notification-style",
    "features.md#media-player": "features/media-player.md",
    "features.md#dashboard-popup": "features/dashboard-popup.md",
    "features.md#display-and-audio": "features/display-and-audio.md",
    "features.md#hotkeys": "features/hotkeys.md",
    "features.md#windows-service": "features/windows-service.md",
    "features.md#commands-the-service-runs": "features/windows-service.md#commands-the-service-runs",
    "features.md#updating-from-home-assistant": "features/updates.md",
    "features.md#who-may-use-it-on-this-pc": "features/users.md",
    "features.md#danger-zone": "features/danger-zone.md",
    "sensors.md": "sensors/index.md",
    "sensors.md#built-in-sensors": "sensors/index.md",
    "sensors.md#sensor-attributes": "sensors/attributes.md",
    "sensors.md#sensor-polling-profiles": "sensors/profiles.md",
    "sensors.md#availability": "sensors/index.md#availability",
    "sensors.md#custom-sensors": "sensors/custom.md",
    "sensors.md#lhm-librehardwaremonitor": "sensors/custom.md#lhm-librehardwaremonitor",
    "commands.md": "commands/index.md",
    "commands.md#system-commands": "commands/index.md",
    "commands.md#custom-commands": "commands/custom.md",
    "connection.md": "connection/index.md",
    "connection.md#connection-modes": "connection/index.md",
    "connection.md#a-home-assistant-user-for-the-pc": "connection/index.md#a-home-assistant-user-for-the-pc",
    "connection.md#mqtt-topics": "connection/mqtt.md",
    "connection.md#ha-api-websocket-events": "connection/ha-api.md",
    "connection.md#local-http-api": "connection/local-api.md",
    "connection.md#windows-firewall": "connection/local-api.md#windows-firewall",
    "migrating.md": "getting-started/migrating.md",
    "development.md": "development.md",
}

_integration_readme: str | None = None


def _legacy_target(path: str, anchor: str) -> str:
    """The new page for an old ``docs/<path>#<anchor>`` link, with the anchor kept where the page keeps it."""
    key = f"{path}#{anchor}" if anchor else path
    if key in LEGACY_PAGES:
        return LEGACY_PAGES[key]
    if path in LEGACY_PAGES:
        return LEGACY_PAGES[path] + (f"#{anchor}" if anchor else "")
    return path + (f"#{anchor}" if anchor else "")


def _relative(target: str, page_dir: str) -> str:
    """A docs-relative path as a link from a page in ``page_dir`` (empty for the docs root)."""
    if not page_dir:
        return target
    return "../" * (page_dir.count("/") + 1) + target


# README sections that have a page of their own on the site.
README_SECTIONS = {
    "code-signing": "getting-started/security.md#signed-releases",
    "privacy-policy": "getting-started/security.md#privacy",
    "install": "getting-started/index.md",
    "quick-start": "getting-started/index.md",
    "installer": "getting-started/installer.md",
}


def _rewrite_repo_links(markdown: str, page_dir: str) -> str:
    """Links written for GitHub (``docs/x.md``, ``README.md#...``) become links of the site."""

    def repl(match: re.Match[str]) -> str:
        target = match.group(2)
        if target.startswith("docs/"):
            path, _, anchor = target[5:].partition("#")
            return f"{match.group(1)}{_relative(_legacy_target(path, anchor), page_dir)})"
        if target.startswith("README.md#") and target[10:] in README_SECTIONS:
            return f"{match.group(1)}{_relative(README_SECTIONS[target[10:]], page_dir)})"
        if re.match(r"^(README|CHANGELOG|CONTRIBUTING|SECURITY)\.md(#|$)", target):
            return f"{match.group(1)}{AGENT_REPO}/blob/main/{target})"
        return match.group(0)

    markdown = re.sub(r"(\]\()([^)\s:]+\.md(?:#[^)\s]*)?)\)", repl, markdown)
    return re.sub(rf"(\]\(){re.escape(AGENT_REPO)}/blob/main/(docs/[^)\s]+\.md(?:#[^)\s]*)?)\)", repl, markdown)


def _slug(heading: str) -> str:
    text = re.sub(r"[`*_]", "", heading).strip().lower()
    text = re.sub(r"[^\w\s-]", "", text)
    return re.sub(r"\s+", "-", text)


def _load_integration_readme() -> str:
    global _integration_readme
    if _integration_readme is not None:
        return _integration_readme
    candidates: list[Path] = []
    if os.environ.get("HASS_AGENT_INTEGRATION_README"):
        candidates.append(Path(os.environ["HASS_AGENT_INTEGRATION_README"]))
    for parent in ROOT.parents:
        candidates.append(parent / "HASS.Agent-Integration" / "README.md")
        candidates.append(parent / "HASS.Agent.NET10-Integration" / "README.md")
    for candidate in candidates:
        if candidate.is_file():
            log.info("Integration README: %s", candidate)
            _integration_readme = candidate.read_text(encoding="utf-8")
            return _integration_readme
    log.info("Integration README: %s", INTEGRATION_README_URL)
    with urllib.request.urlopen(INTEGRATION_README_URL, timeout=30) as response:  # noqa: S310 - fixed https URL
        _integration_readme = response.read().decode("utf-8")
    return _integration_readme


def _integration_section(name: str) -> str:
    """The body of the ``## name`` section of the integration's README, its headings one level up."""
    readme = _load_integration_readme()
    match = re.search(rf"^## {re.escape(name)}\s*$\n(.*?)(?=^## |\Z)", readme, re.M | re.S)
    if not match:
        raise ValueError(f"The integration README has no '## {name}' section")
    body = match.group(1).strip("\n")
    body = re.sub(r"^###", "##", body, flags=re.M)
    return re.sub(r"\n---\s*$", "", body)


def _integration_links(markdown: str, page_dir: str) -> str:
    """Links of the included README: its own anchors stay when the page has the heading, otherwise they go to
    GitHub; links into this repository's docs become links of the site."""
    headings = {_slug(h) for h in re.findall(r"^#+\s+(.+)$", markdown, re.M)}

    def anchor(match: re.Match[str]) -> str:
        if match.group(2)[1:] in headings:
            return match.group(0)
        return f"{match.group(1)}{INTEGRATION_REPO}{match.group(2)})"

    markdown = re.sub(r"(\]\()(#[^)\s]+)\)", anchor, markdown)

    def docs(match: re.Match[str]) -> str:
        path, _, fragment = match.group(2).partition("#")
        return f"{match.group(1)}{_relative(_legacy_target(path, fragment), page_dir)})"

    return re.sub(rf"(\]\(){re.escape(AGENT_REPO)}/blob/main/docs/([^)\s]+)\)", docs, markdown)


def on_page_markdown(markdown: str, page, config, files) -> str:
    page_dir = os.path.dirname(page.file.src_uri)

    if page.file.src_uri == "changelog.md":
        changelog = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
        markdown = markdown.replace("<!-- changelog -->", _rewrite_repo_links(changelog, page_dir))

    if "<!-- integration-readme:" in markdown:
        markdown = re.sub(
            r"<!-- integration-readme:\s*(.+?)\s*-->",
            lambda match: _integration_section(match.group(1)),
            markdown,
        )
        markdown = _integration_links(markdown, page_dir)

    return markdown
