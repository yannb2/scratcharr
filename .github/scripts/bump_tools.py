#!/usr/bin/env python3
"""bump_tools.py: look for newer versions of the tools pinned in the Dockerfile and update the ARG lines.

Run by the weekly "Tool updates" workflow, which opens a pull request with the result; the
image build on that pull request is the test. Nothing is installed or merged here.

    python3 .github/scripts/bump_tools.py            # update the Dockerfile, print a Markdown summary
    python3 .github/scripts/bump_tools.py --dry-run  # only print what would change

"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

DOCKERFILE = Path(__file__).resolve().parents[2] / "Dockerfile"
USER_AGENT = "Scratcharr tool updates (+https://github.com/yannb2/scratcharr)"


@dataclass
class Tool:
    arg: str              # the ARG in the Dockerfile
    name: str
    source: str           # page URL, or owner/repo for a GitHub release
    pattern: str = ""     # for a page: regex whose group 1 is a version
    github: bool = False
    notes: str = ""       # release notes link for the pull request


TOOLS = [
    Tool("SEVENZIP_VERSION", "7-Zip", "ip7z/7zip", github=True, notes="https://www.7-zip.org/history.txt"),
]

# A plain release tag: 1.2.3 or v1.2.3 (dovi_tool's repository also has libdovi-3.x tags).
PLAIN_TAG = re.compile(r"^[vV]?\d+(?:\.\d+)+$")


def version_key(version: str) -> tuple[int, ...]:
    return tuple(int(part) for part in re.findall(r"\d+", version))


def newest(versions: list[str], hold_major: int | None = None) -> str | None:
    """The highest version, keeping the same major version when hold_major is set."""
    allowed = [v for v in versions if hold_major is None or version_key(v)[0] == hold_major]
    return max(allowed, key=version_key, default=None)


def read_args(text: str) -> dict[str, str]:
    """ARG NAME=value lines with a value: the pins."""
    return dict(re.findall(r"^ARG ([A-Z0-9_]+)=(\S+)$", text, re.MULTILINE))


def set_arg(text: str, arg: str, value: str) -> str:
    return re.sub(rf"^ARG {arg}=\S+$", f"ARG {arg}={value}", text, count=1, flags=re.MULTILINE)


def fetch(url: str) -> str:
    headers = {"User-Agent": USER_AGENT}
    if url.startswith("https://api.github.com/") and os.environ.get("GITHUB_TOKEN"):
        headers["Authorization"] = f"Bearer {os.environ['GITHUB_TOKEN']}"
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as response:
        return response.read().decode("utf-8", "replace")


def plain_tags(releases: list[dict]) -> list[str]:
    """The plain version tags of a GitHub releases list, drafts and pre-releases left out."""
    return [r["tag_name"].lstrip("vV") for r in releases
            if not r.get("draft") and not r.get("prerelease") and PLAIN_TAG.match(r.get("tag_name", ""))]


def upstream(tool: Tool) -> list[str]:
    if tool.github:
        return plain_tags(json.loads(fetch(f"https://api.github.com/repos/{tool.source}/releases?per_page=30")))
    return re.findall(tool.pattern, fetch(tool.source))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--dockerfile", type=Path, default=DOCKERFILE)
    args = parser.parse_args()

    text = args.dockerfile.read_text()
    pins = read_args(text)
    rows, failed = [], []
    for tool in TOOLS:
        current = pins.get(tool.arg)
        if current is None:
            failed.append(f"{tool.name}: no ARG {tool.arg} in the Dockerfile")
            continue
        try:
            found = upstream(tool)
        except (urllib.error.URLError, TimeoutError, KeyError, ValueError) as error:
            failed.append(f"{tool.name}: could not check ({error})")
            continue
        latest = newest(found)
        if latest and version_key(latest) > version_key(current):
            rows.append((tool, current, latest))
            text = set_arg(text, tool.arg, latest)

    if rows:
        print("| Tool | Now | New | Notes |\n|---|---|---|---|")
        for tool, current, latest in rows:
            print(f"| {tool.name} | {current} | **{latest}** | [release notes]({tool.notes}) |")
    else:
        print("No newer versions found.")
    for line in failed:
        print(f"\n- {line}")

    if rows and not args.dry_run:
        args.dockerfile.write_text(text)
    if os.environ.get("GITHUB_OUTPUT"):
        with open(os.environ["GITHUB_OUTPUT"], "a") as out:
            out.write(f"changed={'true' if rows else 'false'}\n")
            out.write(f"tools={', '.join(f'{t.name} {v}' for t, _, v in rows)}\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
