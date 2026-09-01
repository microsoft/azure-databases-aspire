# Changelog

All notable changes to the `Aspire.Hosting.DocumentDB` package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project uses [MinVer](https://github.com/adamralph/minver) for versioning based on Git tags.

## [Unreleased]

<!-- auto-generated:documentdb-versions-start -->
_No upstream DocumentDB versions detected since the last release. This block is rewritten in place by `eng/scripts/check-documentdb-versions.py`; reset it to this line when cutting a release, after moving its contents into the dated section below._
<!-- auto-generated:documentdb-versions-end -->

### Fixed
- `WithLogLevel(...)` now sets `DOCUMENTDB_LOG_LEVEL`, the tracing filter consumed by DocumentDB `0.114.0` and later, making the API observably effective on the current default `0.114.0` image. The legacy `LOG_LEVEL` value remains because the Local entrypoint validates its six-value contract, not because any Local image uses it to select gateway verbosity; images through `0.113.0` remain verbosity no-ops. `Quiet` stays mapped to `quiet` for public API compatibility and becomes newly effective on `0.114.0` and later: upstream has no such tracing level, but currently parses it as an unmatched target that suppresses gateway output, so that behavior depends on upstream filter semantics.
- Preserved `WithOpenTelemetryMetrics(...)` behavior for the DocumentDB `0.116.0` candidate image in direct AppHost run mode. Upstream `0.116.0` added explicit disabled telemetry values to `SetupConfiguration.json`, and the gateway gives those JSON values precedence over the standard OpenTelemetry environment variables. The integration now injects a version-scoped compatibility configuration without `TelemetryOptions` into the stock Local image configuration directory, allowing the documented environment variables to remain authoritative without replacing a caller-supplied `CONFIG_DIR`. Private registry mirrors retaining the official image path and tag receive the same override. Aspire publish mode rejects this affected combination because publisher support for the required runtime file override is not universal.
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
