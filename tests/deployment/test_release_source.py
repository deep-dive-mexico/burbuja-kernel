"""The one supported instruction alias must not weaken the source boundary."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "release_source", Path(__file__).resolve().parents[2] / "scripts/release-source.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseAliasTests(unittest.TestCase):
    def test_router_alias_exports_regular_instruction_content(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "AGENTS.md").write_text("Tracked router instructions\n")
            (root / "CLAUDE.md").symlink_to("AGENTS.md")
            self.assertEqual(release.read_tracked(root, "CLAUDE.md"), b"Tracked router instructions\n")

    def test_other_symlinks_and_redirected_router_alias_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "outside.md").write_text("Other content\n")
            for name in ("CLAUDE.md", "README.md"):
                (root / name).symlink_to("outside.md")
                with self.assertRaises(release.ReleaseError):
                    release.read_tracked(root, name)
