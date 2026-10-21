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
