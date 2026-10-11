# Databases

`sbox-ns` stores everything in one of two backends, selected by
`database.provider` in `database.toml` (`sqlite` or `postgres` only).
Check the connection with `sbox-ns db test` and inspect versions with
`sbox-ns db status`. Every key below is also listed in
[configuration.md](configuration.md).

## Which one to choose

| Backend | Choose it when | Scaling note |
| --- | --- | --- |
| SQLite (default) | Solo dev, a single server, or first install. Nothing to install or configure. | One file, one writer machine. Fine for most single-server games. |
| PostgreSQL 13 or newer | You already operate Postgres, want the database on a separate machine, or want your provider's managed backups. | Tune the pool (below). One server instance per schema; see below. |

ScyllaDB is not supported by this server. There is no ScyllaDB provider
setting. ScyllaDB notes elsewhere in this repo describe the managed service
internals, not this binary.

## SQLite

```toml
[database]
provider = "sqlite"

[database.sqlite]
path = "sbox-ns.db"   # relative paths resolve inside the data folder
```

Everything lives in `<data dir>/sbox-ns.db` (WAL mode). No users, no
passwords, no server to run. Copy the file only while the server is stopped;
for live backups use `sbox-ns db backup`, which backs up consistently while
running.

Each connection opens with `journal_mode=WAL` and `synchronous=NORMAL`.
WAL keeps committed transactions atomic across an application crash; NORMAL
avoids a disk sync on every commit. An OS crash or power loss can lose recent
commits, so use reliable local storage and keep backups.

## PostgreSQL

```toml
[database]
provider = "postgres"
startup_timeout_seconds = 60   # keep retrying at startup (for example when the DB boots together with the server)

[database.postgres]
# Full connection string. When set, it overrides the fields below.
connection_string = ""
# Example: "Host=db.internal;Port=5432;Database=sbox_ns;Username=sbox_ns;Password=..."

host = "localhost"
port = 5432
database = "sbox_ns"
username = "sbox_ns"

# Prefer password_file so the secret is not stored in this file.
password = ""
password_file = ""

# Disable, Allow, Prefer, Require, VerifyCA, VerifyFull
ssl_mode = "Prefer"

# Maximum pooled connections.
max_pool_size = 50

# All tables are created inside this schema, so the server can share a
# database with other applications.
schema = "network_storage"

# Seconds to wait when opening a connection.
connect_timeout_seconds = 15
```

Connection fields: `host`, `port`, `database`, `username`, plus either
`password` or `password_file`. A full Npgsql `connection_string` may replace
the individual fields. In containers, the same keys are available as
`NS_` environment variables, for example
`NS_DATABASE__POSTGRES__HOST=db.internal` with a Docker secret at
`NS_DATABASE__POSTGRES__PASSWORD_FILE=/run/secrets/pg_password`
(see [self-hosting.md](self-hosting.md) for a Compose example).

Password handling: `sbox-ns setup` never takes a password as a command line
argument. Typed interactively, the database password is stored in
`config/secrets/postgres_password` and referenced from `password_file`;
scripted installs use `--pg-password-file FILE`. Use an absolute path for
`password_file` outside the default layout.

Schema behavior: every table is created inside `database.postgres.schema`
(default `network_storage`), so one database can host other applications
alongside it. The PostgreSQL user needs permission to create the schema, or
ownership of an existing one:

```sql
CREATE ROLE sbox_ns LOGIN PASSWORD 'change-me';
CREATE DATABASE sbox_ns OWNER sbox_ns;
```

One server instance (one data directory) per schema. Sharing a schema
between independent instances or data directories is not supported. Owner
identity and cookie keys live with the data directory, so a schema moved
without its data directory loses its owner sessions and secret key material.

## Migrations

```sh
sbox-ns db status    # provider, schema version, pending migrations
sbox-ns db migrate   # apply pending migrations
```

`db status` prints the database schema, the binary's supported schema, the
certified minimum binary schema and pending migrations. A newer database is
accepted only when its complete compatibility certificate chain proves that
this binary can read/write it. Missing certificates or a breaking gap require
an upgrade or backup restore. `sbox-ns compatibility --json` performs the target
binary's non-mutating schema/config preflight; it does not run migrations.
`db migrate` applies pending migrations; the server also migrates on start.
Neither rewrites the history of a certified newer schema. Take a backup before
upgrading; releases list minimum upgrade versions, so old installs may need an
intermediate release (see [self-hosting.md](self-hosting.md#updates)).

Schema version 3 adds project/slug endpoint and project/key-identifier API-key
indexes, plus surrogate primary keys for request logs and storage errors.
Events sharing a project and millisecond are now kept separately. Existing log
rows are preserved by the migration. Version 2 remains the earlier
legacy-player-projection opt-in migration.

Schema 4 is additive: request logs gain library-version/client-context columns,
storage errors gain request library/context and nullable package/game revisions,
and daily client-version aggregates store request counts and mergeable distinct
player sketches. It preserves schema 3's data contract. Migration compatibility
certificates are stored in `schema_compatibility` beside the unchanged
`schema_version` table. Historical migrations are conservatively breaking; new
migrations must explicitly choose `additive` only when old read/write assumptions
remain valid. See [compatibility.md](compatibility.md).

Before applying or testing migrations against an existing database, run
`sbox-ns db backup`. Migrations are forward-only. `rollback --keep-data` can
return to a compatibility-aware binary certified for the current schema without
restoring data. Across a breaking gap—or to a pre-compatibility binary—ordinary
rollback restores the pre-update backup and loses writes since that backup.

Endpoint record writes and deletes commit in one store transaction on both
backends. A failure rolls back the complete endpoint flush instead of issuing
compensating writes. Analytics batches also commit transactionally; their
retention and bounded buffer are documented in
[configuration.md](configuration.md#analytics-retention-and-buffering).

## Backups and restores

```sh
sbox-ns db backup                        # timestamped backup inside the data folder
sbox-ns db backup --output /backups/ns.bak
sbox-ns db restore /backups/ns.bak       # stop the server first
```

Backups are same-provider restores, not cross-provider migration. To move
between SQLite and PostgreSQL (either way) or to another machine, use
`sbox-ns export` and `sbox-ns import`; see [export.md](export.md).

SQLite: the backup is a consistent file copy, safe while the server runs.
Restore replaces the database file and keeps the replaced file as
`.before-restore`; WAL sidecars are cleared.

PostgreSQL: the backup uses `pg_dump` custom format scoped to the
configured schema, and restore uses `pg_restore` with `--clean --if-exists
--no-owner --schema=<schema>`. Both tools must be installed and on `PATH`;
the server passes host, port, username, database, password (`PGPASSWORD`)
and SSL mode from your configuration. Stop the server before restoring.

Always back up the config folder with the database, including
`<config dir>/secrets/`. Without the auth session secret, player sessions
become invalid after a restore; without the storage encryption key, every
secret API key stops working.

## Resource guidance

SQLite: no tuning. Keep the data folder on local disk with room for the
database plus timestamped backups under `<data dir>/backups/`.

PostgreSQL: defaults are `max_pool_size = 50`, `connect_timeout_seconds =
15`, `startup_timeout_seconds = 60`. Raise `max_pool_size` only with the
database sized to accept the connections; raise `startup_timeout_seconds`
when the database boots at the same time as the server (Compose, systemd
ordering). Connection problems surface in `sbox-ns db test`,
`sbox-ns doctor`, and `GET /health` (503 with `database-unavailable` when
the database is down).
