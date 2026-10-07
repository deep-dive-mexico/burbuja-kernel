"""Every kernel validation runs its first consumer's complete product suite before publication or deployment."""
from pathlib import Path
import re
import unittest
import json
import tempfile
import copy
import io
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]


def jobs():
    workflow = (ROOT / ".github/workflows/ci.yml").read_text()
    parts = re.split(r"(?m)^  (?=[a-z-]+:\n)", workflow.split("\njobs:\n", 1)[1])
    return {part.split(":", 1)[0]: part for part in parts if part}


import importlib.util
spec = importlib.util.spec_from_file_location("consumer_gate", ROOT / "scripts/consumer-gate.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class ConsumerGateTests(unittest.TestCase):
    def run_campaign(self, attempts, expected, stale=False):
        """Exercise orchestration with on-disk evidence, without a .NET/browser dependency."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            if stale:
                old = root / "candidate-stale" / gate.MARKER
                old.parent.mkdir()
                old.write_text("{}")
            index = 0

            def execute(kernel_root, commit, lane=None):
                nonlocal index
                plan = attempts[index]
                index += 1
                if plan.get("missing"):
                    return plan.get("code", 1)
                snapshot = root / ("candidate-" + str(index))
                snapshot.mkdir()
                metadata = {"kind": gate.KIND, "kernel_commit": commit, "product_commit": "b" * 40,
                            "kernel_root": str(kernel_root.resolve()), "status": "passed", "lanes": {}}
                for name in ({lane} if lane else gate.LANES):
                    failed = name in plan.get("failed", [])
                    status = "failed" if failed else "passed"
                    path = snapshot / (name + "-receipt.json")
                    receipt = {"evidence_kind": gate.KIND, "lane": name, "kernel_commit": commit,
                               "product_commit": "b" * 40, "kernel_release": None, "status": status,
                               "results": {"total": 4, "passed": 3 if failed else 4, "failed": int(failed),
                                           "notExecuted": 0, "aborted": 0}}
                    receipt.update(copy.deepcopy(plan.get("receipts", {}).get(name, {})))
                    path.write_text(json.dumps(receipt))
                    metadata["lanes"][name] = {"status": status, "exit_code": int(failed),
                                              "receipt": str(path), "log": str(snapshot / (name + ".log"))}
                if plan.get("failed"):
                    metadata["status"] = "failed"
                metadata.update(plan.get("metadata", {}))
                marker = snapshot / gate.MARKER
                marker.write_text("{" if plan.get("malformed") else json.dumps(metadata))
                if plan.get("missing_receipt"):
                    (snapshot / "browser-receipt.json").unlink()
                return plan.get("code", int(bool(plan.get("failed"))))

            with patch.object(gate, "harness", side_effect=execute) as harness, patch.object(gate.sys, "stdout", new_callable=io.StringIO) as output:
                code = gate.run_gate(root, ROOT, "c" * 40, "b" * 40)
            self.assertEqual(code, expected)
            summary = json.loads((root / "consumer-gate-summary.json").read_text())
            self.assertEqual(summary["status"], "passed" if expected == 0 else "failed")
            self.assertIn(json.dumps(summary, indent=2), output.getvalue())
            return summary, harness.call_count

    def test_all_four_fresh_lanes_pass_with_source_bound_summary(self):
        summary, calls = self.run_campaign([{}], 0, stale=True)
        self.assertEqual(set(summary["lanes"]), gate.LANES)
        self.assertEqual(summary["kernel_commit"], "c" * 40)
        self.assertEqual(summary["product_commit"], "b" * 40)
        self.assertEqual(calls, 1)
        self.assertTrue(all(lane["results"]["total"] > 0 for lane in summary["lanes"].values()))

    def test_browser_retry_combines_three_initial_lanes_and_fresh_browser_evidence(self):
        summary, calls = self.run_campaign([{"failed": ["browser"]}, {}], 0)
        self.assertEqual(set(summary["lanes"]), gate.LANES)
        self.assertEqual(len(summary["attempts"]), 2)
        self.assertEqual(summary["browser_retry"]["status"], "passed")
        self.assertEqual(calls, 2)

    def test_failure_in_each_non_browser_lane_is_final(self):
        for lane in ("unit", "http", "own-host"):
            with self.subTest(lane=lane):
                _, calls = self.run_campaign([{"failed": [lane, "browser"]}], 1)
                self.assertEqual(calls, 1)

    def test_success_exit_without_fresh_marker_fails(self):
        self.run_campaign([{"missing": True, "code": 0}], 1)

    def test_malformed_marker_fails(self):
        self.run_campaign([{"malformed": True}], 1)

    def test_marker_identity_and_lane_mismatches_fail(self):
        for changes in ({"kernel_commit": "d" * 40}, {"product_commit": "d" * 40},
                        {"kernel_root": "/unrelated"}, {"kind": "stable"}, {"lanes": {}}):
            with self.subTest(changes=changes):
                self.run_campaign([{"metadata": changes}], 1)

    def test_missing_receipt_fails_even_after_success_exit(self):
        self.run_campaign([{"missing_receipt": True}], 1)

    def test_empty_skipped_aborted_and_failed_results_cannot_pass(self):
        for changes in ({"total": 0, "passed": 0}, {"notExecuted": 1}, {"aborted": 1},
                        {"failed": 1}, {"passed": 3}, {"total": -1}, {"total": "4"}):
            counts = {"total": 4, "passed": 4, "failed": 0, "notExecuted": 0, "aborted": 0, **changes}
            with self.subTest(changes=changes):
                self.run_campaign([{"receipts": {"browser": {"results": counts}}}], 1)

    def test_lane_receipt_identity_must_match_marker(self):
        for changes in ({"kernel_commit": "d" * 40}, {"product_commit": "d" * 40},
                        {"kernel_release": "v99.0.0"}, {"lane": "unit"}, {"evidence_kind": "stable"}):
            with self.subTest(changes=changes):
                self.run_campaign([{"receipts": {"browser": changes}}], 1)

    def test_browser_build_failure_has_no_retry(self):
        _, calls = self.run_campaign([{"failed": ["browser"], "receipts": {"browser": {
            "status": "incomplete", "results": {"total": 0, "passed": 0, "failed": 0,
                                                   "notExecuted": 0, "aborted": 0}}}}], 1)
        self.assertEqual(calls, 1)

    def test_retry_failure_is_final_after_two_attempts(self):
        _, calls = self.run_campaign([{"failed": ["browser"]}, {"failed": ["browser"]}], 1)
        self.assertEqual(calls, 2)

    def test_retry_success_without_fresh_receipt_is_rejected(self):
        self.run_campaign([{"failed": ["browser"]}, {"missing": True, "code": 0}], 1)

    def test_retry_cannot_change_product_or_kernel(self):
        for field in ("product_commit", "kernel_commit"):
            with self.subTest(field=field):
                self.run_campaign([{"failed": ["browser"]}, {"metadata": {field: "d" * 40}}], 1)

    def test_retry_cannot_reduce_test_count(self):
        self.run_campaign([{"failed": ["browser"]}, {"receipts": {"browser": {
            "results": {"total": 3, "passed": 3, "failed": 0, "notExecuted": 0, "aborted": 0}}}}], 1)

    def test_preflight_failure_cannot_reuse_a_stale_browser_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            marker = root / "candidate-old" / gate.MARKER
            marker.parent.mkdir()
            marker.write_text(json.dumps({"status": "failed", "lanes": {
                lane: {"status": "failed" if lane == "browser" else "passed"}
                for lane in ("unit", "http", "own-host", "browser")}}))
            arguments = ["consumer-gate.py", "--kernel-root", str(ROOT), "--kernel-commit", "c" * 40,
                         "--compatibility-root", str(root)]
            with patch.object(gate.sys, "argv", arguments), patch.object(gate, "harness", side_effect=[1, 0]) as harness:
                self.assertEqual(gate.main(), 1)
                self.assertEqual(harness.call_count, 1)

    def test_only_a_browser_only_failure_earns_a_retry(self):
        passed = {"status": "passed"}
        lanes = {l: {"status": "passed"} for l in ("unit", "http", "own-host", "browser")}
        self.assertFalse(gate.only_browser_failed({"status": "failed", "lanes": {**lanes, "unit": {"status": "failed"}}}))
        self.assertFalse(gate.only_browser_failed({"status": "failed", "lanes": {**lanes, "http": {"status": "failed"}, "browser": {"status": "failed"}}}))
        self.assertFalse(gate.only_browser_failed({"status": "failed", "lanes": {k: v for k, v in lanes.items() if k != "own-host"}}))
        self.assertFalse(gate.only_browser_failed(passed))
        self.assertTrue(gate.only_browser_failed({"status": "failed", "lanes": {**lanes, "browser": {"status": "failed"}}}))

    def test_akos_suite_runs_against_the_exact_validated_kernel_commit(self):
        job = jobs()["consumer-akos"]
        self.assertIn("needs: [prepare-release, build-and-unit]", job)
        self.assertIn("KERNEL_COMMIT: ${{ needs.prepare-release.outputs.sha || github.sha }}", job)
        self.assertIn("ref: ${{ needs.prepare-release.outputs.sha || github.sha }}", job)
        self.assertIn("repository: deep-dive-mexico/akos-server", job)
        self.assertIn("product-ref: [dev, main]", job)
        self.assertIn("ref: ${{ matrix.product-ref }}", job)
        self.assertIn("fail-fast: false", job)
        self.assertIn("max-parallel: 1", job)
        self.assertIn("name: akos-consumer-compatibility-${{ matrix.product-ref }}", job)
        self.assertIn("include-hidden-files: true", job)
        self.assertIn("ssh-key: ${{ secrets.AKOS_READ_SSH_KEY }}", job)
        self.assertEqual(job.count("persist-credentials: false"), job.count("actions/checkout@"))
        self.assertIn('../kernel/scripts/consumer-gate.py --kernel-root ../kernel --kernel-commit "$KERNEL_COMMIT"', job)
        self.assertIn("scripts/test-product.py install-browser", job)
        self.assertIn("akos/artifacts/kernel-compatibility/consumer-gate-summary.json", job)
        self.assertIn("akos/artifacts/kernel-compatibility/candidate-*/.kernel-candidate-test-only.json", job)
        self.assertIn("akos/artifacts/kernel-compatibility/candidate-*/artifacts/diag/", job)

    def test_diagnostic_upload_failure_cannot_override_test_outcomes(self):
        for name in ("consumer-akos", "build-and-unit"):
            job = jobs()[name]
            self.assertEqual(job.count("continue-on-error: true"), 1)
            upload = job.split("uses: actions/upload-artifact@v4", 1)[1]
            self.assertIn("continue-on-error: true", upload)
            self.assertIn("::warning::Artifact upload failed", upload)
            self.assertIn("retention-days: 7", upload)

    def test_consumer_suite_holds_the_host_wide_akos_lane_lock(self):
        job = jobs()["consumer-akos"]
        self.assertIn("flock -E 75 --verbose --timeout 3600 /home/runner/dive-akos-lanes.lock", job)
        self.assertLess(job.index("flock -E 75"), job.index("scripts/test-product.py install-browser"))
        self.assertLess(job.index("scripts/test-product.py install-browser"), job.index("scripts/consumer-gate.py"))
        self.assertIn("timeout-minutes: 120", job)

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
