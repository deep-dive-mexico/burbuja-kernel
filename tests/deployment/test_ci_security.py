"""Keep public validation isolated from private source and deployment authority."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


def jobs():
    text = (ROOT / ".github/workflows/ci.yml").read_text().split("\njobs:\n", 1)[1]
    return {block.split(":", 1)[0]: block for block in re.split(r"(?m)^  (?=[a-z-]+:\n)", text) if block.strip()}


class PublicCiSecurityTests(unittest.TestCase):
    def test_every_job_uses_a_github_hosted_vm(self):
        for name, job in jobs().items():
            self.assertRegex(job, r"(?m)^    runs-on: ubuntu-24\.04$", name)
            self.assertNotIn("DOTNET_INSTALL_DIR", job, name)

    def test_oidc_authority_is_confined_to_the_deployment_environment(self):
        for name, job in jobs().items():
            if name == "deploy":
                self.assertIn("id-token: write", job)
                self.assertIn("environment:", job)
                self.assertIn("vars.AWS_DEPLOY_ROLE", job)
                self.assertIn("configure-aws-credentials@", job)
                self.assertIn("mask-aws-account-id: true", job)
            else:
                self.assertNotIn("id-token: write", job, name)
                self.assertNotIn("configure-aws-credentials@", job, name)
        text = (ROOT / ".github/workflows/ci.yml").read_text().split("\njobs:\n", 1)[0]
        self.assertIn("permissions:\n  contents: read", text)
        self.assertNotIn("id-token: write", text)

    def test_private_consumer_cannot_run_on_pull_requests_or_publish_diagnostics(self):
        consumer = jobs().get("consumer-akos")
        if consumer is None:
            return
        self.assertIn("github.event_name == 'push' || github.event_name == 'workflow_dispatch'", consumer)
        self.assertNotIn("actions/upload-artifact", consumer)
        self.assertNotIn("flock ", consumer)
        self.assertNotIn("continue-on-error", consumer)
        self.assertIn('> "$RUNNER_TEMP/consumer-gate.log" 2>&1', consumer)
        self.assertEqual(consumer.count("persist-credentials: false"), consumer.count("actions/checkout@"))

    def test_manual_deployment_is_opt_in_and_uses_installed_dependencies(self):
        deploy = jobs().get("deploy")
        if deploy is None:
            return
        text = (ROOT / ".github/workflows/ci.yml").read_text()
        self.assertIn("type: boolean\n        default: false", text)
        self.assertIn("name: Verify short-lived deployment credentials", deploy)
        rollout = deploy.split("name: Build, publish and deploy immutable image", 1)[1]
        self.assertIn("if: github.event_name == 'push' || inputs.deploy", rollout)
        self.assertIn('"$RUNNER_TEMP/kernel-deploy-venv/bin/python" scripts/aws-release.py', rollout)
        self.assertNotIn("/opt/dive/venv", deploy)
        self.assertIn("python3 -m venv", deploy)


if __name__ == "__main__":
    unittest.main()
