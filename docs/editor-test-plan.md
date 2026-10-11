# s&box editor and game test plan

Everything here needs the s&box editor and the real
[Network Storage library](https://github.com/sbox-cool/sbox-network-storage),
so CI cannot run it. Server-only behavior (HTTP contract, SQLite and
PostgreSQL, installers) is covered by CI and the parity corpus.
Tick a box in the PR that records the result, and write the engine build, library
version and server version next to it.

Record the result as a `tested` row in
[`compat/library-matrix.json`](../compat/library-matrix.json), or as a detached
release evidence artifact for the final source commit. Required fields:
`server`, `serverCommit` (40-character tested source SHA), `library`, `revision`
(package revision or null), `engine`, `database` (`sqlite`/`postgres`),
`result` (`pass`/`fail`/`ci-only`) and `evidence` URLs. Link the run's logs,
PRs/issues in both repos and this checklist. A real editor/game run can be
`pass`; backend HTTP tests alone are `ci-only`. Do not change a tested SHA to
the later evidence-bookkeeping commit. Validate/render with
[`scripts/compat-release.py`](../scripts/compat-release.py); see
[compatibility.md](compatibility.md) and
[the editor gate #54](https://github.com/sbox-cool/sbox-network-storage-server/issues/54).

Certify once per pairing: a `pass` row covers exactly the exercised server
commit, library/revision, engine and providers. Reuse that certification until
a fundamental change (request/response, error, auth, save/endpoint semantics,
defaults, endpoint or public-method add/remove/rename, identity/capability,
policy or migration assumption) forces re-testing; see
[certification](compatibility.md#certification-certify-once-re-test-on-fundamental-change).
Library methods and endpoint support are LTS surface: changes need both-repo
`compat` issues, migration evidence and heavy scrutiny.

## Setup

1. Server: `sbox-ns setup --non-interactive --database sqlite --listen 0.0.0.0:8080`,
   then `sbox-ns project create "Editor test"` and one public and one
   secret key (see [client-setup.md](client-setup.md)). Repeat the whole plan once with
   `--database postgres`.
2. Library: replace the project's `Libraries/sboxcool.network-storage` folder with the
   current `main` of the library repo. Never copy files over an old install. A build
   older than 1.0.309133 does not start on editor 26.10.
3. Editor **Network Storage > Setup**: project ID, public key, secret key, Base URL.
4. Record: s&box build, library version, server version (`/v3/server-info`).

## A. Library install and update

- [ ] Library Manager lists a version of 1.0.309133 or newer and updates a project that
      has 1.0.165xxx installed, with no `Constructor on type 'SyncToolWindow' not found`.
- [ ] Editor starts with 0 `Error |` lines in `logs/sbox-dev.log`.
- [ ] Setup window saves and shows **Connected**.

## B. Sync Tool against the server

- [ ] Handshake: `POST /v3/manage/{id}/package-sync` and `GET .../game-package` return
      200 and the window shows "Last synced".
- [ ] Scaffold: the Sync window's sample-project scaffold writes endpoints using the documented
      `check` / `onFail` condition shape (library PR #5), not `field` / `operator`.
- [ ] Push All: preflight 200, `PUT .../sync` 200, dashboard shows the counts.
- [ ] Preflight on a hand-written condition with no `check` shows a `MISSING_CHECK`
      warning (server PR #49) and the push still succeeds.
- [ ] Sync window lists both `*.json` and `*.yml` resources.
- [ ] Push Staged (`x-ns-publish-target: next`) lands in the staged revision and
      Promote makes it live.
- [ ] Test Live: every scaffold endpoint passes `auto-test`; a failing endpoint shows
      `result.body.error` in Test Results, not only "Endpoint returned HTTP 400.".
- [ ] Single-resource push of one endpoint, one collection and one workflow.

## C. Play mode (runtime calls with the real library)

- [ ] `NetworkStorage.Configure(projectId, publicKey, baseUrl)` plus `revision-init`
      returns `ok` and logs the revision lines.
- [ ] `SaveDocument` then `GetDocument` round trip on a per-steamid collection.
- [ ] `CallEndpoint` success, and an error path whose `code` and `message` reach
      `TryGetLastEndpointError`.
- [ ] `inc` / `set` ops through an endpoint change the stored document (v0.4.0
      stored `{ops:[...]}` instead; confirm the build under test applies them).
- [ ] Game values load (`GET /v3/values/{id}`) and `{{values.*}}` resolves in an endpoint.
- [ ] Query and leaderboard through `RunQuery`.
- [ ] Rate-limit behavior: collection `savesPerDay` returns the error the library shows.
- [ ] Heartbeat (`/storage/{id}/stats/heartbeat`) and analytics events
      (`/storage/{id}/analytics/events`) appear in the dashboard activity pages with the
      session id, library version and context fields.

## D. Security modes

- [ ] Auth sessions on: `auth-sessions/{id}/create`, later calls send `x-auth-session`,
      expiry forces a new session.
- [ ] Encrypted requests on: endpoint call succeeds; replaying the same envelope is
      rejected; tampering with `encryptedPayload` or `signature` is rejected.
- [ ] Both on together.
- [ ] Dedicated server with a secret key: secret-only endpoint works; the same call
      with only the public key is rejected.
- [ ] Authority check (dashboard overview card) matches what the project really allows:
      a `public` collection is flagged, an endpoint-only collection is not.

## E. Revision handling (needs server Phase 4)

- [ ] Published bundle sends `x-ns-revision-id`; editor and local play send none.
- [ ] After a package sync raises the live revision, an older client gets
      `revisionOutdated` from `revision-init`.
- [ ] Endpoint responses carry `_revisionStatus`; the in-game outdated panel,
      grace countdown and block-saves flag react as configured.

## F. Hosted name (needs the owner's approval)

- [ ] `sbox-ns dns enable --accept-letsencrypt-terms --email ...` on a host with
      public ports 80 and 443, then the editor connects through the HTTPS name.
- [ ] Same with the tunnel option on a host without open ports.

## G. Independent library/backend releases

- [ ] An older published game with no new identity headers still works after a
      backend update; replay the same save/load/endpoint flow before and after.
- [ ] New runtime and Sync Tool builds report library version/package revision
      separately from game revision. Observe server/version-status headers and
      **Project → Clients**; repeated calls from one player do not become
      multiple distinct players.
- [ ] `outdated`/`deprecated` shows an update recommendation without breaking
      old runtime calls. Library Manager/editor update instructions are tested
      in the [library tracker](https://github.com/sbox-cool/sbox-network-storage/issues/6).
- [ ] Preview a project minimum, verify the affected share, and confirm saving
      without acknowledgement is refused. An explicitly enabled minimum returns
      `LIBRARY_UNSUPPORTED`; clearing it restores acceptance. Advisory blocks
      return `LIBRARY_BLOCKED`. Pull, Test Live and revision-init remain usable.
- [ ] Against compatibility-aware binaries: update the backend, write a new
      player record, `rollback --keep-data`, then read the new record through
      the unchanged game library. A breaking schema gap and a pre-compatibility
      target must refuse preserved-data rollback before changing the binary.
- [ ] Run both SQLite and PostgreSQL, recording which pairing passed. Do not
      infer a result for a library/engine/provider that was not exercised.

## Library repo checks

- [ ] `UnitTests/` and `Tests/test_source_compiler.py` pass on a machine with the
      editor assemblies.
- [ ] `Code/obj` and `Editor/obj` are not tracked (`git rm -r --cached` if they are).
