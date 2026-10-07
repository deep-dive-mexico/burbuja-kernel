#!/usr/bin/env python3
"""Run AKOS's offline candidate gates and retain a source-bound aggregate receipt.

Only an executed browser-test failure earns one retry in a new snapshot of the same
product/kernel commits. Missing, stale, incomplete or mismatched evidence fails closed.
Run from the product checkout, with the kernel checkout given by --kernel-root.
"""

import argparse
import json
from pathlib import Path
import subprocess
import sys

MARKER = ".kernel-candidate-test-only.json"
LANES = {"unit", "http", "own-host", "browser"}
KIND = "non-deployable-kernel-compatibility"


def harness(kernel_root, commit, lane=None):
    command = [sys.executable, "scripts/test-kernel-candidate.py", "--kernel-root", str(kernel_root), "--kernel-commit", commit]
    if lane:
        command += ["--lane", lane]
    return subprocess.run(command).returncode


def markers(root):
    return set(root.glob("candidate-*/" + MARKER))


def attempt(root, kernel_root, kernel_commit, product_commit, lane=None):
    before = markers(root)
    code = harness(kernel_root, kernel_commit, lane)
    fresh = markers(root) - before
    if len(fresh) != 1:
        raise ValueError("harness must produce exactly one fresh compatibility marker")
    marker = fresh.pop()
    metadata = json.loads(marker.read_text())
    if (metadata.get("kind") != KIND or metadata.get("kernel_commit") != kernel_commit
            or metadata.get("product_commit") != product_commit
            or Path(metadata.get("kernel_root", "")).resolve() != kernel_root.resolve()
            or set(metadata.get("lanes", {})) != ({lane} if lane else LANES)):
        raise ValueError("compatibility marker has a different source identity or lane set")
    return code, marker, metadata


def lane_receipt(marker, metadata, lane, status):
    evidence = metadata["lanes"][lane]
    path = Path(evidence.get("receipt") or "").resolve()
    if not path.is_relative_to(marker.parent.resolve()):
        raise ValueError("lane receipt escapes its compatibility snapshot")
    receipt = json.loads(path.read_text())
    counts = receipt.get("results", {})
    if (evidence.get("status") != status or receipt.get("status") != status
            or receipt.get("lane") != lane or receipt.get("evidence_kind") != KIND
            or receipt.get("kernel_commit") != metadata["kernel_commit"]
            or receipt.get("product_commit") != metadata["product_commit"]
            or receipt.get("kernel_release") is not None
            or not all(type(counts.get(key)) is int and counts[key] >= 0
                       for key in ("total", "passed", "failed", "notExecuted", "aborted"))
            or counts["total"] <= 0 or counts["notExecuted"] or counts["aborted"]):
        raise ValueError("lane receipt is incomplete, skipped or has a different source identity")
    if status == "passed":
        if evidence.get("exit_code") != 0 or counts["failed"] or counts["passed"] != counts["total"]:
            raise ValueError("lane did not pass every executed test")
    elif evidence.get("exit_code") in (None, 0) or counts["failed"] <= 0:
        raise ValueError("browser retry requires an executed test failure")
    return receipt


def only_browser_failed(metadata):
    lanes = metadata.get("lanes", {})
    return (metadata.get("status") == "failed" and set(lanes) == LANES
            and all(lanes[l].get("status") == "passed" for l in ("unit", "http", "own-host"))
            and lanes["browser"].get("status") == "failed")


def run_gate(root, kernel_root, kernel_commit, product_commit):
    summary = {"kind": KIND, "kernel_commit": kernel_commit, "product_commit": product_commit,
               "status": "failed", "attempts": [], "lanes": {}}
    root.mkdir(parents=True, exist_ok=True)
    try:
        code, marker, metadata = attempt(root, kernel_root, kernel_commit, product_commit)
        summary["attempts"].append(str(marker))
        if code == 0 and metadata.get("status") == "passed":
            for lane in sorted(LANES):
                lane_receipt(marker, metadata, lane, "passed")
            summary["lanes"] = metadata["lanes"]
        elif code != 0 and only_browser_failed(metadata):
            for lane in ("unit", "http", "own-host"):
                lane_receipt(marker, metadata, lane, "passed")
                summary["lanes"][lane] = metadata["lanes"][lane]
            first = lane_receipt(marker, metadata, "browser", "failed")
            print("consumer gate: only browser tests failed; retrying once", flush=True)
            retry_code, retry_marker, retry = attempt(root, kernel_root, kernel_commit, product_commit, "browser")
            summary["attempts"].append(str(retry_marker))
            if retry_code != 0 or retry.get("status") != "passed":
                raise ValueError("browser retry failed")
            second = lane_receipt(retry_marker, retry, "browser", "passed")
            if second["results"]["total"] != first["results"]["total"]:
                raise ValueError("browser retry changed the test count")
            summary["lanes"]["browser"] = retry["lanes"]["browser"]
            summary["browser_retry"] = {"first_attempt": str(marker), "retry": str(retry_marker), "status": "passed"}
        else:
            raise ValueError("full suite failed outside retryable browser tests")
        summary["status"] = "passed"
    except (OSError, ValueError, KeyError, TypeError) as error:
        summary["error"] = str(error)
        print("consumer gate: " + str(error), file=sys.stderr)
    (root / "consumer-gate-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print("consumer gate: " + summary["status"], flush=True)
    return 0 if summary["status"] == "passed" else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--kernel-root", required=True, type=Path)
    parser.add_argument("--kernel-commit", required=True)
    parser.add_argument("--compatibility-root", type=Path, default=Path("artifacts/kernel-compatibility"))
    args = parser.parse_args()
    product_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    return run_gate(args.compatibility_root, args.kernel_root, args.kernel_commit, product_commit)


if __name__ == "__main__":
    raise SystemExit(main())
