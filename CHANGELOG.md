# Changelog

All notable changes to the `Aspire.Hosting.DocumentDB` package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project uses [MinVer](https://github.com/adamralph/minver) for versioning based on Git tags.

## [Unreleased]

<!-- auto-generated:documentdb-versions-start -->
### Added (auto-detected upstream DocumentDB versions)

- DocumentDB `0.111.0` upstream release detected on 2026-05-12 (container tags `pg15-0.111.0`, `pg16-0.111.0`, `pg17-0.111.0`).

_Maintainer: append the matching `DocumentDBVersion.V0_X_Y` enum members and `public const string V0_X_Y = "X.Y.Z";` lines to `src/Aspire.Hosting.DocumentDB/api/Aspire.Hosting.DocumentDB.cs` before merging._
<!-- auto-generated:documentdb-versions-end -->

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

[0.110.0]: https://github.com/microsoft/azure-databases-aspire/releases/tag/v0.110.0
[0.109.2]: https://github.com/microsoft/azure-databases-aspire/releases/tag/v0.109.2
[0.109.0]: https://github.com/microsoft/azure-databases-aspire/releases/tag/v0.109.0
[0.1.0]: https://github.com/microsoft/azure-databases-aspire/compare/32cee17...4aa9aac
