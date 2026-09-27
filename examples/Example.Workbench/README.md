# Example Workbench

A standalone consumer with two modules:

- **Example.Workbench.Host** is its own executable, composed with `Burbuja.Hosting`. It registers
  the `/workbench` portal and owns its configuration.
- **Example.Workbench.Plugin** declares `Example.Workbench/notes`, implements the generic resource
  handler contracts/SDK, contributes two RBAC roles, and records a small event trace. It is installed
  dynamically; the host has no project or assembly reference to it.

## Build, install, run

Prerequisites: .NET SDK **10.0.400** (10.0.401 tested), NuGet restore access, and a pinned Burbuja
source checkout containing `src/Burbuja.Hosting`. The **0.4.0 packages do not include Hosting**;
this example uses project references, including when copied outside that checkout.

From this directory, set the absolute source checkout path:

```bash
BURBUJA_ROOT=/absolute/path/to/burbuja-server
dotnet build Example.Workbench.slnx -p:BurbujaRoot="$BURBUJA_ROOT"
dotnet publish Example.Workbench.Host/Example.Workbench.Host.csproj \
  -c Release -p:BurbujaRoot="$BURBUJA_ROOT" -o artifacts/host
dotnet publish Example.Workbench.Plugin/Example.Workbench.Plugin.csproj \
  -c Release -p:BurbujaRoot="$BURBUJA_ROOT" -o artifacts/host/plugins/Example.Workbench
dotnet artifacts/host/Example.Workbench.Host.dll --urls=http://127.0.0.1:18490
```

The host loads settings and relative plugin paths from its executable directory. Stop with Ctrl-C.
Open <http://127.0.0.1:18490/workbench> and sign in with `owner` / `owner-dev-password`.
These are public development credentials; data is in memory and disappears on restart.

For the exact HTTP bootstrap, RBAC examples, external-copy procedure and bounded tests, read
`docs/developer-workbench.md` in the pinned Burbuja checkout. The sample's local `Directory.*`
files and `global.json` must travel with both project folders.
