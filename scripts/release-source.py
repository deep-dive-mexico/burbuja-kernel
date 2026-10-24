#!/usr/bin/env python3
"""Export only tracked, clean, committed source from an authoritative product repository.

This is also shipped as scripts/release-source.py in both repositories. It never reads
the historical integration checkout, fetches, commits, or modifies its source.
"""

from __future__ import annotations

import argparse
import fnmatch
import hashlib
import ipaddress
import json
import posixpath
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
REPOSITORIES = {
    "kernel": "https://github.com/deep-dive-mexico/burbuja-kernel.git",
    "akos": "https://github.com/deep-dive-mexico/akos-server.git",
}
BLOCKED = {"bin", "obj", ".git", ".claude", ".opencode", "node_modules", "artifacts",
           "testresults", "__pycache__", "akos-source", "soka-fuentes", "personal-data",
           "secrets", "backups", "backup", "vendor", "recordings", "state", "data"}
SUFFIXES = {".cs", ".csproj", ".razor", ".html", ".css", ".js", ".svg", ".c", ".h",
            ".md", ".json", ".jsonl", ".props", ".targets", ".slnx", ".yml",
            ".yaml", ".py", ".sh", ".txt", ".woff2"}
SECRET = re.compile(rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----\s+[A-Za-z0-9+/=]{40}|\b(?:AKIA|ASIA)[A-Z0-9]{16}\b|\bgh[pousr]_[A-Za-z0-9]{30,}\b")


class ReleaseError(Exception):
    pass


def git(source: Path, *args: str) -> str:
    result = subprocess.run(["git", "-C", str(source), *args], capture_output=True, check=False)
    if result.returncode:
        raise ReleaseError(f"cannot inspect committed source ({' '.join(args)}): {result.stderr.decode(errors='replace').strip()}")
    return result.stdout.decode().strip()


def allowed(path: str) -> bool:
    relative = PurePosixPath(path)
    if (relative.is_absolute() or ".." in relative.parts or "\\" in path or ":" in path
            or not relative.parts or relative.name.endswith((".bak", ".orig", ".tmp", "~"))):
        return False
    if path in {".gitignore", ".github/workflows/ci.yml", ".github/workflows/workloads.yml", "nuget.config", "LICENSE", "Makefile",
                ".dockerignore", "deploy/Dockerfile"}:
        return True
    if any(part.lower() in BLOCKED or part.startswith(".") or part.lower().startswith(("backup-", "secrets."))
           for part in relative.parts):
        # Source contracts legitimately use this namespace, not a credential directory.
        if not (relative.parts[:3] in {("src", "Burbuja.Core.Abstractions", "Secrets"),
                                       ("src", "Burbuja.Core.Abstractions", "Artifacts")}
                and all(part.lower() not in BLOCKED and not part.startswith(".") for part in relative.parts[3:])):
            return False
    return relative.suffix in SUFFIXES


def clean_source(source: Path, repository: str) -> str:
    source = source.resolve()
    if not source.is_dir():
        raise ReleaseError(f"source does not exist: {source}")
    if Path(git(source, "rev-parse", "--show-toplevel")).resolve() != source:
        raise ReleaseError("source must be its own repository, not a directory inside the legacy checkout")
    github_path = repository.removeprefix("https://github.com/")
    origins = {repository, repository.removesuffix(".git"),
               "git@github.com:" + github_path, "ssh://git@github.com/" + github_path}
    if git(source, "remote", "get-url", "origin") not in origins:
        raise ReleaseError(f"source origin must be {repository}")
    sha = git(source, "rev-parse", "HEAD")
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        raise ReleaseError("source requires a full committed HEAD")
    if git(source, "status", "--porcelain", "--untracked-files=all"):
        raise ReleaseError("source must be clean and committed before official release")
    return sha


def read_tracked(source: Path, relative: str) -> bytes:
    source = source.resolve()
    if not allowed(relative):
        raise ReleaseError(f"tracked path outside release source boundary: {relative}")
    path = source / relative
    # The router alias is the sole supported source symlink. Export its actual
    # tracked instructions as a regular file; all other symlinks remain refused.
    if relative == "CLAUDE.md" and path.is_symlink() and path.readlink() == Path("AGENTS.md"):
        return read_tracked(source, "AGENTS.md")
    for candidate in (path, *path.parents):
        if candidate == source:
            break
        if candidate.is_symlink():
            raise ReleaseError(f"symlink source refused: {relative}")
    if not path.is_file() or not path.resolve().is_relative_to(source):
        raise ReleaseError(f"tracked source missing or escaping checkout: {relative}")
    content = path.read_bytes()
    content_to_scan = content
    for dummy in (b"AKIAIOSFODNN7EXAMPLE", b"ASIAIOSFODNN7EXAMPLE", b"ghp_abcdefghijklmnopqrstuvwxyz0123456789"):
        content_to_scan = content_to_scan.replace(dummy, b"")
    if SECRET.search(content_to_scan):
        raise ReleaseError(f"credential signature in tracked source: {relative}")
    return content


def updated_props(data: bytes, repository: str, sha: str) -> bytes:
    root = ET.fromstring(data)
    group = root.find("PropertyGroup")
    repo_prop = group.find("RepositoryUrl") if group is not None else None
    if repo_prop is None or repo_prop.text not in {repository, repository.removesuffix(".git")}:
        raise ReleaseError("Directory.Build.props must identify this authoritative repository")
    assert group is not None
    for node in root.findall(".//RepositoryUrl"):
        node.text = repository
    for name, value in (("PackageProjectUrl", repository.removesuffix(".git")),
                        ("RepositoryCommit", sha), ("SourceRevisionId", sha)):
        nodes = root.findall(f".//{name}")
        if not nodes:
            nodes = [ET.SubElement(group, name)]
        for node in nodes:
            node.text = value
    ET.indent(root, space="  ")
    return ET.tostring(root, encoding="utf-8") + b"\n"


def validate_build_inputs(files: dict[str, bytes], product: str) -> None:
    for project, data in files.items():
        if not project.endswith(".csproj"):
            continue
        document = ET.fromstring(data)
        for item in document.iter():
            if item.tag not in {"ProjectReference", "Compile", "EmbeddedResource", "Content", "None"}:
                continue
            include = item.get("Include")
            if not include:
                continue
            include = include.replace("\\", "/")
            if include.startswith("$(BurbujaRoot)/"):
                target = include.removeprefix("$(BurbujaRoot)/")
                if not allowed(target) or (product == "kernel" and target not in files):
                    raise ReleaseError(f"unresolved kernel project/source dependency: {project}: {include}")
                continue
            if include.startswith("$(WorkbenchRoot)/") and product == "kernel" and project == "tests/Burbuja.Hosting.Tests/Burbuja.Hosting.Tests.csproj":
                if document.findtext(".//WorkbenchRoot") != "$(MSBuildThisFileDirectory)../../examples/Example.Workbench":
                    raise ReleaseError("WorkbenchRoot no longer resolves to the committed example")
                target = "examples/Example.Workbench/" + include.removeprefix("$(WorkbenchRoot)/")
                if item.tag != "ProjectReference" or target not in files:
                    raise ReleaseError(f"missing committed Workbench project: {include}")
                continue
            if "$" in include or "@(" in include or "%(" in include:
                raise ReleaseError(f"unreviewed dynamic build input in official source: {project}: {include}")
            target = posixpath.normpath(posixpath.join(posixpath.dirname(project), include))
            wildcard = "*" in target or "?" in target
            if target.startswith("../") or target == ".." or (not wildcard and not allowed(target)):
                raise ReleaseError(f"source build input escapes boundary: {project}: {include}")
            matches = (any(fnmatch.fnmatchcase(name, target) or fnmatch.fnmatchcase(name, target.replace("**/", ""))
                           for name in files) if wildcard else target in files)
            if not matches:
                raise ReleaseError(f"missing committed build input: {project}: {include}")


def validate_kernel_fixtures(files: dict[str, bytes]) -> None:
    base = "tests/Burbuja.HostFacts.MacOs.UnitTests/fixtures/"
    provenance = files.get(base + "PROVENANCE.md", b"")
    if not provenance.startswith(b"# Synthetic HostFacts parser fixtures\n"):
        raise ReleaseError("kernel release requires authored synthetic HostFacts fixtures")
    status = files.get(base + "tailscale-status-online.txt", b"")
    if not status or b".local" in status or b"demo-" not in status:
        raise ReleaseError("HostFacts tailnet fixture must contain only synthetic nodes")
    for line in status.decode().splitlines():
        first = line.split(maxsplit=1)[0]
        try:
            address = ipaddress.IPv4Address(first)
        except ipaddress.AddressValueError as error:
            raise ReleaseError("HostFacts tailnet fixture has an unexpected address") from error
        if address not in ipaddress.IPv4Network("100.64.0.0/24"):
            raise ReleaseError("HostFacts tailnet fixture has a non-synthetic node address")
        for address_text in re.findall(r"\[([0-9a-fA-F:]+)\]", line):
            if ipaddress.IPv6Address(address_text) not in ipaddress.IPv6Network("2001:db8::/32"):
                raise ReleaseError("HostFacts fixture has a non-documentation IPv6 address")
    for name, data in files.items():
        if not name.startswith(base) or not name.endswith(".txt"):
            continue
        if b".local" in data or b"/Users/" in data or re.search(rb'"(?:Serial|DeviceName|MfgData)"\s*=', data):
            raise ReleaseError(f"HostFacts fixture contains private device/account fields: {name}")


def plan_release(source: Path, product: str | None = None) -> dict[str, bytes]:
    source = source.resolve()
    if product is None:
        product = "kernel" if (source / "Burbuja.slnx").is_file() and not (source / "Akos.slnx").exists() else "akos"
    if product not in REPOSITORIES:
        raise ReleaseError(f"unknown product: {product}")
    repository = REPOSITORIES[product]
    sha = clean_source(source, repository)
    solution = "Burbuja.slnx" if product == "kernel" else "Akos.slnx"
    required = (solution, "Directory.Build.props", "Directory.Packages.props", ".gitignore",
                "scripts/release-source.py")
    if not (source / solution).is_file() or (source / ("Akos.slnx" if product == "kernel" else "Burbuja.slnx")).exists():
        raise ReleaseError(f"{product} requires only its own solution and source identity")
    required_project = "src/Burbuja.Hosting/Burbuja.Hosting.csproj" if product == "kernel" else "src/Akos.Host/Akos.Host.csproj"
    forbidden_project = "src/Akos.Host/Akos.Host.csproj" if product == "kernel" else "src/Burbuja.Hosting/Burbuja.Hosting.csproj"
    if not (source / required_project).is_file() or (source / forbidden_project).exists():
        raise ReleaseError("source contains a mixed or wrong product project identity")
    names = git(source, "ls-files", "-z").split("\0")
    # A NUL-separated Git listing must end in a NUL. No discovery of untracked files.
    if names[-1] != "" or len(names[:-1]) != len(set(names[:-1])):
        raise ReleaseError("invalid tracked source inventory")
    names.pop()
    if any(name not in names for name in (*required, required_project)):
        raise ReleaseError("required official source/build input is not tracked")
    files = {name: read_tracked(source, name) for name in names if name != "export-manifest.json"}
    if product == "akos":
        lock = json.loads(files.get("kernel.lock.json", b"{}"))
        if (lock.get("repository") != REPOSITORIES["kernel"] or not re.fullmatch(r"[0-9a-f]{40}", lock.get("commit", ""))
                or lock.get("path") != "vendor/burbuja"):
            raise ReleaseError("private release requires an immutable authoritative kernel.lock.json")
    else:
        validate_kernel_fixtures(files)
    validate_build_inputs(files, product)
    files["Directory.Build.props"] = updated_props(files["Directory.Build.props"], repository, sha)
    report = {"version": 1, "product": product, "mode": "official-release",
              "source_repository": repository, "source_commit": sha, "final_synchronization": True,
              "files": {name: hashlib.sha256(data).hexdigest() for name, data in sorted(files.items())}}
    files["export-manifest.json"] = (json.dumps(report, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode()
    return files


def write_export(files: dict[str, bytes], destination: Path, source: Path) -> None:
    destination = destination.absolute()
    if destination.exists() or destination.is_symlink():
        raise ReleaseError(f"destination already exists: {destination}")
    if not destination.parent.is_dir() or destination.resolve().is_relative_to(source.resolve()):
        raise ReleaseError("destination needs an existing parent outside the source repository")
    staging = Path(tempfile.mkdtemp(prefix=".release-source-", dir=destination.parent))
    try:
        for relative, data in sorted(files.items()):
            target = staging / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            target.chmod(0o755 if relative.endswith((".py", ".sh")) else 0o644)
        if destination.exists() or destination.is_symlink():
            raise ReleaseError("destination appeared during release export")
        staging.rename(destination)
    finally:
        if staging.exists():
            shutil.rmtree(staging)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=ROOT)
    parser.add_argument("--destination", type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args(argv)
    if not args.check and args.destination is None:
        parser.error("--destination is required unless --check is used")
    try:
        files = plan_release(args.source)
        if not args.check:
            write_export(files, args.destination, args.source)
        report = json.loads(files["export-manifest.json"])
        print(json.dumps({"product": report["product"], "mode": report["mode"], "source_commit": report["source_commit"],
                          "files": len(files), "destination": str(args.destination) if not args.check else None}, indent=2))
        return 0
    except (ReleaseError, OSError, ValueError, ET.ParseError) as error:
        print(f"release export refused: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
