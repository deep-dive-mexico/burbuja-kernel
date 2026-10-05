# Kernel versions and release policy

Consumers pin a stable released tag and its full commit in their dependency lock.
A branch tip is a development source line, not an immutable dependency. Verify the
clean checkout, origin, tag, declared version and full commit before building.
Hosting is consumed through pinned source; packable contracts alone do not supply
a complete hosting package.

| Ref | Declared version | Purpose |
| --- | --- | --- |
| `dev`, `main` | Development version such as `X.Y.Z-dev` | Changes awaiting release |
| `release/X.Y` | Stable patch version | Maintenance within a released line |
| `vX.Y.Z` | Exactly `X.Y.Z` | Immutable dependency identity |

Below 1.0, read compatibility notes before adopting a new minor line. The major
and minor baseline choose the release line; normal automatic preparation selects
the next available patch version and changes only the declared version. Source
validation, builds, tests and required consumer gates must pass before normal
publication or deployment.

## Source history sanitation

The public repository was prepared from a sanitized source history. Historical
release names retain their source versions and runtime code, while rewritten
commits have new identities. The original private repository is retained as an
archive. Consumers must use the full commit of the corresponding sanitized tag;
old locks cannot silently follow a rewritten identity. The first private consumer
is updated separately and tested against its exact sanitized released pin.

Future tags are immutable. If a release needs a fix, publish a new patch. Do not
repeat a history rewrite as part of ordinary development.

## Consumer gate

Current workflows use disposable GitHub-hosted `ubuntu-24.04` VMs and read-only
default tokens. Generic tests run on public pull requests. Where a private-consumer
gate exists, it runs only on trusted push/manual events, tests both configured
consumer source lines against the exact kernel commit, and remains required for
release/deployment. Raw private output remains on the disposable VM; only numeric
counts are reported publicly.

The candidate gate requires fresh source-matched markers and complete passing
receipts with positive counts and no failures/skips/aborts. Only a browser-only
executed failure after the other lanes pass earns one fresh-snapshot retry of
the same source and test count. It does not adopt or deploy the consumer.

Release jobs request scoped GitHub write permission. Deployment alone requests
an environment-bound OIDC token; tenant roles are separate. Manual validation
defaults to authentication verification without installing an image. See the
[deployment guide](aws-deployment.md). Older tags may predate this automation;
backport the required tooling before cutting a maintenance release from them.

## Release notes

Where present, `CHANGELOG.md` owns the curated reusable behavior and compatibility
summary. The publisher reads notes from the exact validated checkout and preserves
existing pages on reruns. Private client acceptance and production inventory belong
in private operational records. Public source archives must come only from the
sanitized tags in this repository.
