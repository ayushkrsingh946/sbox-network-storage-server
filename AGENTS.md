# AGENTS.md

Paste this into a coding agent to give it context on this repo.

You are working with `sbox-ns`, the self-hosted s&box game backend in this
repo (sbox-cool/sbox-network-storage-server, .NET 8, AGPL-3.0-only).
One binary is both the server and its operator CLI. It serves the existing
Network Storage client library for s&box; games point the library at this
server with a base URL and need no client code changes:

```csharp
NetworkStorage.Configure( projectId, apiKey, "http://your-server:8080" );
```

Run it locally with SQLite (the default, zero config):

```sh
dotnet run --project src/SboxNetworkStorage.Server -- start
sbox-ns setup --non-interactive --database sqlite --listen 0.0.0.0:8080
sbox-ns project create "My Game"
sbox-ns key create <projectId> --type public --label game
```

The HTTP API contract lives in `tests/parity/corpus/*.json` (request shapes,
headers, expected bodies) with the runner in `tools/SboxNetworkStorage.Parity`
and live route tests in `tests/SboxNetworkStorage.Server.Tests` (for example
`RevisionInitHttpTests`). The game client source it must stay compatible with
is `github.com/sbox-cool/sbox-network-storage` (library page
`sbox.game/sboxcool/network-storage`, API docs `sbox.cool/wiki/network-storage-v3`).
Game dev setup is documented in `docs/client-setup.md`, the startup
handshakes in `docs/game-client.md`, databases in `docs/database.md`.

Do not touch: `CHANGELOG.md`, `install/` scripts, `.github/workflows/`,
`tests/parity/corpus/` fixtures, anything under `<config dir>/secrets/`,
and never put a secret API key in game code (public keys only). Do not share
one PostgreSQL schema between independent server instances or data
directories. Keep backups (`sbox-ns db backup`) before touching migrations.

## Compatibility invariant

Read `docs/compatibility.md` before changing game-facing API syntax, defaults,
save semantics, library-version policy or migrations. Backend updates must keep
older and unidentified published games working by default: no request without a
parseable library version is ever refused for its version, and the release-feed
overlay only ever tightens reported statuses, never enforcement. Add
replacements without removing old forms; support the old form for at least six
months after its successor ships and publish at least 90 days' removal notice.
No surprise game-facing breaking patch; prefer side-by-side API generations.

Link compatibility issues/PRs in both repos and record the exact tested source
commit, server/library/package revisions, engine and database in the matrix
evidence. Backend HTTP tests are not a real s&box editor pass. New migrations are
conservatively `breaking` unless old read/write assumptions demonstrably survive;
never modify released migration SQL. Exercise unchanged legacy requests and
SQLite/PostgreSQL parity before delivery. Version headers are self-reported
metadata, never authentication; runtime minimum enforcement remains explicit
owner opt-in.

Certify each server/library/engine/provider pairing once with real evidence;
a fundamental wire/semantic/storage change invalidates affected certifications
and must be re-tested before release. Library methods and endpoint support are
LTS surface: no silent rename/removal/narrowing.

## Reporting test results

Any result you report for the s&box editor or a real game client (a PR, an issue, a
release check) MUST name all of these, or it is invalid evidence and does not count:

- **Engine build**: the full s&box build string from the editor (About dialog or its
  `.version`), for example `26.10.02`.
- **Library version**: the git tag or `git:<sha>` for a library checked out from git, or the
  version Library Manager shows (the `LibraryVersion` constant) for one installed through the
  editor. Add the package revision (`.version`, `1.0.<revision>`) when known.
- **Server build**: `version`, `kind` (`self-hosted` or `hosted`) and `database` from
  `GET /v3/server-info`, plus the commit when it is not a release build.
- **Operating system** the editor or game ran on.

Record each editor run as a `tested` row in `compat/library-matrix.json` (fields `server`,
`kind`, `library`, `librarySource`, `revision`, `engine`, `os`, `database`, `result`,
`evidence`) instead of free text; see `docs/editor-test-plan.md`. A `pass` row without
`engine`, `os`, `kind` and `librarySource` fails the schema test. Say exactly what you ran and
what you did not. CI-only runs are `ci-only`, never `pass`. Never invent a version or an
engine build you did not read from the machine.
