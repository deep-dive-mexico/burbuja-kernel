#!/usr/bin/env python3
"""Offline, real-process proof: two SDK-only applications and one kernel service."""
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]


def port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def get(url, data=None):
    request = urllib.request.Request(url, json.dumps(data).encode() if data is not None else None,
                                     headers={"Content-Type": "application/json"},
                                     method="PUT" if data is not None else "GET")
    with urllib.request.urlopen(request, timeout=10) as response:
        return json.load(response)


def wait(url):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        try:
            return get(url)
        except (OSError, ValueError):
            time.sleep(0.1)
    raise RuntimeError("Process did not become ready: " + url)


def main():
    run = ROOT / "artifacts/connected-proof" / uuid.uuid4().hex
    run.mkdir(parents=True)
    kernel, application = run / "kernel", run / "application"
    commands = [
        ["dotnet", "publish", "src/Burbuja.Host/Burbuja.Host.csproj", "-c", "Release", "-m:1", "-o", str(kernel)],
        ["dotnet", "build", "plugins/Burbuja.Plugins.Store.Sqlite/Burbuja.Plugins.Store.Sqlite.csproj", "-c", "Release", "-m:1"],
        ["dotnet", "publish", "examples/Example.Connected/Example.Connected.csproj", "-c", "Release", "-m:1", "-o", str(application)],
    ]
    with (run / "build.log").open("w") as log:
        for command in commands:
            subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
    plugin = ROOT / "plugins/Burbuja.Plugins.Store.Sqlite/bin/Release/net10.0"
    shutil.copytree(plugin, kernel / "plugins/Store.Sqlite", ignore=shutil.ignore_patterns("publish"))
    for forbidden in ("Burbuja.Core.dll", "Burbuja.Core.Sdk.dll", "Burbuja.Core.Abstractions.dll"):
        if (kernel / "plugins/Store.Sqlite" / forbidden).exists():
            raise RuntimeError("Plugin violated shared-contract load identity")
    libraries = json.loads((application / "Example.Connected.deps.json").read_text())["libraries"]
    forbidden = {"Burbuja.Core", "Burbuja.Agents", "Burbuja.Compute", "Burbuja.Hosting"}
    if forbidden & {name.split('/')[0] for name in libraries}:
        raise RuntimeError("Connected application publishes an embedded runtime")
    kernel_port, alpha_port, beta_port = port(), port(), port()
    kernel_url = f"http://127.0.0.1:{kernel_port}"
    keys = {name: secrets.token_urlsafe(32) for name in ("alpha", "beta")}
    clean = {k: v for k, v in os.environ.items() if not (k.startswith(("Kernel__", "ConnectedKernel__", "Auth__", "Akos__", "Agents__", "Pomnibot__", "Kestrel__", "Application__"))
             or k in ("ASPNETCORE_URLS", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT"))}
    kernel_env = dict(clean, DOTNET_ENVIRONMENT="Production", Kernel__Seed="false", Kernel__Role="all",
                      Kestrel__Endpoints__Http__Url=kernel_url, Kernel__PluginsDirectory=str(kernel / "plugins"),
                      Kernel__TrustedPlugins__0="Policy.Core", Kernel__TrustedPlugins__1="Supervisor",
                      Kernel__TrustedPlugins__2="Store.Sqlite", Kernel__TrustedPlugins__3="Burbuja.Compute",
                      Kernel__TrustedPlugins__4="Tools.Workspace", Kernel__TrustedPlugins__5="Tools.Http",
                      Kernel__TrustedPlugins__6="Tools.Agents", Kernel__TrustedPlugins__7="Llm.Scripted",
                      Sqlite__Path=str(run / "embedded-control.db"), Sqlite__ArtifactRoot=str(run / "embedded-artifacts"),
                      ConnectedKernel__Enabled="true", ConnectedKernel__Environment="development",
                      ConnectedKernel__StorageRoot=str(run / "bindings"))
    for index, name in enumerate(keys):
        prefix = f"ConnectedKernel__Applications__{index}__"
        kernel_env.update({prefix + "Tenant": "example", prefix + "Application": name,
                           prefix + "Credential": keys[name], prefix + "WriteNamespaces__0": "Example.Connected",
                           prefix + "EventPrefixes__0": "example.", prefix + "RuntimeEnabled": "true",
                           prefix + "DelegatedRoles__0": "Reader"})
    processes, logs, app_environments = {}, [], {}

    def start(name, assembly, environment, directory):
        log = (run / (name + ".log")).open("a")
        logs.append(log)
        processes[name] = subprocess.Popen(["dotnet", str(assembly)], cwd=directory, env=environment,
                                           stdout=log, stderr=subprocess.STDOUT)

    def stop(name):
        process = processes.pop(name)
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()

    receipt = {"status": "incomplete", "network": "real loopback sockets", "models": "no calls",
               "kernelSource": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
               "sourceDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip()),
               "applicationRuntimeClosure": sorted(name.split('/')[0] for name in libraries if name.startswith("Burbuja."))}
    try:
        start("kernel", kernel / "Burbuja.Host.dll", kernel_env, kernel)
        wait(kernel_url + "/health/plugins")
        for name, app_port in (("alpha", alpha_port), ("beta", beta_port)):
            environment = dict(clean, ASPNETCORE_URLS=f"http://127.0.0.1:{app_port}", DOTNET_ENVIRONMENT="Production",
                               Application__Environment="development", Application__Tenant="example", Application__Name=name,
                               Kernel__Url=kernel_url, Kernel__Credential=keys[name])
            app_environments[name] = environment
            start(name, application / "Example.Connected.dll", environment, application)
        a, b = f"http://127.0.0.1:{alpha_port}", f"http://127.0.0.1:{beta_port}"
        identities = [wait(url + "/binding") for url in (a, b)]
        assert identities[0]["processId"] != identities[1]["processId"]
        assert all(not (forbidden & set(identity["dependencies"])) for identity in identities)
        for url in (a, b):
            assert any(provider["name"] == "Burbuja.Agents" for provider in get(url + "/runtime/providers")["value"])
        get(a + "/notes/same", {"owner": "alpha"})
        get(b + "/notes/same", {"owner": "beta"})
        assert get(a + "/notes/same")["properties"]["owner"] == "alpha"
        assert get(b + "/notes/same")["properties"]["owner"] == "beta"
        stop("alpha")
        assert get(b + "/notes/same")["properties"]["owner"] == "beta"
        start("alpha", application / "Example.Connected.dll", app_environments["alpha"], application)
        wait(a + "/binding")
        assert get(a + "/notes/same")["properties"]["owner"] == "alpha"
        stop("beta")
        assert get(a + "/notes/same")["properties"]["owner"] == "alpha"
        start("beta", application / "Example.Connected.dll", app_environments["beta"], application)
        wait(b + "/binding")
        stop("kernel")
        for url in (a, b):
            for endpoint in ("/notes/same", "/runtime/providers"):
                try:
                    get(url + endpoint)
                    raise AssertionError("Application fell back to a local authority")
                except urllib.error.HTTPError as error:
                    assert error.code == 503
                    assert json.load(error)["code"] == "KernelUnavailable"
        start("kernel", kernel / "Burbuja.Host.dll", kernel_env, kernel)
        wait(kernel_url + "/health/plugins")
        assert get(a + "/notes/same")["properties"]["owner"] == "alpha"
        assert get(b + "/notes/same")["properties"]["owner"] == "beta"
        for url in (a, b):
            assert any(provider["name"] == "Burbuja.Agents" for provider in get(url + "/runtime/providers")["value"])
        receipt.update(status="passed", independentApplications=identities, runtimeThroughKernel=True,
                       sameIdentifiersIsolated=True, independentAppRestarts=True, kernelRestartReadback=True,
                       unavailableWithoutFallback=True, sharedStateMounts=False)
    finally:
        for name in list(processes):
            stop(name)
        for log in logs:
            log.close()
        (run / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print("Connected applications proof:", run / "receipt.json")


if __name__ == "__main__":
    main()
