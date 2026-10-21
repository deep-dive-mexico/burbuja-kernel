# Public source preparation

Preparation date: 2026-10-21.

The original repository's private diagnostics and operational records are retained
in a private archive. The public-facing repository is populated independently
with sanitized Git history, without copying old pull requests, Actions logs,
artifacts, or cached historical references.

## Source boundary

Sanitation replaces historical operational deployment records with reusable
guidance, removes private client acceptance status, normalizes personal/local
commit email identities to the owner's public GitHub noreply identity, and
replaces the historical personal-account policy fixture with a synthetic account.
Application source and declared release versions are preserved. Local records map
old commits and tags to sanitized identities; private-consumer locks are updated
separately against the exact corresponding released tag.

Public source scans distinguish documented local demo credentials, synthetic
redaction fixtures, and file checksums from live secrets. No real environment
files, production state, private keys, recordings, or private-product source belong
in this repository. Component and example READMEs remain part of the source tree.

## Automation boundary

Current branch workflows use GitHub-hosted `ubuntu-24.04`; no self-hosted runner
registration is transferred. Public pull requests get generic validation without
private-consumer source or deployment authority. Trusted consumer jobs retain
raw output on their disposable VM and publish only numeric counts.

AWS deployment uses environment-bound OIDC roles and masked environment secrets.
Only matching branches can enter dev/production environments. Role trust must
match the new repository identity. No long-lived AWS access key is used. Manual
validation defaults to authentication verification, without an application rollout.

## Verification limits

Local kernel and branch-specific contract tests validate the sanitized source.
The private consumer is checked in its own repository against its immutable
sanitized release pin. This does not establish live model/provider behavior or
manual client acceptance. Actual GitHub-hosted execution and OIDC token exchange
remain pending because organization billing prevents runner allocation; the
maintainer explicitly chose to leave that verification pending.

## Cleanup verification

The independent replacement contains all 29 branch tips and five release tags.
Runtime source and declared versions were compared at every corresponding ref and
remain unchanged. Personal/local commit emails were normalized to public GitHub
identities; historical account-path fixtures use a synthetic account. Identified
cloud inventory and private rollout records were removed from reachable history.

All 43 old hosted artifacts were privately backed up, CRC-checked, indexed with
SHA-256 hashes, and deleted from GitHub. The 102 existing workflow log sets were
also removed. One unallocated historical queued record has no jobs or logs and
remains only in the private archive. No old artifacts, runs, pull requests or
cached original release commits were transferred to the replacement.

The original release commits were checked through the new repository's API and
are not accessible there. The replacement is independent, not a fork of the
archive. The archive remains private with Actions disabled. The working checkout
was replaced with a fresh sanitized clone to avoid accidentally pushing old refs.

The sanitized runtime passed 1,428 kernel tests with no failures/skips and a build
with no warnings/errors. The current deployment/security contract suite passed
62 tests. The full-history credential scan reported only the 14 previously
reviewed synthetic fixture and checksum false positives. No live credential was
confirmed. Existing read-only source access was preserved, consumer locks were
coordinated privately, and deployment trust was rebound to the new repository ID.

The replacement has zero self-hosted runner registrations and uses masked
per-environment deployment settings without plaintext resource-variable copies.
GitHub-hosted execution remains pending as described above. No production
application rollout was performed. Repository visibility remains private until
the maintainer changes it.
