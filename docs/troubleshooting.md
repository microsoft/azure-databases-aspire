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
1. Verify network connectivity: `docker pull ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.116.0`
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

### The container image changed after it was sealed

**Symptom:** starting the resource fails with an `InvalidOperationException` such as:

```
DocumentDB resource 'documentdb' changed the container image it will run after this
package sealed it: the image reference it will run changed. Aspire snapshots a
container's image into the DCP container spec while it prepares resources, ...
Sealed reference: 'ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.111.0'.
Current reference: 'ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.116.0'.
```

The clause after the colon says what changed: `the image reference it will run changed`,
`the container build it is produced by was added, removed or replaced`, or `the release
the image names changed`.

**Cause:** Aspire composes the image reference once, while it prepares the
application's containers, and writes it into the DCP container spec. That happens
before endpoints are allocated and before `BeforeResourceStartedEvent` is published,
so an image chosen after that point is only visible to the application model — the
container runtime still launches the earlier one, while this package's version floors,
data-directory interlock and declared-volume advice judge another release. Rather than
let the model and the running container disagree in either direction, the integration
refuses the run.

**Solution:** Choose the image while the application model is being built:

```csharp
var db = builder.AddDocumentDB("documentdb")
                .WithDocumentDBVersion(DocumentDBVersion.V0_116_0);
```

`WithImageTag(...)`, `WithImageRegistry(...)`, `WithImage(...)`, `WithImageSHA256(...)`
and `WithDockerfile(...)` are all still honoured anywhere during model building,
including from a `BeforeStartEvent` subscriber. Re-applying the same image is not a
change. Only a change made from a `ResourceEndpointsAllocatedEvent` /
`BeforeResourceStartedEvent` subscriber, or from a lifecycle hook that runs after this
package's, is refused. Publish mode is unaffected: no container is prepared, so the
manifest is written from the model as usual.
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
1. **Container image is older than `0.114.0`.** Images up to and including `0.113.0` always enforced TLS on the gateway and rejected plain connections, regardless of the `TLS_MODE` setting. Use the default (`0.116.0`) image tag, another `0.114.0` or newer image, or keep TLS enabled in the connection string.
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
validated. Use `WithBindMount(...)`, `WithVolume(...)`, and `WithEnvironment(...)`; harmless known
runtime options continue to work.

Diagnostics may name a known option or package-owned environment variable, but never repeat an
operand or value. Image names, rootfs paths, mount specs, environment values, URIs, credentials,
deferred resolutions, and failed-resolution exception text are deliberately omitted.

