# Orion Copy Watchdog

Configurable health monitor and automatic recovery service for Orion Copy infrastructure.
Configure it to monitor your own servers, allocators, game instances, services, processes,
ports, and health endpoints.

The core promise is:

```
MONITOR → DETECT → RECOVER → VERIFY
```

…without endless restart loops or deployment-specific hardcoding. You configure servers
in `watchdog.yml` — no source-code changes, no recompiling to add a server.

## What it does

- **Watches** HTTP endpoints, TCP ports, OS processes, Windows services, systemd units,
  and custom health commands — combined per service (`health_mode: all/any`).
- **Confirms** failures (configurable threshold; one failed ping never triggers a restart).
- **Recovers** gracefully: stop → wait → force only if needed → start → verify healthy.
- **Protects** against restart loops: max restarts in a window, cooldowns, exponential
  backoff, dependency holds, and a `FAILED` state that requires operator reset.
- **Reports** via CLI status tables, rotating logs, optional generic webhooks, and
  optional Orion Copy Server status reporting.

## Why it exists

Game servers freeze while their process stays alive; allocators die holding ports;
a backend outage makes every dependent look broken. This watchdog encodes the lessons
from allocator watchdog designs (notably: *never act on a guess* — verify liveness
before killing anything, always kill-before-replace so a wedged process can't hold its
port, and retire crash-looping deployments instead of spinning forever) as a generic,
config-driven tool anyone can point at their own infrastructure.

## Requirements

- .NET 8 runtime to run (installers handle this); .NET 8 SDK to build from source.
- Windows 10/11 / Server 2019+ **or** a modern Linux distribution (systemd).

## Quick start

1. Copy the example config and edit it for **your** servers:

   ```powershell
   Copy-Item watchdog.example.yml watchdog.yml; notepad watchdog.yml
   ```
   ```bash
   cp watchdog.example.yml watchdog.yml && nano watchdog.yml
   ```

   ```yaml
   services:
     - name: My Orion Backend
       checks:
         - type: http
           url: http://127.0.0.1:8080/health
       restart:
         type: systemd
         service_name: orion-copy-server
   ```

2. Validate:

   ```
   orion-watchdog validate-config
   ```

3. Start:

   ```
   orion-watchdog start
   ```

More examples: [`examples/`](examples/) (`simple-http.yml`, `windows-service.yml`,
`linux-systemd.yml`, `allocator.yml`, `full-stack.yml`).

## Installation

Windows (elevated PowerShell, from the repo root):

```powershell
.\install.ps1            # build + publish + install as Windows Service + start
.\install.ps1 -Status    # service + monitored state
.\install.ps1 -NoBuild   # re-apply config + restart after editing watchdog.yml
.\install.ps1 -Uninstall # stop + remove service (leaves data + config)
```

Linux:

```bash
sudo ./install.sh            # build + publish + systemd unit + start
sudo ./install.sh --status   # unit + monitored state
sudo ./install.sh --no-build # re-apply config + restart after editing
sudo ./install.sh --uninstall
```

Installers never overwrite an existing `watchdog.yml`, validate the config before
(start/re)starting, lock down config file permissions, and configure automatic
restarts for the watchdog itself (`sc failure` actions on Windows,
`Restart=on-failure` on Linux).

## Configuration

