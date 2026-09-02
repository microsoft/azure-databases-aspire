# Troubleshooting

Common issues when using `Aspire.Hosting.DocumentDB` and how to resolve them.

## Docker issues

### Docker is not running

**Symptom:** The DocumentDB resource fails to start with an error about the Docker daemon.

**Solution:** Start Docker Desktop or your Docker daemon. Aspire requires a running Docker runtime to start container resources.

```bash
# Verify Docker is running
docker info
```

### Container image pull fails

**Symptom:** Timeout or network error pulling `ghcr.io/documentdb/documentdb/documentdb-local`.

**Solution:**
1. Verify network connectivity: `docker pull ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.114.0`
2. Check if you need to authenticate to GitHub Container Registry (public images should not require auth)
3. If behind a corporate proxy, configure Docker's proxy settings

### Container fails to start

**Symptom:** The resource shows as "Failed" in the Aspire dashboard.

**Solution:** Check the container logs in the Aspire dashboard or via Docker:

```bash
# Find the container
docker ps -a | grep documentdb

# View logs
docker logs <container-id>
```

Common causes:
- Port already in use (see [Port conflicts](#port-conflicts) below)
- Insufficient Docker resources (memory, disk)
- Corrupted data volume (remove the volume and restart)
- A username beginning with a reserved DocumentDB `0.116.0` prefix (`documentdb`, `citus`, `pg`, or `internal_role`)

## Connection issues

### TLS certificate errors

**Symptom:** `MongoAuthenticationException` or `SslPolicyErrors.RemoteCertificateChainErrors` when connecting from your service.

**Cause:** The DocumentDB container uses a self-signed TLS certificate. The .NET MongoDB driver rejects it unless `tlsInsecure=true` is in the connection string.

**Solution:** This should work automatically. The extension adds `tls=true&tlsInsecure=true` to the connection string by default. If you see this error:

1. Verify you are referencing the resource correctly: `.WithReference(db)` where `db` is the database resource from `AddDatabase()`.
2. If you manually constructed a connection string, add `tlsInsecure=true` (not `tlsAllowInvalidCertificates=true` — the .NET driver does not fully honor the latter for self-signed certificates).
3. If connecting from `mongosh` or another MongoDB CLI tool outside of Aspire, use this format:
   ```
   mongodb://admin:<password>@localhost:<port>/?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsAllowInvalidCertificates=true
   ```
   `mongosh` accepts `tlsAllowInvalidCertificates=true`. For .NET applications, prefer `tlsInsecure=true`.

### Plain (non-TLS) connections are refused

**Symptom:** `UseTls(false)` is set, but the client or health check cannot connect to the container.

**Causes and solutions:**
1. **Container image is older than `0.114.0`.** Images up to and including `0.113.0` always enforced TLS on the gateway and rejected plain connections, regardless of the `TLS_MODE` setting. Use the default (`0.114.0` or newer) image tag, or keep TLS enabled in the connection string.
2. **`TLS_MODE` is set to `requireTLS`.** This makes the container reject plain connections by design, which contradicts `UseTls(false)`. Remove the environment variable to fall back to the default `allowTLS` (accepts both plain and TLS connections), or re-enable TLS with `UseTls(true)`.

   ```csharp
   // Only if you want plain connections rejected — do NOT combine with UseTls(false).
   var server = builder.AddDocumentDB("documentdb")
                       .WithEnvironment("TLS_MODE", "requireTLS");
   ```

   `TLS_MODE=disabled` is not a plain-only mode; the gateway treats it exactly like `allowTLS` and the entrypoint logs a warning.
3. **`TLS_MODE` is misspelled.** The value is case-sensitive (`allowTLS`, `requireTLS`, `disabled`). The entrypoint rejects anything else and exits, so the container never starts — check the container logs for the rejected value.

### Connection refused / timeout

**Symptom:** `MongoConnectionException` with "connection refused" or timeout.

**Causes and solutions:**
1. **Container not ready yet.** DocumentDB takes a few seconds to initialize. Use `.WaitFor(db)` in your AppHost to improve startup ordering.

> [!IMPORTANT]
> This integration registers an authenticated MongoDB `ping` health check. It proves that the
> gateway is accepting authenticated requests, but on DocumentDB `0.116.0` the gateway can become
> reachable before one-shot custom or sample initialization has completed. A dependent resource
> that requires seeded data should still retry that data access rather than treating `WaitFor` as
> proof that initialization scripts have finished.
2. **Wrong port.** By default, Aspire assigns a random host port. Do not hardcode ports in your service — use `WithReference()` to inject the connection string automatically.
3. **Firewall or network issue.** If running Docker in a VM or WSL2, ensure port forwarding is configured.

### Authentication failures

**Symptom:** `MongoAuthenticationException: Unable to authenticate`.

**Causes and solutions:**
1. **Wrong credentials.** The extension generates a random password on each run (unless you provide fixed parameters). Always use the Aspire-injected connection string rather than hardcoding credentials. If you persist data with `WithDataVolume()`/`WithDataBindMount()`, a generated password also stops matching the role stored in the volume — see [Authentication fails on the second run after adding a data volume](#authentication-fails-on-the-second-run-after-adding-a-data-volume).
2. **Auth mechanism mismatch.** DocumentDB uses `SCRAM-SHA-256`. The connection string includes `authMechanism=SCRAM-SHA-256` automatically. If you construct your own connection string, include this parameter.
3. **Auth database.** Credentials are created in the `admin` database. The connection string includes `authSource=admin` automatically.

## OpenTelemetry metrics

### The container command looks wrapped in `/bin/bash -c ...`

Expected on DocumentDB `0.116.0` and later whenever `WithOpenTelemetryMetrics(...)` is called,
including with `enabled: false`. Those images resolve telemetry as *JSON > environment > default*
and ship a `SetupConfiguration.json` that pins metrics off, so the integration wraps the entrypoint
to remove the keys your settings have to win over before the image's own entrypoint runs. The
wrapper is carried in the container `entrypoint`/`args`, so it survives `aspire publish` and `azd`.
See [WithOpenTelemetryMetrics](configuration.md#withopentelemetrymetrics).

### `WithOpenTelemetryMetrics()` throws about a digest

Pinning the official image with `WithImageSHA256(...)` supersedes the tag, so the DocumentDB
version cannot be determined. Applying the compatibility wrapper and skipping it are both silently
wrong, so this fails instead. Select the image by tag, or drop `WithOpenTelemetryMetrics(...)` and
configure telemetry inside the image the digest names.

### `WithOpenTelemetryMetrics()` throws about the entrypoint

The wrapper has to own the container command, so any entrypoint you set on the same resource —
`/bin/bash` included, because its arguments would be yours — is rejected. The same applies when a
`BeforeStartEvent` subscriber or lifecycle hook replaces the entrypoint later in the same startup:
that is caught when the wrapper's arguments are resolved, and `aspire publish` reports the
`publish-manifest` step as failed. Drop the custom entrypoint, or drop
`WithOpenTelemetryMetrics(...)` and configure telemetry from your own entrypoint.

### `WithOpenTelemetryMetrics()` throws about `ShellExecution`

Leave `ContainerResource.ShellExecution` as `null` or set it to `false`. With `true`, DCP joins the
already validated wrapper arguments into a second `-c` command after the package's terminal check,
so `/bin/bash` no longer receives `-c <wrapper> --` and DocumentDB does not start. The effective
setting is sealed with the command and checked again before both container creation and manifest
serialization, so enabling it after an early configuration read also fails clearly.

### `WithOpenTelemetryMetrics()` rejects a container runtime option

`WithContainerRuntimeArgs(...)` is applied outside the resource model. Raw Docker/Podman mounts
can put DATA_PATH storage back under a scratch root after the wrapper has selected it, raw
`--entrypoint` can replace `/bin/bash`, and Podman `--rootfs` or a bare positional operand can
replace the model-selected image. `--read-only` and `--storage-opt` also change root filesystem
behavior outside the model. Podman-specific `--secret`, `--image-volume`, `--chrootdirs`,
`--init-path`, `--ipc`, `--read-only-tmpfs`, and `--systemd` are therefore covered alongside
Docker's mount grammar. `--pod` and `--pod-id-file` are rejected too because joining a pod can
replace the `/dev/shm` backing through its shared IPC namespace.

Protected `--env`/`-e`, `--env-merge`, and `--unsetenv` values are rejected too. So are
`--env-file`, `--env-host`, and `--unsetenv-all`, whose complete environment effects cannot be
validated. Podman's value-less `--env`/`-e` names ending in `*` import every matching host
variable, so a prefix that can match protected telemetry configuration is rejected without being
reported. Unrelated prefixes and wildcard-looking assigned forms containing `=` remain valid. Use
`WithBindMount(...)`, `WithVolume(...)`, and `WithEnvironment(...)`; harmless known runtime options
continue to work.

Diagnostics may name a known option or package-owned environment variable, but never repeat an
operand or value. Image names, rootfs paths, mount specs, environment values, URIs, credentials,
deferred resolutions, and failed-resolution exception text are deliberately omitted.

### `WithOpenTelemetryMetrics()` throws about a later command-line callback

The wrapper's `-c <script> --` prefix has to be the first thing on the container command line, so
its callback is the resource's last command-line callback and retakes that position at every phase
a run offers. A callback added after the last of them — a `BeforeResourceStartedEvent` subscriber
registered after `AddDocumentDB`, or any `IDistributedApplicationLifecycleHook` in publish mode,
where no per-resource event is raised — could still put a value in front of the wrapper, which
would leave `/bin/bash` running your value with the wrapper as its operands. That fails the
resource instead, and `aspire publish` reports the `publish-manifest` step as failed. The message
does not repeat the value, because the callback that displaced the wrapper may be the one carrying
a secret. Add the arguments with `WithArgs(...)` while the application model is being built — any
order and any mutation is fine there, including inserting at the front — or register the subscriber
before `AddDocumentDB`.

### `WithOpenTelemetryMetrics()` throws about a configuration that changed

The app host records each callback's result the first time it runs and reuses it, and it takes the
last callback's recorded result as the whole argument list. Building the resource's configuration
early — `ExecutionConfigurationBuilder`, or the obsolete `GetArgumentValuesAsync`, typically from an
`IDistributedApplicationLifecycleHook` — and *then* changing the resource therefore drops the
recorded wrapper from the command line without re-running anything that would notice.

The wrapper records what its answer depended on and compares it at the last point the app host runs
unconditionally: a container-runtime-arguments callback in a run, `BeforePublishEvent` in a publish.
A mismatch fails the resource before the container is created or the manifest is written, and names
what kind of thing changed — callbacks, entrypoint, `ShellExecution`, or image — without repeating
a value, because whatever changed the model may be carrying a secret.

Finish configuring the resource before anything reads its configuration. If a lifecycle hook has to
contribute arguments, let it add them without reading first: they are ordered behind the wrapper
automatically. Changing the entrypoint or the image of a resource that uses
`WithOpenTelemetryMetrics(...)` is never supported after the fact — select the image and leave the
entrypoint alone, or drop `WithOpenTelemetryMetrics(...)` and configure telemetry from your own
entrypoint.

### No metrics arrive at the collector
1. Confirm the collector is reachable from *inside* the container network and that you passed an
   explicit `endpoint:`. The gateway default (`http://localhost:4317`) resolves to the DocumentDB
   container itself.
2. Check the gateway startup line in the container logs. It prints the resolved configuration; on
   `0.116.0` and later a working setup shows `metrics: None` inside `telemetry_options`, meaning
   the JSON no longer pins anything about metrics and the environment decides.
3. `aspire-documentdb -- ...` on the first lines of the container log means the wrapper could not
   read the gateway configuration or could not find `jq`. The wrapper only ever applies to the
   official `documentdb/documentdb-local` image path, so this means a mirror or re-tag reuses that
   path with contents that are not the official image. Publish it under your own image name so the
   wrapper is skipped, then configure telemetry inside that image.

### The service name is not the one I set

`OTEL_SERVICE_NAME` set through `WithEnvironment(...)` is not an override, so the JSON
`TelemetryOptions.ServiceName` is left in place and still wins inside the gateway. Pass
`serviceName:` to `WithOpenTelemetryMetrics(...)` instead — that removes the JSON key. Note that
the gateway shares one OpenTelemetry `Resource` across signals, so this changes the identity of
traces as well as metrics.

## Port conflicts

**Symptom:** `Bind for 0.0.0.0:10260 failed: port is already allocated`.

**Solution:**
1. By default, Aspire uses a random port, so this only happens if you used `WithHostPort(10260)` or `port: 10260`.
2. Either remove the fixed port to let Aspire pick a random one, or stop whatever is using port 10260:
   ```bash
   # Find what's using the port
   lsof -i :10260   # macOS/Linux
   netstat -ano | findstr :10260   # Windows
   ```

## Wrong resource reference

**Symptom:** MongoDB operations fail with errors about missing database name, or data is written to an unexpected database.

**Cause:** You are referencing the *server* resource instead of the *database* resource. The server connection string does not include a database name in the path.

**Solution:** Always reference the database resource returned by `AddDatabase()`:

```csharp
var server = builder.AddDocumentDB("documentdb");
var db = server.AddDatabase("mydb");

builder.AddProject<Projects.MyService>("myservice")
       // Correct -- connection string includes /mydb
       .WithReference(db);

       // Wrong -- connection string has no database name
       // .WithReference(server);
```

## Data persistence

### Data lost after container restart

**Symptom:** All documents disappear when the Aspire application or Docker restarts.

**Cause:** By default, DocumentDB stores data inside the container filesystem. This storage is ephemeral.

**Solution:** Use `WithDataVolume()` to persist data in a Docker named volume, and pin the credentials at the same time (see the next entry for why):

```csharp
var userName = builder.AddParameter("documentdb-user");
var password = builder.AddParameter("documentdb-password", secret: true);

var server = builder.AddDocumentDB("documentdb", userName: userName, password: password)
                    .WithDataVolume();
```

Or use `WithDataBindMount()` to persist data to a specific host directory:

```csharp
var server = builder.AddDocumentDB("documentdb", userName: userName, password: password)
                    .WithDataBindMount("./data/documentdb");
```

### Authentication fails on the second run after adding a data volume

**Symptom:** The first run works. Every later run fails with `MongoAuthenticationException: Unable to authenticate using sasl protocol mechanism SCRAM-SHA-256` and an inner `Command saslContinue failed: Invalid key`. Removing the volume "fixes" it — and loses the data.

**Cause:** The container hashes the configured password into a PostgreSQL role the first time it initialises a data directory, and that role is stored in the volume. `AddDocumentDB` generates a random password when you do not supply one, so the next run presents a password that no longer matches the persisted role. The data is intact; the credentials no longer open it.

**Solutions:**
1. **Supply explicit credential parameters** (the fix — see [Data lost after container restart](#data-lost-after-container-restart) above). Persisted data needs a password whose lifetime you control.
2. **Already stuck with a volume you want to keep?** Change the role's password through the PostgreSQL backend instead of discarding the volume: enable [`WithPostgresEndpoint()`](configuration.md#withpostgresendpoint), connect with the credentials the volume was created with, and `ALTER ROLE ... PASSWORD ...`.
3. **Do not need the data?** `docker volume rm <name>` and start again with pinned credentials.

### Corrupted data volume

**Symptom:** Container fails to start with errors about `PG_VERSION` or data directory corruption.

**Solution:** Remove the existing volume and let DocumentDB recreate it:

```bash
# Find and remove the volume
docker volume ls | grep documentdb
docker volume rm <volume-name>
```

## Debugging with mongosh

You can connect to the running DocumentDB container directly using `mongosh` for debugging:

```bash
# Find the allocated port from the Aspire dashboard, or:
docker ps | grep documentdb

# Connect with mongosh
mongosh "mongodb://admin:<password>@localhost:<port>/?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsAllowInvalidCertificates=true"
```

Replace `<password>` and `<port>` with the values from the Aspire dashboard (click on the resource to see its connection string).
For `mongosh`, `tlsAllowInvalidCertificates=true` matches the upstream DocumentDB documentation. For .NET applications, use the Aspire-generated connection string, which uses `tlsInsecure=true`.

### Useful `mongosh` commands

```javascript
// List databases
show dbs

// Switch to your database
use mydb

// List collections
show collections

// Find documents
db.mycollection.find()

// Check server status
db.runCommand({ ping: 1 })
```

## Viewing container logs

DocumentDB container logs can help diagnose startup and runtime issues:

1. **Aspire Dashboard:** Click on the DocumentDB resource and switch to the "Logs" tab.
2. **Docker CLI:**
   ```bash
   docker ps | grep documentdb
   docker logs <container-id>

   # Follow logs in real-time
   docker logs -f <container-id>
   ```

## Known limitations

- **Health readiness is gateway readiness.** The built-in authenticated MongoDB health check does
  not prove that DocumentDB `0.116.0` one-shot initialization scripts have completed.
- **No built-in backup/restore.** For development data, use `WithDataVolume()` for persistence. For important data, use `mongodump` / `mongorestore` manually.
- **Single server only.** The extension does not support replica sets or sharded clusters. It runs a single DocumentDB container intended for local development.
