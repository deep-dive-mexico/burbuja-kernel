#!/usr/bin/env python3
"""Prepare, validate and publish stable releases for main pushes and version tags."""

import argparse
import base64
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SEMVER = re.compile(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?")
STABLE_TAG = re.compile(r"v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)")


def declared_version(content):
    nodes = ET.fromstring(content).findall(".//Version")
    if len(nodes) != 1 or not SEMVER.fullmatch((nodes[0].text or "").strip()):
        raise ValueError("Directory.Build.props must declare one semantic version")
    return nodes[0].text.strip()


def next_version(version, tags):
    major, minor, patch = map(int, SEMVER.fullmatch(version).groups())
    patches = [int(match[3]) for tag in tags
               if (match := STABLE_TAG.fullmatch(tag)) and (int(match[1]), int(match[2])) == (major, minor)]
    return f"{major}.{minor}.{max(patch, max(patches, default=-1) + 1)}"


def release_tag(version, ref, event, prepared_tag=""):
    if ref == "refs/heads/main" and event == "push":
        tag = prepared_tag
    elif ref.startswith("refs/tags/") and event == "push":
        tag = ref.removeprefix("refs/tags/")
        if not STABLE_TAG.fullmatch(tag):
            return None
    else:
        return None
    if not STABLE_TAG.fullmatch(tag) or tag != "v" + version:
        raise ValueError(f"release tag {tag!r} does not match declared stable version {version}")
    return tag


def gh(*args, payload=None):
    result = subprocess.run(["gh", *args], input=json.dumps(payload) if payload is not None else None,
                            text=True, capture_output=True)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or "GitHub CLI request failed")
    return result.stdout


def api(path, payload=None):
    args = ["api", path]
    if payload is not None:
        args.extend(["--input", "-"])
    return json.loads(gh(*args, payload=payload))


def optional_api(path):
    try:
        return api(path)
    except RuntimeError as error:
        if "HTTP 404" in str(error):
            return None
        raise


def require_sha(sha):
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ValueError("release requires a full commit SHA")


def prepare(repository, source_sha, run_id, content):
    """Stage a version-only commit without changing main; reuse it on reruns."""
    require_sha(source_sha)
    if not re.fullmatch(r"[1-9]\d*", run_id):
        raise ValueError("release preparation requires a positive workflow run ID")
    base = f"repos/{repository}"
    staging_prefix = f"heads/automation/release-{run_id}/"
    matches = optional_api(f"{base}/git/matching-refs/{staging_prefix}") or []
    if len(matches) > 1:
        raise ValueError("workflow run has multiple staged releases")
    existing = matches[0] if matches else None
    if existing:
        sha = existing["object"]["sha"]
        commit = api(f"{base}/git/commits/{sha}")
        if [parent["sha"] for parent in commit["parents"]] != [source_sha]:
            raise ValueError("staged release belongs to a different main commit")
        props = api(f"{base}/contents/Directory.Build.props?ref={sha}")
        version = declared_version(base64.b64decode(props["content"]).decode())
        if not STABLE_TAG.fullmatch("v" + version):
            raise ValueError("staged release must declare a stable version")
    else:
        tags = gh("api", "--paginate", f"{base}/tags", "--jq", ".[].name").splitlines()
        # Failed or pending runs reserve their version so a later merge cannot take it.
        reserved = gh("api", "--paginate", f"{base}/git/matching-refs/heads/automation/release-",
                      "--jq", ".[].ref").splitlines()
        tags.extend(ref.rsplit("/", 1)[-1] for ref in reserved)
        version = next_version(declared_version(content), tags)
        updated, count = re.subn(r"(<Version>).*?(</Version>)", rf"\g<1>{version}\g<2>", content)
        if count != 1:
            raise ValueError("cannot update exactly one product version")
        parent = api(f"{base}/git/commits/{source_sha}")
        tree = api(f"{base}/git/trees", {
            "base_tree": parent["tree"]["sha"],
            "tree": [{"path": "Directory.Build.props", "mode": "100644", "type": "blob", "content": updated}],
        })
        commit = api(f"{base}/git/commits", {
            "message": f"Release v{version}", "tree": tree["sha"], "parents": [source_sha],
        })
        sha = commit["sha"]
        api(f"{base}/git/refs", {"ref": "refs/" + staging_prefix + "v" + version, "sha": sha})
    return {"sha": sha, "tag": "v" + version}


