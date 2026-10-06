#!/usr/bin/env python3
"""Build a committed application, publish its digest, and deploy it through SSM."""
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import tarfile
import tempfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
# Production keeps the historical host names; every other tenant is a suffixed slice.
TENANTS = {"production": "", "dev": "-dev"}
BRANCHES = {"refs/heads/main": "production", "refs/heads/dev": "dev"}


def run(args, **kwargs):
    return subprocess.run(args, cwd=ROOT, check=True, **kwargs)


def public_host(app, tenant, ref=None):
    """Resolve the tenant's public host, refusing a branch that does not own that tenant."""
    if tenant not in TENANTS:
        raise ValueError("unknown tenant")
    if ref is not None and BRANCHES.get(ref) != tenant:
        raise ValueError(f"{ref} may not deploy the {tenant} tenant")
    return app + TENANTS[tenant] + ".dive.ai"


def install_commands(key, tenant):
    # The host declares its tenant at launch; a misdirected target fails before anything stops.
    probe = ("/opt/dive/venv/bin/python -c 'import json, sys; sys.exit(json.load(open(\"/opt/dive/config.json\"))"
             ".get(\"tenant\", \"production\") != sys.argv[1])' " + tenant)
    return ["set -eu", probe + " || { echo 'deploy target belongs to another tenant' >&2; exit 1; }",
            "/opt/dive/host.py install " + key]


def main():
    app = os.environ["AWS_APPLICATION"]
    if app not in ("akos", "kernel"):
        raise ValueError("unknown application")
    tenant = os.environ.get("AWS_TENANT", "production")
    host = public_host(app, tenant, os.environ.get("GITHUB_REF"))
    region = os.environ["AWS_REGION"]
    repository = os.environ["AWS_ECR_REPOSITORY"]
    bucket = os.environ["AWS_RELEASE_BUCKET"]
    instance = os.environ["AWS_DEPLOY_INSTANCE"]
    sha = run(["git", "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    if not re.fullmatch(r"[0-9a-f]{40}", sha) or run(
            ["git", "status", "--porcelain"], capture_output=True, text=True).stdout:
        raise ValueError("release source must be clean and committed")
    import boto3  # Only the deploy runner has AWS tooling; the tenant rules stay testable without it.
    aws = boto3.Session(region_name=region)
    ecr = aws.client("ecr")
    name = repository.split("/", 1)[1]
    tag = "sha-" + sha
    try:
        digest = ecr.describe_images(repositoryName=name, imageIds=[{"imageTag": tag}])["imageDetails"][0]["imageDigest"]
    except ecr.exceptions.ImageNotFoundException:
        password = run(["aws", "ecr", "get-login-password", "--region", region],
                       capture_output=True, text=True).stdout
        run(["docker", "login", "--username", "AWS", "--password-stdin", repository.split("/")[0]],
            input=password, text=True, stdout=subprocess.DEVNULL)
        args = ["docker", "build", "-f", "deploy/Dockerfile", "-t", repository + ":" + tag,
                "--build-arg", "SOURCE_COMMIT=" + sha]
        if app == "akos":
            spec = importlib.util.spec_from_file_location("client_host", ROOT / "scripts/client-host.py")
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            identity = module.source_identity()
            for setting, value in (("AKOS_KERNEL_COMMIT", identity["kernel"]),
                                   ("AKOS_TREE_SHA256", identity["fingerprint"]),
                                   ("AKOS_PRODUCT_HEAD", sha), ("AKOS_TREE_DIRTY", "false")):
                args += ["--build-arg", setting + "=" + value]
        run(args + ["."])
        run(["docker", "push", repository + ":" + tag])
        digest = ecr.describe_images(repositoryName=name, imageIds=[{"imageTag": tag}])["imageDetails"][0]["imageDigest"]
    manifest = {"application": app, "commit": sha, "image": repository + "@" + digest}
    if app == "akos":
        manifest["kernel"] = json.loads((ROOT / "kernel.lock.json").read_text())["commit"]
    key = f"releases/{app}/{sha}/release.tgz"
    with tempfile.TemporaryDirectory() as temporary:
        directory = Path(temporary)
        (directory / "release.json").write_text(json.dumps(manifest) + "\n")
        archive = directory / "release.tgz"
        with tarfile.open(archive, "w:gz") as stream:
            stream.add(ROOT / "deploy/aws/compose.yaml", arcname="compose.yaml")
            stream.add(directory / "release.json", arcname="release.json")
            if app == "akos":
                for path in ("scripts/akos-seed-roles.sh", "scripts/lib/common.sh"):
                    stream.add(ROOT / path, arcname=path)
        aws.client("s3").upload_file(str(archive), bucket, key)
    ssm = aws.client("ssm")
    command = ssm.send_command(InstanceIds=[instance], DocumentName="AWS-RunShellScript",
                              Parameters={"commands": install_commands(key, tenant),
                                          "executionTimeout": ["1200"]})["Command"]["CommandId"]
    deadline = time.monotonic() + 1250
    while time.monotonic() < deadline:
        time.sleep(10)
        try:
            result = ssm.get_command_invocation(CommandId=command, InstanceId=instance)
        except ssm.exceptions.InvocationDoesNotExist:
            continue
        if result["Status"] in ("Pending", "InProgress", "Delayed"):
            continue
        print(result.get("StandardOutputContent", ""))
        if result["Status"] != "Success":
            print(result.get("StandardErrorContent", ""))
            raise RuntimeError("deployment failed: " + result["Status"])
        break
    else:
        raise TimeoutError("SSM deployment timeout")
    deadline = time.monotonic() + 180
    while True:
        try:
            with urllib.request.urlopen(f"https://{host}/health/ready", timeout=15) as response:
                if response.status == 200:
                    break
        except Exception:
            if time.monotonic() >= deadline:
                raise
            time.sleep(10)
    print(f"Deployed to {tenant} and publicly healthy:", json.dumps(manifest))


if __name__ == "__main__":
    main()
