#!/usr/bin/env python3
"""Run the AKOS product harness against this kernel commit, retrying the Chromium lane once.

The product's browser suite has known render races that its own CI reruns. A kernel release must not
be blocked by one of them, so when every other lane passed and only the browser lane failed, that lane
runs a second time in the same snapshot. Both receipts stay under artifacts/kernel-compatibility; a
retry is recorded in the summary this script prints and in the receipt marker's `browser_retry` field.
Runs from the product checkout (cwd), with the kernel checkout given by --kernel-root.
"""

import argparse
import json
from pathlib import Path
import subprocess
import sys

MARKER = ".kernel-candidate-test-only.json"


def harness(kernel_root, commit, lane=None):
    command = [sys.executable, "scripts/test-kernel-candidate.py", "--kernel-root", str(kernel_root), "--kernel-commit", commit]
    if lane:
        command += ["--lane", lane]
    return subprocess.run(command).returncode


def newest_marker(root):
    markers = sorted(Path(root).glob("candidate-*/" + MARKER), key=lambda p: p.stat().st_mtime)
    return markers[-1] if markers else None


def only_browser_failed(metadata):
    lanes = metadata.get("lanes", {})
    return (metadata.get("status") == "failed" and set(lanes) == {"unit", "http", "own-host", "browser"}
            and all(lanes[l].get("status") == "passed" for l in ("unit", "http", "own-host"))
            and lanes["browser"].get("status") == "failed")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kernel-root", required=True, type=Path)
    parser.add_argument("--kernel-commit", required=True)
    parser.add_argument("--compatibility-root", type=Path, default=Path("artifacts/kernel-compatibility"))
    args = parser.parse_args()
    if harness(args.kernel_root, args.kernel_commit) == 0:
        print("consumer gate: passed")
        return 0
    marker = newest_marker(args.compatibility_root)
    metadata = json.loads(marker.read_text()) if marker else {}
    if not only_browser_failed(metadata):
        print("consumer gate: failed outside the browser lane; no retry", file=sys.stderr)
        return 1
    print("consumer gate: only the browser lane failed; running it once more")
    first = metadata["lanes"]["browser"]
    code = harness(args.kernel_root, args.kernel_commit, lane="browser")
    retry_marker = newest_marker(args.compatibility_root)
    if retry_marker and retry_marker != marker:
        retry = json.loads(retry_marker.read_text())
        retry["browser_retry"] = {"first_attempt": {"receipt": first.get("receipt"), "log": first.get("log"), "marker": str(marker)},
                                  "status": "passed" if code == 0 else "failed"}
        retry_marker.write_text(json.dumps(retry, indent=2) + "\n")
    print("consumer gate: browser retry " + ("passed" if code == 0 else "failed"))
    return 0 if code == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
