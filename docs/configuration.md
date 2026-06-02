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
| `isReadOnly` | `bool` | `false` | Mount the volume as read-only. |
| `targetPath` | `string?` | `/data` | Path inside the container where the volume is mounted when this helper is used. |

This method mounts the volume at `targetPath` (which defaults to `/data`, matching the container default) and sets the `DATA_PATH` environment variable to match so DocumentDB writes to the mounted directory.

## WithDataBindMount

Mounts a host directory into the container for data persistence. Prefer `WithDataVolume` for most cases; bind mounts are useful when you need direct access to the data files on the host.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithDataBindMount("./data/documentdb");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `source` | `string` | (required) | Path on the host machine to mount. |
| `isReadOnly` | `bool` | `false` | Mount as read-only. |

By default, this helper mounts data at `/data` inside the container (matching the container default) and sets `DATA_PATH` accordingly.

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
container image v0.112-0 or later. Only metrics are exported today; traces and logs are not yet
supported by the gateway.

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
| `serviceName` | `string?` | `null` | Logical service name attached to the metrics. When provided, sets `OTEL_SERVICE_NAME`. Must be non-empty when provided. |
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

`exportInterval` and `timeout` are written as integer milliseconds via the invariant culture.
Values smaller than one millisecond (sub-ms ticks) truncate to `0`; pass whole-millisecond or
larger granularities.

The gateway also reads `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_TIMEOUT` when the
signal-specific variants above are unset, plus `OTEL_RESOURCE_ATTRIBUTES`. These are not exposed
by the typed API — set them via `WithEnvironment(...)` if you need them. Note that
`OTEL_RESOURCE_ATTRIBUTES` is parsed by the gateway but not yet wired into startup as of v0.112-0.


## WithOwner

Sets the container `OWNER` environment variable, which DocumentDB Local uses to label resources.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithOwner("documentdb");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `owner` | `string` | (required) | The owner value. |

## UseTls

Controls whether TLS is included in the generated connection string. TLS is **enabled by default** because the DocumentDB Local container requires TLS connections.

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
| `pgVersion` | `DocumentDBPostgresVersion` | `Pg17` (when not called) | One of `Pg15`, `Pg16`, `Pg17`. |

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

The `DocumentDBVersion` enum is the **curated, append-only** list of versions known to this build of the package. New entries are added by the `check-documentdb-version` GitHub Actions workflow only when the version is published as a non-prerelease GitHub Release on [`documentdb/documentdb`](https://github.com/documentdb/documentdb/releases) AND the `pg15-X.Y.Z`, `pg16-X.Y.Z`, and `pg17-X.Y.Z` container tags all exist on GHCR. Existing entries are never renamed, removed, or renumbered.

You can enumerate the full list at runtime via `DocumentDBVersions.All`, and read the newest version known to the current package build via `DocumentDBVersions.Latest` (a property, not a `const`, so it is re-resolved after a package upgrade rather than inlined).

| Symbol | Notes |
|---|---|
| `enum DocumentDBVersion` | Curated members like `V0_109_0`, `V0_110_0`, `V0_111_0`. Stable forever once shipped. |
| `enum DocumentDBPostgresVersion` | `Pg15`, `Pg16`, `Pg17`. Default `Pg17`. |
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
| Image tag | `pg17-{DocumentDBVersions.Latest}` (currently `pg17-0.111.0`) |
| DocumentDB version | `DocumentDBVersions.Latest` (the newest version known to this build) |
| PostgreSQL backend | `DocumentDBPostgresVersion.Pg17` |
| Container port | `10260` |
| Host port | Random (unless set with `WithHostPort` or `port` parameter) |
| Username | `admin` |
| Password | Auto-generated (no special characters) |
| TLS | Enabled |
| Insecure TLS | Enabled (allows self-signed certificates) |
| Container default data path | `/data` |
| Persistence helper default path | `/data` |
| Auth mechanism | `SCRAM-SHA-256` |
| Auth database | `admin` |

## Container environment variables

The extension passes these environment variables to the DocumentDB container:

| Variable | Value | Purpose |
|---|---|---|
| `USERNAME` | The configured username | Container creates this user on startup |
| `PASSWORD` | The configured password | Password for the created user |
| `DATA_PATH` | Path inside the container for the mounted data directory | Only set when using `WithDataVolume` or `WithDataBindMount`; otherwise the container uses its default `/data` |
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
