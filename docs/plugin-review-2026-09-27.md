# Plugin portability review — 2026-09-27

Review of generic plugin loader and authenticated transport contracts.

## Findings closed

- Disk manifests could disagree with code about capabilities, trust, roles and dependencies.
  The loader now compares these declarations before registering services. Mismatches name
  the plugin and differing fields; valid static declarations may still be installed dynamically.
- A staged built-in with a missing DLL also registered its static fallback, producing an
  unhelpful duplicate-ID exception. Installed manifests now claim their identity; damaged
  installs fail through `PluginBootException`, with the manifest and missing assembly named.
- Duplicate installs now report both locations before topological sorting.
- An independent consuming E2E host exposed an existing `Policy.Core` disk/code disagreement:
  `config_section` was null on disk and `Plugins:Policy.Core` in code. The disk manifest is
  corrected, and the all-shipped-plugins test now calls the same complete comparison as boot,
  instead of checking only four fields. Its pre-fix failure and the subsequent full kernel
  gate were observed; the consuming application must use the corrected follow-up commit.
- `/portal/download` is intentionally shared authenticated transport, not a kernel-console
  page. A consumer-only session can read only what its ARM key permits. Tests cover anonymous,
  authorized, denied and external-path requests; the public developer guide states the contract.

## Evidence

The new loader/failure regressions were observed failing before the implementation change.
`make test` then built the official kernel solution with **0 warnings / 0 errors** and ran
**1,315 tests, all passed, none skipped**. The real Workbench loader tests cover a valid
dynamic replacement as well as damaged and duplicate installs. `git diff --check` passed.

The independent developer example and its runtime remain in this repository. AKOS consumes
an immutable commit of this source; it does not need the historical integration tree or a
private client assembly in the kernel. Hosting is currently distributed as pinned source,
not a claimed published NuGet package. See [the developer guide](developer-workbench.md).