All of these are rejected on every DocumentDB resource — see
[A container-runtime argument is rejected](#a-container-runtime-argument-is-rejected) — and this
message is the wrapper's own wording for them, with two additions it owns: an option neither
runtime documents, and a required value that is missing, are rejected rather than passed through.

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

The wrapper records what its answer depended on and compares it at the last points the app host runs
unconditionally: a container-runtime-arguments callback in a run, and — in a publish —
`BeforePublishEvent` and again while the resource is being serialized into the manifest.
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

On Docker Desktop, prefer `WithDataVolume()` — see [Bind-mounted data fails to restart on Docker Desktop](#bind-mounted-data-fails-to-restart-on-docker-desktop).

### Bind-mounted data fails to restart on Docker Desktop

**Symptom:** With `WithDataBindMount()`, the first run works. Every later run leaves the DocumentDB resource unreachable — connections time out or the TLS handshake ends with `Received an unexpected EOF or 0 bytes from the transport stream` — and the container log contains:

```text
pg_ctl: could not start server
[POSTGRES] FATAL:  data directory "/data" has wrong ownership
[POSTGRES] HINT:  The server must be started by the user that owns the data directory.
```

**Cause:** PostgreSQL refuses to start unless the data directory's owner is the user starting the postmaster. The DocumentDB container establishes that by running `chown` on `DATA_PATH` and starting the postmaster a few milliseconds later. Docker Desktop's host file sharing applies `chown` on a bind mount asynchronously: **measured on macOS with VirtioFS**, `stat` inside the container keeps reporting the previous owner for roughly a second afterwards, so the postmaster reads a stale owner and aborts. The first run is unaffected because `initdb` runs for several seconds in between. A restart has no `initdb`, so it fails every time.

Docker Desktop on Windows and on Linux is expected to be affected in the same way, since both share host paths through the same VM file-sharing design, but that has not been measured here; whether a particular Windows setup is affected can depend on how the host path is shared. Named volumes do not go through that layer, and a bind mount on a native container engine (a Linux host running Docker Engine, which is what CI uses) is an ordinary mount, so neither is affected — the end-to-end test requires the restart to succeed there and only tolerates the failure when `docker info` reports `Docker Desktop`.

**Solutions:**
1. **Use `WithDataVolume()`** instead. It is the persistence mechanism the DocumentDB image documents and tests, and it is unaffected by this.
2. **Need the files on the host?** Copy them out of the volume when you need them (`docker run --rm -v <volume>:/data -v "$PWD":/out alpine tar -C /data -cf /out/data.tar .`) rather than running the database directly on a bind mount.
3. **On a native container engine**, `WithDataBindMount()` restarts normally; nothing needs to change.

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

### "Another DocumentDB container is already using the data directory"

**Symptom:** A container exits immediately after start with:

```text
Error: another DocumentDB container is already using the data directory /data. Refusing to start: ...
```

Or the application fails to start with an `InvalidOperationException` saying two DocumentDB resources mount the same volume or host directory.

**Cause:** Two PostgreSQL instances on one data directory would corrupt it. From `0.116.0` the container claims the directory with an exclusive lock, so the second container refuses to start rather than taking over, and the container that already holds it keeps serving, unaffected. Images at or below `0.114.0` have no such lock — nothing refuses the second start, and the corruption happens silently — which is why the application-model check is stricter for them.

**Solutions:**
1. **Stop whatever already holds the directory.** A leftover `docker run` session, a second AppHost instance, or a container from a previous debug session that was not removed — `docker ps --filter volume=<volume-name>` finds it.
2. **Give each resource its own storage.** Two DocumentDB resources in one application model cannot use the same volume name or bind-mount source as their *data directory*; use `WithDataVolume(name: "<resource>-data")` per resource. Sharing a directory as read-only *input* (`WithInitData(...)`, `WithTlsCertificate(...)`) is fine.
3. **Do not run the AppHost twice against the same volume.** Only one instance can hold the data directory at a time.
4. **`WithExplicitStart()` is not an escape hatch on older images.** The pair is downgraded to a warning only when both resources resolve to a recognised `0.116.0`-or-later tag, where the container itself refuses the overlap. Otherwise it remains a hard failure.

### Read-only data volume or bind mount is rejected

**Symptom:** `WithDataVolume(isReadOnly: true)` or `WithDataBindMount(..., isReadOnly: true)` throws an `ArgumentException`; a read-only mount added with the raw `WithVolume`/`WithBindMount` APIs fails the resource start with an `InvalidOperationException`.

**Cause:** DocumentDB cannot run against a read-only data directory. The entrypoint takes ownership of it, and `initdb` must change its permissions before PostgreSQL can create the cluster or write WAL. Allowed through, the container logs `chown: changing ownership of '/data': Read-only file system` and `initdb: error: could not change permissions of directory "/data": Read-only file system` inside interleaved log streams, then waits a full minute before exiting with the misleading `PostgreSQL failed to start within 60 seconds` banner.

**Solution:** Mount the data directory writable. Read-only mounts are correct for *input* only — `WithInitData(...)` mounts seed scripts read-only at `/init_doc_db.d`, and `WithTlsCertificate(...)` mounts the certificate and key read-only.

### `DATA_PATH` is rejected at start

**Symptom:** starting the resource throws an `InvalidOperationException` saying the resource `sets DATA_PATH to '...'`, which resolves to the container root, reaches above it, or is not absolute.

**Cause:** the container runtime resolves `.`, `..` and repeated separators before it mounts, so `/data/..` is the container root — which Docker refuses outright (`invalid mount config for type "volume": invalid specification: destination can't be '/'`) — and `/../data` reaches above it. Neither is a directory that can hold a PostgreSQL cluster. The value checked is the effective one: `WithDataVolume()`/`WithDataBindMount(...)` and any `WithEnvironment("DATA_PATH", ...)` contribute in call order, and the last caller wins. An empty `DATA_PATH` is *not* rejected: the entrypoint applies `DATA_PATH=${DATA_PATH:-/data}`, so an empty value is the image's `/data` default and is treated as such.

**Solution:** set `DATA_PATH` to an absolute path below the container root, or leave it to `WithDataVolume()`/`WithDataBindMount(...)`, which mount the container default `/data`. The same rule is applied at the API boundary: `WithDataVolume(targetPath: "/data/..")` throws an `ArgumentException` while the model is being built.

### A mount target above the container root is rejected

**Symptom:** starting the resource throws an `InvalidOperationException` saying it `mounts a volume ... at '/../data', which reaches above the container root`.

**Cause:** Docker does **not** refuse that spelling. It clamps the target to the root and mounts on the clamped destination, so `-v name:/../data` inspects back as `Destination: /data` and collides with a plainly written `/data` as `Duplicate mount point: /data`. The mount therefore lands on a directory the call never named, and can silently become — or collide with — the DocumentDB data directory. (Only a target that clamps all the way to `/`, such as `/data/../..`, is refused by the daemon.)

**Solution:** write the resolved target. `WithVolume("data", "/../data")` means `/data`, so say `/data`. `WithDataVolume(targetPath: ...)` applies the same rule while the model is being built, as an `ArgumentException`.

### `--data-path` or `-d` is rejected

**Symptom:** starting the resource throws an `InvalidOperationException` saying it `passes the command-line argument '--data-path'`.

**Cause:** the container entrypoint accepts `--data-path` and documents it as "Overrides DATA_PATH environment variable" — it runs `export DATA_PATH=$1` while parsing arguments. That is a second channel for the same setting, and one the model does not see: the data directory would move to a path the environment never names, past the read-only, duplicate-mount and shared-data-directory checks, and past the mount that was supposed to back it. `-d` is reserved with it (today's entrypoint answers `Unknown option -d` and exits 1).

**Solution:** set the data directory through storage: `WithDataVolume()`, `WithDataBindMount(...)`, or `WithEnvironment("DATA_PATH", ...)`. Remove the argument.

### A command-line argument "is only known later"

**Symptom:** starting or publishing the resource throws an `InvalidOperationException` saying it `passes a command-line argument whose value is only known later ... in a position where the container entrypoint reads an option name`.

**Cause:** the token is a parameter or a `ReferenceExpression`, so its value does not exist yet. It could resolve to `--data-path`, which would move the data directory past every storage check, and the only way to rule that out would be to resolve the token a second time — duplicating the evaluation Aspire is about to make, and risking a secret ending up somewhere it does not belong.

**Solution:** write option names as literal strings. A deferred value is fine as an option's *operand* — `WithArgs("--log-level", level)` or `WithArgs("--password", password)` — because the entrypoint reads that position as a value, never as an option name. Note that an option taking no value (`--skip-init-data`, `--disable-extended-rum`) and an option that already carries its value (`--log-level=debug`) do not shelter the token after them.

### `DATA_PATH` from a parameter is rejected when publishing

**Symptom:** `aspire publish` (or manifest generation) throws an `InvalidOperationException` saying the resource `sets DATA_PATH to a value that is only known at deployment time, and also mounts storage`.

**Cause:** in publish mode a parameter is a manifest expression, not a path, so the data directory cannot be identified. The read-only, duplicate-mount and shared-data-directory rules would all be skipped silently, and a manifest that puts two DocumentDB resources on one data directory would be published without complaint. The value is deliberately not resolved: it belongs to the deployment, and a parameter may be a secret.

**Solution:** give `DATA_PATH` a literal container path — or leave it to `WithDataVolume()`/`WithDataBindMount(...)` — and use a parameter for the storage *source* if that is what varies per environment. Run mode is unaffected: there the value is resolved once and checked normally.

### A callback registered after the guard fails the resource

**Symptom:** the resource fails with an `InvalidOperationException` saying it `has a later environment callback registered after its data-storage guard` (or `command-line callback`).

**Cause:** the storage guard is installed while `AddDocumentDB` runs and moved back to the end of the environment pipeline by every DocumentDB configuration API and at every lifecycle phase up to the one immediately before the resource starts, so it sees the final `DATA_PATH`; its command-line rule is a step of the resource's single package-owned command-line callback, which gets the same treatment. Something is still behind it. Either another callback was appended after the last of those phases — a `BeforeResourceStartedEvent` subscriber registered after `AddDocumentDB`, or, in publish mode where no such event is published, an `IDistributedApplicationLifecycleHook` — or a raw `WithEnvironment(...)`/`WithArgs(...)` added after `AddDocumentDB` was read by a subscriber registered *before* `AddDocumentDB`, which gathers the pipeline before any phase can move the guard back. The guard cannot vouch for a configuration that is still changing, so the resource is failed rather than started on an unchecked data directory. The command-line message is shared with the `WithOpenTelemetryMetrics(...)` gateway wrapper, which needs the same last position for a different reason: `/bin/bash` reads its command from the first arguments, so a callback that ran after it could displace the whole wrapper.

**Solution:** make the configuration part of the application model — `WithDataVolume()`, `WithDataBindMount(...)`, `WithEnvironment("DATA_PATH", ...)`, `WithArgs(...)` — instead of adding it after the model is built, and finish building the model before anything reads the resource's configuration. Any `WithArgs(...)` written while the model is being built is fine, in any order and however it mutates the list. If a subscriber has to read the configuration, register that subscriber *after* `AddDocumentDB`, so this package has put its callbacks back in the last position first.

### A change made after the storage was checked fails the resource

**Symptom:** starting or publishing the resource fails with an `InvalidOperationException` saying it `was changed after its data directory ('/data') had already been checked`, followed by what changed — for example `a volume or bind mount was added, removed or changed`.

**Cause:** Aspire records each callback's result the first time it runs and reuses it for the rest of the run. Something built the resource's configuration early — `ExecutionConfigurationBuilder` (or the obsolete `GetEnvironmentVariableValuesAsync`) from an `IDistributedApplicationLifecycleHook` or an event subscriber — and then changed the resource. Storage lives in annotations, so a volume or bind mount added afterwards changes what the container really mounts without any rule running again. The guard records what it judged and compares it at the container-runtime-arguments callback in a run and while the resource is serialized in a publish, and fails the resource rather than starting it on a data directory nothing checked.

**Solution:** finish configuring the resource before anything reads its configuration, or make the change part of the application model (`WithDataVolume()`, `WithDataBindMount(...)`, `WithEnvironment("DATA_PATH", ...)`) while it is being built. Re-declaring the same storage is not a change: the mounts are compared by value, so replacing a mount with an identical one, or reordering them, is accepted.

### A change made while the manifest was being written fails the publish

**Symptom:** `aspire publish` fails and the pipeline reports an `InvalidOperationException` saying the resource `was changed while its manifest entry was being written`, followed by what changed — for example `a volume or bind mount was added, removed or changed`.

**Cause:** Aspire writes a container's `build` object, image, entrypoint and mounts before it evaluates the environment callbacks, and its bindings after them. A `WithEnvironment(...)` callback that adds, removes or replaces a mount, re-points an endpoint, swaps the image or the registry it is pulled from, or adds, replaces or edits the Dockerfile build definition therefore changes the resource halfway through its own manifest entry: the fields already written describe the resource as it was, and the rules judged the resource as it became. The entry cannot be taken back, so the publish is failed and the partly written manifest is abandoned rather than completed.

**Solution:** configure the resource while the application model is being built — `WithDataVolume()`, `WithDataBindMount(...)`, `WithEndpoint(...)`, `WithImage(...)`, `WithImageTag(...)`, `WithImageRegistry(...)`, `WithDockerfile(...)` and the build arguments and secrets that go with it — instead of mutating it from an environment callback. Writing environment values from an environment callback is unaffected, and so is re-declaring or reordering the same mounts, or re-declaring the registry it already had. A build definition swapped for an identically valued one is still reported: its Dockerfile can be generated by a factory, which cannot be compared by value.

### A registry change made while the manifest was being written fails the publish

**Symptom:** `aspire publish` fails and the pipeline reports an `InvalidOperationException` saying the resource `was changed while its manifest entry was being written: the exact image reference it publishes changed`.

**Cause:** the registry is not part of the release. `WithImageRegistry("mirror.example")` still names the same repository, the same tag and the same DocumentDB version, so every version floor and every storage rule answers exactly as before — but the `image` field of the manifest entry carries the whole composed reference, and Aspire wrote it before it evaluated a single environment callback. A callback such as `.WithEnvironment(_ => db.WithImageRegistry("mirror.example"))`, or a caller's own `WithManifestPublishingCallback(...)` that changes the registry while it writes, therefore publishes an entry pointing at the registry the resource was configured with while the model names another one, and every deployment reading the manifest pulls from the wrong host. The checkpoint records the exact reference Aspire composes — `registry/` prefix, `:tag` suffix and `@sha256:` form included — and compares it after the entry has been written.

**Solution:** call `WithImageRegistry(...)`, `WithImage(...)`, `WithImageTag(...)` and `WithImageSHA256(...)` while the application model is being built. Setting the registry to the value it already has is not a change. A resource built from a Dockerfile you own is unaffected: its entry carries a `build` object and no `image` at all, so its build definition — context, Dockerfile, stage, image name and tag, arguments and secrets — is what is compared instead.

### A TLS change made while the manifest was being written fails the publish

**Symptom:** `aspire publish` fails and the pipeline reports an `InvalidOperationException` saying the resource `was changed while its manifest entry was being written: whether its published connection string accepts an invalid TLS certificate changed`, or the same for `whether its published connection string uses TLS changed` or `the connection string it publishes changed`. For a database the message names the database resource and reads `whether it accepts an invalid TLS certificate changed`.

**Cause:** Aspire writes a resource's `connectionString` before it evaluates a single environment callback. A callback such as `.WithEnvironment(_ => db.AllowInsecureTls(false))` therefore changes the setting strictly after the string describing it has been written: the manifest would ship `tlsInsecure=true` while the final model says certificates must be valid, and every deployment reading it would skip certificate validation. Databases publish nothing but the string their parent server builds, so the same change moves every child too.

**Solution:** call `UseTls(...)` and `AllowInsecureTls(...)` while the application model is being built. Setting a flag to the value it already has is not a change, and an environment callback that only sets environment variables is unaffected.

The same failure is reported when the writer that made the change was installed late — from a `BeforePublishEvent` subscriber registered after `AddDocumentDB`, or from one a lifecycle hook registers. Those run after this package has put its checkpoints back, and a `WithManifestPublishingCallback(...)` they install would otherwise displace the checkpoint entirely, since the publisher reads the last callback and no other. The checkpoints are put back once more from the publishing pipeline, which runs after every subscriber and before the manifest is written, so a caller's own writer still writes the entry and a change it makes while writing is still caught.

### A container-runtime argument is rejected

**Symptom:** starting the resource fails with an `InvalidOperationException` saying it `passes the container-runtime argument '--mount', which changes what the container mounts` — or the same for `-v`, `--volume`, `--volume-driver`, `--volumes-from`, `--tmpfs`, `--use-api-socket`, `--read-only`, for `'--env DATA_PATH'`/`'--env-file'`, which set an environment variable this package has already decided, for `'--entrypoint'`, or for an operand the runtime would read as the image. While `WithOpenTelemetryMetrics(...)` needs its compatibility wrapper the same readings are reported in the wrapper's own words; see [`WithOpenTelemetryMetrics()` rejects a container runtime option](#withopentelemetrymetrics-rejects-a-container-runtime-option).

**Cause:** `WithContainerRuntimeArgs(...)` passes its arguments straight to the container runtime ahead of the image, so they are not part of the application model and none of the storage rules can see them. A read-only mount added that way starts a container DocumentDB cannot initialise, a shared one puts two clusters on one data directory, and a replaced entry point or `DATA_PATH` silently discards everything that was checked.

**Solution:** declare storage in the application model — `WithDataVolume()`, `WithDataBindMount(...)`, `WithVolume(...)`, `WithBindMount(...)` — set the data directory with `WithEnvironment("DATA_PATH", ...)`, supply credentials through the `userName`/`password` parameters of `AddDocumentDB(...)`, and choose the image with `WithImageTag(...)`. Ordinary container-runtime arguments are unaffected: the line is parsed with the runtimes' own grammar, so `--label -v` passes a label and `--cap-add`, `--network`, `--memory`, `--pull`, `--platform`, `--dns`, `--ulimit`, `--sysctl` and `-it` reach the runtime untouched. A token whose value is only known later is rejected where the runtime reads an option name — it could resolve to `--mount` — but is fine as the operand of one, as in `WithContainerRuntimeArgs("--network", network)`.

The failure never repeats anything from the argument. It names the option, and for an environment option the variable, and nothing else — no mount source, no variable value, and no positional token, because that is where a credential would be.

### A Podman-only container-runtime argument is rejected

**Symptom:** starting the resource fails with the message above naming `--secret`, `--image-volume` or `--init-path` (storage), `--env-host`, `--env-merge`, `--unsetenv` or `--unsetenv-all` (environment), or `--rootfs` (what the runtime runs).

**Cause:** the app host drives Docker or Podman, and which one is behind a container-runtime argument is not something this package can know, so the arguments are read with the union of the two grammars. Podman's extra options reach exactly the places Docker's do: `--secret` mounts a file into the container or, with `type=env`, sets a variable; `--image-volume` decides whether the image's own `VOLUME` declarations become volumes, a tmpfs or nothing; `--init-path` bind-mounts a host binary; `--env-host` imports the entire host environment and `--unsetenv-all` clears every variable the image declares, either of which can move or erase `DATA_PATH`; `--env-merge` and `--unsetenv` rewrite or remove the one variable they name; and `--rootfs` makes the operand a root filesystem directory instead of an image.

**Solution:** the same as above — configure storage, the data directory, the credentials and the image through the application model. `--env-merge` and `--unsetenv` are rejected only when they name a variable this package owns (`DATA_PATH`, `USERNAME`, `PASSWORD`), so `--unsetenv TZ` and `--env-merge TZ=${TZ}-utc` pass through, and an explicitly disabled value-less option (`--env-host=false`, `--read-only=false`) passes through as well. Ordinary Podman configuration — `--tz`, `--umask`, `--sdnotify`, `--uidmap`, `--gidmap`, `--authfile`, `--cert-dir`, `--seccomp-policy`, `--requires`, `--retry` — is unaffected.

### A container-runtime argument that mounts without naming a mount is rejected

**Symptom:** starting the resource fails with the message above naming `--storage-opt`, `--volume-driver`, `--chrootdirs`, `--ipc`, `--pod`, `--pod-id-file`, `--read-only-tmpfs`, `--systemd` or `--use-api-socket` as changing what the container mounts.

**Cause:** each of these creates, selects or alters storage without spelling a mount, and does it where no `ContainerMountAnnotation` exists for any rule to read. `--storage-opt` sets the storage driver's options for this container, which is the size and backing of the root filesystem the data directory sits on when nothing is mounted over it; `--volume-driver` chooses which driver supplies every `-v` volume; `--chrootdirs` makes the runtime bind-mount its own managed files into further directories inside the container; `--ipc` decides whose `/dev/shm` the container gets (`host` mounts the host's, `container:`*id* another container's); `--pod` and `--pod-id-file` join a pod, whose infra container brings namespaces and the pod's own volumes with it, and `--pod new:`*name* creates one; `--read-only-tmpfs` is what mounts the read-write tmpfs over `/dev`, `/dev/shm`, `/run`, `/tmp` and `/var/tmp` in a `--read-only` container; `--systemd` puts the container in systemd mode, which mounts tmpfs on `/run`, `/run/lock`, `/tmp` and `/var/log/journal` and mounts the cgroup filesystem; and `--use-api-socket` bind-mounts the runtime's own API socket and a synthesized credential file into the container.

**Solution:** declare storage in the application model — `WithDataVolume()`, `WithDataBindMount(...)`, `WithVolume(...)`, `WithBindMount(...)`. The value-less ones are rejected only when they are on: `--read-only-tmpfs=false` and `--use-api-socket=false` pass through, and so does `--systemd=false`, which is the one off spelling Podman reads for a three-valued option (`--systemd=0` is a value Podman rejects, so it is read as the option being set). None of the three consumes the token behind it, so a bare `--systemd` cannot hide a following `--mount=type=bind,...` as its operand; the cost is that the split `--systemd false` is rejected too, and `--systemd=false` is the spelling to use.

### "Directory /data exists but doesn't appear to contain a valid PostgreSQL data directory"

**Symptom:** A container using a bind-mounted data directory logs

```text
Warning: Directory /data exists but doesn't appear to contain a valid PostgreSQL data directory.
Use -c flag to force cleanup and re-initialization, or specify a different directory with -d.
```

then never becomes healthy and exits `1` about a minute later with `PostgreSQL failed to start within 60 seconds`.

**Cause:** The data directory is not empty but does not hold a PostgreSQL cluster (no `PG_VERSION` file). The container refuses to initialise over unrecognised contents rather than deleting them, and PostgreSQL never starts — so the only failure you see is the generic 60-second timeout banner. One stray file is enough. Common culprits are a `.gitkeep` committed so the empty directory survives in source control, a `.DS_Store` written by macOS Finder, or leftovers from an unrelated tool.

**Solutions:**
1. **Empty the host directory** (or point `WithDataBindMount(...)` at a genuinely empty one) and start again. Nothing was deleted, so anything you need is still there.
2. **Keep the directory out of source control** rather than seeding it with a placeholder file — add it to `.gitignore` and let the first run create it.
3. **Prefer `WithDataVolume()`** when you do not need host access to the files: a fresh Docker volume is always empty.

### Initialization scripts do not re-run after being fixed

**Symptom:** A custom initialization script mounted with `WithInitData(...)` failed or was corrected, but later runs log `Custom data already initialized ...; skipping` or `a previous custom data initialization was attempted but its success was not recorded` and the data never appears.

**Cause:** DocumentDB `0.116.0` makes initialization one-shot per data directory. Markers under `<data-path>/.documentdb-local/` (`custom_data_attempted`, `custom_data_succeeded`, `sample_data_initialized`) live inside the persisted data, so they survive restarts, `docker compose down && up`, host reboots, and volume backups. The attempt marker is written *before* the first user script runs, so a non-idempotent script that failed part way is never retried — re-running it against half-seeded data caused restart loops.

**Solution:** Fix the scripts, then start against a **fresh** data directory — a new volume name, or `docker volume rm <name>` / an emptied bind-mount directory. Editing script contents alone never re-triggers initialization. Writing idempotent scripts avoids the problem in the first place.

> [!NOTE]
> On `0.114.0` and earlier the opposite is true: there are no markers, and the requested initialization runs on **every** container start. Against a persisted data directory the same scripts are replayed over data they already seeded, so duplicated documents — not missing ones — are the symptom to expect there. Idempotent scripts (and `WithoutSampleData()` for the built-in import) are the fix.

### Orphaned anonymous volumes accumulate

**Symptom:** `docker system df` shows a growing number of unused local volumes with random 64-character hexadecimal names, roughly the size of a PostgreSQL data directory each.

**Cause:** DocumentDB `0.116.0` and later declare `/data` as an image `VOLUME`. Any run that does not mount storage there gets a fresh anonymous volume, and removing the container without `-v` strands it. Neither Docker nor Aspire can un-declare an image volume. Images at or below `0.114.0` declare no volume and do not produce these.

**Solutions:**
1. **Mount your own storage on `/data`.** `WithDataVolume()` and `WithDataBindMount(...)` do this by default, which suppresses the anonymous volume. A non-default `targetPath` cannot: the declared `/data` volume is still created, and the resource logs a warning saying so (only on images known to declare it — recognised `0.116.0`-or-later tags).
2. **Reclaim what has accumulated:** `docker volume prune` (removes *all* unused local volumes — check `docker volume ls -f dangling=true` first).

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
- **`WithDataBindMount()` does not restart on Docker Desktop.** Docker Desktop's host file sharing applies ownership changes to bind-mounted paths asynchronously — measured on macOS/VirtioFS, and expected on its other hosts — and PostgreSQL reads the stale owner and refuses the data directory. Use `WithDataVolume()` there — see [Bind-mounted data fails to restart on Docker Desktop](#bind-mounted-data-fails-to-restart-on-docker-desktop).
- **No built-in backup/restore.** For development data, use `WithDataVolume()` for persistence. For important data, use `mongodump` / `mongorestore` manually.
- **Single server only.** The extension does not support replica sets or sharded clusters. It runs a single DocumentDB container intended for local development.