`watchdog.yml` (gitignored — may contain private hostnames; only
`watchdog.example.yml` is tracked). JSON is also accepted (`--config file.json`).
Config resolution order: `--config` flag → `ORION_WATCHDOG_CONFIG` → `./watchdog.yml`
→ system location (`%ProgramData%\OrionCopyWatchdog\` / `/etc/orion-copy-watchdog/`).

Global settings (`watchdog:`):

| Key | Default | Purpose |
|---|---|---|
| `check_interval` | `10s` | probe cadence per service |
| `failure_threshold` | `3` | consecutive failures before UNHEALTHY |
| `recovery_timeout` | `60s` | cap for stop → start → verified |
| `restart_cooldown` | `30s` | quiet period after a restart |
| `startup_grace` | `60s` | fresh processes get time to boot |
| `state_directory` | `./state` | persisted counters/state |
| `log_level` / `log_file` | `info` / unset | `debug\|info\|warn\|error`; rotating file log |
| `restart_policy.max_restarts` / `.window` | `5` / `10m` | crash-loop circuit breaker |
| `backoff.enabled|initial|multiplier|max` | `true` / `5s` / `2` / `2m` | exponential restart delays |
| `auto_retry_failed` | `false` | leave FAILED automatically once the window slides |

Durations look like `500ms`, `10s`, `5m`, `2h`, `1d` (bare numbers mean seconds).

Secrets stay out of the file: `api_key: ${ORION_WATCHDOG_API_KEY}` (with optional
default: `${VAR:-fallback}`). Only simple substitution — no expression evaluation.
Referencing an unset variable without a default is a validation **error**.

## Health checks

| `type` | Needs | Meaning |
|---|---|---|
| `http` | `url`, `expected_status` (default 200), `timeout`, optional `contains`, `method`, `headers` | GET/POST returns expected status (and body substring) |
| `tcp` | `host`, `port`, `timeout` | a TCP connection can actually be established |
| `process` | `process_name` | an OS process with that name is alive |
| `windows_service` | `service_name` | the Windows service status is Running |
| `systemd` | `service_name` | `systemctl is-active` succeeds |
| `command` | `command`, `timeout` | exit code 0 = healthy, non-zero = unhealthy |

Combine them per service:

```yaml
checks:
  - { type: process, process_name: OrionCopyServer }
  - { type: http, url: http://127.0.0.1:8080/health }
health_mode: all   # every check must pass (default); use `any` for either-is-fine
```

A live process is **not** health: with `health_mode: all`, a wedged-but-alive server
still counts as down. The `command` check is an explicit advanced feature for local,
trusted scripts — there is no remote shell and no unauthenticated command API.

## Restart strategies

| `restart.type` | Needs | Behavior |
|---|---|---|
| `process` | `executable`, `arguments?`, `working_directory?` | graceful close → kill after `graceful_timeout` → launch |
| `windows_service` | `service_name` | native ServiceController stop/start |
| `systemd` | `service_name` | `systemctl stop/start` |
| `command` | `command` (or `start_command` + `stop_command?`) | run your local restart script |
| `http` | `url`, `method?`, `expected_status?`, `authorization_env?` | call an authenticated management endpoint you configured |
| `none` | — | monitor only: stays `UNHEALTHY` without burning restart budget; recovery needs an operator |

Recovery always verifies: after starting, health checks must pass within
`recovery_timeout` or the recovery is marked failed and the loop guard counts it.

## Restart-loop protection & backoff

- `max_restarts` in `window` (sliding): exceeding it marks the service `FAILED`
  (manual intervention required) and stops restarting until `reset`, the window
  slides past with `auto_retry_failed: true`, or an operator-triggered restart.
- Counters persist in `state/services.json`, so rebooting the watchdog doesn't
  reset the circuit breaker.
- `restart_cooldown` separates consecutive attempts; exponential backoff grows the
  delay per consecutive restart (first restart is immediate, then `10s → 20s → 40s …`, capped).

## Dependencies

```yaml
- name: Allocator
  depends_on: [Orion Backend]
- name: Arena Server
  depends_on: [Allocator]
```

A service only restarts when **all** its dependencies are `HEALTHY`. If the backend
is down, dependents hold (logged) instead of restart-storming. Recovery order
therefore follows the chain automatically. Cycles and unknown names are validation
errors.

## Allocator support

The watchdog has no allocator-specific code — it exposes generic primitives that
fit allocator deployments: HTTP health + TCP agent-port checks in one service,
heartbeat freshness via a `command` check (exit 0 = fresh), dependencies so game
servers hold while the allocator is down, and `dynamic` lifecycle for spawned
instances (below). See [`examples/allocator.yml`](examples/allocator.yml).

## Dynamic vs persistent instances

```yaml
lifecycle: persistent  # backend, allocator, dashboard, lounge (default)
lifecycle: dynamic     # allocator-spawned matches, temporary lobbies
```

- **persistent**: an unexpected stop triggers restart.
- **dynamic**: a stopped process is *expected* (match ended) → state `STOPPED`, no
  restart, no noise. If the process is alive but app-unhealthy (wedged mid-match),
  it still recovers. Set `restart_on_exit: true` on a dynamic service to also
  restart vanished processes.

## Health states

`STARTING → HEALTHY ⇄ DEGRADED → UNHEALTHY → RECOVERING / BACKOFF → HEALTHY`,
plus `STOPPED` (expected dynamic stop), `DISABLED` (paused/disabled),
`FAILED` (restart limit; needs `reset`).

## CLI

```
orion-watchdog start                  # run the daemon (console/service/systemd)
orion-watchdog status [name]          # table: state, uptime, restarts, last message
orion-watchdog list
orion-watchdog check [name]           # one-shot probes now (no daemon needed)
orion-watchdog restart <name>         # immediate graceful restart + verify
orion-watchdog pause <name>           # stop monitoring (DISABLED)
orion-watchdog resume <name>          # resume monitoring
orion-watchdog reset <name>           # clear counters (use after fixing FAILED)
orion-watchdog reload                 # reload watchdog.yml: added services start now,
                                    # removed ones stop, healthy ones are untouched
orion-watchdog validate-config        # explain exactly what is wrong (with typo suggestions)
orion-watchdog version
```

Example:

```
Orion Copy Watchdog

NAME                         STATE        UPTIME       RESTARTS  LAST MESSAGE
Orion Backend                HEALTHY      2d 04h       0         all checks passed
Allocator                    HEALTHY      2d 04h       1         all checks passed
Persistent Lounge            HEALTHY      18h 12m      2         all checks passed
Arena Match                  STOPPED      -            0         dynamic instance stopped (expected)

Last recovery: Allocator restarted successfully 7h ago
```

Control commands (`restart/pause/resume/reset/reload`) are delivered to the running
daemon through `state/control/` request files; no network listener, no open ports,
no authentication surface to misconfigure.

## State persistence

`state/services.json` (atomic writes): state, failure counters, window counters,
backoff, timestamps (last check/success/failure/restart/recovery), pause flags.
`status` reads this file, so it works from any shell. Deleting it is safe — the
watchdog starts fresh.

## Logging

Structured lines, UTC ISO-8601:

```
2026-10-05T20:15:00Z INFO  backend health-check passed
2026-10-05T20:15:10Z WARN  allocator health-check failed attempt=1/3
2026-10-05T20:15:30Z ERROR allocator declared unhealthy
2026-10-05T20:15:31Z INFO  allocator restart initiated
2026-10-05T20:15:43Z INFO  allocator recovered
```

Console plus optional rotating file logs (`log_file`, 10 MB × 5). Every line passes
through secret redaction (Authorization headers, `*_key/token/secret/password`
values, Bearer tokens, URL credentials). Webhook/reporting URLs are logged
host-only.

## Webhook notifications

Generic JSON POST to any receiver (no provider-specific code in core):

```yaml
notifications:
  webhook:
    enabled: true
    url: ${ORION_WATCHDOG_WEBHOOK}
```

Events: `service_down`, `restart_started`, `service_recovered`, `restart_failed`,
`restart_limit_reached`, `watchdog_started`. Payload:
`{event, service, state, timestamp, message}` (snake_case).

## Orion Copy Server reporting (optional)

Off by default — the watchdog is fully useful without it. When enabled, it POSTs a
status summary every `interval`:

```yaml
reporting:
  enabled: true
  api_url: ${ORION_API_URL}
  api_key: ${ORION_WATCHDOG_API_KEY}
  interval: 60s
```

Expected server contract: `POST {api_url}/api/watchdog/status` with
`Authorization: Bearer {api_key}` and body
`{watchdog, timestamp, services: [{name, state, restarts, last_check, last_recovery}], failed_services: [...]}`.
Implement that endpoint on your backend (see `Reporting/StatusReporter.cs`); 4xx/5xx
and network errors are logged without the key and retried next interval.

Only these environment variables exist (see [`.env.example`](.env.example)):
`ORION_WATCHDOG_CONFIG`, `ORION_WATCHDOG_LOG_LEVEL`, `ORION_API_URL`,
`ORION_WATCHDOG_API_KEY`, `ORION_WATCHDOG_WEBHOOK`.

## Windows Service & Linux systemd

Covered by the installers. The same binary runs as a console app, a Windows Service
(`OrionCopyWatchdog`, recovery actions preconfigured), or a systemd unit
(`orion-copy-watchdog.service`, `Restart=on-failure`, hardened, dedicated
`orion-watchdog` user). Graceful shutdown handles SIGTERM / Ctrl+C / service stop;
one misconfigured service can never crash the monitoring loop.

## Security

- No network listener: CLI↔daemon control uses local files only.
- No remote shell, no unauthenticated restart API, no arbitrary remote commands.
- `command` checks/restarts run **local configured** commands only (flagged in
  validation output as a reminder to keep them trusted).
- Secrets come from the environment, are never logged, and never appear in status
  output. `watchdog.yml` is gitignored; only `.example` files are tracked.
- Installers set restrictive file permissions (Windows ACL / `0640` + service user).

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| `validate-config` fails | read the exact error lines; usually a typo'd key, bad duration, or unset `${VAR}` |
| Service flaps HEALTHY/DEGRADED | threshold too low or `check_interval` shorter than endpoint latency; raise both |
| `FAILED` after restarts | crash loop — fix the underlying service, then `orion-watchdog reset <name>` |
| Dependent never restarts | its dependency isn't HEALTHY — fix upstream first (by design) |
| Dynamic instance STOPPED | expected: the process ended; it resumes watching if the process reappears |
| Control command "daemon did not answer" | daemon isn't running — `orion-watchdog start` (only `check` works daemonless) |
| Windows service won't start | Event Viewer → check `watchdog.yml` path in the service args; run `validate-config` |

## Development

```
dotnet restore
dotnet build
dotnet test
dotnet publish src/OrionCopy.Watchdog -c Release
```

Architecture: `Config/` (load/substitute/validate) · `Health/` (`IHealthCheck` +
six probes) · `Restart/` (`IRestartStrategy` + six strategies) · `Core/`
(`ServiceMonitor` state machine, `WatchdogEngine`, backoff/window/dependency
policies) · `State/` (atomic JSON store) · `Cli/` · `Logging/` (redacting,
rotating) · `Notifications/` + `Reporting/`. Tests use fake checks/strategies —
never real processes.

## License

MIT — see [LICENSE](LICENSE).
