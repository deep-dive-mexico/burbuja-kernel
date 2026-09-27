#!/usr/bin/env python3
"""Run the explicitly exported generic unit projects after make build."""
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
for item in ET.parse(root / "Burbuja.slnx").getroot().findall("Project"):
    project = item.attrib["Path"]
    if project.startswith("tests/") and not project.startswith("tests/plugins/"):
        subprocess.run(["dotnet", "test", project, "-c", "Release", "--no-build",
                        "--filter", "Category!=Live&Category!=E2E", "--logger",
                        f"trx;LogFileName=unit-{Path(project).stem}.trx", "--results-directory",
                        str(root / "artifacts/test-results")], cwd=root, check=True)
