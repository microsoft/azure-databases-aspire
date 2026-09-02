# Configuration reference

This page covers the public API methods you can use in `Aspire.Hosting.DocumentDB`, along with usage examples and default values.

## AddDocumentDB

The extension adds a DocumentDB server resource to the Aspire application model and starts a container for local development.

```csharp
// Minimal -- random port, generated credentials
var server = builder.AddDocumentDB("documentdb");

// Fixed host port
var server = builder.AddDocumentDB("documentdb", port: 10260);

// Custom credentials via Aspire parameters
var user = builder.AddParameter("db-user");
var pass = builder.AddParameter("db-pass", secret: true);
var server = builder.AddDocumentDB("documentdb", userName: user, password: pass);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` | (required) | Resource name. Also used as the connection string name when referenced by services. |
| `port` | `int?` | `null` (random) | Host port to expose. When `null`, Aspire assigns a random available port. |
| `userName` | `IResourceBuilder<ParameterResource>?` | `null` | Custom username parameter. When `null`, defaults to `admin`. |
| `password` | `IResourceBuilder<ParameterResource>?` | `null` | Custom password parameter. When `null`, a random password is generated. |

## AddDatabase

Adds a named database as a child resource of a DocumentDB server. Services reference the database resource to get a connection string that includes the database name.

```csharp
var db = builder.AddDocumentDB("documentdb")
                .AddDatabase("mydb");

// Custom database name (resource name differs from database name)
var db = builder.AddDocumentDB("documentdb")
                .AddDatabase("db-resource", databaseName: "actual_database_name");

// Multiple databases on the same server
var server = builder.AddDocumentDB("documentdb");
var ordersDb = server.AddDatabase("orders");
var usersDb = server.AddDatabase("users");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` | (required) | Resource name. Used as the connection string name when referenced. |
| `databaseName` | `string?` | Same as `name` | The actual database name in DocumentDB. Defaults to the resource `name` if not specified. |

## WithHostPort

Binds the DocumentDB container to a specific host port instead of a randomly assigned one. Useful for development when you want a predictable port.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithHostPort(10260);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `port` | `int?` | `null` (random) | The host port to bind. `null` reverts to a random port. |

## WithDataVolume

Attaches a Docker named volume to persist DocumentDB data across container restarts.

```csharp
// Auto-generated volume name
var server = builder.AddDocumentDB("documentdb")
                    .WithDataVolume();

// Explicit volume name
var server = builder.AddDocumentDB("documentdb")
                    .WithDataVolume(name: "documentdb-data");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string?` | Auto-generated | Docker volume name. When `null`, a name is generated from the application and resource names. |
