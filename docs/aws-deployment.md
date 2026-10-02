# Deploying the generic kernel

The current branch supplies the deployment tooling it supports. Start with the
[root README](../README.md) and inspect `deploy/` before adopting this guide on an
older maintenance line. Production inventories and installation/restore receipts
belong in private operator records.

## Container and state

Where present, `deploy/Dockerfile` publishes `Burbuja.Host`, installs the SQLite
plugin, removes Development settings, and runs as a non-root user. State is stored
under `/var/burbuja/state`. The image supplies no production users or API keys.
Keep authentication configuration, local environment files, database state,
backups, recordings, and credentials outside Git. Use consistent backups of
SQLite and artifacts, and verify restored application state separately from
HTTP readiness. A single application instance is not highly available.

`deploy/aws/compose.yaml` expects an immutable image digest and an operator-managed
environment file. Adapt its host paths, HTTPS/access controls and volume settings
to your infrastructure. Do not reuse the public credentials from local examples.

## GitHub-hosted automation

Current branch-tip workflows run on disposable GitHub-hosted Ubuntu VMs. The
workflow token defaults to read-only. Private-consumer checks, when present, run
only on trusted push/manual events and publish numeric test counts rather than
private output, screenshots or diagnostic archives. Public pull requests receive
no private-consumer checkout or AWS authority.

Deployment jobs use separate environment-bound AWS OIDC roles. The `dev` and
`production` GitHub environments admit only their owning branches. Role trust
must use the repository's current OIDC subject, including its immutable repository
ID when enabled. Each role is restricted to kernel images, the kernel release
prefix, and the matching tenant's SSM target.

Configure these as masked environment secrets when the branch has a deployment
job: `AWS_DEPLOY_ROLE`, `AWS_ECR_REPOSITORY`, `AWS_RELEASE_BUCKET`, and
`AWS_DEPLOY_INSTANCE`. Region and tenant selection remain explicit workflow
settings. No long-lived AWS access key belongs in GitHub or source.

The installer implementation assumes a provisioned host layout; it does not
provision AWS infrastructure. Current manual workflows default to authentication
verification only. Enable the boolean `deploy` input to request installation.
Trusted branch pushes retain their defined deployment behavior.

See [GitHub's AWS OIDC guidance](https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-in-aws).

## Verification

Where `tests/deployment` exists, run:

```sh
python3 -m unittest discover -s tests/deployment
```

These contracts do not prove a live installation or restore. Retain actual
release, image, authentication and restore evidence privately. Public release
notes describe reusable behavior and compatibility requirements. Hosted execution
and OIDC exchange remain pending while organization billing blocks allocation;
no application rollout was performed by the publication cleanup.
