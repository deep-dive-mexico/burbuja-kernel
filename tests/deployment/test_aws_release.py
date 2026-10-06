"""Keep tenant deploys on their own branch, host and public name."""
import importlib.util
from pathlib import Path
import shlex
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("aws_release", ROOT / "scripts/aws-release.py")
aws_release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(aws_release)


class TenantDeployTests(unittest.TestCase):
    def test_each_branch_owns_one_tenant(self):
        self.assertEqual(aws_release.public_host("APP", "production", "refs/heads/main"), "APP.dive.ai")
        self.assertEqual(aws_release.public_host("APP", "dev", "refs/heads/dev"), "APP-dev.dive.ai")
        for tenant, ref in (("production", "refs/heads/dev"), ("dev", "refs/heads/main"),
                            ("dev", "refs/heads/feature"), ("staging", "refs/heads/dev")):
            with self.assertRaises(ValueError):
                aws_release.public_host("APP", tenant, ref)

    def test_host_must_declare_the_requested_tenant(self):
        with tempfile.TemporaryDirectory() as temporary:
            config = Path(temporary) / "config.json"
            probe = aws_release.install_commands("releases/x/y/release.tgz", "dev")[1]
            probe = probe.replace("/opt/dive/venv/bin/python", "python3").replace("/opt/dive/config.json", str(config))
            for content, accepted in (('{"app": "x"}', False), ('{"app": "x", "tenant": "dev"}', True),
                                      ('{"app": "x", "tenant": "production"}', False)):
                config.write_text(content)
                result = subprocess.run(["bash", "-c", probe], capture_output=True, text=True)
                self.assertEqual(result.returncode == 0, accepted, content)

    def test_install_runs_only_after_the_probe(self):
        commands = aws_release.install_commands("releases/x/y/release.tgz", "production")
        self.assertEqual(commands[0], "set -eu")
        self.assertEqual(shlex.split(commands[-1]), ["/opt/dive/host.py", "install", "releases/x/y/release.tgz"])

    def test_each_job_uses_its_own_runner(self):
        import re
        workflow = (ROOT / ".github/workflows/ci.yml").read_text()
        jobs = re.split(r"(?m)^  (?=[a-z-]+:\n)", workflow.split("\njobs:\n", 1)[1])
        for job in filter(None, jobs):
            name = job.split(":", 1)[0]
            runner = re.search(r"(?m)^    runs-on: (.+)$", job).group(1)
            self.assertNotIn("ubuntu-latest", runner, name)
            expected = {"deploy": "dive-kernel", "prepare-release": "dive-release",
                        "release": "dive-release"}.get(name, "dive-ci")
            self.assertIn('"' + expected + '"', runner, name)

    def test_compose_volume_is_named_by_the_installer(self):
        self.assertIn("name: ${APP_STATE_VOLUME:-", (ROOT / "deploy/aws/compose.yaml").read_text())


if __name__ == "__main__":
    unittest.main()
