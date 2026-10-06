"""Stable publication validates the versioned source and preserves tag and rerun identity."""
import base64
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location(
    "github_release", Path(__file__).resolve().parents[2] / "scripts/github-release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
SOURCE_SHA = "a" * 40
SHA = "b" * 40
TAG = "v0.5.0"
PROPS = '<Project><PropertyGroup><Version>0.5.0-dev</Version></PropertyGroup></Project>\n'


class ReleaseIdentityTests(unittest.TestCase):
    def test_next_patch_uses_only_stable_tags_in_the_current_minor(self):
        self.assertEqual(release.next_version("0.5.0-dev", ["v0.4.9", "v0.6.0", "v0.5.7-dev"]), "0.5.0")
        self.assertEqual(release.next_version("0.5.0-dev", ["v0.5.0", "v0.5.2"]), "0.5.3")
        self.assertEqual(release.next_version("0.5.7", ["v0.5.2"]), "0.5.7")

    def test_main_requires_the_prepared_stable_version(self):
        self.assertEqual(release.release_tag("0.5.0", "refs/heads/main", "push", TAG), TAG)
        for version, tag in (("0.5.0-dev", TAG), ("0.5.0", ""), ("0.5.1", TAG)):
            with self.assertRaises(ValueError):
                release.release_tag(version, "refs/heads/main", "push", tag)

    def test_other_branches_and_manual_or_pr_runs_do_not_publish(self):
        for ref, event in (("refs/heads/feature", "push"), ("refs/heads/main", "pull_request"),
                           ("refs/heads/main", "workflow_dispatch"), ("refs/tags/v0.5.0", "workflow_dispatch")):
            self.assertIsNone(release.release_tag("0.5.0", ref, event))

    def test_stable_tag_must_match_source_version_and_prereleases_are_skipped(self):
        self.assertEqual(release.release_tag("0.5.0", "refs/tags/v0.5.0", "push"), TAG)
        with self.assertRaises(ValueError):
            release.release_tag("0.5.1", "refs/tags/v0.5.0", "push")
        self.assertIsNone(release.release_tag("0.5.0-dev", "refs/tags/v0.5.0-dev", "push"))

    def test_missing_duplicate_or_malformed_versions_are_refused(self):
        for body in ("", "<Version>bad</Version>", "<Version>0.4.0</Version><Version>0.4.0</Version>"):
            with self.assertRaises(ValueError):
                release.declared_version(f"<Project>{body}</Project>")


class PreparationTests(unittest.TestCase):
    def test_preparation_changes_only_version_and_reserves_the_next_patch(self):
        def gh(*args, **kwargs):
            return "v0.5.0\n" if args[2].endswith("/tags") else "refs/heads/automation/release-100/v0.5.1\n"

        def api(path, payload=None):
            if payload is None:
                return {"tree": {"sha": "c" * 40}}
            return {"sha": SHA}

        with mock.patch.object(release, "optional_api", return_value=[]), \
                mock.patch.object(release, "gh", side_effect=gh), \
                mock.patch.object(release, "api", side_effect=api) as requests:
            result = release.prepare("org/repo", SOURCE_SHA, "123", PROPS)
        self.assertEqual(result, {"sha": SHA, "tag": "v0.5.2"})
        tree = next(call.args[1] for call in requests.call_args_list if call.args[0].endswith("/git/trees"))
        self.assertEqual(tree["tree"], [{"path": "Directory.Build.props", "mode": "100644", "type": "blob",
                                        "content": PROPS.replace("0.5.0-dev", "0.5.2")}])
        commit = next(call.args[1] for call in requests.call_args_list if len(call.args) > 1 and call.args[0].endswith("/git/commits"))
        self.assertEqual(commit["parents"], [SOURCE_SHA])
        self.assertEqual(requests.call_args_list[-1].args[1],
                         {"ref": "refs/heads/automation/release-123/v0.5.2", "sha": SHA})

    def test_rerun_reuses_the_staged_commit_without_allocating_another_version(self):
        props = base64.b64encode(PROPS.replace("0.5.0-dev", "0.5.0").encode()).decode()
        def api(path, payload=None):
            return {"parents": [{"sha": SOURCE_SHA}]} if "/git/commits/" in path else {"content": props}
        with mock.patch.object(release, "optional_api", return_value=[{"object": {"sha": SHA}}]), \
                mock.patch.object(release, "api", side_effect=api), mock.patch.object(release, "gh") as gh:
            self.assertEqual(release.prepare("org/repo", SOURCE_SHA, "123", PROPS), {"sha": SHA, "tag": TAG})
        gh.assert_not_called()

    def test_stage_for_another_main_commit_is_refused(self):
        with mock.patch.object(release, "optional_api", return_value=[{"object": {"sha": SHA}}]), \
                mock.patch.object(release, "api", return_value={"parents": [{"sha": "c" * 40}]}), \
                self.assertRaises(ValueError):
            release.prepare("org/repo", SOURCE_SHA, "123", PROPS)

    def test_invalid_run_or_source_is_refused_before_api(self):
        with mock.patch.object(release, "gh") as gh:
            for sha, run in (("short", "123"), (SOURCE_SHA, "0"), (SOURCE_SHA, "01")):
                with self.assertRaises(ValueError):
                    release.prepare("org/repo", sha, run, PROPS)
        gh.assert_not_called()


class PublicationTests(unittest.TestCase):
    def test_publication_refuses_a_checkout_other_than_the_validated_release(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Directory.Build.props").write_text(PROPS.replace("0.5.0-dev", "0.5.0"))
            environment = {"GITHUB_REF": "refs/heads/main", "GITHUB_EVENT_NAME": "push",
                           "GITHUB_SHA": SOURCE_SHA, "RELEASE_SHA": SHA, "RELEASE_TAG": TAG}
            with mock.patch.object(release, "ROOT", root), mock.patch.dict(os.environ, environment), \
                    mock.patch("sys.argv", ["github-release.py", "publish"]), \
                    mock.patch.object(release.subprocess, "run", return_value=subprocess.CompletedProcess(
                        ["git"], 0, stdout=SOURCE_SHA + "\n")), mock.patch.object(release, "publish") as publish:
                self.assertEqual(release.main(), 1)
            publish.assert_not_called()

    def fake_api(self, ref=None, existing_release=False):
        calls = []
        state = {"ref": ref, "release": existing_release}
        def gh(*args, payload=None):
            calls.append((args, payload))
            if args[0] == "release":
                state["release"] = True
                return ""
            path = args[1]
            if path.endswith("/git/refs"):
                state["ref"] = {"object": {"type": "commit", "sha": SHA}}
                return "{}"
            if "/git/tags/" in path:
                return json.dumps({"object": {"type": "commit", "sha": SHA}})
            value = state["ref"] if "/git/ref/" in path else ({"id": 1} if state["release"] else None)
            if value is None:
                raise RuntimeError("Not Found (HTTP 404)")
            return json.dumps(value)
        return calls, gh

    def test_main_creation_targets_validated_release_commit_and_publishes_stable(self):
        calls, gh = self.fake_api()
        with mock.patch.object(release, "gh", side_effect=gh):
            release.publish("org/repo", TAG, SHA, "Kernel", SOURCE_SHA, allow_create_tag=True)
        payload = next(payload for args, payload in calls if args[1].endswith("/git/refs"))
        self.assertEqual(payload, {"ref": "refs/tags/" + TAG, "sha": SHA})
        create = next(args for args, _ in calls if args[0] == "release")
        for flag in ("--verify-tag", "--generate-notes"):
            self.assertIn(flag, create)
        self.assertNotIn("--prerelease", create)
        self.assertIn(SOURCE_SHA, create[-1])

    def test_rerun_does_not_create_another_tag_or_overwrite_notes(self):
        calls, gh = self.fake_api()
        with mock.patch.object(release, "gh", side_effect=gh):
            release.publish("org/repo", TAG, SHA, "Kernel", SOURCE_SHA, allow_create_tag=True)
            calls.clear()
            release.publish("org/repo", TAG, SHA, "Kernel", SOURCE_SHA, allow_create_tag=True)
        self.assertTrue(all(args[0] == "api" and payload is None for args, payload in calls))

    def test_tag_collision_with_another_commit_is_refused(self):
        calls, gh = self.fake_api({"object": {"type": "commit", "sha": SOURCE_SHA}})
        with mock.patch.object(release, "gh", side_effect=gh), self.assertRaises(ValueError):
            release.publish("org/repo", TAG, SHA, "Kernel", SOURCE_SHA, allow_create_tag=True)
        self.assertEqual(len(calls), 1)

    def test_annotated_manual_tag_is_resolved(self):
        calls, gh = self.fake_api({"object": {"type": "tag", "sha": "c" * 40}})
        with mock.patch.object(release, "gh", side_effect=gh):
            release.publish("org/repo", TAG, SHA, "Kernel", SHA)
        self.assertEqual(sum(args[0] == "release" for args, _ in calls), 1)

    def test_deleted_manual_tag_is_not_recreated(self):
        calls, gh = self.fake_api()
        with mock.patch.object(release, "gh", side_effect=gh), self.assertRaises(ValueError):
            release.publish("org/repo", TAG, SHA, "Kernel", SHA)
        self.assertEqual(len(calls), 1)

    def test_api_permission_failure_does_not_attempt_a_write(self):
        with mock.patch.object(release, "gh", side_effect=RuntimeError("Forbidden (HTTP 403)")) as gh:
            with self.assertRaises(RuntimeError):
                release.publish("org/repo", TAG, SHA, "Kernel", SOURCE_SHA, allow_create_tag=True)
        self.assertEqual(gh.call_count, 1)

    def test_invalid_commit_is_refused_before_any_api_call(self):
        with mock.patch.object(release, "gh") as gh, self.assertRaises(ValueError):
            release.publish("org/repo", TAG, "abc123", "Kernel", SOURCE_SHA, allow_create_tag=True)
        gh.assert_not_called()
