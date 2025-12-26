# Procvd

Procvd is a library for running and monitoring process groups with dependencies and resilient restarts. Think foreman/goreman, but with explicit groups, group sets, and pluggable output.

## Console app

By default Procvd reads a config file next to the binary. The config file name matches the binary name and uses the `.ini` extension. You can pass exactly one argument: the config path (absolute or relative). No other CLI parameters are supported.

Examples:

```bash
./procvd
./procvd ./procvd.ini
```

If the config file is missing, Procvd creates a sample file with comments and a basic structure.

## Quick start (INI)

Minimal config is just the path to a binary:

```ini
./bin/api
```

Load and run:

```csharp
using Itexoft.Threading;
using Procvd.Configuration;
using Procvd.Runtime;

var configPath = "procvd.ini";
await using var stream = File.OpenRead(configPath);

var loader = new IniProcessConfigLoader();
var config = await loader.LoadAsync(stream);

var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? Environment.CurrentDirectory;
var resolved = new ProcessConfigResolver().Resolve(config, baseDirectory);

var supervisor = new ProcessSupervisor(resolved);
await supervisor.RunAsync();
```

Stop by timeout:

```csharp
var token = CancelToken.None.Branch(TimeSpan.FromSeconds(30));
await supervisor.RunAsync(token);
```

## Quick start (JSON)

```csharp
using Procvd.Configuration;
using Procvd.Runtime;

await using var stream = File.OpenRead("procvd.json");
var loader = new JsonProcessConfigLoader();
var config = await loader.LoadAsync(stream);

var baseDirectory = Path.GetDirectoryName(Path.GetFullPath("procvd.json")) ?? Environment.CurrentDirectory;
var resolved = new ProcessConfigResolver().Resolve(config, baseDirectory);

var supervisor = new ProcessSupervisor(resolved);
await supervisor.RunAsync();
```

## Configuration model

### `ProcessConfig`

- `Defaults` (`ProcessSettings`) - base settings for all groups.
- `Groups` (`IReadOnlyDictionary<string, ProcessGroupConfig>`) - process groups.
- `GroupSets` (`IReadOnlyDictionary<string, ProcessGroupSetConfig>`) - group sets with shared settings and dependencies.

### `ProcessGroupConfig`

- `DependsOn` - dependencies on groups or group sets.
- `Settings` - group settings (`ProcessSettings`).
- `RestartMode` - `Group` or `Process`.
- `RestartPolicy` - restart policy (`ProcessRestartPolicy`).
- `Processes` - group processes (`ProcessConfigItem`).

### `ProcessGroupSetConfig`

- `Groups` - groups included in the set.
- `GroupSets` - nested group sets.
- `DependsOn` - set dependencies.
- `Settings` - set settings (`ProcessSettings`).

### `ProcessConfigItem`

- `Command` - command (shell).
- `Path` - path to executable.
- `Settings` - process settings.
- `Enabled` - enabled or disabled (default `true`).

### `ProcessSettings`

- `Args` - argument list (appended during merge).
- `Env` - environment variables (`key -> value`, later values override earlier ones).
- `WorkingDirectory` - working directory (last non-null wins).
- `OutputMode` - `inherit` (default, output goes directly to console) or `file` (output is written to a file and tailed in chunks).
- `OutputDirectory` - logs directory (default `procvd-logs` next to the config file).
- `OutputMaxBytes` - log size limit in bytes (default 32MB).
- `OutputMaxFiles` - number of files in rotation (default 3, including current).

When `output = file`, rotation happens at process start: if the current log exceeds the limit, it is moved to an archive. During runtime the active log is owned by the process, so it is not renamed.

Sizes can be in bytes or with `kb`, `mb`, `gb` suffixes.

Default log path: `procvd-logs/<group>/<process>.log`.

To disable rotation, set `output_max_bytes = 0`.

### `GroupRestartMode`

- `Process` - restart only the failed process.
- `Group` - restart the entire group when any process exits.

### `ProcessRestartPolicy`

- `MaxRestarts` - restart limit (null means unlimited).
- `RestartDelay` - delay between restarts.

### `IProcessConfigLoader`

Loader interface:

```csharp
ValueTask<ProcessConfig> LoadAsync(Stream stream, CancelToken token = default);
```

