# Library/server compatibility

Server releases and game-library releases are independent. Updating the backend must not require republishing an otherwise working game. Library version, package revision, game revision, API version (`v3`) and database schema version are different identifiers; never compare them as one version.

Tracking: [server epic #57](https://github.com/sbox-cool/sbox-network-storage-server/issues/57) and [library tracker #6](https://github.com/sbox-cool/sbox-network-storage/issues/6). Backend/client contracts link [S1 #58](https://github.com/sbox-cool/sbox-network-storage-server/issues/58) ↔ [L1 #7](https://github.com/sbox-cool/sbox-network-storage/issues/7), [S3 #60](https://github.com/sbox-cool/sbox-network-storage-server/issues/60) ↔ [L2 #8](https://github.com/sbox-cool/sbox-network-storage/issues/8), and [S6 #63](https://github.com/sbox-cool/sbox-network-storage-server/issues/63) ↔ [L3 #9](https://github.com/sbox-cool/sbox-network-storage/issues/9). GitHub dependency links, not prose alone, define the implementation order.

## Non-breaking release rules

- Patch releases preserve existing request syntax, response meanings, error codes, authentication, save semantics and defaults. New fields/capabilities are additive. Follow the existing operator-version rules in [releasing.md](releasing.md); a server version bump does not authorize breaking the game API.
- A rename introduces the replacement while continuing to accept the old form. Keep actual translation logic at the API boundary; do not scatter version checks through storage/endpoint execution. Record why the old form exists and its linked deprecation issue. Do not create empty compatibility abstractions ahead of a real rename.
- Deprecation is a warning, not a rejection. Support a replaced library/API form for **at least six calendar months after its successor ships**, with **at least 90 days of published removal notice**. The later date wins. Announced sunsets cannot be shortened. An editor-constructor incident is not evidence that published games are incompatible.
- A removal needs a replacement already released in the library, matching issues in both repos, a migration guide, real old/new library test evidence, and an impact review including unidentified clients. Unknown clients are not proof that everyone upgraded. Prefer retaining `v3` and adding a new API generation over altering existing `v3` syntax. Any eventual removal is an explicitly migration-gated release, never a surprise patch or routine unattended update.
- Security/data-loss emergencies can use a narrow, advisory-linked block. Do not turn ordinary obsolescence into a security block. Player game-revision enforcement is separate from library compatibility; players cannot replace the library inside a published game.

### Every compatibility-affecting PR

Link the parent server/client issues, counterpart PR, dependency and priority (`compat`, `priority: P0/P1/P2`). State whether the change is additive, deprecated or removal; which existing clients remain accepted; the replacement and sunset; and the request/response and database implications. A library counterpart must be merged/released before a backend removal depends on it. Include SQLite/PostgreSQL corpus results and update the tested matrix evidence. Run the unchanged legacy path, not only a newly versioned client. Reviewers must reject a shortened notice, a patch-only syntax change, or an untested removal.

## Certification: certify once, re-test on fundamental change

Both sides are LTS: games embed the library for months and self-hosted servers
update independently. A certified pairing is the unit of trust. Certify it once
with real evidence, then hold that certification until a fundamental change
forces re-testing. Do not re-certify the same pairing on every release, and do
not extend a certification to a pairing that was not exercised.

A certification is one `tested` row: exact server version and 40-character
source commit, library version and package revision (or null when genuinely
unassigned), s&box engine build, database providers actually exercised, a
`pass` result, and evidence URLs (run logs/artifacts, both-repo PRs/issues).
`ci-only` (HTTP parity without the real s&box editor/game run) never certifies
a pairing. A certified request pattern (for example legacy revision-init shape,
save/load round trip, endpoint success/error path) covers that pattern only;
new patterns need new rows.

Fundamental changes invalidate affected certifications and MUST be re-tested
before release. Treat these as fundamental on either side:
request/response shapes, error codes/envelopes, authentication/authorization
behavior, save/endpoint/query/game-value semantics, defaults, rate-limit or
revision behavior, added/removed/renamed endpoints or library public methods,
identity parsing, capability advertisement, version-policy evaluation, migration
assumptions, or supported-provider changes. Internal refactors with no wire,
semantic or storage-contract change do not invalidate certification, but still
require the unchanged parity corpus and SQLite/PostgreSQL suite.

Library public methods and endpoint support are LTS surface. Do not rename,
remove, narrow or change the meaning of a library method, endpoint, field,
error code or auth requirement without a shipped replacement, both-repo
`compat` issues/PRs, a migration guide, the six-month/90-day support window,
real old-client and new-client evidence, and an impact review covering
unidentified traffic. Reviewers MUST reject silent removals, narrowed support,
renames without translation, shortened notice, patch-only syntax changes, and
any fundamental change without new certification rows. When in doubt, keep the
old form and add side-by-side.

## Client identity and response contract

New library builds can send these optional headers on runtime and Sync Tool calls:

| Header | Meaning |
| --- | --- |
| `x-ns-library-version` | Library SemVer, independently released from the server |
| `x-ns-library-revision` | Positive numeric s&box package revision, when known |
| `x-ns-client-type` | `game`, `editor` or `dedicated` |
| `x-ns-revision-id` | Existing published-game revision; not a library revision |

Missing/invalid versions are stored as `legacy` and advertised as `unknown`. This is accepted by default. Existing revision-init, heartbeat and analytics JSON already parsed by their handlers can supply `networkStorageVersion`, `libraryVersion` or `library_version`; a version header takes precedence. The body fallback enriches telemetry, not authentication or a pre-handler enforcement decision. No additional body buffering/parsing is needed. Existing response bodies are unchanged except the additive `compat` object in `/v3/server-info`.

The project's Errors tab captures the same request identity (library/context,
package revision and game revision). Errors raised outside an HTTP request have
legacy/unknown context and no revision; no player IDs or API keys are added.

