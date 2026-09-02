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

Starting with DocumentDB `0.116.0`, the upstream image declares `/data` as a Docker
`VOLUME` even when this helper is not called. That produces a Docker-managed anonymous volume;
do not depend on that anonymous volume for durable or predictable storage. Use
`WithDataVolume()` with an explicit or generated named volume when persistence is intentional.
DocumentDB `0.116.0` also prevents two running containers from sharing the same data directory.

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
| `isReadOnly` | `bool` | `false` | Mount as read-only. |

By default, this helper mounts data at `/data` inside the container (matching the container default) and sets `DATA_PATH` accordingly.

> The credential caveat under [WithDataVolume](#withdatavolume) applies here too: supply explicit `userName`/`password` parameters, or the generated password will stop matching the role stored in the mounted directory on the next run.

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
for a build neither is what runs. A private registry mirror that keeps the official image path and
tag is wrapped, because only the registry differs — however the reference spells the boundary
between registry and repository, see
[How the image is recognised](#how-the-image-is-recognised). Four situations throw instead of
guessing:

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

The wrapper needs `bash`, `jq`, `realpath`, and `mktemp`, all of which the official image provides.
If one is missing, the configuration file cannot be read, or no safe temporary directory is
available, the container fails to start with a diagnostic rather than starting silently without
the override.

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

### How the image is recognised

`ContainerImageAnnotation` models a reference as a registry prefix plus a repository, and Aspire
joins the two with a single separator — it never re-splits them. Where you put that boundary is
therefore up to you, and all of these resolve to the same official image:

```csharp
builder.AddDocumentDB("documentdb");                                   // ghcr.io/documentdb + documentdb/documentdb-local

builder.AddDocumentDB("documentdb")
       .WithImage("ghcr.io/documentdb/documentdb/documentdb-local", "pg17-0.116.0")
       .WithImageRegistry(null);                                       // the whole reference in one field

builder.AddDocumentDB("documentdb")
       .WithImage("documentdb/documentdb/documentdb-local", "pg17-0.116.0")
       .WithImageRegistry("ghcr.io");                                  // the boundary moved one segment
```

Recognition is done on the composed reference, so every spelling of one image is classified the
same way. The repository identity stays exact — `documentdb/documentdb-local`, segment for
segment — and only the prefix in front of it may vary, which is what keeps a **private mirror**
covered: `contoso.azurecr.io/documentdb/documentdb-local` and `localhost:5000/documentdb/documentdb-local`
are the curated image behind a different registry, whether you write the registry in
`WithImageRegistry(...)` or inline.

A leading path that is not a registry is part of the repository, so it names a different image and
keeps the custom-image treatment. `evil/documentdb/documentdb-local` has no registry in front of it
(a first segment is a registry host only when it contains a `.` or a `:`, or is `localhost`), and
`ghcr.io/evil/documentdb/documentdb-local` has a path segment that is not part of the curated
registry. Writing the full reference into the image *without* clearing the registry is also not the
official image: Aspire composes `ghcr.io/documentdb/ghcr.io/documentdb/documentdb/documentdb-local`,
which resolves to nothing.

A digest is read whether it arrives through `WithImageSHA256(...)` or inline as
`repository@sha256:...`, and a `:` is a tag only in the last path segment, so a registry port is
never mistaken for one.

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

- `WithOpenTelemetryMetrics(...)` does not install the gateway configuration wrapper. On a
  `0.116.0`-or-later base your image's own `SetupConfiguration.json` therefore still wins over the
  `OTEL_*` variables — configure telemetry inside your Dockerfile, or use the official image
  instead of building one.

Everything that does not depend on the image version still applies: the connection string, the
`OTEL_*` and other environment variables, the mounts, and the health check.

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
