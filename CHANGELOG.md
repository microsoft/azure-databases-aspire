# Changelog

All notable changes to the `Aspire.Hosting.DocumentDB` package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project uses [MinVer](https://github.com/adamralph/minver) for versioning based on Git tags.

## [Unreleased]

<!-- auto-generated:documentdb-versions-start -->
_No upstream DocumentDB versions detected since the last release. This block is rewritten in place by `eng/scripts/check-documentdb-versions.py`; reset it to this line when cutting a release, after moving its contents into the dated section below._
<!-- auto-generated:documentdb-versions-end -->

### Fixed
- The `WithOpenTelemetryMetrics(...)` gateway wrapper is no longer bypassed by reading the resource's configuration before changing it. Aspire records each callback's result the first time it runs and reuses it for the rest of the run, and the argument gatherer takes the *last* callback's recorded result as the whole argument list. A caller who built the configuration through the public `ExecutionConfigurationBuilder` (or the obsolete `GetArgumentValuesAsync`) — from an `IDistributedApplicationLifecycleHook`, or as the dependency pass a container creation performs before it builds its spec — and only then appended an argument callback, re-pointed `Entrypoint`, or selected another image got a validated answer recorded before the change and reused after it. Appending a callback in that state did not reorder the command line, it *replaced* it: the recorded wrapper was dropped and the manifest carried only the late callback's arguments, so publish emitted `/bin/bash` with the wrapper gone. Run mode had the same hole through `WithContainerRuntimeArgs(...)`, which Aspire never caches and invokes after the dependency pass but before the container's command is read. The wrapper now records what its answer depended on — the callbacks in the command-line, environment and container-runtime pipelines, the entrypoint, and the effective image — and compares that at the two phases Aspire runs unconditionally: a container-runtime-arguments callback the package owns and keeps last, which runs on every container creation after any caller callback and before the command, arguments and environment are read; and `BeforePublishEvent`, which is raised after every lifecycle hook and before the publishing pipeline serializes anything. A mismatch fails the resource before the container is created or the manifest is written, naming what kind of thing changed and no value, since whatever changed the model may be carrying a secret. The package also owns the resource's last environment callback, so an environment callback that re-points the entrypoint — the last caller-reachable code before a container's command is read — is caught as well. Arguments added by a lifecycle hook that does *not* read first are now ordered behind the wrapper and published normally, where they previously failed the resource: `BeforePublishEvent` is late enough to repair them. Annotations are still moved rather than re-created, so every callback is evaluated exactly once per run, re-evaluated on restart, and a deferred or secret-bearing argument is resolved by Aspire once and never by this package.
- The `WithOpenTelemetryMetrics(...)` gateway configuration wrapper is now the last word on the container command line, so a later argument callback can no longer displace it. Aspire evaluates command-line callbacks in annotation order over one shared list, and the wrapper's callback used to be registered where `WithOpenTelemetryMetrics(...)` was called; a `WithArgs(...)` registered after it therefore ran last. `/bin/bash` reads its command from the first arguments, so `.WithOpenTelemetryMetrics().WithArgs(context => context.Args.Insert(0, "--help"))` published and ran `/bin/bash --help -c <script> --`, and bash exited without starting DocumentDB. The wrapper now contributes the resource's single package-owned command-line callback and retakes the last position at `BeforeStartEvent`, `ResourceEndpointsAllocatedEvent`, and `BeforeResourceStartedEvent` — the latest phases a run publishes before a container's arguments are gathered — which covers every builder-time `WithArgs(...)` whatever the call order, a `BeforeStartEvent` subscriber registered after `AddDocumentDB`, and an `IDistributedApplicationLifecycleHook`. Caller arguments that prepend, clear, reorder or replace the list now run before the wrapper, so what they produce becomes arguments of the image entrypoint, which is what they were asking for; appending is unchanged. Anything that appends a command-line callback after the last phase — a `BeforeResourceStartedEvent` subscriber registered after `AddDocumentDB`, or, in publish mode, any lifecycle hook, because publish raises no per-resource event — now fails the resource instead of shipping a command line that was built and then taken apart, and the message names the shape of the pipeline without repeating any argument value, since the displacing callback may be the one carrying a secret. The finished command line is verified before it is used: the entrypoint must still be `/bin/bash`, the arguments must begin with exactly `-c`, this run's script instance and `--`, the script must not appear twice, and the image is classified again at that point so a late image, tag, digest or Dockerfile change is judged on what the container will actually run. The annotation is moved rather than re-created, so the wrapper is still evaluated exactly once per run and re-evaluated on restart, and the manifest still carries the same brace-free one-line script in `entrypoint`/`args`.
- The official `documentdb-local` image is now recognised from the fully composed image reference rather than from `ContainerImageAnnotation.Image` alone, and the registry field is no longer trusted to hold a registry. Aspire joins the two annotation fields with a separator and validates neither, so `WithImage("ghcr.io/documentdb/documentdb/documentdb-local", "pg17-0.116.0").WithImageRegistry(null)` publishes and runs exactly the reference the default spelling does; comparing the image field on its own called that a custom image and withheld everything that is true of the official one: the `WithOpenTelemetryMetrics(...)` gateway configuration wrapper and its digest rejection, the `WithPostgresEndpoint()` credential floor, and the `Pg18` publish floor. Recognition now composes the reference first and then removes exactly one prefix — either the curated registry `ghcr.io/documentdb`, or a single registry host (DNS name, IPv4 or bracketed IPv6 literal, or `localhost`, each with an optional port) with nothing after it — and what remains must be `documentdb/documentdb-local` exactly. Two spellings that compose the same reference therefore always classify the same. A private mirror is the curated repository directly beneath a registry host, in either spelling, such as `contoso.azurecr.io/documentdb/documentdb-local` or `localhost:5000/documentdb/documentdb-local`. A namespace, project or mirror path in front of the repository names a different repository and stays custom, whether it is written into the registry field or inline: `.WithImageRegistry("ghcr.io/evil")`, `.WithImageRegistry("contoso.azurecr.io/mirrors")` and `.WithImageRegistry("harbor.corp.local/library")` over `documentdb/documentdb-local` are now custom where a registry-prefixed spelling was previously accepted verbatim, as are `evil/documentdb/documentdb-local`, a bare registry name with no port such as `myregistry/documentdb/documentdb-local`, a reference that repeats the registry in the image field (composing an unresolvable `ghcr.io/documentdb/ghcr.io/...`), and any reference with a doubled, leading or trailing separator. A digest is read whether it arrives through `WithImageSHA256(...)` or inline as `repository@sha256:...`, and a `:` is a tag only in the last path segment, so a registry port is never mistaken for one. Dockerfile builds remain custom whatever their image text says. A digest now beats every tag: a reference carrying both — `repository:pg17-0.116.0@sha256:...`, an inline tag beside a `WithImageSHA256(...)` digest, or the reverse — is resolved by the runtime from the digest, so it is treated as version unknown and no longer inherits the tag's release. Previously such a reference could take `0.116.0` assumptions from the tag while running an older image, which enforced or refused the version floors against a tag the runtime discards. The repository is still recognised, so `WithOpenTelemetryMetrics(...)` still rejects a digest-pinned curated image with its actionable message, and a digest on any other repository remains untouched.
- The `WithOpenTelemetryMetrics(...)` gateway wrapper now keeps its sanitized `SetupConfiguration.json` outside the effective canonical `DATA_PATH`, including a `-d` or `--data-path` command-line override. It previously used the default `mktemp -d` location, so a valid data path of `/tmp` caused the wrapper to create a directory inside the fresh PostgreSQL data target before initialization; DocumentDB `0.116.0` then correctly refused the non-empty non-cluster directory. The wrapper now selects a writable subtree from `/tmp`, `/var/tmp`, and `/dev/shm` that is disjoint from `DATA_PATH` both as a container path and as storage, and fails clearly if none is safe. The storage half matters because two container paths that do not contain one another can still be one directory: one host directory bind-mounted at both `/tmp` and `/data`, or one named volume mounted twice, put the scratch copy straight back inside the data directory. The mount table is known while the model is built, so the aliasing decision is made there — including mount targets that are ancestors of the data directory or of a candidate root, and the relative subpaths below them — and travels with the container command as the exact set of data directories each candidate cannot be used with, which keeps the runtime `-d`/`--data-path` override working. Both enabled and explicitly disabled metrics are covered because both modes install the wrapper.
- A DocumentDB resource whose container image is built from a Dockerfile (`WithDockerfile(...)`, `WithDockerfileFactory(...)`, `WithDockerfileBuilder(...)`) no longer receives the `WithOpenTelemetryMetrics(...)` gateway configuration wrapper, whatever its image annotation says. Aspire keeps the `ContainerImageAnnotation` that `AddDocumentDB` installs when a Dockerfile build is added, and a caller can re-point it at `documentdb/documentdb-local` and a recognised `pg{NN}-X.Y.Z` tag afterwards, so such a resource could previously be mistaken for the official `0.116.0` image and have its entrypoint replaced by a wrapper built entirely out of official-image facts — the `emulator_entrypoint.sh` path, the packaged `/etc/documentdb/gateway` layout, `bash` and `jq` — none of which is established for an image this package did not publish. A digest pin on such a resource no longer throws either, because for a build the digest is no more what runs than the tag is. The published manifest emits a `build` object and no `image` at all, which is what makes the annotation unusable as evidence. Image classification is now resolved in one place; true official images, private mirrors, digest pins, recognised tags, unrecognised tags, custom repositories and caller-owned entrypoints are unaffected, and overriding only the base image of a generated Dockerfile (`WithDockerfileBaseImage(...)`) is correctly not treated as a build.
- Preserved `WithOpenTelemetryMetrics(...)` behavior for DocumentDB `0.116.0` and later in both direct AppHost run mode and every publisher, including `aspire publish` and `azd`. Upstream `0.116.0` made the gateway resolve telemetry as JSON > environment > default and shipped a `SetupConfiguration.json` that pins metrics off, so the documented `OTEL_*` variables no longer took effect. The integration now wraps the container entrypoint for the official image at that version or later. The wrapper resolves the configuration directory exactly as the image entrypoint does (`CONFIG_DIR`, then the packaged `/etc/documentdb/gateway` layout, then `$GATEWAY_HOME/pg_documentdb_gw`), removes the `TelemetryOptions.Metrics` object whole — this API owns that signal, the shipped `OtlpEndpoint` would otherwise beat both `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT` and `OTEL_EXPORTER_OTLP_ENDPOINT`, and deleting today's keys individually would leave any field a future gateway release adds authoritative over the environment — plus the shared `ServiceName`/`ServiceVersion` only when the corresponding override was explicitly supplied, then repoints `CONFIG_DIR` and execs the image's own entrypoint. `TelemetryOptions.Tracing` and the shipped service identity are left in place, and no default identity is injected. `enabled: false` installs the wrapper as well, so it beats a configuration file that enables metrics from JSON. Because the wrapper is carried in the container `entrypoint` and `args`, the published manifest still names the official image and remains deployable. Custom images and tags outside the `pgNN-X.Y.Z` grammar are untouched; private registry mirrors of the official image are covered. Digest-pinned official images, caller-supplied entrypoints, and an entrypoint replaced after the wrapper installed one — including by a later `BeforeStartEvent` subscriber in the same startup, which the argument callback re-checks for — all fail with actionable errors rather than silently skipping or over-applying the override. The image is evaluated at start/publish time, so selecting the version after calling `WithOpenTelemetryMetrics(...)` works.
- Corrected troubleshooting guidance that claimed health checks were not registered. The integration uses an authenticated MongoDB `ping`; documentation now distinguishes gateway availability from completion of the one-shot initialization phase introduced in DocumentDB `0.116.0`.
- Corrected `WithOwner(...)` documentation: `OWNER` names an existing PostgreSQL role used for database operations, not an arbitrary resource label. The bundled image creates the default `documentdb` role. A missing custom role causes startup to fail: DocumentDB `0.116.0` aborts explicitly during admin-user creation, while earlier images fail later while waiting for the gateway.

### Changed
- Added candidate-only container coverage for DocumentDB `0.116.0`: PG15-PG18 runtime smoke tests, persisted PG17 `0.114.0` to `0.116.0` migration, one-shot initialization, reserved username rejection, external PostgreSQL access, the new `lz4` TOAST default, and real OTLP metrics export. `DocumentDBVersions.Latest` remains `0.114.0` until this compatibility matrix passes in Docker-backed CI.
- Documented the `0.116.0` `/data` image volume, data-directory lock, credential lifetime, and initialization-readiness semantics.

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

[Unreleased]: https://github.com/microsoft/azure-databases-aspire/compare/v0.114.1...HEAD
[0.114.1]: https://github.com/microsoft/azure-databases-aspire/compare/v0.114.0...v0.114.1
[0.114.0]: https://github.com/microsoft/azure-databases-aspire/compare/v0.113.0...v0.114.0
[0.113.0]: https://github.com/microsoft/azure-databases-aspire/compare/v0.112.0...v0.113.0
[0.112.0]: https://github.com/microsoft/azure-databases-aspire/compare/v0.111.0...v0.112.0
[0.111.0]: https://github.com/microsoft/azure-databases-aspire/compare/v0.110.0...v0.111.0
[0.110.0]: https://github.com/microsoft/azure-databases-aspire/releases/tag/v0.110.0
[0.109.2]: https://github.com/microsoft/azure-databases-aspire/releases/tag/v0.109.2
[0.109.0]: https://github.com/microsoft/azure-databases-aspire/releases/tag/v0.109.0
[0.1.0]: https://github.com/microsoft/azure-databases-aspire/compare/32cee17...4aa9aac