| `isReadOnly` | `bool` | `false` | Unsupported. Passing `true` throws an `ArgumentException` — see [Storage requirements](#storage-requirements). |
| `targetPath` | `string?` | `/data` | Path inside the container where the volume is mounted when this helper is used. Must be an absolute container path below `/`. Repeated separators, `.`, and `..` are resolved the way the container runtime resolves them, so `/data/`, `//data`, and `/foo/../data` are the same path; one that resolves to the container root (`/data/..`) or reaches above it (`/../data`, which the runtime silently clamps onto `/data`) is rejected. |

This method mounts the volume at `targetPath` (which defaults to `/data`, matching the container default) and sets the `DATA_PATH` environment variable to match so DocumentDB writes to the mounted directory.

Where an unmounted `/data` lives depends on the image version:

| Image | `/data` when nothing is mounted there |
|---|---|
| `0.114.0` and earlier | An ordinary directory in the container's writable layer, discarded when the container is removed. |
| `0.116.0` and later | The `Dockerfile` declares `/data` as a `VOLUME`, so every run gets a fresh **anonymous** volume that the container runtime names and owns. Container removal can strand it, so unmounted runs accumulate orphaned volumes (`docker volume prune` reclaims them). |

Either way the data is not durable, and the anonymous volume is never reused: do not depend on it
for predictable storage. Use `WithDataVolume()` with an explicit or generated named volume when
persistence is intentional.

On `0.116.0` and later, mounting on `/data` is also the *only* way to suppress the anonymous
volume: neither Docker nor Aspire can un-declare an image `VOLUME`. Leaving `targetPath` at its
default does that for you. A non-default `targetPath` still stores data where you asked, but an
unused anonymous volume is created at `/data` on every run, and a warning saying so is written to
the AppHost log under the `Aspire.Hosting.DocumentDB.Storage` category (the same place the
shared-data-directory warning goes; these are diagnostics about how the resource was configured, and
they are produced before the container exists). The warning is raised only for images known to
declare that volume — recognised
`documentdb-local` tags at `0.116.0` or later. Older tags, unrecognised tags, custom images, and
resources built from your own Dockerfile get no warning, because for them nothing is known to be
created at `/data`.

> **Pin your credentials when you persist data.** The container hashes the configured password into a PostgreSQL role the first time it initialises a data directory, and that role then lives in the volume. `AddDocumentDB` generates a random password when you do not supply one, so the *second* run presents a different password than the one stored in the volume and every connection fails with `MongoAuthenticationException: ... Command saslContinue failed: Invalid key`. The data is intact but unreachable. Pass explicit `userName`/`password` parameters whenever you use `WithDataVolume` or `WithDataBindMount`:
>
> ```csharp
> var userName = builder.AddParameter("documentdb-user");
> var password = builder.AddParameter("documentdb-password", secret: true);
>
> var server = builder.AddDocumentDB("documentdb", userName: userName, password: password)
>                     .WithDataVolume();
> ```
>
> To change the password later, delete the volume (losing its contents) or alter the role through the PostgreSQL endpoint — see [WithPostgresEndpoint](#withpostgresendpoint).

## WithDataBindMount

Mounts a host directory into the container for data persistence. Prefer `WithDataVolume` for most cases; bind mounts are useful when you need direct access to the data files on the host.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithDataBindMount("./data/documentdb");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `source` | `string` | (required) | Path on the host machine to mount. |
| `isReadOnly` | `bool` | `false` | Unsupported. Passing `true` throws an `ArgumentException` — see [Storage requirements](#storage-requirements). |

By default, this helper mounts data at `/data` inside the container — the container default, and the path `0.116.0` and later declare as an image volume, so no anonymous volume is created there — and sets `DATA_PATH` accordingly.

> **A bind mount only carries a PostgreSQL data directory on a container runtime that applies ownership changes to the mounted host path immediately.** PostgreSQL refuses to start unless the data directory is already owned by the user starting the postmaster, and the container entrypoint establishes that by running `chown` on `DATA_PATH` milliseconds before starting it. Docker Desktop applies that `chown` asynchronously — measured on macOS with VirtioFS, and expected on its Windows and Linux hosts, which share the same file-sharing design — so the postmaster reads the previous owner and aborts with `FATAL: data directory "/data" has wrong ownership`. The first run hides this behind the seconds `initdb` spends between the two steps, so the container comes up once and then fails **every** restart, with the data intact on the host but unreadable. Nothing in the application model can order that runtime's `chown`. Use [`WithDataVolume`](#withdatavolume) there — a named volume lives inside the runtime's own filesystem and is unaffected. A bind mount on a native container engine is an ordinary mount and restarts normally. See [Bind-mounted data fails to restart on Docker Desktop](troubleshooting.md#bind-mounted-data-fails-to-restart-on-docker-desktop).

> The credential caveat under [WithDataVolume](#withdatavolume) applies here too: supply explicit `userName`/`password` parameters, or the generated password will stop matching the role stored in the mounted directory on the next run.

## Storage requirements

These constraints come from the DocumentDB Local container itself, not from Aspire. Where a
configuration cannot work, the integration fails it while the application model is being built, or
at resource start, rather than letting the container fail confusingly a minute later. Rules that
arrived in a specific image version are marked as such; everything unmarked applies to every image
this package supports.

### How the data directory is identified

The rules below apply to the directory the container will really write to, which is the effective
value of `DATA_PATH`. `DATA_PATH` is an ordinary environment variable: `WithDataVolume()` and
`WithDataBindMount(...)` set it to the path they mount on, and a raw
`WithEnvironment("DATA_PATH", ...)` — a literal, a parameter, or a value computed in an environment
callback — participates on the same "last call wins" terms as every other Aspire environment
configuration. Set it after a storage helper and it wins; set it before and the helper wins. When
nothing sets it — or when it is set to an empty value, which the entrypoint's
`DATA_PATH=${DATA_PATH:-/data}` treats identically — the container's own `/data` default applies.

The checks run inside the resource's own configuration pipeline, as its last environment callback,
so they read the final value rather than recomputing it: a callback that answers differently each
time it runs is evaluated once, and the guard and the container cannot see different directories.
The canonical path replaces `DATA_PATH` — and is written even when nothing else set it, so an image
whose own default is somewhere other than `/data` cannot write to a directory the checks never
looked at. Nothing else is read or resolved: the password and every other environment value are
left exactly as the callbacks produced them.

The guard takes the last position twice and then verifies it. It is appended when the application
starts, and moved back to the end of both pipelines immediately before the resource starts, which
covers `IDistributedApplicationLifecycleHook.BeforeStartAsync` and any `BeforeStartEvent` subscriber
registered after `AddDocumentDB` — both of which run after the guard is installed and can add more
callbacks. Anything that appends later still — a `BeforeResourceStartedEvent` subscriber registered
after `AddDocumentDB`, or, in publish mode where no per-resource event is published, a lifecycle
hook — makes the resource fail rather than start on a data directory nothing checked. Configure
storage through the application model rather than after it is built, or register such a subscriber
before `AddDocumentDB`.

Position alone is not enough, because the app host records each callback's result the first time it
runs and reuses it for the rest of the run. Storage is the sharpest form of that: a volume or bind
mount is a plain annotation, so anything that builds the resource's configuration early —
`ExecutionConfigurationBuilder`, the same public API the app host uses, from a lifecycle hook or an
event subscriber — and only then mounts `/data` read-only, or puts a second resource on the same
volume, changes what the container mounts without running a single check again. The guard therefore
records what it judged — the mounts by value, the membership of both callback pipelines,
`WithExplicitStart()` and the effective image — and it is compared by the same authority, and at the
same two uncached checkpoints, as the container's command line: a container-runtime-arguments
callback on every container creation in a run, and the manifest publishing callback in a publish,
which runs while the resource is serialized and so after every model event. A mismatch fails the
resource, naming what kind of thing changed and no value. Re-declaring the same storage is not a
change: replacing a mount with an identical one, or reordering them, is accepted.

In publish mode a `DATA_PATH` supplied as a parameter is a manifest expression, not a path.
Resolving it is not an option — the value belongs to the deployment, and a parameter may be a
secret — so a resource that supplies one *and* mounts storage is refused: every rule below would
otherwise be silently skipped, and a manifest putting two DocumentDB resources on one data
directory would publish without complaint. A resource that mounts nothing has no storage to get
wrong, so there the expression is kept. Run mode resolves the value once and checks it normally.

Container paths are compared the way the container runtime resolves them: repeated separators
collapse, and `.` and `..` segments are resolved before the mount is created. `/data`, `/data/`,
`//data` and `/foo/../data` are one directory, so an alias cannot slip past any rule below. A path
that resolves to the container root (`/data/..`) is rejected, because the runtime refuses it
outright (`destination can't be '/'`) and the root cannot hold a cluster.

A path that reaches above the root (`/../data`) is rejected too, but for the opposite reason: the
runtime does **not** refuse it. Docker clamps the target and mounts on `/data`, so the storage lands
on a directory the call never named — and can silently become, or collide with, the data directory.
Write the resolved path instead.

A mount does not have to be *on* `DATA_PATH` to back it. A volume mounted at `/data` also supplies
`/data/cluster`, so if `DATA_PATH` is `/data/cluster` that volume is the data mount and the
read-only and duplicate rules apply to it. The most specific mount wins, matching on segment
boundaries: a mount on `/data/cluster` takes precedence over one on `/data`, and `/database` is not
below `/data`.

When `DATA_PATH` is set to an empty value, or is not set at all, the container default `/data`
applies and is written into the environment as such.

### The entrypoint's `--data-path` argument is reserved

The container entrypoint accepts `--data-path`, documented by the image as "Overrides DATA_PATH
environment variable", and exports it while parsing arguments. Passing it from Aspire would move
the data directory to a path the environment never names, past every rule below and past the mount
that was supposed to back it, so the integration refuses it with an `InvalidOperationException`
before the container is created. `-d` is refused with it: today's entrypoint answers `Unknown option
-d` and exits, and reserving the conventional short form keeps it from becoming a second channel
later. Use `WithDataVolume()`, `WithDataBindMount(...)` or `WithEnvironment("DATA_PATH", ...)`
instead.

A token whose value only arrives later — a parameter, or a `ReferenceExpression` — cannot be read
without resolving it, and resolving it here would duplicate the evaluation Aspire is about to make
and risk putting a secret somewhere it does not belong. Such a token is therefore refused wherever
the entrypoint would read an option name, because it could resolve to `--data-path`. It is accepted
in the one position where it cannot be an option: directly after a literal option that takes a
value, which is the entrypoint's own `--option value` grammar. So `WithArgs("--log-level", level)`
is fine, while `WithArgs(flag, "/pgdata")`, `WithArgs("--skip-init-data", flag)` (that option takes
no value, so the next token is read as an option name) and `WithArgs("--log-level=debug", flag)`
(the option already carries its value) are refused.

### The data directory must be writable

`WithDataVolume(isReadOnly: true)` and `WithDataBindMount(..., isReadOnly: true)` throw an
`ArgumentException`, and a read-only volume or bind mount placed on the data path with the raw
Aspire `WithVolume`/`WithBindMount` APIs fails the resource start with an `InvalidOperationException`.

A read-only data directory can never work: the entrypoint takes ownership of the directory, and
`initdb` has to change its permissions before PostgreSQL can create the cluster or write WAL. The
container does not report that clearly — it fails with `initdb: error: could not change permissions
of directory "/data": Read-only file system` buried in interleaved log streams, then spends 60
seconds before printing the misleading `PostgreSQL failed to start within 60 seconds` banner.

Read-only mounts are still correct for *input*: `WithInitData(...)` mounts seed scripts read-only
at `/init_doc_db.d`, and `WithTlsCertificate(...)` mounts the certificate and key read-only.

### One running container per data directory

Two PostgreSQL instances on one data directory corrupt it. From `0.116.0` the container defends
itself: it claims the directory with an exclusive `flock`, and a second container that mounts the
same volume or host directory exits immediately with

```text
Error: another DocumentDB container is already using the data directory /data. Refusing to start: ...
```

while the container already serving the directory keeps running, unaffected. **Images at or below
`0.114.0` have no such interlock**: nothing refuses the second start, and the two instances corrupt
the directory silently.

Because of this, give every DocumentDB resource its own storage. Two DocumentDB resources in one
application model whose *data directories* resolve to the same volume name or bind-mount source
fail with an explanatory `InvalidOperationException`. Each resource registers the storage its data
directory occupies while its own configuration is built, so the second one to reach the same
storage is the one that fails — which is also the one whose container the image would refuse.

"Same storage" means the same directory, not the same pair of strings.

For a **bind mount** that directory is one host path: the mount source with whatever part of
`DATA_PATH` falls below the mount target appended, resolved with the host's own rules. So a resource
that binds `/srv/documentdb` at `/data` and writes to `/data/cluster` and a resource that binds
`/srv/documentdb/cluster` at `/data` are recognised as the one directory they share, in either
declaration order — and `/srv/documentdb`, `/srv/documentdb/`, `/srv/documentdb/.` and
`/srv/documentdb/../documentdb` are all that same source. Because the comparison is the host's,
`/data/Cluster` and `/data/cluster` are one directory on macOS and Windows and two on Linux, exactly
as they are on disk. (Symbolic links are not followed: two resources aimed at one directory through
different links are not detected here, and on `0.116.0` and later the container's own lock still
refuses the overlap.)

For a **volume** the identity is the volume name plus the subdirectory, compared exactly — a volume
name is not a path and cannot be combined with one, and the container reads that subdirectory on its
own case-sensitive filesystem. Two resources on one volume at `/data/alpha` and `/data/beta` are two
clusters and are allowed; two at `/data/cluster` are one and are refused.

One narrow case is downgraded to a warning: both resources resolve to a recognised `0.116.0`-or-
later tag **and** one of them is started manually with `WithExplicitStart()`. There the pair may
never run at the same time, and if they do, the image refuses the second start loudly rather than
corrupting anything. When either side is an older, unrecognised, or custom image — or is built
from your own Dockerfile or pinned by digest, whatever its image annotation says — the combination
stays a hard failure even with `WithExplicitStart()`, because there is no interlock to fall back
on.

Sharing storage that is *not* the peer's data directory is not a conflict: a resource may point
`WithInitData(...)` or `WithTlsCertificate(...)` at the same host directory another resource uses
for data, because those are read-only inputs on different container paths.

The check compares DocumentDB resources within one application model only. The same rule applies
across application models and across tools: stop a container that is holding a volume before
starting another one on it, including `docker run` sessions and a second AppHost instance. On
`0.116.0` and later those cases are still caught at runtime by the container's own lock.

### Ownership and permissions

The container runs as its own `documentdb` user and takes ownership of the data directory
(`chown -R` followed by `chmod -R 750`) on every start, and `initdb` leaves the cluster directory
mode `0700`. Consequences:

- **Volumes** need nothing from you; Docker seeds the volume and the container adjusts it.
- **Bind mounts** must point at a host directory the container may take ownership of. On Linux the
  host directory changes uid/gid on disk, so after the first run the files usually belong to a uid
  your host user is not, and reading them back needs `sudo` or a container. Docker Desktop on macOS
  and Windows hides this behind its filesystem translation layer.
- Point the mount at a directory that is **empty** or holds an existing DocumentDB cluster. The
  container does not clean a directory it does not recognise — it refuses it. A single stray file
  (a `.gitkeep` committed to keep the directory in source control, or a `.DS_Store` written by
  macOS) is enough to produce `Warning: Directory /data exists but doesn't appear to contain a
  valid PostgreSQL data directory`, after which PostgreSQL never starts and the container exits
  non-zero a minute later behind the `PostgreSQL failed to start within 60 seconds` banner. Your
  files are left where they are.
- Do not put two mounts on the directory that backs the data path (for example `WithDataVolume()`
  *and* `WithDataBindMount(...)`, or two spellings of one path); the container runtime rejects
  duplicate mount targets, so the integration fails the start with a clear message instead.

### Initialization is one-shot per data directory (`0.116.0` and later)

Sample data (`INIT_DATA`) and custom scripts mounted with `WithInitData(...)` run **once per data
directory**, not once per container start. `0.116.0` introduced that guarantee and records it with
markers under `<data-path>/.documentdb-local/`:

| Marker | Meaning |
|---|---|
| `custom_data_attempted` | Custom initialization began. Written *before* the first user script runs. |
| `custom_data_succeeded` | Custom initialization completed successfully. |
| `sample_data_initialized` | Built-in sample data was loaded successfully. |

Because the markers live inside the data directory, they survive restarts, `docker compose down &&
up`, host reboots, and volume backups.

- If initialization **succeeded**, later starts skip it and log that it was already initialized.
- If initialization **failed part way**, the attempt marker is present without the success marker.
  The container does *not* retry: it logs a warning that the previous attempt may have partially
  applied, and continues. This is deliberate — re-running non-idempotent seed scripts against
  half-seeded data caused restart loops.
- Editing your scripts does not re-run them. To re-initialize, start against a **fresh or emptied**
  data directory (a new volume name, or `docker volume rm` on the old one).
- The very first attempt is different: if custom initialization fails on a fresh data directory, the
  container exits non-zero, so the resource fails fast rather than serving an incompletely seeded
  database.

Without persistent storage, every run starts from an empty data directory, so initialization runs
on every run.

**On `0.114.0` and earlier there are no markers and no one-shot behavior.** The entrypoint runs
whatever initialization you requested on **every container start**: custom scripts whenever the
mounted `INIT_DATA_PATH` directory contains `.js` files, and sample data whenever `INIT_DATA=true`.
With a persisted data directory that means the same scripts are replayed against data they have
already seeded, so on these images seed scripts must be **idempotent** — guard inserts on a lookup,
prefer upserts over blind inserts, and do not assume an empty collection. `WithoutSampleData()`
stops the built-in sample import from being replayed. There is no failed-attempt marker either: a
script that failed part way is simply run again on the next start.

## WithLogLevel

Sets both the gateway's canonical `DOCUMENTDB_LOG_LEVEL` environment variable and the legacy
container `LOG_LEVEL` variable. Starting with DocumentDB `0.114.0`, the gateway reads
`DOCUMENTDB_LOG_LEVEL` as a tracing filter, so this API changes observable verbosity on the current
default image. Gateways through `0.113.0` consume neither variable for verbosity, so
`WithLogLevel(...)` remains a verbosity no-op on those images.

`LOG_LEVEL` is retained to preserve the Local entrypoint contract: the entrypoint validates its six
legal values, but no Local image uses it to select gateway verbosity. Both variables receive the
same lowercase public-API value. In particular, `Quiet` remains `quiet` and becomes newly effective
on `0.114.0` and later. Upstream tracing does not define `quiet` as a level; it currently parses the
value as an unmatched tracing target, which suppresses gateway output. That preserves the existing
Aspire API contract, but the mechanism depends on upstream tracing-filter semantics.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithLogLevel(DocumentDBLogLevel.Debug);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `logLevel` | `DocumentDBLogLevel` | (required) | One of `Quiet`, `Error`, `Warn`, `Info`, `Debug`, `Trace`. Mapped to the lowercase string passed to the container. |

## WithInitData

Bind-mounts a host directory containing custom initialization scripts (for example, MongoDB shell scripts) into the container at `/init_doc_db.d`. Built-in sample data is implicitly disabled so the mounted scripts are the only initialization source.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithInitData("./seed");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `source` | `string` | (required) | Host directory containing initialization scripts. Mounted read-only at `/init_doc_db.d` and exposed via `INIT_DATA_PATH`. |

This helper sets both `INIT_DATA=false` and `SKIP_INIT_DATA=true`, matching the
container's canonical `--skip-init-data` behavior so an earlier
`INIT_DATA=true` setting cannot re-enable the built-in sample collections.

> [!NOTE]
> From `0.116.0` these scripts run **once per data directory** and are not retried after a failed attempt. On `0.114.0` and earlier they run on **every container start**, so with persisted storage they must be idempotent. See [Initialization is one-shot per data directory](#initialization-is-one-shot-per-data-directory-01160-and-later).

## WithoutSampleData

Disables the built-in sample data initialization. Custom scripts configured
through `WithInitData(...)` are unaffected and still run for a new data volume.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithoutSampleData();
```

This sets `INIT_DATA=false` and `SKIP_INIT_DATA=true` on the container, matching
the canonical `--skip-init-data` option.

## WithoutExtendedRum

Disables the `extended_rum` index access method in the DocumentDB Local container. Extended RUM is enabled by default starting with DocumentDB v0.111-0.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithoutExtendedRum();
```

This sets `DISABLE_EXTENDED_RUM=true` on the container. On container images older than v0.111-0 the environment variable is ignored.

## WithoutUserCreation

Disables the automatic user creation performed by the DocumentDB Local
container on startup.

For curated images 0.112.0 and older, built-in sample initialization is enabled
by default. On a fresh container, `WithoutUserCreation()` must be paired with
`WithoutSampleData()` so the default initialization does not require the
skipped credentials.

For images from 0.113.0 onward, including 0.116.0, built-in sample
initialization does not run unless requested. A fresh container can therefore
remain running with `CREATE_USER=false` when no initialization requiring those
credentials is requested. The generated connection strings still will not
authenticate unless that user already exists, typically in persisted storage
from an earlier run.

> [!WARNING]
> On every version, requested built-in sample initialization and custom scripts
> mounted through `WithInitData(...)` authenticate using the configured
> credentials. If the skipped user does not already exist, that initialization
> can fail and cause the container to exit. `WithoutSampleData()` disables only
> built-in sample data; it does not disable custom initialization.

```csharp
// Reuse a previously created user while skipping built-in sample data
var server = builder.AddDocumentDB("documentdb")
                    .WithDataVolume()
                    .WithoutUserCreation()
                    .WithoutSampleData();
```

This sets `CREATE_USER=false` on the container.

## WithTlsCertificate

Mounts a custom TLS certificate and key into the container so DocumentDB Local serves connections with your certificate instead of its default self-signed one.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithTlsCertificate("./certs/documentdb.pem", "./certs/documentdb.key");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `certPath` | `string` | (required) | Path on the host to the certificate file. |
| `keyPath` | `string` | (required) | Path on the host to the private key file. |

The certificate and key are bind-mounted at distinct container paths (`/documentdb-cert-<filename>` and `/documentdb-key-<filename>`), so they can be supplied even when the host file names are identical. The corresponding `CERT_PATH` and `KEY_FILE` environment variables are set automatically.

## WithTelemetry (obsolete)

> **Deprecated since this release.** The `ENABLE_TELEMETRY` environment variable is no longer
> consumed by the DocumentDB gateway in container image v0.112-0 or later. This method continues
> to set the variable for binary compatibility but has no observable effect on the running
> container on those images. Calling it produces compiler diagnostic `ASPIREDOCDB0001`. Use
> [`WithOpenTelemetryMetrics`](#withopentelemetrymetrics) to configure OTLP metrics export
> instead. The method may be removed in a future release.

Sets the `ENABLE_TELEMETRY` environment variable. Retained for binary compatibility only.

```csharp
// Disable telemetry (no-op against gateway v0.112-0+; retained for binary compatibility).
var server = builder.AddDocumentDB("documentdb")
                    .WithTelemetry(enabled: false);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `enabled` | `bool` | `true` | Value written to `ENABLE_TELEMETRY`. Not consumed by the gateway in v0.112-0 or later. |


## WithOpenTelemetryMetrics

Enables OpenTelemetry metrics export from the DocumentDB gateway via OTLP/gRPC. Requires
container image v0.112-0 or later. This API configures metrics only. The upstream gateway also
supports tracing starting in v0.116-0, but this package does not yet expose a typed tracing API.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithOpenTelemetryMetrics(
                        endpoint: "http://otel-collector:4317",
                        exportInterval: TimeSpan.FromSeconds(30),
                        serviceName: "documentdb-local",
                        serviceVersion: "0.112.0");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `endpoint` | `string?` | `null` | OTLP/gRPC endpoint of the collector to receive metrics. When provided, sets `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT`, which takes precedence over the generic `OTEL_EXPORTER_OTLP_ENDPOINT` per the OpenTelemetry specification. Must be non-empty when provided. |
| `enabled` | `bool` | `true` | Whether metrics export is enabled. Sets `OTEL_METRICS_ENABLED`. The container default is `false`; calling this method flips it on unless `enabled: false` is passed. |
| `exportInterval` | `TimeSpan?` | `null` | How often the gateway flushes metrics. When provided, sets `OTEL_METRIC_EXPORT_INTERVAL` (milliseconds, integer, invariant culture). Must be non-negative. |
| `timeout` | `TimeSpan?` | `null` | Per-export request timeout. When provided, sets `OTEL_EXPORTER_OTLP_METRICS_TIMEOUT` (milliseconds, integer, invariant culture). Must be non-negative. |
| `serviceName` | `string?` | `null` | Logical service name attached to the metrics. When provided, sets `OTEL_SERVICE_NAME`. Must be non-empty when provided. For the default official `0.116.0` image, omitting it preserves the image default of `documentdb_gateway`. |
| `serviceVersion` | `string?` | `null` | Logical service version attached to the metrics. When provided, sets `OTEL_SERVICE_VERSION`. Must be non-empty when provided. |

When `endpoint` is omitted, the gateway falls back to the standard OTLP/gRPC default
(`http://localhost:4317`). In typical Aspire container scenarios that fallback is unreachable,
so an explicit endpoint pointing to your collector (for example, the Aspire dashboard or an
OpenTelemetry Collector container) is recommended.

Merge semantics across multiple calls on the same builder:

- `enabled` is non-nullable and is therefore written on every call. The last call's value wins
  (defaulting to `true` when omitted), even if a previous call set it to `false`. To preserve a
  `false` setting through subsequent calls, pass `enabled: false` explicitly each time.
- All other parameters are nullable; later calls override only the environment variables they
  explicitly set, and values from earlier calls are preserved for parameters left at `null`.

`WithOpenTelemetryMetrics` and the obsolete `WithTelemetry` set disjoint environment variables
and do not interact.

### Gateway configuration compatibility from `0.116.0`

Starting with DocumentDB `0.116.0`, the gateway resolves telemetry settings as
*JSON > environment variable > default*, reading them from `SetupConfiguration.json`. The file
shipped in the image pins `TelemetryOptions.Metrics.Enabled` to `false` and
`TelemetryOptions.ServiceName` to `documentdb_gateway`, so setting the environment alone can no
longer decide whether metrics run.

Whenever this method is called against an official `documentdb-local` image of `0.116.0` or later,
it therefore wraps the container entrypoint. The wrapper reads the configuration file the
container would otherwise have used, removes the keys the environment has to win over, points
`CONFIG_DIR` at the sanitized copy, and execs the image's own entrypoint.

The sanitized copy is created under the first writable directory among `/tmp`, `/var/tmp`, and
`/dev/shm` whose filesystem subtree does not overlap the effective canonical `DATA_PATH`, including
a `-d` or `--data-path` command-line override. This matters when the database itself uses a normally
temporary path such as `/tmp`: placing the copy there would make a fresh data directory non-empty
before PostgreSQL initialization. If none of those locations is safely separated and writable,
startup fails with a diagnostic instead of writing into the data directory.

**Which directory the wrapper reads.** Exactly the one the image entrypoint would have read:

1. `CONFIG_DIR`, when the caller set it.
2. Otherwise `/etc/documentdb/gateway`, when the packaged layout is present (detected the same way
   the image entrypoint detects it: both `/usr/share/documentdb/scripts/start_oss_server.sh` and
   `/usr/share/documentdb/scripts/utils.sh` exist).
3. Otherwise `$GATEWAY_HOME/pg_documentdb_gw`, defaulting `GATEWAY_HOME` to
   `/home/documentdb/gateway` as upstream does.

**Which keys are removed.**

| JSON key | Removed |
|---|---|
| `TelemetryOptions.Metrics` (the whole object, every key inside it) | always |
| `TelemetryOptions.ServiceName` | only when `serviceName` was supplied |
| `TelemetryOptions.ServiceVersion` | only when `serviceVersion` was supplied |
| `TelemetryOptions.Tracing` (and everything else) | never |

The `TelemetryOptions.Metrics` object is removed whole — not key by key — because this method owns
the metrics signal end to end. Any surviving key would re-pin that setting ahead of the environment
precedence documented above: the shipped `OtlpEndpoint: http://localhost:4317` would beat both
`OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` and `OTEL_EXPORTER_OTLP_ENDPOINT` and export metrics into the
DocumentDB container itself, and enumerating today's keys individually would silently leave any
field a future gateway release adds authoritative over the environment. Removing the object costs
nothing on the stock image: the values it ships are byte-identical to the gateway's own compiled-in
defaults.

The identity keys are treated differently because the gateway shares one OpenTelemetry `Resource`
across signals and the shipped `ServiceName` is *not* the gateway's compiled-in default, so
removing it would silently rename traces too. Everything the caller did not override survives, so
a configuration file you point `CONFIG_DIR` at keeps its other values and the stock image keeps
its shipped identity and its disabled tracing. No default identity is injected on your behalf.

> **Explicit identity is cross-signal.** The gateway builds one OpenTelemetry `Resource` for all
> signals, so supplying `serviceName` or `serviceVersion` removes the shared JSON identity and
> changes the identity of exported traces too, not only metrics. Omit them to keep the identity
> the configuration file specifies. Setting `OTEL_SERVICE_NAME` through `WithEnvironment(...)` is
> *not* an override: the JSON key is left in place and continues to win inside the gateway.

**`enabled: false` is wrapped too.** A configuration file can switch metrics on from JSON, which
would beat `OTEL_METRICS_ENABLED=false`. Disabling metrics therefore installs the same wrapper and
removes the `TelemetryOptions.Metrics` object, so an explicit `enabled: false` actually disables
them.

**Publishing.** The wrapper is expressed entirely as the container `entrypoint` and `args`, the
only file-shaped mechanisms that round-trip through the Aspire manifest. Consequently:

- `aspire publish` and `azd` emit a manifest that still names the official image and carries the
  wrapper in `entrypoint`/`args`, so published apps deploy and export metrics.
- Direct AppHost runs execute exactly the same command.
- The decision is made against the resource's *final* image, so
  `.WithOpenTelemetryMetrics().WithDocumentDBVersion(...)` and
  `.WithOpenTelemetryMetrics().WithImageTag(...)` behave the same as the reverse order.

**What is left alone, and what fails loudly.** Custom images and tags outside the `pgNN-X.Y.Z`
grammar are untouched, as is any resource built from your own Dockerfile — including one whose
image annotation names the official image and a recognised tag, and one pinned by digest, because
for a build neither is what runs. A private registry mirror is wrapped when it is the curated
repository directly beneath a registry host — only the host differs — however the reference spells
the boundary between the two annotation fields; a mirror that adds a namespace path, such as
`contoso.azurecr.io/mirrors/documentdb/documentdb-local`, is a different repository and is left
alone. See [How the image is recognised](#how-the-image-is-recognised). Four situations throw
instead of guessing:

- Pinning the official image by digest (`WithImageSHA256`). The digest supersedes the tag — Aspire
  clears it — so the DocumentDB version is unknowable, and both applying and skipping the wrapper
  would be silently wrong. Select the image by tag, or drop `WithOpenTelemetryMetrics(...)` and
  configure telemetry inside the image the digest names.
- Supplying your own container entrypoint on the same resource, including `/bin/bash`: the two
  cannot both own the container command.
- Replacing the wrapper's entrypoint after it has been installed — including from a
  `BeforeStartEvent` subscriber or lifecycle hook that runs after the wrapper's own, later in the
  same startup. The wrapper re-checks that it still owns the entrypoint when its arguments are
  resolved, which is after every subscriber has run, so this is caught rather than splicing those
  arguments into somebody else's command line.
- Selecting an image that does not need the wrapper *after* the wrapper has taken over the
  entrypoint — including by adding a Dockerfile build at that point. The wrapper cannot be
  uninstalled, and dropping its arguments would leave `/bin/bash` with nothing to run. Select the
  image before configuring metrics.

**Argument ordering is fixed, and enforced.** `/bin/bash` reads its command from the first
arguments, so the wrapper's `-c <script> --` prefix has to stay in front of everything else: one
value inserted before it would turn the whole wrapper into bash operands and start nothing. The
wrapper therefore contributes the container's *last* command-line callback, and retakes that
position at every phase the app host offers — `BeforeStartEvent`,
`ResourceEndpointsAllocatedEvent`, `BeforeResourceStartedEvent` and, in publish,
`BeforePublishEvent`. In practice:

- `WithArgs(...)` works in either order and however it is written. Static values, an appending
  callback, and a callback that inserts at the front, clears, or rewrites the list all run *before*
  the wrapper, so what they produce becomes arguments of the image entrypoint rather than of
  `bash`. `.WithOpenTelemetryMetrics().WithArgs(context => context.Args.Insert(0, "--help"))`
  publishes and runs `/bin/bash -c <script> -- --help`.
- The same holds for arguments added later than the model: from a `BeforeStartEvent` subscriber
  registered after `AddDocumentDB`, or from an
  `IDistributedApplicationLifecycleHook.BeforeStartAsync`.
- Adding a command-line callback after the last of those phases — a `BeforeResourceStartedEvent` or
  `BeforePublishEvent` subscriber registered after `AddDocumentDB` — **fails the resource** rather
  than shipping a command line that was built and then taken apart.
- The finished command line is checked before it is used: the entrypoint must still be `/bin/bash`,
  the arguments must begin with exactly `-c`, this run's script, `--`, and the script must not
  appear twice.

**Reading the configuration early freezes it.** The app host records each callback's result the
first time it runs and reuses it for the rest of the run, and it takes the *last* callback's
recorded result as the whole argument list. So a caller who builds the resource's configuration
early — `ExecutionConfigurationBuilder` or `GetArgumentValuesAsync`, typically from an
`IDistributedApplicationLifecycleHook` — and only then changes the resource does not merely reorder
the command line: the recorded wrapper is dropped from it entirely, and nothing re-validates.

The wrapper closes that by recording what the container's command depended on when it produced its
answer — the callbacks in every pipeline, the entrypoint, and the image the resource will run — and
comparing it at the two points the app host never caches:

| Mode | Checkpoint | Runs |
| --- | --- | --- |
| Run | a container-runtime-arguments callback the package adds | on every container creation, after any `WithContainerRuntimeArgs(...)` callback of yours and before the container's command, arguments and environment are read |
| Publish | the manifest publishing callback the package adds | while the resource is serialized, after every lifecycle hook and every model event, including ones raised by subscribers registered after `AddDocumentDB` |

If anything changed, the resource is failed — a publish before the manifest is written, a run
before the container is created. Nothing is repaired at that point: the recorded configuration is
the one the container would receive, and re-running the wrapper would not replace it. Configure the
resource fully before anything reads it, and it never comes up. See
[the troubleshooting entry](troubleshooting.md#withopentelemetrymetrics-throws-about-a-configuration-that-changed).

The wrapper needs `bash`, `jq`, `realpath`, and `mktemp`, all of which the official image provides.
If one is missing, the configuration file cannot be read, or no safe temporary directory is
available, the container fails to start with a diagnostic rather than starting silently without
the override.

`exportInterval` and `timeout` are written as integer milliseconds via the invariant culture.
Values smaller than one millisecond (sub-ms ticks) truncate to `0`; pass whole-millisecond or
larger granularities.

The gateway also reads `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_TIMEOUT` when the
signal-specific variants above are unset. Starting in `0.116.0`, it also applies
`OTEL_RESOURCE_ATTRIBUTES`; images before `0.116.0`, including `0.114.0`, parse that variable but
do not apply it during startup. These are not exposed by the typed API — set them via
`WithEnvironment(...)` if you need them.


## WithOwner

Sets the container `OWNER` environment variable, which names the PostgreSQL role used for
DocumentDB database operations. It is not an arbitrary resource label.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithOwner("documentdb");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `owner` | `string` | (required) | An existing PostgreSQL role used for DocumentDB database operations. |

The bundled PostgreSQL instance creates the default `documentdb` role. A custom owner must already
exist, which is primarily useful with an externally managed PostgreSQL instance. DocumentDB
`0.116.0` aborts explicitly while creating the DocumentDB admin user when the configured role is
absent. Earlier images also fail startup, but only later while waiting for the gateway to start.

## UseTls

Controls whether TLS is included in the generated connection string. TLS is **enabled by default** because the DocumentDB Local container serves TLS on its gateway port.

```csharp
// Disable TLS (for example, connecting to a non-TLS endpoint)
var server = builder.AddDocumentDB("documentdb")
                    .UseTls(false);

// Explicitly enable TLS (this is the default)
var server = builder.AddDocumentDB("documentdb")
                    .UseTls(true);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `useTls` | `bool` | `true` | Whether to add `tls=true` to the connection string. |

> [!NOTE]
> **Container TLS enforcement changed in DocumentDB `0.114.0`.** In container images up to and including `0.113.0`, the gateway always enforced TLS and rejected plain (non-TLS) connections regardless of the documented `TLS_MODE` setting, so `UseTls(false)` produced a connection string the container would refuse. From `0.114.0` the container honors `TLS_MODE`, whose default value `allowTLS` accepts **both** plain and TLS connections — so `UseTls(false)` now works against the default container.

To make the container *reject* plain connections, set the `TLS_MODE` environment variable (there is no dedicated API for this):

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithEnvironment("TLS_MODE", "requireTLS");
```

> [!WARNING]
> Combining `.WithEnvironment("TLS_MODE", "requireTLS")` with `UseTls(false)` is self-contradictory: the generated connection string omits `tls=true` while the container rejects plain connections, so health checks and client connections will fail. `TLS_MODE=disabled` behaves identically to `allowTLS` — the gateway has no plain-only mode — and the container entrypoint prints a warning when it is used. The value is case-sensitive and must be exactly `allowTLS`, `requireTLS`, or `disabled`; the entrypoint exits with an error on anything else (for example `requiretls`), so the container fails at startup.

## AllowInsecureTls

Controls whether `tlsInsecure=true` is added to the connection string, which disables certificate validation. This is **enabled by default** so the .NET MongoDB driver can connect to the self-signed certificate used by the DocumentDB Local container.

```csharp
// Require valid certificates (for example, production with real certs)
var server = builder.AddDocumentDB("documentdb")
                    .AllowInsecureTls(false);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `allowInsecureTls` | `bool` | `true` | Whether to add `tlsInsecure=true` to the connection string. |

> [!NOTE]
> The extension uses `tlsInsecure=true` rather than `tlsAllowInvalidCertificates=true` because the .NET MongoDB driver does not fully honor `tlsAllowInvalidCertificates` for self-signed certificates and raises `UntrustedRoot` errors. `tlsInsecure=true` disables both certificate validation and hostname verification, which is the correct setting for local development containers.

## WithDocumentDBVersion

Pins the DocumentDB version to a specific release known to this build of the package. The selected version is combined with the currently selected `DocumentDBPostgresVersion` (default `Pg17`) to produce the container image tag `pgN-X.Y.Z`.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithDocumentDBVersion(DocumentDBVersion.V0_111_0);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `version` | `DocumentDBVersion` | (required) | One of the supported versions enumerated by `DocumentDBVersion`. |

## WithPostgresVersion

Selects the PostgreSQL backend variant of the `documentdb-local` container image.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithPostgresVersion(DocumentDBPostgresVersion.Pg16)
                    .WithDocumentDBVersion(DocumentDBVersion.V0_111_0);
// -> image tag "pg16-0.111.0"
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `pgVersion` | `DocumentDBPostgresVersion` | `Pg17` (when not called) | One of `Pg15`, `Pg16`, `Pg17`, `Pg18`. `Pg18` images are available upstream from DocumentDB `0.114.0` onwards; pairing `Pg18` with an older `DocumentDBVersion` throws before the container starts, and again before it is created or published, rather than failing the container pull with an opaque manifest error. |

## WithPostgresEndpoint

Exposes the PostgreSQL backend coordinator port of the DocumentDB Local container (default container port `9712`) as a second endpoint on the resource, and enables `DocumentDBServerResource.PostgresConnectionStringExpression`.

The `documentdb-local` container bundles a MongoDB-compatible gateway and a PostgreSQL coordinator on separate ports. By default, only the gateway port (`10260`) is published and only a `mongodb://` connection string is generated. Call `WithPostgresEndpoint()` when you also want to talk to the PostgreSQL backend directly (psql / Npgsql / etc.).

```csharp
var documentDB = builder.AddDocumentDB("documentdb")
                        .WithPostgresEndpoint();

builder.AddProject<Projects.Worker>("worker")
       .WithReference(documentDB) // injects the mongodb:// connection string
       .WithEnvironment("ConnectionStrings__pg", documentDB.Resource.PostgresConnectionStringExpression);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `port` | `int?` | `null` | Host port to bind to. When `null`, Aspire assigns a random host port. |

> [!IMPORTANT]
> **`WithPostgresEndpoint()` requires DocumentDB container image `>= 0.112.0`.**
>
> The `documentdb-local` entrypoint hard-codes the PostgreSQL admin role to
> `docdb_admin`/`Admin100` on image tags below `0.112.0`, so the
> `postgresql://admin:<password>@host:port/postgres` connection string Aspire
> generates would silently fail authentication. To prevent the silent failure,
> the integration validates the resource's effective `ContainerImageAnnotation`
> tag and throws an `InvalidOperationException` if it is older than
> `pg{NN}-0.112.0`. The exception message looks like:
>
> ```
> DocumentDB resource 'documentdb' is configured with image tag 'pg17-0.111.0',
> but WithPostgresEndpoint() requires DocumentDB v0.112.0 or later. Earlier
> images hard-code the PostgreSQL admin credentials to 'docdb_admin'/'Admin100',
> so the Aspire-generated postgresql:// connection string would silently fail to
> authenticate. Recovery: chain '.WithImageTag("pg{NN}-0.112.0")' (or newer)
> after AddDocumentDB(...). See https://github.com/microsoft/azure-databases-aspire/issues/71.
> ```
>
> **Recovery recipe:**
>
> ```csharp
> var db = builder.AddDocumentDB("documentdb")
>                 .WithImageTag("pg17-0.112.0")   // or any newer tag
>                 .WithPostgresEndpoint();
> ```
>
> Once `DocumentDBVersion.V0_112_0` is added to the curated enum
> (tracked by issue [#70](https://github.com/microsoft/azure-databases-aspire/issues/70)),
> the typed form `.WithDocumentDBVersion(DocumentDBVersion.V0_112_0)` becomes
> available as an equivalent recovery. Until then, prefer the free-form
> `.WithImageTag(...)` shown above.
>
> **Scope of the guard:**
> - **Judged on the image that will actually run.** The floor is reported by a
>   `BeforeResourceStartedEvent` subscriber, and applied again from the
>   package's uncached checkpoints — immediately before each container is
>   created, and while `azd publish` / `--publisher manifest` serializes the
>   resource — so a subscriber or lifecycle hook that swaps the image after the
>   event cannot slip a pre-`0.112.0` image past it. Publishing a manifest for
>   such a resource is refused with the same message; `ExcludeFromManifest()`
>   remains the way to keep a resource out of the manifest entirely.
> - **Curated image only.** A custom image (any reference that is not the
>   curated repository under a registry host, e.g. a fork via
>   `.WithImage("myorg/build", "pg17-0.110.0")` or a mirror path such as
>   `.WithImageRegistry("contoso.azurecr.io/mirrors")`) is exempt with a
>   one-time warning. Which registry **host** the image comes from does not
>   factor into the carve-out — a private mirror of the curated image is still
>   guarded — and it may be written into either annotation field, because the
>   composed reference is what is judged. See
>   [How the image is recognised](#how-the-image-is-recognised).
> - **Unknown tag patterns** (`:latest`, `:nightly`, `pg17-0.112.0-rc.1`,
>   custom non-`pg{NN}-X.Y.Z` strings) bypass the version check with a
>   one-time warning, so pinning a custom build or pre-release does not break
>   startup.
> - **Dockerfile builds.** A resource built with `WithDockerfile(...)` is exempt
>   on the same terms, with a one-time warning, even when its image annotation
>   names the curated image and a recognised tag. The build output is what
>   starts, and the floor is a property of the published release.
> - **Digest pins.** A reference pinned by digest is exempt on the same terms,
>   with a one-time warning, even when a recognised tag stands beside the
>   digest: the runtime resolves the digest, so the tag is not what runs.

### Generated PostgreSQL connection string

```
postgresql://<username>:<password>@<host>:<port>/postgres
```

- The same `userName` / `password` parameters as the MongoDB gateway are used, because the upstream container provisions a single admin user shared by both surfaces.
- The credentials are percent-encoded exactly as in the `mongodb://` string; see [Credential encoding](#credential-encoding).
- The default database is `postgres`, matching the upstream entrypoint's `-d postgres` convention.
- No `sslmode` query parameter is added, because the bundled PostgreSQL server is started with `ssl = off`; the `UseTls` / `AllowInsecureTls` flags only affect the MongoDB connection string. If you have configured TLS on the PostgreSQL side, append `?sslmode=...` yourself.

> [!NOTE]
> Accessing `PostgresConnectionStringExpression` before calling `WithPostgresEndpoint()` throws `InvalidOperationException`. Calling `WithPostgresEndpoint()` more than once on the same resource also throws.

Calling this method also sets `ALLOW_EXTERNAL_CONNECTIONS=true` on the container so the upstream entrypoint configures PostgreSQL to listen on all interfaces with a permissive `pg_hba.conf`. Publishing the host port alone is not enough to guarantee external reachability across upstream container revisions.

The supplied `userName`/`password` (default `admin` + auto-generated) are usable as PostgreSQL credentials because the upstream entrypoint creates a real PostgreSQL role via `documentdb_api.create_user(...)` with a SCRAM-SHA-256-hashed password. When `WithoutUserCreation()` is used, those credentials authenticate only if the role already exists, typically in a persisted data volume from an earlier run.

## Supported versions

The `DocumentDBVersion` enum is the **curated, append-only** list of versions known to this build of the package. New entries are added by the `check-documentdb-version` GitHub Actions workflow only when the version is published as a non-prerelease GitHub Release on [`documentdb/documentdb`](https://github.com/documentdb/documentdb/releases) AND the `pg15-X.Y.Z`, `pg16-X.Y.Z`, `pg17-X.Y.Z`, and `pg18-X.Y.Z` container tags all exist on GHCR. Existing entries are never renamed, removed, or renumbered.

You can enumerate the full list at runtime via `DocumentDBVersions.All`, and read the newest version known to the current package build via `DocumentDBVersions.Latest` (a property, not a `const`, so it is re-resolved after a package upgrade rather than inlined).

| Symbol | Notes |
|---|---|
| `enum DocumentDBVersion` | Curated members like `V0_109_0`, `V0_110_0`, `V0_111_0`. Stable forever once shipped. |
| `enum DocumentDBPostgresVersion` | `Pg15`, `Pg16`, `Pg17`, `Pg18`. Default `Pg17`. `Pg18` requires DocumentDB `0.114.0` or newer — enforced against the image that will actually run, at startup and again at container creation and manifest serialization, so an unpublished combination fails with an actionable message. |
| `DocumentDBVersions.All` | All known version strings, ascending semver. |
| `DocumentDBVersions.Latest` | The newest version known to *this build* of the package. |

### Using a version not (yet) in the enum

Aspire's framework `WithImageTag` is the free-form escape hatch. Use it to pin to a brand-new upstream release this package has not been updated to know about, or to a custom build:

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithImageTag("pg17-0.999.0");
```

### Precedence (last-call-wins)

`WithDocumentDBVersion`, `WithPostgresVersion`, `WithImage`, and `WithImageTag` all converge on the same single `ContainerImageAnnotation`. The most recent call wins, regardless of which API was used:

```csharp
// Final tag is "pg17-0.111.0" -- the typed call wins because it came last.
builder.AddDocumentDB("documentdb")
       .WithImageTag("pg15-0.999.0")
       .WithDocumentDBVersion(DocumentDBVersion.V0_111_0);

// Final tag is "pg17-0.999.0" -- the free-form call wins because it came last.
builder.AddDocumentDB("documentdb")
       .WithDocumentDBVersion(DocumentDBVersion.V0_111_0)
       .WithImageTag("pg17-0.999.0");
```

### How the image is recognised

`ContainerImageAnnotation` has a registry field and an image field, but Aspire joins them with a
single separator and validates neither — it never re-splits them, and it never checks that the
registry is a registry. Where you put the boundary is therefore not evidence about anything, so
recognition works on the **composed reference**: two spellings that compose the same string always
classify the same way.

Exactly one prefix may stand in front of the repository, and only one of two kinds:

- the curated registry `ghcr.io/documentdb`; or
- a single registry **host** — a DNS name, an IPv4 or bracketed IPv6 literal, or `localhost`, each
  with an optional port — and nothing after it.

What remains must be `documentdb/documentdb-local` exactly. So all of these are the official image:

```csharp
builder.AddDocumentDB("documentdb");                                   // ghcr.io/documentdb + documentdb/documentdb-local

builder.AddDocumentDB("documentdb")
       .WithImage("ghcr.io/documentdb/documentdb/documentdb-local", "pg17-0.116.0")
       .WithImageRegistry(null);                                       // the whole reference in one field

builder.AddDocumentDB("documentdb")
       .WithImage("documentdb/documentdb/documentdb-local", "pg17-0.116.0")
       .WithImageRegistry("ghcr.io");                                  // the boundary moved one segment
```

A **private mirror** is a registry host and that exact repository beneath it — nothing in between.
`contoso.azurecr.io/documentdb/documentdb-local` and `localhost:5000/documentdb/documentdb-local`
are the curated image behind a different registry, whether you write the host in
`WithImageRegistry(...)` or inline.

A namespace, project or mirror path in front of the repository names a **different** repository and
keeps the custom-image treatment — in every spelling, because the composed reference is the same:

| Not the official image | Composed reference |
|---|---|
| `.WithImageRegistry("ghcr.io/evil")` + `documentdb/documentdb-local` | `ghcr.io/evil/documentdb/documentdb-local` |
| `.WithImage("ghcr.io/evil/documentdb/documentdb-local")` | `ghcr.io/evil/documentdb/documentdb-local` |
| `.WithImageRegistry("contoso.azurecr.io/mirrors")` + `documentdb/documentdb-local` | `contoso.azurecr.io/mirrors/documentdb/documentdb-local` |
| `.WithImageRegistry("harbor.corp.local/library")` + `documentdb/documentdb-local` | `harbor.corp.local/library/documentdb/documentdb-local` |
| `.WithImage("evil/documentdb/documentdb-local")` | `evil/documentdb/documentdb-local` |

A bare name is a host only when it carries a port, following the container reference grammar:
`myregistry:5000/documentdb/documentdb-local` is a mirror, `myregistry/documentdb/documentdb-local`
is not. Writing the full reference into the image *without* clearing the registry is not the
official image either: Aspire composes
`ghcr.io/documentdb/ghcr.io/documentdb/documentdb/documentdb-local`, which resolves to nothing, as
does any reference with a doubled, leading or trailing separator.

A digest is read whether it arrives through `WithImageSHA256(...)` or inline as
`repository@sha256:...`, and a `:` is a tag only in the last path segment, so a registry port is
never mistaken for one.

**A digest beats every tag.** A reference can carry both — `repository:pg17-0.116.0@sha256:...`, or
an inline tag beside a `WithImageSHA256(...)` digest, or the reverse — and the runtime resolves the
digest and ignores the tag. The version is therefore unknown for such a reference no matter what the
tag reads, so no version-dependent behaviour is applied to it: no declared-`/data`-volume warning,
no interlock and so no `WithExplicitStart()` downgrade of a shared data directory, and neither the
`WithPostgresEndpoint()` credential floor nor the `Pg18` publish floor is enforced or refused on the
strength of the tag. The repository is still recognised, which is what lets
`WithOpenTelemetryMetrics(...)` reject the pin with an actionable message rather than silently skip
it; a digest on a repository this package does not publish is left alone as any custom image is.

### Building your own image from a Dockerfile

`WithDockerfile(...)` — and `WithDockerfileFactory(...)` / `WithDockerfileBuilder(...)` — tells
Aspire to *build* the resource's container image instead of pulling one. Aspire keeps the
resource's `ContainerImageAnnotation` when you do that, and you can set it yourself afterwards, so
a Dockerfile-built resource may be labelled
`ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.116.0` while running something else
entirely. The published manifest makes this explicit: it emits a `build` object and no `image` at
all.

This package therefore classifies **any** resource with a Dockerfile build as a custom image of
unknown version — whatever its repository, tag or digest says, and whatever the Dockerfile's own
`FROM` line is:

- version floors are not enforced: `WithPostgresEndpoint()` warns once instead of failing, and the
  `Pg18` publish floor stays silent;
- `0.116.0`-only storage behaviour is not assumed. There is no declared-`/data`-volume warning, and
  a data directory shared with another DocumentDB resource stays a hard failure instead of being
  downgraded to a warning by `WithExplicitStart()`, because nothing establishes that your image
  still claims the directory with an exclusive `flock`;
- `WithOpenTelemetryMetrics(...)` does not install the gateway configuration wrapper. On a
  `0.116.0`-or-later base your image's own `SetupConfiguration.json` therefore still wins over the
  `OTEL_*` variables — configure telemetry inside your Dockerfile, or use the official image
  instead of building one.

Everything that does not depend on the image version still applies: the connection string, the
environment variables, the health check, and the image-independent storage rules (a writable data
directory, one mount per data path, no `--data-path` argument).

Overriding only the base image of a *generated* Dockerfile (`WithDockerfileBaseImage(...)`) is not
a build. With no Dockerfile to generate, it changes neither the image the resource pulls nor the
manifest it publishes, so it does not change the classification.

## Connection string format

The extension generates a MongoDB connection string with the following format:

```
mongodb://<username>:<password>@<host>:<port>[/<database>]?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true
```

### Breakdown

| Component | Value | Source |
|---|---|---|
| Protocol | `mongodb://` | Always MongoDB wire protocol |
| Username | `admin` (default) or custom | `userName` parameter or default |
| Password | Auto-generated or custom | `password` parameter or generated |
| Host:Port | Allocated by Aspire | Endpoint binding |
| Database | Resource name or `databaseName` | `AddDatabase()` parameter |
| `authSource` | `admin` | Fixed authentication database |
| `authMechanism` | `SCRAM-SHA-256` | DocumentDB authentication |
| `tls` | `true` | Controlled by `UseTls()` |
| `tlsInsecure` | `true` | Controlled by `AllowInsecureTls()` |

### Credential encoding

The user name and password are percent-encoded before they are placed in the URI, so characters such
as `:`, `@`, `/`, `?`, `#`, `%`, `&`, spaces and non-ASCII text cannot be read as URI delimiters or
as extra connection options. MongoDB and PostgreSQL clients decode the userinfo component, so they
receive the original value back.

- **Whenever Aspire resolves the reference, encoding is RFC 3986** (`Uri.EscapeDataString`), so
  arbitrary credential values round-trip exactly. This covers `aspire run`, values injected through
  `WithReference(...)`, and the health checks this integration registers.
- Credentials made only of unreserved characters (`A-Z`, `a-z`, `0-9`, `-`, `.`, `_`, `~`) are
  emitted verbatim. That includes the default `admin` user name and the auto-generated password, so
  simple setups see no change.
- Encoding applies only to the connection strings. The container's `USERNAME` and `PASSWORD`
  environment variables keep the raw values, because that is what the entrypoint expects.
- Encoding is resolved late, so no secret is read while the application model is built.

#### Publishing: the encoding is the publisher's

A published manifest does not inline the encoded value. The connection string instead references an
`annotated.string` companion resource (`<parameter>-uri-encoded`, `"filter": "uri"`) whose value
points back at the original parameter, and the *downstream publisher* implements the `uri` filter.
The escaping used at deployment time is therefore that publisher's, and this integration cannot
guarantee every publisher matches the RFC 3986 escaping described above.

> [!IMPORTANT]
> The Azure Developer CLI (`azd`) currently implements the `uri` filter for Container Apps with Go's
> `url.QueryEscape`, which is *query* escaping: a literal space becomes `+`, not `%20`. MongoDB and
> libpq decode userinfo per RFC 3986, where `+` is a literal plus sign and is **not** converted back
> into a space. A credential containing a space is therefore not guaranteed to survive an `azd`
> deployment, even though it resolves correctly under `aspire run`. Delimiters (`:`, `@`, `/`, `?`,
> `#`, `%`, `&`, `=`) and non-ASCII text are escaped compatibly by both, so the literal space is the
> known divergence. If you publish with `azd`, avoid spaces in `userName` / `password` — the
> auto-generated password is already free of them.

## Defaults summary

| Setting | Default Value |
|---|---|
| Container image | `ghcr.io/documentdb/documentdb/documentdb-local` |
| Image tag | `pg17-{DocumentDBVersions.Latest}` (currently `pg17-0.116.0`) |
| DocumentDB version | `DocumentDBVersions.Latest` (the newest version known to this build) |
| PostgreSQL backend | `DocumentDBPostgresVersion.Pg17` |
| Container port | `10260` |
| Host port | Random (unless set with `WithHostPort` or `port` parameter) |
| Username | `admin` |
| Password | Auto-generated (no special characters) |
| TLS | Enabled |
| Insecure TLS | Enabled (allows self-signed certificates) |
| Container default data path | `/data` (declared as an image `VOLUME` from `0.116.0`) |
| Persistence helper default path | `/data` |
| Data directory access | Writable, empty or an existing cluster; from `0.116.0` also initialized once and exclusive to one running container |
| Auth mechanism | `SCRAM-SHA-256` |
| Auth database | `admin` |

## Container environment variables

The extension passes these environment variables to the DocumentDB container:

| Variable | Value | Purpose |
|---|---|---|
| `USERNAME` | The configured username | Container creates this user on startup |
| `PASSWORD` | The configured password | Password for the created user |
| `DATA_PATH` | Path inside the container for the mounted data directory | Set by `WithDataVolume` and `WithDataBindMount`; can also be set directly with `WithEnvironment("DATA_PATH", ...)`, in which case the last caller wins and the storage guards follow it. The integration rewrites the value to its canonical form before the container receives it. When nothing sets it — or it is set empty — the container uses its default `/data`, which is an anonymous volume from `0.116.0` and a container-layer directory before that |
| `INIT_DATA` | `false` | Set by `WithInitData(...)` and `WithoutSampleData()` |
| `DOCUMENTDB_LOG_LEVEL` | `quiet`, `error`, `warn`, `info`, `debug`, or `trace` | Gateway tracing filter set by `WithLogLevel(...)` and consumed in DocumentDB `0.114.0`+ |
| `LOG_LEVEL` | `quiet`, `error`, `warn`, `info`, `debug`, or `trace` | Retained for the Local entrypoint's value-validation contract; not consumed by a Local gateway for verbosity |
| `INIT_DATA_PATH` | `/init_doc_db.d` | Set by `WithInitData(...)` |
| `SKIP_INIT_DATA` | `true` | Set by `WithInitData(...)` and `WithoutSampleData()` |
| `CERT_PATH` | Container path of the mounted certificate file | Set by `WithTlsCertificate(...)` |
| `KEY_FILE` | Container path of the mounted key file | Set by `WithTlsCertificate(...)` |
| `ENABLE_TELEMETRY` | `true` or `false` | Set by `WithTelemetry(...)` — **deprecated**, no longer consumed by the gateway in v0.112-0+ |
| `OTEL_METRICS_ENABLED` | `true` or `false` | Set by `WithOpenTelemetryMetrics(...)` |
| `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` | OTLP/gRPC collector endpoint | Set by `WithOpenTelemetryMetrics(endpoint: ...)` |
| `OTEL_METRIC_EXPORT_INTERVAL` | Milliseconds (integer) | Set by `WithOpenTelemetryMetrics(exportInterval: ...)` |
| `OTEL_EXPORTER_OTLP_METRICS_TIMEOUT` | Milliseconds (integer) | Set by `WithOpenTelemetryMetrics(timeout: ...)` |
| `OTEL_SERVICE_NAME` | Service name string | Set by `WithOpenTelemetryMetrics(serviceName: ...)` |
| `OTEL_SERVICE_VERSION` | Service version string | Set by `WithOpenTelemetryMetrics(serviceVersion: ...)` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP/gRPC endpoint | Read by gateway as fallback when `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` is unset; not set by the typed API |
| `OTEL_EXPORTER_OTLP_TIMEOUT` | Milliseconds (integer) | Read by gateway as fallback when `OTEL_EXPORTER_OTLP_METRICS_TIMEOUT` is unset; not set by the typed API |
| `OWNER` | The configured owner string | Set by `WithOwner(...)` |
| `DISABLE_EXTENDED_RUM` | `true` | Set by `WithoutExtendedRum()` |
| `CREATE_USER` | `false` | Set by `WithoutUserCreation()` |

## Resource model

The extension defines two resource types:

```
IDistributedApplicationBuilder
  |
  +-- AddDocumentDB("server-name")
        |
        +-- DocumentDBServerResource  (container resource with connection string)
              |
              +-- AddDatabase("db-name")
                    |
                    +-- DocumentDBDatabaseResource  (child resource with connection string)
```

- **DocumentDBServerResource** — Represents the DocumentDB container. Implements `IResourceWithConnectionString`. The server-level connection string does not include a database name.
- **DocumentDBDatabaseResource** — A child resource that represents a specific database on the server. Its connection string includes the database name in the path. This is what services typically reference with `WithReference()`.

## Chaining methods

All configuration methods return the builder, so they can be chained:

```csharp
var db = builder.AddDocumentDB("documentdb")
                .WithHostPort(10260)
                .WithDataVolume()
                .WithDocumentDBVersion(DocumentDBVersion.V0_111_0)
                .WithPostgresVersion(DocumentDBPostgresVersion.Pg17)
                .WithLogLevel(DocumentDBLogLevel.Debug)
                .WithoutSampleData()
                .WithoutExtendedRum()
                .WithoutUserCreation()
                .UseTls(true)
                .AllowInsecureTls(true)
                .AddDatabase("mydb");
```
