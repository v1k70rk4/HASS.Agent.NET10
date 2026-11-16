"""The pages of a freshly built site that differ from the live site, for IndexNow.

IndexNow tells Bing, Yandex, Seznam and Naver at once which pages changed. Only the changed ones are sent:
each page of the built site's sitemap is compared with what the site serves now, so a page that is new or
differs counts, and one that is the same does not. With ``--all`` every page of the sitemap is listed.

Usage: ``python website/indexnow.py site [--all]``. It prints ``urls=<JSON list>`` for ``$GITHUB_OUTPUT``.
"""

from __future__ import annotations

import json
import sys
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET  # noqa: S405 - our own sitemap, just built
from pathlib import Path

SITE_URL = "https://hass-agent.com/"
SITEMAP_NS = {"s": "http://www.sitemaps.org/schemas/sitemap/0.9"}


def _local_file(site: Path, url: str) -> Path:
    path = url[len(SITE_URL) :]
    if path == "" or path.endswith("/"):
        path += "index.html"
    return site / path


def _live(url: str) -> bytes | None:
    """The page as the site serves it now, or None when it does not (a new page) or cannot be read."""
    try:
        with urllib.request.urlopen(url, timeout=30) as response:  # noqa: S310 - our own https site
            return response.read()
    except (urllib.error.URLError, TimeoutError):
        return None


def changed_urls(site: Path, everything: bool) -> list[str]:
    tree = ET.parse(site / "sitemap.xml")  # noqa: S314 - our own sitemap, just built
    urls = [loc.text.strip() for loc in tree.getroot().findall("s:url/s:loc", SITEMAP_NS) if loc.text]
    urls = [url for url in urls if url.startswith(SITE_URL)]
    if everything:
        return urls
    return [url for url in urls if _live(url) != _local_file(site, url).read_bytes()]


def main() -> None:
    site = Path(sys.argv[1])
    urls = changed_urls(site, "--all" in sys.argv[2:])
    print(f"{len(urls)} page(s) to submit", file=sys.stderr)
    for url in urls:
        print(f"  {url}", file=sys.stderr)
    print(f"urls={json.dumps(urls)}")


if __name__ == "__main__":
    main()
