# Changelog

All notable changes to the `Aspire.Hosting.DocumentDB` package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project uses [MinVer](https://github.com/adamralph/minver) for versioning based on Git tags.

## [Unreleased]

<!-- auto-generated:documentdb-versions-start -->
_No upstream DocumentDB versions detected since the last release. This block is rewritten in place by `eng/scripts/check-documentdb-versions.py`; reset it to this line when cutting a release, after moving its contents into the dated section below._
<!-- auto-generated:documentdb-versions-end -->

## [0.116.0] - 2026-09-01

### Added
- `DocumentDBVersion.V0_116_0` curated enum member and `DocumentDBVersions.V0_116_0 = "0.116.0"` constant. Upstream skipped the `v0.115-0` release and rolled its prepared changes into `v0.116-0`, so there is intentionally no `V0_115_0` member.

### Changed
- `DocumentDBVersions.Latest` and the default `documentdb-local` image now resolve to `0.116.0` and `ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.116.0`. Upstream user-visible changes include `$jsonSchema` support for `enum` and `oneOf`, wire-compatible `$sample` size validation, indexed streaming and planning improvements for grouped and composite queries, and reuse of warm gateway PostgreSQL connection pools.
- Published manifests now route the connection-string credentials through `annotated.string` companion resources (`<parameter>-uri-encoded`, with `"filter": "uri"`) instead of referencing the credential parameters directly, because a manifest cannot inline the encoded value. The downstream publisher implements that filter, so the escaping applied at deployment time is the publisher's rather than Aspire's. Notably, `azd`'s current Container Apps generation implements `uri` with Go's `url.QueryEscape`, which encodes a literal space as `+`; MongoDB and libpq decode userinfo per RFC 3986 and do not convert `+` back into a space, so credentials containing spaces are not guaranteed to round-trip through an `azd` deployment even though they resolve correctly under `aspire run`. Delimiters and non-ASCII text are escaped compatibly by both. See [configuration.md](docs/configuration.md#credential-encoding).
- Adopted the DocumentDB `0.116.0` Local image runtime contract across PG15-PG18, including its `/data` image volume, single-container data-directory lock, one-shot initialization state, reserved username prefixes, and `lz4` TOAST default. Persistence, initialization-readiness, external PostgreSQL, and OTLP metrics behavior are documented and covered by Docker-backed tests, including a PG17 `0.114.0` to `0.116.0` data upgrade.
- Documented the DocumentDB storage contract in a new "Storage requirements" section of the configuration reference, with matching troubleshooting entries: the data directory must be writable, must be empty or hold an existing cluster, is owned by the container's `documentdb` user, and has version-specific volume, locking, and initialization behavior. From `0.116.0`, `/data` is an image volume, the directory is protected by an exclusive lock, and marker-backed initialization is one-shot. At or below `0.114.0`, requested initialization runs on every container start, so seed scripts used with persisted storage must be idempotent.
- Container-backed bind-mount persistence coverage now requires the restarted container to serve the persisted data directory on every runtime except Docker Desktop, which is identified from `docker info`'s `OperatingSystem` field rather than from the host OS (falling back to "not Docker Desktop on a Linux host" if the daemon cannot be asked). A refusal anywhere else fails with the container's own log and the runtime identification. The refusal is recognised only when the *current* run failed to start PostgreSQL and logged the ownership error at or after the container's start time, so the previous run's failure — replayed into the next container out of the persisted `pglog.log` the entrypoint tails — cannot be mistaken for a refusal by a run that actually recovered.

### Fixed
- The `WithOpenTelemetryMetrics(...)` gateway wrapper now keeps its sanitized `SetupConfiguration.json` outside the canonical `DATA_PATH`. It previously used the default `mktemp -d` location, so a valid `DATA_PATH=/tmp` caused the wrapper to create a directory inside the fresh PostgreSQL data target before initialization; DocumentDB `0.116.0` then correctly refused the non-empty non-cluster directory. The wrapper now selects a writable, disjoint subtree from `/tmp`, `/var/tmp`, and `/dev/shm`, and fails clearly if none is safe. Both enabled and explicitly disabled metrics are covered because both modes install the wrapper.
- A DocumentDB resource whose container image is built from a Dockerfile (`WithDockerfile(...)`, `WithDockerfileFactory(...)`, `WithDockerfileBuilder(...)`) is now classified as a custom image of unknown version for every version-dependent behaviour, whatever its image annotation says. Aspire keeps the `ContainerImageAnnotation` that `AddDocumentDB` installs when a Dockerfile build is added, and a caller can re-point it at `documentdb/documentdb-local` and a recognised `pg{NN}-X.Y.Z` tag afterwards, so such a resource could previously be mistaken for an official `0.116.0` image and granted behaviour that had not been established for it: the shared-data-directory hard failure was downgraded to a warning by `WithExplicitStart()` on the strength of a `flock` the build may not perform, the declared-`/data`-volume warning was raised for a `VOLUME` the build may not declare, the `WithPostgresEndpoint()` credential floor and the `Pg18` publish floor were enforced against a tag the runtime never resolves, and `WithOpenTelemetryMetrics(...)` replaced the container entrypoint with a wrapper built entirely out of official-image facts — the `emulator_entrypoint.sh` path, the packaged `/etc/documentdb/gateway` layout, `bash` and `jq` — or threw on a digest pin that is no more what runs than the tag is. The published manifest emits a `build` object and no `image` at all, which is what makes the annotation unusable as evidence. Image classification is now resolved in one place for all of these; true official images, private mirrors, digest pins, recognised tags, unrecognised tags and custom repositories are unaffected, and overriding only the base image of a generated Dockerfile (`WithDockerfileBaseImage(...)`) is correctly not treated as a build.
- Read-only DocumentDB data storage now fails immediately instead of producing a misleading startup failure. `WithDataVolume(isReadOnly: true)` and `WithDataBindMount(..., isReadOnly: true)` throw an `ArgumentException`, and a read-only volume or bind mount placed on the data path with the raw Aspire `WithVolume`/`WithBindMount` APIs fails the resource start with an `InvalidOperationException`. The rejection names the target path actually requested. `WithInitData(...)` and `WithTlsCertificate(...)` still mount their inputs read-only, which is correct and unaffected.
- Sharing one data directory between two DocumentDB resources in the same application model now fails at start with an explanatory `InvalidOperationException` naming both resources. From `0.116.0` the container defends itself with an exclusive `flock`; at or below `0.114.0` there is no interlock. The combination is downgraded to a warning only when both resources resolve to a recognized `0.116.0`-or-later tag and one is started manually with `WithExplicitStart()`.
- Two mounts on the same DocumentDB data path now fail at start with an actionable message rather than letting the container runtime reject the duplicate mount target.
- Corrected the documented handling of a data directory that is not empty and not a PostgreSQL cluster. The container refuses it, never starts PostgreSQL, and leaves the contents intact; a stray `.gitkeep` or `.DS_Store` is enough to trigger the failure.
- `WithDataVolume(targetPath: ...)` now validates and canonicalizes the target path. Empty, whitespace, and relative paths are rejected with an `ArgumentException`, and repeated separators, `.` and `..` segments are resolved as the container runtime resolves them, so `/data/`, `//data`, and `/foo/../data` all land on the image's declared `/data` volume. A target that resolves to the container root (`/data/..`) is rejected because the runtime refuses `destination can't be '/'`; one that reaches above the root (`/../data`) is rejected for the opposite reason, that the runtime *accepts* it — it clamps the destination and mounts on `/data`, so the storage lands on a path the call never named.
- The data-directory guards now run *inside* the resource's own configuration pipeline instead of beside it. They are appended as the last environment and command-line callbacks when the application starts, so they observe the final `DATA_PATH` and the final argument list — including values produced by dynamic callbacks, in whatever order the calls were made — and the canonical `DATA_PATH` they validated replaces the value the container is given. Aspire evaluates each callback once per run, so a callback that computes a different path every time it is asked cannot make the guard and the container disagree, and nothing but `DATA_PATH` is resolved: the password and every other environment value are left as the callbacks produced them. Previously the guards ran their own copy of the environment pipeline from a `BeforeResourceStartedEvent` handler, which evaluated every callback a second time (without the context Aspire supplies), executed peers' callbacks on their behalf, and could validate one value while the container received another. A raw `WithEnvironment("DATA_PATH", ...)`, a value supplied as a parameter, or one computed in a callback participates in the read-only, duplicate-mount, and shared-data-directory rules with the documented "last call wins" precedence against `WithDataVolume(...)`/`WithDataBindMount(...)`, and every comparison runs on canonicalized container paths — previously the guards compared the storage helpers' own target after trimming trailing slashes only, so an alias such as `/foo/../data` was treated as a different directory from `/data` even though the runtime mounts both on the same destination, which on images with no data-directory interlock let two resources take one data directory concurrently.
- A mount target that reaches above the container root, such as `WithVolume("data", "/../data")`, is now rejected before the container is created. Docker does not refuse that spelling — it clamps the destination and mounts on `/data`, which `docker inspect` confirms and which collides with a plainly spelled `/data` as `Duplicate mount point: /data` — so the storage silently lands on a directory the call never named and can become, or collide with, the DocumentDB data directory. It was previously recorded as "escaping" and then ignored, which meant the read-only, duplicate and shared-storage rules never saw it.
- A mount on an ancestor of `DATA_PATH` is now recognised as the mount that backs it. A volume at `/data` supplies `/data/cluster`, so with `DATA_PATH=/data/cluster` that volume is the data mount and the read-only and duplicate-mount rules apply to it; previously only a mount on exactly `DATA_PATH` counted, and a read-only ancestor was ignored. The most specific mount wins, matched on segment boundaries, so a mount on `/data/cluster` takes precedence over one on `/data` and `/database` is not treated as living under `/data`. Shared-storage identity now includes the path from the mount target down to `DATA_PATH`: two resources sharing one volume at `/data/alpha` and `/data/beta` are two clusters and are allowed, while two at `/data/cluster` are one and are refused.
- Shared-storage identity is now the directory the cluster occupies rather than the pair of strings it was spelled with. For a bind mount that is one host path — the mount source with whatever part of `DATA_PATH` falls below the mount target appended — so a resource binding `/srv/documentdb` and writing to `/data/cluster` and a resource binding `/srv/documentdb/cluster` and writing to `/data` are recognised as sharing one directory, in either declaration order; previously they compared unequal and two PostgreSQL instances could take one directory, silently on images at or below `0.114.0`, which have no interlock. The comparison uses the host's own case rules, so `/data/Cluster` and `/data/cluster` are one directory on macOS and Windows and two on Linux, matching what is on disk. Bind sources are also canonicalized, so `/srv/documentdb`, `/srv/documentdb/.` and `/srv/documentdb/../documentdb` are one source; symbolic links are deliberately not resolved, because that would depend on the state of the host filesystem at model-build time. A volume keeps its name-plus-subdirectory identity compared exactly: a volume name is not a path, and the container reads that subdirectory on its own case-sensitive filesystem.
- The data-storage guard now takes the last position in the environment and command-line pipelines twice, and verifies it. It is appended when the application starts and moved back to the end immediately before the resource starts, which covers `IDistributedApplicationLifecycleHook.BeforeStartAsync` and any `BeforeStartEvent` subscriber registered after `AddDocumentDB` — both run after the guard installs itself and could previously append a callback that moved the data directory past every check. Anything appended later still, including a lifecycle hook in publish mode where no per-resource event is published, now fails the resource with an explanatory `InvalidOperationException` instead of letting it start on a data directory nothing checked; the message describes the shape of the configuration and never repeats the value that displaced the guard. Moving re-uses the same callbacks, so the guarantee of a single evaluation is unchanged.
- A command-line token whose value is only known later — a parameter or a `ReferenceExpression` — is now rejected wherever the container entrypoint reads an option name, because it could resolve to `--data-path` and the only way to know would be to resolve it a second time. It is still accepted in the one position where it cannot be an option: directly after a literal option that takes a value, which is the entrypoint's own `--option value` grammar, so `WithArgs("--log-level", level)` and `WithArgs("--password", password)` keep working. Previously only literal strings were examined, so a deferred token could carry `--data-path` past the guard.
- `DATA_PATH` supplied as a parameter is now rejected in publish mode when the resource also mounts storage. A manifest carries the expression, not a path, so the read-only, duplicate-mount and shared-data-directory rules were all being skipped and a manifest putting two DocumentDB resources on one data directory could be published without complaint. The value is deliberately not resolved — it belongs to the deployment, and a parameter may be a secret — so the configuration is refused instead. A resource that mounts nothing has no storage to get wrong and keeps the expression; run mode resolves the value once and checks it as before.
- `DATA_PATH` is now written into the container environment even when nothing else sets it, using the canonical `/data` the checks were made against. Leaving it unset let an image whose own default is somewhere else write to a directory the guard never looked at.
- The storage guard's advisory warnings — the shared-data-directory downgrade and the declared-image-volume notice — now reach the AppHost log under the `Aspire.Hosting.DocumentDB.Storage` category. They were being written to the environment callback's own logger, which is not attached during the pass Aspire makes over those callbacks while discovering a container's dependencies; because that pass is the one whose result is cached for the run, every such warning was silently discarded on a real start.
- The container entrypoint's `--data-path` argument (and the reserved `-d` short form) is now rejected with an actionable `InvalidOperationException`. The image documents `--data-path` as "Overrides DATA_PATH environment variable" and the entrypoint exports it while parsing arguments, so a resource that passed it moved its data directory to a path the environment never named, past every storage rule and past the mount that was supposed to back it. The message points at `WithDataVolume()`, `WithDataBindMount(...)` and `WithEnvironment("DATA_PATH", ...)`.
- An empty `DATA_PATH` now follows the image default instead of being treated as an error. The entrypoint applies `DATA_PATH=${DATA_PATH:-/data}`, which treats empty and unset alike, so the guards judge an empty value as `/data` — and write that canonical value through, so the dashboard and the container agree.
- `WithLogLevel(...)` now sets `DOCUMENTDB_LOG_LEVEL`, the tracing filter consumed by DocumentDB `0.114.0` and later, making the API observably effective on those images. The legacy `LOG_LEVEL` value remains because the Local entrypoint validates its six-value contract, not because any Local image uses it to select gateway verbosity; images through `0.113.0` remain verbosity no-ops. `Quiet` stays mapped to `quiet` for public API compatibility and becomes newly effective on `0.114.0` and later: upstream has no such tracing level, but currently parses it as an unmatched target that suppresses gateway output, so that behavior depends on upstream filter semantics.
- `WithInitData(...)` and `WithoutSampleData()` now set `INIT_DATA=false` together with `SKIP_INIT_DATA=true`, overriding an earlier `INIT_DATA=true` and aligning the runtime environment and published manifests with upstream's canonical `--skip-init-data` behavior. Corrected `WithoutUserCreation()` guidance to distinguish the default built-in sample initialization in curated images `0.112.0` and older from the opt-in behavior in `0.113.0` and later, while clarifying that `WithoutSampleData()` does not disable custom initialization.
- Percent-encoded the user name and password in the generated `mongodb://` and `postgresql://` connection strings. Arbitrary credential parameters were interpolated raw into the URI userinfo, so a value containing `:`, `@`, `/`, `?`, `#`, `%`, a space, or non-ASCII text produced a malformed or misparsed connection string, and a value containing `&` could inject extra connection options. The registered health checks consume the same expressions, so those values also broke readiness. Encoding uses Aspire's `uri` reference-expression format, which applies RFC 3986 `Uri.EscapeDataString` when the expression is resolved, so no secret is read while the application model is built. Credentials made only of unreserved characters — including the default `admin` user name and the auto-generated password — are still emitted verbatim, leaving existing connection strings byte-identical. The container's `USERNAME` and `PASSWORD` environment variables deliberately keep the raw values because the entrypoint consumes them directly.
- Corrected troubleshooting guidance that claimed health checks were not registered. The integration uses an authenticated MongoDB `ping`; documentation now distinguishes gateway availability from completion of the one-shot initialization phase introduced in DocumentDB `0.116.0`.
- Corrected `WithOwner(...)` documentation: `OWNER` names an existing PostgreSQL role used for database operations, not an arbitrary resource label. The bundled image creates the default `documentdb` role. A missing custom role causes startup to fail: DocumentDB `0.116.0` aborts explicitly during admin-user creation, while earlier images fail later while waiting for the gateway.
- Documented that `WithDataBindMount(...)` does not survive a container restart on Docker Desktop, and recommended `WithDataVolume(...)` there. PostgreSQL refuses a data directory whose owner is not the user starting the postmaster, and the `documentdb-local` entrypoint establishes that ownership by running `chown` on `DATA_PATH` milliseconds before starting the postmaster. Docker Desktop applies that `chown` to a bind-mounted host path asynchronously — measured on macOS with VirtioFS, where `stat` keeps reporting the previous owner for roughly a second, and expected on its Windows and Linux hosts, which share the same file-sharing design — so the restart aborts with `FATAL: data directory "/data" has wrong ownership` and the data stays on the host but unreadable. The first run is unaffected because `initdb` runs for several seconds between the two steps. This is a container-runtime limitation rather than a package defect: it reproduces with a plain `docker run`, and `pg17-0.114.0` behaves identically (a fresh first run on an empty bind mount succeeds and becomes ready; a restart against that initialized bind-mounted data fails with the same ownership error), so it is not a DocumentDB `0.116.0` regression. `WithDataVolume(...)` is unaffected because a named volume lives inside the container runtime's own filesystem, and a bind mount on a native container engine is an ordinary mount that restarts normally.
- Preserved `WithOpenTelemetryMetrics(...)` behavior for DocumentDB `0.116.0` and later in both direct AppHost run mode and every publisher, including `aspire publish` and `azd`. Upstream `0.116.0` made the gateway resolve telemetry as JSON > environment > default and shipped a `SetupConfiguration.json` that pins metrics off, so the documented `OTEL_*` variables no longer took effect. The integration now wraps the container entrypoint for the official image at that version or later. The wrapper resolves the configuration directory exactly as the image entrypoint does (`CONFIG_DIR`, then the packaged `/etc/documentdb/gateway` layout, then `$GATEWAY_HOME/pg_documentdb_gw`), removes the `TelemetryOptions.Metrics` object whole — this API owns that signal, the shipped `OtlpEndpoint` would otherwise beat both `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` and `OTEL_EXPORTER_OTLP_ENDPOINT`, and deleting today's keys individually would leave any field a future gateway release adds authoritative over the environment — plus the shared `ServiceName`/`ServiceVersion` only when the corresponding override was explicitly supplied, then repoints `CONFIG_DIR` and execs the image's own entrypoint. `TelemetryOptions.Tracing` and the shipped service identity are left in place, and no default identity is injected. `enabled: false` installs the wrapper as well, so it beats a configuration file that enables metrics from JSON. Because the wrapper is carried in the container `entrypoint` and `args`, the published manifest still names the official image and remains deployable. Custom images and tags outside the `pgNN-X.Y.Z` grammar are untouched; private registry mirrors of the official image are covered. Digest-pinned official images, caller-supplied entrypoints, and an entrypoint replaced after the wrapper installed one — including by a later `BeforeStartEvent` subscriber in the same startup, which the argument callback re-checks for — all fail with actionable errors rather than silently skipping or over-applying the override. The image is evaluated at start/publish time, so selecting the version after calling `WithOpenTelemetryMetrics(...)` works.

## [0.114.1] - 2026-07-29

### Fixed
- Pairing `WithPostgresVersion(DocumentDBPostgresVersion.Pg18)` with a DocumentDB version older than `0.114.0` now fails at startup with an actionable message naming the recovery, instead of failing the container pull with an opaque manifest-not-found error. Upstream only publishes `pg18-` images from `0.114.0` onwards, so every older pairing produces a well-formed tag that does not exist. `Pg18` was introduced in `0.114.0`, so this gap shipped with that release. Custom images and tags outside the `pg{NN}-X.Y.Z` grammar are exempt, and manifest generation is unaffected.
- Corrected the `UseTls` XML documentation (and the Markdown docs) that claimed the DocumentDB Local container *requires* TLS connections. From DocumentDB `0.114.0` the container's default `TLS_MODE=allowTLS` accepts both plain and TLS connections, so `UseTls(false)` now works against the default image; images up to `0.113.0` rejected plain connections regardless of that setting. Documented `.WithEnvironment("TLS_MODE", "requireTLS")` as the way to reject plain connections, including that it contradicts `UseTls(false)` and that the value is case-sensitive.
- Corrected the data-persistence documentation, which recommended `WithDataVolume()` and `WithDataBindMount()` without pinning credentials — the one combination that breaks. The container hashes the configured password into a PostgreSQL role the first time it initialises a data directory, and that role is stored in the volume; because `AddDocumentDB` generates a random password when none is supplied, the *second* run presents a password the volume no longer recognises and every connection fails with `MongoAuthenticationException` / `Command saslContinue failed: Invalid key`, with the data intact but unreachable. `docs/configuration.md` now documents the caveat under both methods, and `docs/troubleshooting.md` carries a dedicated symptom entry with recovery paths (including `ALTER ROLE` through `WithPostgresEndpoint()`, so an existing volume need not be discarded).

## [0.114.0] - 2026-07-20

### Added
- `DocumentDBVersion.V0_114_0` curated enum member and `DocumentDBVersions.V0_114_0 = "0.114.0"` constant. `DocumentDBVersions.Latest` and the default container tag (`pg17-0.114.0`) now follow this entry.
- `DocumentDBPostgresVersion.Pg18` enum member, enabling `WithPostgresVersion(DocumentDBPostgresVersion.Pg18)` to target the new `pg18-` container variant that upstream publishes starting with DocumentDB `0.114.0`.

### Changed
- Default `documentdb-local` container image updated to `ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.114.0`. Upstream DocumentDB `0.114.0` enables schema validation by default and non-blocking unique index builds, and adds gateway operational features (environment-variable configuration overlay, a `documentdb-gateway check` subcommand, and PostgreSQL peer-auth hardening) that apply to standalone/systemd gateway installs rather than the combined `documentdb-local` entrypoint the Aspire integration uses. No new package APIs are required beyond the version and PG-variant additions above.

## [0.113.0] - 2026-07-06

### Added
- `DocumentDBVersion.V0_113_0` curated enum member and `DocumentDBVersions.V0_113_0 = "0.113.0"` constant. `DocumentDBVersions.Latest` and the default container tag (`pg17-0.113.0`) now follow this entry.

### Changed
- Default `documentdb-local` container image updated to `ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.113.0`. Upstream DocumentDB `0.113.0` is a performance and indexing release (sort-into-accumulator GUC for `$sortGroup`, collation for non-unique ordered indexes with `$in`/`$nin`, dead-index-entry pruning for ordered TTL indexes, and index-only scans for composite-covered `$group` accumulators); no new package APIs are required.

## [0.112.0] - 2026-06-02

### Highlights
- Default `documentdb-local` container tag bumped to `pg17-0.112.0`. `DocumentDBVersion.V0_112_0` added to the curated enum and is now `DocumentDBVersions.Latest`.
- New typed extension methods round out v0.112-era container parity: `WithoutExtendedRum()`, `WithoutUserCreation()`, `WithPostgresEndpoint()`, and `WithOpenTelemetryMetrics(...)`.
- `WithTelemetry(bool)` is now `[Obsolete]` (warning only) because the underlying `ENABLE_TELEMETRY` env var is no longer consumed by the v0.112-0 gateway; migrate to `WithOpenTelemetryMetrics(...)` for OTLP metrics.

### Added
- `WithoutExtendedRum()` extension method to disable the `extended_rum` index access method in the DocumentDB Local container ([documentdb/documentdb#470](https://github.com/documentdb/documentdb/pull/470))
- `WithoutUserCreation()` extension method to skip automatic user creation on container startup
- `WithPostgresEndpoint()` extension method to opt in to exposing the PostgreSQL backend coordinator port (`9712`), plus `DocumentDBServerResource.PostgresEndpoint` and `DocumentDBServerResource.PostgresConnectionStringExpression` for accessing the `postgresql://` connection string ([#10](https://github.com/microsoft/azure-databases-aspire/issues/10)). Requires DocumentDB container image `>= 0.112.0`; see [configuration.md](docs/configuration.md#withpostgresendpoint) for the recovery recipe when an older tag is pinned.
- `WithOpenTelemetryMetrics(endpoint?, enabled?, exportInterval?, timeout?, serviceName?, serviceVersion?)` extension method to wire the OTLP/gRPC metrics exporter introduced in DocumentDB Local container image `v0.112-0`. Sets `OTEL_METRICS_ENABLED`, `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT`, `OTEL_METRIC_EXPORT_INTERVAL`, `OTEL_EXPORTER_OTLP_METRICS_TIMEOUT`, `OTEL_SERVICE_NAME`, and `OTEL_SERVICE_VERSION` as appropriate ([#72](https://github.com/microsoft/azure-databases-aspire/issues/72))
- `DocumentDBVersion.V0_112_0` curated enum member and `DocumentDBVersions.V0_112_0 = "0.112.0"` constant. `DocumentDBVersions.Latest` and the default container tag (`pg17-0.112.0`) now follow this entry ([#70](https://github.com/microsoft/azure-databases-aspire/issues/70))

### Changed
- Default container image updated to `ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.112.0`. Upgraded .NET Aspire to 13.3.5, Microsoft.NET.Test.Sdk to 18.6.0, and bumped centrally managed `Microsoft.Extensions.*` to 10.0.7 to satisfy the Aspire transitive floor.

### Deprecated
- `WithTelemetry(bool)` is marked `[Obsolete]` with diagnostic ID `ASPIREDOCDB0001`. The `ENABLE_TELEMETRY` environment variable it sets is not consumed by the DocumentDB gateway in container image `v0.112-0` or later, so calling it has no observable effect. The method is retained for binary compatibility and may be removed in a future release. Use `WithOpenTelemetryMetrics(...)` to configure OTLP metrics export instead ([#72](https://github.com/microsoft/azure-databases-aspire/issues/72))

### Fixed
- Default data volume path changed from `/home/documentdb/postgresql/data` to `/data` to match the DocumentDB Local container default ([documentdb/documentdb#556](https://github.com/documentdb/documentdb/issues/556))
- `WithPostgresEndpoint()` now validates the effective container image tag at startup (via `BeforeResourceStartedEvent`) and throws `InvalidOperationException` if the tag is older than `pg{NN}-0.112.0`, preventing the previously silent PostgreSQL authentication failure caused by the legacy `docdb_admin`/`Admin100` admin role in pre-v0.112-0 `documentdb-local` images. Custom images and unknown tag patterns are exempt with a warning. ([#71](https://github.com/microsoft/azure-databases-aspire/issues/71))

## [0.110.0] - 2026-05-12

### Added
- `DocumentDBVersion` enum (curated, append-only) and `DocumentDBPostgresVersion` enum exposing
  the PostgreSQL backend choice.
- `DocumentDBVersions` static class with `All`, `Latest`, and per-version string constants.
- `WithDocumentDBVersion(...)` extension method to pin the DocumentDB version from code.
- `WithPostgresVersion(...)` extension method to pick a PG15/PG16/PG17 backend variant.
- `.github/workflows/check-documentdb-version.yml` scheduled workflow + companion
  `eng/scripts/check-documentdb-versions.py` that detects new upstream releases (when both a
  GitHub release and `pg15/16/17-X.Y.Z` GHCR tags exist) and opens a PR appending them to the
  curated supported-versions list.
- Configuration APIs: `WithLogLevel(...)`, `WithInitData(...)`, `WithoutSampleData()`,
  `WithTlsCertificate(...)`, `WithTelemetry(...)`, and `WithOwner(...)` extension methods
  for fine-grained container configuration.

### Changed
- Default container image updated to `ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.110.0`
- `DocumentDBContainerImageTags.Tag` is now a computed property
  (`pg17-{DocumentDBVersions.Latest}`) instead of a `const`, so the default tag follows the
  curated `Latest` version without manual edits to two files.
- Upgraded .NET Aspire to 13.3.0
- Updated Microsoft.NET.Test.Sdk to 18.5.1

### Fixed
- Pinned SharpCompress and Snappier transitive dependencies to resolve NuGet vulnerability
  audit errors.
- Fixed TLS certificate/key bind-mount collision when both files mapped to the same
  container path.

## [0.109.2] - 2026-04-13

### Added
- Unit and integration test suite (39 unit tests, 2 E2E tests)
- Build and test CI workflow to gate pull requests
- Dependabot configuration for weekly NuGet dependency updates
- Getting started guide, configuration reference, and troubleshooting documentation

### Changed
- Upgraded to .NET 10 SDK (10.0.100)
- Upgraded to .NET Aspire 13.2.2 (Aspire.Hosting, Aspire.Hosting.Testing, Aspire.MongoDB.Driver)
- Updated Microsoft.Extensions.Http.Resilience to 10.4.0
- Updated Microsoft.Extensions.* packages to 10.0.5+
- Updated MongoDB.Driver to 3.6.0
- Updated AspNetCore.HealthChecks.MongoDb to 9.0.0
- Updated MinVer to 7.0.0
- Updated Microsoft.NET.Test.Sdk to 18.4.0
- Updated xunit.analyzers to 1.27.0
- Updated xunit.runner.visualstudio to 3.1.5

## [0.109.0] - 2026-04-07

### Added
- NuGet package metadata (description, tags, icon, license, project URL)
- NuGet publish workflow for automated releases on version tags
- `UseTls()` and `AllowInsecureTls()` extension methods for explicit TLS control

### Changed
- Upgraded to .NET Aspire 13.1.2
- Default container image updated to `ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.109.0`
- TLS is now enabled by default (`tls=true&tlsInsecure=true`) for the local container's self-signed certificate
- Uses `tlsInsecure=true` instead of `tlsAllowInvalidCertificates=true` for better .NET MongoDB driver compatibility

### Fixed
- Connection string TLS handling for .NET MongoDB driver self-signed certificate validation
- `WithHostPort()` now updates the correct `tcp` endpoint (previously referenced `http`)

## [0.1.0] - 2025-08-20

### Added
- Initial release of `Aspire.Hosting.DocumentDB`
- `AddDocumentDB()` extension method for adding a DocumentDB server resource
- `AddDatabase()` extension method for adding database child resources
- `WithHostPort()` for fixed port binding
- `WithDataVolume()` for Docker volume persistence
- `WithDataBindMount()` for host directory persistence
- Auto-generated connection strings with MongoDB wire protocol
- SCRAM-SHA-256 authentication support
- Container image: `ghcr.io/documentdb/documentdb/documentdb-local`

[Unreleased]: https://github.com/microsoft/azure-databases-aspire/compare/v0.116.0...HEAD
[0.116.0]: https://github.com/microsoft/azure-databases-aspire/compare/v0.114.1...v0.116.0
[0.114.1]: https://github.com/microsoft/azure-databases-aspire/compare/v0.114.0...v0.114.1
[0.114.0]: https://github.com/microsoft/azure-databases-aspire/compare/v0.113.0...v0.114.0
[0.113.0]: https://github.com/microsoft/azure-databases-aspire/compare/v0.112.0...v0.113.0
[0.112.0]: https://github.com/microsoft/azure-databases-aspire/compare/v0.111.0...v0.112.0
[0.111.0]: https://github.com/microsoft/azure-databases-aspire/compare/v0.110.0...v0.111.0
[0.110.0]: https://github.com/microsoft/azure-databases-aspire/releases/tag/v0.110.0
[0.109.2]: https://github.com/microsoft/azure-databases-aspire/releases/tag/v0.109.2
[0.109.0]: https://github.com/microsoft/azure-databases-aspire/releases/tag/v0.109.0
[0.1.0]: https://github.com/microsoft/azure-databases-aspire/compare/32cee17...4aa9aac