Responses include `x-ns-server-version` and `x-ns-library-status`: `unknown`, `supported`, `outdated`, `deprecated`, `unsupported` or `blocked`. A known latest version and deprecation sunset are advertised in `x-ns-library-latest` and `x-ns-library-sunset` when relevant. The explicit project minimum can override the status of a rejected request to `unsupported`.

`GET /v3/server-info` adds `compat.library` with nullable `minimum`, `recommended`, `latest`, supported `legacy`, `deprecated`, `blocked` and `tested`, plus `compat.matrixUrl`. Null means no released/tested version has been established; it does not mean an invented `1.0.0`. Recommendations and sunsets do not expire working games automatically. A declared global minimum governs editor Push; runtime blocking additionally requires the project's explicit minimum, except advisory blocks. Pull, Test Live and revision-init remain available for recovery.

The shipped matrix is immutable/offline. No unsigned update feed can impose a library minimum or a remote kill switch. A future overlay must authenticate its provenance before it can enforce anything. Header identities and player counts are self-reported, not a security boundary; a hostile caller can spoof versions. Keep all real authentication/authorization checks.

## Test evidence, not guessed compatibility

[`compat/library-matrix.json`](../compat/library-matrix.json) is embedded in the server and validated against [`library-matrix.schema.json`](../compat/library-matrix.schema.json). It currently records supported legacy clients and the separate old-editor incident, not fictional library releases or editor passes.

Each published `libraries` entry records `version`, `status`, numeric `revision`
(when assigned by s&box), `released` and `minServer`. The legacy entry instead
uses the `revisions` range. Release dates anchor the deprecation window: a
matching successor's recorded release date must agree with `successorReleased`;
it cannot be backdated to shorten support.

Deprecation and blocked-version ranges use space-separated SemVer comparisons
(`=`, `<`, `<=`, `>`, `>=`, or an exact version); all comparisons must match.
Every token is validated by the release gate and when loading the embedded
policy, even when an earlier comparison would not match the current client.
Version values also respect the server parser's 64-character limit and
non-negative 32-bit major/minor/patch components.

A `tested` row records the exact server version/source commit, library version/package revision, s&box engine build, database providers, `pass`/`fail`/`ci-only`, and evidence URLs. Follow [editor-test-plan.md](editor-test-plan.md). Evidence links should identify the run, logs/artifacts and related PRs/issues, with secrets removed. Automated HTTP parity is `ci-only`, not a real editor pass.

```sh
python3 scripts/compat-release.py
python3 scripts/compat-release.py --matrix /path/to/release-evidence.json \
  --server 0.x.y --commit TESTED_SOURCE_COMMIT \
  --baseline /path/to/previous-release-matrix.json \
  --policy-source compat/library-matrix.json
```

Release notes require a matching-commit `pass` for the recommended library (or `legacy` while no recommendation exists). A maintainer can explicitly use `--allow-ci-only` only when a matching `ci-only` row exists; the output prominently says no editor pass is certified. `dotnet test` validates the committed matrix and exercises these gates; Python 3 is required.

A source commit cannot contain its own hash in a newly committed evidence row. Keep the release evidence as a **detached build/release artifact** after testing the final source commit; record it in the repo subsequently. Never change the tested commit to the later bookkeeping commit. Use `--matrix` for that artifact, and attach the generated table and matrix to the release. A binary's `tested` advertisement only includes records actually embedded for its version; `matrixUrl` also points to the maintained evidence history, not an authenticated policy override. The maintainer owns release-workflow attachment and the real s&box editor run.

Validate a detached artifact with `--policy-source`: it may change `tested`
evidence only, not the matrix policy that was exercised by the tests. Embed that
validated artifact without changing the source commit:

