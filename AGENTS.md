# Burbuja kernel — agent router

This repo owns **reusable** contracts, SDK, Core/Agents/Compute, Hosting, generic Host/Portal, TestKit and generic plugins. It has an independent consumer at `examples/Example.Workbench`. Read [the kernel wiki index](docs/wiki/OVERVIEW.md) and the relevant row before changing code.

| Task | Read first | Update when proven |
|---|---|---|
| Release notes or promotion | `CHANGELOG.md`, `docs/release-notes.md`, `docs/aws-deployment.md` | Curated candidate summary before promotion; versioned notes and installed-release evidence after proof |
| Core/SDK/ARM/agent or resource contract, plugin loader | `docs/wiki/OVERVIEW.md`, affected `src/` contract and tests | That wiki topic and affected tests |
| Generic Host/Portal/Hosting or external consumer | `docs/developer-workbench.md` and `examples/Example.Workbench/README.md` | The owning guide and consumer tests |
| Plugin install, manifest, auth or porting | `docs/plugin-review-2026-09-27.md`, affected plugin manifest and tests | Plugin guide/README and regression witness |
| AKOS-specific persona, UI, risk rule or client expectation | Sibling `../akos-server/AGENTS.md` when available; otherwise `deep-dive-mexico/akos-server`'s docs | In the product repo, unless the reusable kernel mechanism really changes |

Run kernel tests in this repository (`make test`); for a generic contract also check its independently consuming Workbench/test surface. CI also runs AKOS's complete product suite from its `dev` branch against every validated kernel commit (`consumer-akos`, see [releases](docs/releases.md#consumer-gate)); publication and deployment wait for it. If the change serves AKOS, commit and verify the generic kernel first, then cut a kernel release ([releases](docs/releases.md)) and update the product's immutable `kernel.lock.json` to that tag and its full commit, then run product gates **in the product repo**. A product test cannot use a dirty/moving kernel checkout as a verified lock. For the multi-repo source/acceptance map, consult `deep-dive-mexico/akos-docs` when available; this kernel must remain usable without it.

Record a durable gotcha in the nearest topic doc with the code/test evidence that establishes it. Product or client-source status belongs to AKOS, not a generic kernel document.