Implementations:

- `JsonProcessConfigLoader` - JSON via `System.Text.Json`.
- `IniProcessConfigLoader` - INI with minimal syntax for fast starts.

Extras:

- `JsonProcessConfigLoader.CreateDefaultOptions()` - default `JsonSerializerOptions` (case-insensitive, comments, trailing commas).

### Resolving

`ProcessConfigResolver`:

- Converts paths to absolute using `baseDirectory`.
- Builds dependencies and checks cycles.
- Computes group set membership.
- Merges settings: `Defaults` -> group sets (alphabetical) -> group -> process.
- Throws `ProcessConfigException` if a group has no enabled processes.

### Validation

`ProcessConfigValidator.Validate(ProcessConfig)` checks the config structure and throws `ProcessConfigException` on errors.

### Resolved models

After resolving:

- `ResolvedProcessConfig`
  - `BaseDirectory` - absolute base directory.
  - `Groups` - resolved groups (`ResolvedProcessGroup`).
- `ResolvedProcessGroup`
  - `Name`
  - `RestartMode`
  - `RestartPolicy`
  - `Dependencies`
  - `Processes` (`ResolvedProcess`)
- `ResolvedProcess`
  - `Key` (`ProcessKey`)
  - `ExecutablePath`
  - `DisplayPath` (relative to `BaseDirectory`)
  - `WorkingDirectory`
  - `Arguments`
  - `Environment`

## INI format

### Minimal file

```ini
./bin/api
```

Creates a `main` group and an `api` process. The command is executed via the system shell. The default group name can be set via `IniProcessConfigLoader(IniReaderOptions? options = null, string? defaultGroupName = null)`.

### `defaults` section

```ini
[defaults]
arg = --verbose
env.LOG_LEVEL = info
workdir = .
output = inherit
```

### Group section

```ini
[core]
./bin/core
api = ./bin/api --port 5000
depends = db
restart = group
restart_delay = 2s
restart_limit = 3
arg = --group
env.CORE = 1

process.api.env.PORT = 5000
```

Supported group keys:

- `depends` / `depends_on` - dependencies (space or comma separated).
- `restart` / `restart_mode` / `mode` - `group` or `process`.
- `restart_delay` - duration (`2s`, `500ms`, `00:00:02`).
- `restart_delay_seconds` - seconds as a number.
- `restart_delay_ms` - milliseconds as a number.
- `restart_limit` / `restart_max` / `restart_max_restarts` - restart limit.
- `arg`, `args`, `env.KEY`, `workdir`, `output`, `output_dir`, `output_max_bytes`, `output_max_files` - shared group settings.

Processes inside a group:

- Shell command: `./bin/api --flag`
- Named shell command: `api = ./bin/api --flag`
- No shell: `process.api.path = ./bin/api`, `process.api.args = --port=5000`
- Process overrides: `process.api.env.X`, `process.api.workdir`, `process.api.enabled`

### Process section

```ini
[process:core/api]
path = ./bin/api
args = --port=5000 --public
env.PORT = 5000
workdir = ./services/api
output = file
output_dir = ./logs
output_max_bytes = 10mb
output_max_files = 5
enabled = true
```

### Group set section

```ini
[set:backend]
groups = core, api
depends = base
arg = --backend
```

Supported group set keys:

- `groups` - groups inside the set.
- `sets` / `groupsets` - nested sets.
- `depends` / `depends_on` - dependencies for the set.
- `arg`, `args`, `env.KEY`, `workdir` - settings applied to all groups in the set.

### INI notes

- Keys and section names are case-insensitive for parsing, but group and process names are case-sensitive. Keep a consistent style.
- `args` is split by spaces or commas, quotes are supported: `args = "--name a b" --flag`.
- `env.KEY = null` removes the variable from the environment.
- If a process name is derived from a path, the filename without extension is used. Duplicates get suffixes `-2`, `-3`, etc.
- `command` and `path/args` are mutually exclusive. Group section values run via the shell (macOS/Linux: `/bin/sh -c`, Windows: `cmd /c`).

## JSON format

Structure matches the object model:

