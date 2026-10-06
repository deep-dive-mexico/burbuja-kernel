# Maintaining kernel release notes

`CHANGELOG.md` owns the curated change summary. `docs/releases.md` owns release
and consumer policy; `docs/aws-deployment.md` owns standalone installation evidence.
Keep kernel notes useful to an independent consumer without private product data.

## Before promotion

1. Each change adds a concise entry under `## Unreleased`: the consumer-visible
   behavior, compatibility/configuration requirements, and important limits.
   Group parallel entries under Added, Changed, Fixed, Compatibility or Delivery
   as useful. Cite the relevant issue/PR.
2. The dev-to-main promotion reviews that section against the actual source
   difference. Include only candidate changes. Record the exact dev manifest,
   generic/independent-consumer gates and witnessed journeys in the deployment
   topic. Fixture MCP calls and restart tests are separate from real delivery
   and production recovery.
3. Keep the declared version on its development line. Release preparation
   allocates the stable version and creates a child that changes only that field.
   A consumer contract change below 1.0 deserves explicit minor-line notes.

## Publication and closeout

The publisher reads `CHANGELOG.md` from the **validated release checkout**.
It selects `## vX.Y.Z — DATE` for an explicitly documented maintenance release,
otherwise `## Unreleased`. The body, including level-three subsections, becomes
the GitHub release's curated summary. It also includes source/release identity,
a link to that immutable changelog, and GitHub's generated PR list. A missing
changelog supports older maintenance trees; an empty section publishes identity
and generated notes without copying an unrelated historical release.

Use absolute GitHub/source links inside entries that will appear on a release
page; local relative links do not resolve there. Dates in version headings use
UTC and the exact `## vX.Y.Z — YYYY-MM-DD` format.

After standalone production installation is witnessed, copy the shipped summary
into its version heading and append tag/full commit, CI/deployment receipts,
consumer migration requirements and known limits. Clear only shipped entries
from Unreleased; preserve newer dev work. Land the documentation closeout with
`[skip ci]` as appropriate, then bring it into `dev` through a reviewed merge.
A documentation merge need not allocate another release or replace the image.

Existing release pages are preserved on reruns. Editing a historical GitHub
release description is a separate publication action; it never moves its tag.
Generated PR lists can repeat earlier work because stable releases are children
outside the branch tip history. The curated summary states the actual delta.
Standalone publication/deployment never changes a consumer's dependency lock.

## Verification

Run `python3 -m unittest discover -s tests/deployment` before landing publication
changes. The notes tests cover publication arguments, exclusion of historical
entries, explicit maintenance selection, older/empty changelogs and preservation
of existing release notes. Existing regressions retain version-only preparation,
tag immutability, rerun identity and validated-checkout enforcement.

Prepared on 2026-10-06 from source `9cff2eaa86c3f578fc78f204393bfc28526716f7`.
Local deployment/publication contracts passed **34/34**, zero failures/skips.
The new publisher and changelog are pending review/merge; this is not evidence
that a live GitHub release has used the curated summary yet.