```sh
dotnet publish src/SboxNetworkStorage.Server -c Release \
  -p:Version=0.x.y \
  -p:LibraryCompatibilityMatrix=/absolute/path/to/release-evidence.json
```

Without this build property, the committed matrix is embedded. The build
property supplies a build input, not a runtime overlay or an unsigned policy
refresh. Keep the artifact with the signed release outputs.


## Operator impact and opt-in enforcement

Open **Project → Clients** (`/dashboard/projects/{id}/clients`) or run:

```sh
sbox-ns project clients PROJECT_ID --days 30
sbox-ns project clients PROJECT_ID --days 30 --json
```

The read-only MCP tool is `project_clients`. Reports group library/package revision, client context and game revision, showing request count, approximate distinct players, last seen, status/sunset and affected share. The dashboard previews a proposed minimum without changing it. Saving a non-empty/new minimum requires owner authentication, an antiforgery token and explicit acknowledgement that older **and unidentified** published games will be blocked; the change is audited. Clear the minimum to restore default acceptance. A backend update never sets it. Updates through this dashboard invalidate the policy cache immediately; external project-setting changes can take up to 30 seconds.

Project JSON stores `libraryMinimum` and optional `libraryAlertPercent`; both are absent by default. Version refusals are HTTP 426 using the library's existing structured error envelope:

```json
{"ok":false,"error":{"code":"LIBRARY_UNSUPPORTED","message":"Update the library and republish the game."}}
```

Advisory blocks use `LIBRARY_BLOCKED` with the advisory in the message. Push includes batch sync/preflight and package-sync. Missing/invalid keys and unauthorized management keys retain their original 401/403 paths. The s&box runtime can hide 4xx bodies; use response headers, server logs and the library-side update UI, not an assumption that every old game can display this message.

Traffic is buffered in memory, flushed once per minute and at graceful shutdown, and retained locally for 90 UTC days. The collector accepts at most 2,000 identity groups per UTC day/process and 2,000 pending groups; flushing does not reset the daily budget. Overflow is best-effort telemetry loss, never a failed game request. Restarting resets the in-process cardinality/drop counters; the CLI counter belongs to its own process, while the live dashboard shows the running server's counter. Database queries do not silently truncate the report.

Distinct-player sketches merge across requests, flushes and days with approximately 3.25% relative error. No raw player IDs or hashes are persisted in this compatibility table. Unidentified players are not counted. Percentages are **by requests**, not players; this traffic cannot prove every installed game upgraded. Setting the project's alert threshold enables rate-limited Discord/SMTP alerts through the existing alert sink when at least 100 requests in a flushed batch meet the affected percentage. Notifications remain opt-in; nothing is sent to Cloudflare or a hosted analytics service.

## Safe binary rollback is a separate guarantee

Migrations declare `additive` or `breaking`; the default is conservative `breaking`. Existing migration SQL is immutable. A breaking classification covers changed read/write assumptions, not just dropping columns. New compatibility certificates live in `schema_compatibility`, alongside the unchanged `schema_version` history, so old metadata schemas are not rewritten. Historical migrations are conservatively breaking; schema 4 is additive to schema 3.

A binary may read a newer database only when the complete, contiguous certificate chain proves `min_binary_schema <= supported schema`. Missing certificates, lowered/inconsistent floors and breaking gaps are refused. Startup does not downgrade history or replay migrations against a certified newer schema. `doctor` and `db status` show this distinction.

```sh
sbox-ns compatibility --json       # target binary's non-mutating schema/config preflight
sbox-ns rollback --keep-data       # previous certified binary, current data/layout retained
sbox-ns rollback --all-instances --keep-data
sbox-ns update --version 0.x.y --allow-downgrade
```

The updater runs the actual target's preflight, as the service account where needed, before stopping/swapping and again with services stopped. Every instance sharing a binary must pass. Failed checks leave the binary/data unchanged. Preserved-data rollback neither restores a backup nor reverts the runtime layout. Automatic updates never downgrade. A pinned downgrade needs both `--version` and `--allow-downgrade`; `--allow-downgrade` is not an automatic-update option.

**Pre-compatibility binaries cannot certify a rollback.** Use ordinary `rollback` and its pre-update backup restore for those targets or breaking gaps; accepted writes since the backup will be lost. Keep backups even when preserved-data rollback is available. A schema/config certificate does not certify game behavior; include update → write → rollback → read evidence and test the actual game pairing. The maintainer-owned install matrix must exercise this on the supported service platforms.

For release notes, capture each prospective target's successful `compatibility --json` result against the release's schema/config and pass it as `--rollback-report /path/to/report.json` to `compat-release.py`. Without reports, the generated notes explicitly certify no preserved-data target. Do not infer a safe target from server-version ordering alone. See [database.md](database.md) and [self-hosting.md](self-hosting.md#updates) for recovery operations.