```json
{
  "defaults": {
    "args": ["--verbose"],
    "env": { "LOG_LEVEL": "info" },
    "workingDirectory": "."
  },
  "groups": {
    "core": {
      "dependsOn": ["db"],
      "restartMode": "Group",
      "restartPolicy": { "maxRestarts": 3, "restartDelay": "00:00:02" },
      "settings": { "args": ["--group"] },
      "processes": {
        "api": { "path": "./bin/api", "settings": { "args": ["--port=5000"] } }
      }
    }
  },
  "groupSets": {
    "backend": {
      "groups": ["core", "api"],
      "dependsOn": ["base"],
      "settings": { "args": ["--backend"] }
    }
  }
}
```

Note: JSON can specify `command` instead of `path/args` and it will execute via shell. `command` cannot be combined with `path` or `args`.

## Runtime API

### `ProcessSupervisor`

Main orchestrator. Takes `ResolvedProcessConfig` and starts groups in dependency order.

```csharp
var supervisor = new ProcessSupervisor(resolved, new ProcessSupervisorOptions
{
    Executor = new ProcessRunnerExecutor(),
    Output = new ProcessConsoleOutputSink(),
});

await supervisor.RunAsync();
```

Behavior:

- Groups are started in topological order.
- On group restart, dependent groups receive a restart request.

### `ProcessSupervisorOptions`

- `Executor` - `IProcessExecutor` implementation.
- `Output` - `IProcessOutputSink` implementation.

### `ProcessGroupSupervisor`

Controls a single group:

- `RunAsync(CancelToken)` - main run/restart loop.
- `RequestRestartAsync()` - force group restart.
- `Restarting` - `ProcessGroupRestartEvent` event.

### `ProcessKey`

Process identifier in the form `group/process`.

### `ProcessGroupRestartEvent` / `ProcessGroupRestartReason`

Group restart event and reason:

- `ProcessExit` - a process exited.
- `ExternalRequest` - external restart request.

### `IProcessExecutor`

Process execution contract:

```csharp
Task<ProcessExecutionResult> RunAsync(
    ProcessExecutionRequest request,
    IProcessOutputSink output,
    CancelToken token = default);
```

### `ProcessRunnerExecutor`

Default implementation that launches processes via `Itexoft.Processes.ProcessRunner`.

### `ProcessExecutionRequest`

Contains:

- `Process` (`ProcessKey`)
- `ExecutablePath`
- `DisplayPath`
- `WorkingDirectory`
- `Arguments`
- `Environment`
- `ShellCommand`
- `OutputMode`
- `OutputPath`
- `OutputMaxBytes`
- `OutputMaxFiles`

### `ProcessExecutionResult`

- `ExitCode` (null if cancelled or failed)
- `IsCancelled`
- `Exception` (when failed)
- `IsFaulted`

### `ProcessDependencyGraph`

Builds start order and dependency map.

## Output API

### `IProcessOutputSink`

```csharp
void Write(ProcessOutputLine line);
void WriteEvent(ProcessOutputEvent message);
```

### `ProcessConsoleOutputSink`

Writes to console and colors lines by process.

### `ProcessChunkedConsoleOutputSink`

Buffers output and flushes in time chunks per process.

### `ProcessOutputFormatter`

Builds lines like:

```
[2025-01-01T10:00:00.0000000Z] [group:core] [proc:api] [path:bin/api] [out] Started
```

Events:

```
[...][event:exited] [code:0]
```

### `ProcessOutputFormatterOptions`

- `TimestampFormat` - time format (default `"O"`).
- `UseUtc` - use UTC (default `true`).

### `ProcessOutputLine`

- `Process` (`ProcessKey`)
- `DisplayPath`
- `Stream` (`StdOut` / `StdErr`)
- `Line`
- `Timestamp`

### `ProcessOutputEvent`

- `Process`
- `DisplayPath`
- `Kind` (`Starting`, `Exited`, `Restarting`, `Stopped`, `Failed`)
- `Timestamp`
- `ExitCode`
- `Message`

## Errors

Configuration errors and dependency cycles throw `ProcessConfigException`.

## CancelToken

All APIs use `Itexoft.Threading.CancelToken`. To integrate with BCL APIs that require `CancellationToken`, use `Bridge(out var token)`:

```csharp
using (cancelToken.Bridge(out var cancellationToken))
{
    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
}
```
