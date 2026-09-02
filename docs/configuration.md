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
from your own Dockerfile, whatever its image annotation says — the combination stays a hard failure
even with `WithExplicitStart()`, because there is no interlock to fall back on.

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

Sets the container `LOG_LEVEL` environment variable to control DocumentDB Local log verbosity.

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

This helper also sets `SKIP_INIT_DATA=true` so the container does not also import its built-in sample collections.

> [!NOTE]
> From `0.116.0` these scripts run **once per data directory** and are not retried after a failed attempt. On `0.114.0` and earlier they run on **every container start**, so with persisted storage they must be idempotent. See [Initialization is one-shot per data directory](#initialization-is-one-shot-per-data-directory-01160-and-later).

## WithoutSampleData

Disables the built-in sample data initialization without mounting custom scripts. Use this when you want a clean DocumentDB instance.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithoutSampleData();
```

This sets `SKIP_INIT_DATA=true` on the container.

## WithoutExtendedRum

Disables the `extended_rum` index access method in the DocumentDB Local container. Extended RUM is enabled by default starting with DocumentDB v0.111-0.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithoutExtendedRum();
```

This sets `DISABLE_EXTENDED_RUM=true` on the container. On container images older than v0.111-0 the environment variable is ignored.

## WithoutUserCreation

Disables the automatic user creation performed by the DocumentDB Local container on startup. Use only after a previous run has already created the user in persisted storage (`WithDataVolume` or `WithDataBindMount`). To avoid spurious init-data runs on subsequent starts, also call `WithoutSampleData()`.

> [!WARNING]
> Setting `CREATE_USER=false` on a fresh container (without a persisted user) will cause the container entrypoint to exit non-zero. The container's init-data scripts (both built-in sample data and custom scripts mounted via `WithInitData`) authenticate using the configured credentials, and will fail if the user does not exist. Always pair this method with `WithoutSampleData()` and ensure the user already exists in the persisted data.

```csharp
// Typical pattern: persist data and skip user creation + sample data on subsequent runs
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
| `serviceName` | `string?` | `null` | Logical service name attached to the metrics. When provided, sets `OTEL_SERVICE_NAME`. Must be non-empty when provided. For the official `0.116.0` image, omitting it preserves the image default of `documentdb_gateway`. |
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

DocumentDB `0.116.0` ships telemetry values in its stock `SetupConfiguration.json`, and those
JSON values take precedence over the standard environment variables. To preserve the documented
environment-variable behavior, this method injects a compatibility `SetupConfiguration.json`
into the stock Local image configuration directory. It retains the stable ports, certificate
defaults, and reserved username prefixes but omits `TelemetryOptions`, so the gateway resolves
metrics settings from the environment on both older images and `0.116.0`. If the caller sets a
custom `CONFIG_DIR`, that custom configuration remains authoritative.

The compatibility file is injected when the AppHost runs the official `0.116.0` Local image.
Aspire publish mode is rejected for that exact image when metrics are enabled, because not every
publisher carries the required runtime file override. Failing explicitly avoids publishing a
deployment where metrics are silently disabled. Explicitly disabling metrics does not require the
override and remains publishable. Direct AppHost run mode is supported. A custom image with
corrected upstream telemetry configuration is not subject to this guard, and neither is a resource
built from your own Dockerfile — even one whose image annotation names the official image and that
exact tag, because what starts is the build output. A private registry mirror that keeps the
official `documentdb/documentdb-local:pgNN-0.116.0` image path and tag receives the same
compatibility override and publish guard.

`exportInterval` and `timeout` are written as integer milliseconds via the invariant culture.
Values smaller than one millisecond (sub-ms ticks) truncate to `0`; pass whole-millisecond or
larger granularities.

The gateway also reads `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_TIMEOUT` when the
signal-specific variants above are unset. Starting in `0.116.0`, it also applies
`OTEL_RESOURCE_ATTRIBUTES`; the current default `0.114.0` image parses that variable but does not
apply it during startup. These are not exposed by the typed API — set them via
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
| `pgVersion` | `DocumentDBPostgresVersion` | `Pg17` (when not called) | One of `Pg15`, `Pg16`, `Pg17`, `Pg18`. `Pg18` images are available upstream from DocumentDB `0.114.0` onwards; pairing `Pg18` with an older `DocumentDBVersion` throws at startup rather than failing the container pull with an opaque manifest error. |

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
> tag at startup and throws an `InvalidOperationException` if it is older than
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
> - **Run mode only.** Manifest generation (`azd publish`, `--publisher
>   manifest`) does not start the container, so the guard does not fire and
>   the manifest is produced regardless of tag.
> - **Curated image only.** A custom image (any
>   `ContainerImageAnnotation.Image` other than
>   `documentdb/documentdb-local`, e.g. a fork via `.WithImage("myorg/build", "pg17-0.110.0")`)
>   is exempt with a one-time warning. The container registry does not factor
>   into the carve-out — a private mirror of the curated image is still
>   guarded.
> - **Unknown tag patterns** (`:latest`, `:nightly`, `pg17-0.112.0-rc.1`,
>   custom non-`pg{NN}-X.Y.Z` strings) bypass the version check with a
>   one-time warning, so pinning a custom build or pre-release does not break
>   startup.
> - **Dockerfile builds.** A resource built with `WithDockerfile(...)` is exempt
>   on the same terms, with a one-time warning, even when its image annotation
>   names the curated image and a recognised tag. The build output is what
>   starts, and the floor is a property of the published release.

### Generated PostgreSQL connection string

```
postgresql://<username>:<password>@<host>:<port>/postgres
```

- The same `userName` / `password` parameters as the MongoDB gateway are used, because the upstream container provisions a single admin user shared by both surfaces.
- The default database is `postgres`, matching the upstream entrypoint's `-d postgres` convention.
- No `sslmode` query parameter is added, because the bundled PostgreSQL server is started with `ssl = off`; the `UseTls` / `AllowInsecureTls` flags only affect the MongoDB connection string. If you have configured TLS on the PostgreSQL side, append `?sslmode=...` yourself.

> [!NOTE]
> Accessing `PostgresConnectionStringExpression` before calling `WithPostgresEndpoint()` throws `InvalidOperationException`. Calling `WithPostgresEndpoint()` more than once on the same resource also throws.

Calling this method also sets `ALLOW_EXTERNAL_CONNECTIONS=true` on the container so the upstream entrypoint configures PostgreSQL to listen on all interfaces with a permissive `pg_hba.conf`. Publishing the host port alone is not enough to guarantee external reachability across upstream container revisions.

The supplied `userName`/`password` (default `admin` + auto-generated) are usable as PostgreSQL credentials because the upstream entrypoint creates a real PostgreSQL role via `documentdb_api.create_user(...)` with a SCRAM-SHA-256-hashed password. The same caveat as the gateway side applies: combining this with `WithoutUserCreation()` only works against a persisted data volume that already contains the role.

## Supported versions

The `DocumentDBVersion` enum is the **curated, append-only** list of versions known to this build of the package. New entries are added by the `check-documentdb-version` GitHub Actions workflow only when the version is published as a non-prerelease GitHub Release on [`documentdb/documentdb`](https://github.com/documentdb/documentdb/releases) AND the `pg15-X.Y.Z`, `pg16-X.Y.Z`, `pg17-X.Y.Z`, and `pg18-X.Y.Z` container tags all exist on GHCR. Existing entries are never renamed, removed, or renumbered.

You can enumerate the full list at runtime via `DocumentDBVersions.All`, and read the newest version known to the current package build via `DocumentDBVersions.Latest` (a property, not a `const`, so it is re-resolved after a package upgrade rather than inlined).

| Symbol | Notes |
|---|---|
| `enum DocumentDBVersion` | Curated members like `V0_109_0`, `V0_110_0`, `V0_111_0`. Stable forever once shipped. |
| `enum DocumentDBPostgresVersion` | `Pg15`, `Pg16`, `Pg17`, `Pg18`. Default `Pg17`. `Pg18` requires DocumentDB `0.114.0` or newer — enforced at startup, so an unpublished combination fails with an actionable message. |
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
- `WithOpenTelemetryMetrics(...)` does not inject the `0.116.0` compatibility
  `SetupConfiguration.json`, and does not reject publishing. On a `0.116.0`-or-later base your
  image's own configuration therefore still wins over the `OTEL_*` variables — configure telemetry
  inside your Dockerfile, or use the official image instead of building one.

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

## Defaults summary

| Setting | Default Value |
|---|---|
| Container image | `ghcr.io/documentdb/documentdb/documentdb-local` |
| Image tag | `pg17-{DocumentDBVersions.Latest}` (currently `pg17-0.114.0`) |
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
| `LOG_LEVEL` | `quiet`, `error`, `warn`, `info`, `debug`, or `trace` | Set by `WithLogLevel(...)` |
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
