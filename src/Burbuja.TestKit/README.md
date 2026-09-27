# Consumer host targeting

Existing calls still target `Burbuja.Host.dll`, the kernel host project, and
`artifacts/hostonce`. New optional arguments let a consumer select its own executable:

```csharp
var root = RepoLayout.FindRoot(solutionFileName: "Consumer.slnx");
var published = HostPublisher.EnsurePublished(
    projectPath: "src/Consumer.Host/Consumer.Host.csproj",
    entryAssemblyName: "Consumer.Host.dll",
    sourceRoot: root);
await using var host = await ServerProcess.StartAsync(
    published, settings, environment,
    entryAssemblyName: "Consumer.Host.dll",
    repositoryRoot: root);
```

- `RepoLayout.FindRoot(root: absoluteRoot)` needs no solution or sibling kernel checkout.
  `FindRoot(startDirectory: ..., solutionFileName: ...)` walks upward for the named solution;
  omit the name to find any `.slnx` or `.sln`.
- `RepoLayout.Root` honors `BURBUJA_TEST_ROOT`. `KernelRoot` honors `BURBUJA_ROOT` and is resolved
  only when requested. Explicit publish/start roots bypass both discovery paths.
- Publish project/output paths are absolute or relative to `sourceRoot`. An explicit output
  `directory` wins over `BURBUJA_E2E_HOST_DIR`. A consumer's default output is
  `<sourceRoot>/artifacts/hostonce/<entry assembly stem>/<configuration>`.
- Publish caching checks project/entry/configuration/root identity and source mtimes, including
  `src`, `plugins`, root build files and the explicit project's directory. `IsStale` accepts the
  same `sourceRoot`, `entryAssemblyName` and `projectPath` arguments. Build and selected publish
  output are excluded. Referenced source trees outside those directories are not tracked.
- Settings and environment are per-child dictionaries. Supply distinct state paths in settings
  as usual. Default ports use `FreePort` and `BURBUJA_PORT_MIN/MAX`; automatic claims are released
  on disposal or failed startup. Explicit `port` values remain caller-owned.
- The child runs from the publish directory and waits for `/health/ready`. Logs default to
  `<repositoryRoot>/artifacts/e2e-logs/host-<port>.log`; an explicit `logPath` bypasses root discovery.

Entry assembly arguments take a DLL filename **including `.dll`**. The process helper is
`ServerProcess`.
