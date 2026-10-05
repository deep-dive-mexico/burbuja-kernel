"""A build's declared version must agree with the release line it is built from."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "kernel_version", Path(__file__).resolve().parents[2] / "scripts/kernel-version.py")
kernel_version = importlib.util.module_from_spec(spec)
spec.loader.exec_module(kernel_version)


class KernelVersionTests(unittest.TestCase):
    def test_main_must_carry_a_prerelease(self):
        kernel_version.check("0.5.0-dev", "refs/heads/main")
        with self.assertRaises(kernel_version.VersionError):
            kernel_version.check("0.5.0", "refs/heads/main")

    def test_release_branch_carries_its_own_stable_line(self):
        kernel_version.check("0.4.1", "refs/heads/release/0.4")
        for version in ("0.5.0", "0.4.1-dev", "1.4.0"):
            with self.assertRaises(kernel_version.VersionError):
                kernel_version.check(version, "refs/heads/release/0.4")

    def test_tag_must_equal_the_declared_version(self):
        kernel_version.check("0.4.0", "refs/tags/v0.4.0")
        for tag in ("v0.4.1", "0.4.0", "v0.4"):
            with self.assertRaises(kernel_version.VersionError):
                kernel_version.check("0.4.0", "refs/tags/" + tag)

    def test_other_refs_only_need_a_well_formed_version(self):
        kernel_version.check("0.5.0-dev", "refs/heads/feature/x")
        kernel_version.check("0.5.0-dev", "")

    def test_props_version_must_be_single_and_semantic(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for body in ("<Version>0.4</Version>", "", "<Version>1.0.0</Version><Version>1.0.1</Version>"):
                (root / "Directory.Build.props").write_text(f"<Project><PropertyGroup>{body}</PropertyGroup></Project>")
                with self.assertRaises(kernel_version.VersionError):
                    kernel_version.read_version(root)

    def test_pull_request_is_judged_by_its_base_branch(self):
        import os
        from unittest import mock
        with mock.patch.dict(os.environ, {"GITHUB_REF": "refs/pull/7/merge", "GITHUB_BASE_REF": "main"}):
            self.assertEqual(kernel_version.ci_ref(), "refs/heads/main")
        with mock.patch.dict(os.environ, {"GITHUB_REF": "refs/tags/v0.4.0", "GITHUB_BASE_REF": ""}):
            self.assertEqual(kernel_version.ci_ref(), "refs/tags/v0.4.0")


if __name__ == "__main__":
    unittest.main()
