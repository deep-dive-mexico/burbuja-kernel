#!/usr/bin/env python3
"""Keep Directory.Build.props' <Version> consistent with the Git ref being built.

Releases are cut on `release/X.Y` branches and tagged `vX.Y.Z` on the exact commit; `main`
always carries the next prerelease (`X.Y.Z-dev`) so a main build can never claim a release
number. A consumer pins a tag's commit and can check the checkout's version against it.
"""

import argparse
import os
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SEMVER = re.compile(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?")


class VersionError(Exception):
    pass


def read_version(root: Path = ROOT) -> str:
    nodes = ET.parse(root / "Directory.Build.props").getroot().findall(".//Version")
    if len(nodes) != 1 or not (nodes[0].text or "").strip():
        raise VersionError("Directory.Build.props must declare exactly one <Version>")
    version = nodes[0].text.strip()
    if not SEMVER.fullmatch(version):
        raise VersionError(f"kernel version {version!r} is not semantic X.Y.Z[-prerelease]")
    return version


def check(version: str, ref: str) -> str:
    """Return a one-line description of the rule applied to `ref`, or raise."""
    major, minor, _, prerelease = SEMVER.fullmatch(version).groups()
    if ref.startswith("refs/tags/"):
        tag = ref.removeprefix("refs/tags/")
        if tag != "v" + version:
            raise VersionError(f"tag {tag} does not match Directory.Build.props version {version}")
        return f"tag {tag} matches the declared version"
    branch = ref.removeprefix("refs/heads/")
    if branch == "main":
        if not prerelease:
            raise VersionError(f"main must carry a prerelease version (X.Y.Z-dev), not release {version}")
        return f"main carries prerelease {version}"
    line = re.fullmatch(r"release/(\d+)\.(\d+)", branch)
    if line:
        if prerelease or (major, minor) != line.groups():
            raise VersionError(f"{branch} must carry a {line[1]}.{line[2]}.Z release version, not {version}")
        return f"{branch} carries release {version}"
    return f"{version} is well formed (no rule for {ref or 'an unnamed ref'})"


def ci_ref() -> str:
    # A pull request is judged by the branch it would land on.
    base = os.environ.get("GITHUB_BASE_REF")
    return "refs/heads/" + base if base else os.environ.get("GITHUB_REF", "")


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("command", choices=("show", "check"))
    parser.add_argument("--ref", help="Git ref to judge (default: the GitHub Actions ref)")
    args = parser.parse_args(argv)
    try:
        version = read_version()
        if args.command == "show":
            print(version)
        else:
            print("kernel version ok: " + check(version, args.ref if args.ref is not None else ci_ref()))
        return 0
    except (VersionError, OSError, ET.ParseError) as error:
        print(f"kernel version refused: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
