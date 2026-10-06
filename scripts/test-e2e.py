#!/usr/bin/env python3
"""Run public kernel journeys on disposable local hosts; never attach to a live deployment."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = "tests/Burbuja.E2E.Tests/Burbuja.E2E.Tests.csproj"
FILTERS = {
    "http": "Category=E2E&Category!=Live&OwnsHost!=true&Browser!=chromium",
    "own-host": "Category=E2E&Category!=Live&OwnsHost=true&Browser!=chromium",
    "browser": "Category=E2E&Category!=Live&Browser=chromium",
}


def clean_result(counters, code):
    return (code == 0 and counters["total"] > 0 and counters["passed"] == counters["total"]
            and not any(counters[name] for name in ("failed", "notExecuted", "aborted")))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("lane", choices=(*FILTERS, "all", "install-browser"))
    args = parser.parse_args()
    if args.lane == "install-browser":
        node = ET.parse(ROOT / "Directory.Packages.props").find(".//PackageVersion[@Include='Microsoft.Playwright']")
        command = ["npx", "--yes", "playwright@" + node.attrib["Version"], "install"]
        if sys.platform == "linux":
            command.append("--with-deps")
        subprocess.run([*command, "chromium"], cwd=ROOT, check=True)
        return
    environment = os.environ.copy()
    environment.update({"BURBUJA_TEST_ROOT": str(ROOT), "MSBUILDDISABLENODEREUSE": "1",
                        "DOTNET_CLI_USE_MSBUILD_SERVER": "0"})
    subprocess.run(["dotnet", "build", "Burbuja.slnx", "-c", "Release", "-m:1"], cwd=ROOT,
                   env=environment, check=True)
    directory = ROOT / "artifacts" / "e2e-runs" / uuid.uuid4().hex[:12]
    directory.mkdir(parents=True)
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    dirty = bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip())
    receipt = {"kernel_commit": head, "kernel_dirty": dirty, "status": "incomplete", "lanes": {}}
    path = directory / "receipt.json"
    path.write_text(json.dumps(receipt, indent=2) + "\n")
    failures = []
    for lane in FILTERS if args.lane == "all" else [args.lane]:
        trx = directory / (lane + ".trx")
        outcome = subprocess.run(["dotnet", "test", PROJECT, "-c", "Release", "--no-build",
                                  "--filter", FILTERS[lane], "--logger", "trx;LogFileName=" + trx.name,
                                  "--results-directory", str(directory)], cwd=ROOT, env=environment)
        counters = ET.parse(trx).find(".//{*}Counters") if trx.is_file() else None
        values = {name: int(counters.get(name, "0")) for name in
                  ("total", "passed", "failed", "notExecuted", "aborted")} if counters is not None else {}
        passed = bool(values) and clean_result(values, outcome.returncode)
        receipt["lanes"][lane] = {"status": "passed" if passed else "failed",
                                 "exit_code": outcome.returncode, "results": values, "trx": str(trx.relative_to(ROOT))}
        if not passed:
            failures.append(lane)
        path.write_text(json.dumps(receipt, indent=2) + "\n")
    receipt["status"] = "failed" if failures else "passed"
    path.write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps(receipt, indent=2))
    if failures:
        raise SystemExit("E2E lanes failed: " + ", ".join(failures))


if __name__ == "__main__":
    main()
