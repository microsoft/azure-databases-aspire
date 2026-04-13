# Changelog

All notable changes to the `Aspire.Hosting.DocumentDB` package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project uses [MinVer](https://github.com/adamralph/minver) for versioning based on Git tags.

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

[0.109.2]: https://github.com/microsoft/azure-databases-aspire/releases/tag/v0.109.2
[0.109.0]: https://github.com/microsoft/azure-databases-aspire/releases/tag/v0.109.0
[0.1.0]: https://github.com/microsoft/azure-databases-aspire/compare/32cee17...4aa9aac
