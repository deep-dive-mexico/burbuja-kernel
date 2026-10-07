"""Every kernel validation runs its first consumer's complete product suite before publication or deployment."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


def jobs():
    workflow = (ROOT / ".github/workflows/ci.yml").read_text()
    parts = re.split(r"(?m)^  (?=[a-z-]+:\n)", workflow.split("\njobs:\n", 1)[1])
    return {part.split(":", 1)[0]: part for part in parts if part}


class ConsumerGateTests(unittest.TestCase):
    def test_akos_suite_runs_against_the_exact_validated_kernel_commit(self):
        job = jobs()["consumer-akos"]
        self.assertIn("needs: [prepare-release, build-and-unit]", job)
        self.assertIn("KERNEL_COMMIT: ${{ needs.prepare-release.outputs.sha || github.sha }}", job)
        self.assertIn("ref: ${{ needs.prepare-release.outputs.sha || github.sha }}", job)
        self.assertIn("repository: deep-dive-mexico/akos-server", job)
        self.assertIn("ref: dev\n", job)
        self.assertIn("ssh-key: ${{ secrets.AKOS_READ_SSH_KEY }}", job)
        self.assertEqual(job.count("persist-credentials: false"), job.count("actions/checkout@"))
        self.assertIn('scripts/test-kernel-candidate.py --kernel-root ../kernel --kernel-commit "$KERNEL_COMMIT"', job)
        self.assertIn("scripts/test-product.py install-browser", job)
        self.assertIn("path: akos/artifacts/kernel-compatibility/", job)

    def test_consumer_hosts_use_a_port_band_disjoint_from_the_product_and_kernel_lanes(self):
        job = jobs()["consumer-akos"]
        low = int(re.search(r'BURBUJA_PORT_MIN: "(\d+)"', job).group(1))
        high = int(re.search(r'BURBUJA_PORT_MAX: "(\d+)"', job).group(1))
        self.assertLess(low, high)
        # Product lanes claim 18025-18099 (akos-server scripts/test-product.py); kernel lanes default to 18100-18399.
        self.assertTrue(high < 18025 or low > 18399, (low, high))

    def test_publication_and_deployment_wait_for_the_consumer_gate(self):
        for name in ("release", "deploy"):
            job = jobs()[name]
            self.assertIn("consumer-akos", re.search(r"needs: \[(.+)\]", job).group(1), name)
            self.assertIn("needs.consumer-akos.result == 'success'", job, name)

    def test_consumer_gate_has_no_secrets_beyond_the_read_only_product_key(self):
        job = jobs()["consumer-akos"]
        self.assertEqual(re.findall(r"secrets\.[A-Z_]+", job), ["secrets.AKOS_READ_SSH_KEY"])
        self.assertNotIn("AWS_", job)
        self.assertNotIn("permissions:", job)


if __name__ == "__main__":
    unittest.main()