def curated_notes(tag):
    """Read notes from the validated tree, preferring an explicit maintenance release."""
    path = ROOT / "CHANGELOG.md"
    if not path.exists():
        return ""
    content = path.read_text()
    for heading in (tag, "Unreleased"):
        section = re.search(
            rf"^## {re.escape(heading)}(?: — [^\n]+)?\n(.*?)(?=^## |\Z)",
            content, re.MULTILINE | re.DOTALL)
        if section:
            return section[1].strip()
    return ""


def publish(repository, tag, sha, title_prefix, source_sha, allow_create_tag=False):
    """Never move a tag or replace existing release notes."""
    require_sha(sha)
    base = f"repos/{repository}"
    ref = optional_api(f"{base}/git/ref/tags/{tag}")
    if ref is None:
        if not allow_create_tag:
            raise ValueError(f"stable tag {tag} must already exist")
        api(f"{base}/git/refs", {"ref": f"refs/tags/{tag}", "sha": sha})
    else:
        obj = ref["object"]
        while obj["type"] == "tag":
            obj = api(f'{base}/git/tags/{obj["sha"]}')["object"]
        if obj["type"] != "commit" or obj["sha"] != sha:
            raise ValueError(f"refusing to reuse {tag}: it does not point to {sha}")
    if optional_api(f"{base}/releases/tags/{tag}") is not None:
        print(f"Release already exists: {tag}")
        return
    notes = f"Validated release commit: `{sha}`.\n"
    if source_sha != sha:
        notes += f"Main source commit: `{source_sha}`. The release commit changes only the declared version.\n"
    curated = curated_notes(tag)
    if curated:
        notes += "\n## Release summary\n\n" + curated + "\n"
    if (ROOT / "CHANGELOG.md").exists():
        notes += f"\n[Curated changelog at this release](https://github.com/{repository}/blob/{sha}/CHANGELOG.md).\n"
    gh("release", "create", tag, "--repo", repository, "--verify-tag",
       "--title", f"{title_prefix} {tag}", "--generate-notes", "--notes", notes)
    print(f"Published {tag} at {sha}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("prepare", "check", "publish"))
    parser.add_argument("--title-prefix", default="Burbuja kernel")
    args = parser.parse_args()
    try:
        content = (ROOT / "Directory.Build.props").read_text()
        version = declared_version(content)
        ref, event = os.environ.get("GITHUB_REF", ""), os.environ.get("GITHUB_EVENT_NAME", "")
        if args.command == "prepare":
            if (ref, event) != ("refs/heads/main", "push"):
                raise ValueError("only a main push may prepare a release")
            outputs = prepare(os.environ["GH_REPO"], os.environ["GITHUB_SHA"],
                              os.environ["GITHUB_RUN_ID"], content)
            with open(os.environ["GITHUB_OUTPUT"], "a") as output:
                for name, value in outputs.items():
                    output.write(f"{name}={value}\n")
            print(f"Prepared {outputs['tag']} at {outputs['sha']}")
            return 0
        tag = release_tag(version, ref, event, os.environ.get("RELEASE_TAG", ""))
        if args.command == "check" or tag is None:
            print(f"Release identity checked: {tag or 'no publication'}")
            return 0
        sha = subprocess.run(["git", "rev-parse", "HEAD"], check=True, capture_output=True,
                             text=True).stdout.strip()
        expected_sha = os.environ.get("RELEASE_SHA") or os.environ["GITHUB_SHA"]
        if sha != expected_sha:
            raise ValueError("checkout differs from the validated release commit")
        publish(os.environ["GH_REPO"], tag, sha, args.title_prefix, os.environ["GITHUB_SHA"],
                allow_create_tag=(ref == "refs/heads/main"))
        return 0
    except (ValueError, RuntimeError, OSError, KeyError, ET.ParseError, subprocess.CalledProcessError) as error:
        print(f"GitHub release refused: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
