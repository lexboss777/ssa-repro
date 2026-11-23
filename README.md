# ssa-repro — minimal repro for dotnet/runtime#126448

A minimal, self-contained reproduction of the **Mono interpreter SSA crash on .NET 10 iOS**
([dotnet/runtime#126448](https://github.com/dotnet/runtime/issues/126448)).

The crash is an `EXC_BAD_ACCESS (SIGSEGV)` on a `.NET TP Worker` thread:

```
KERN_INVALID_ADDRESS at 0x0000000100000019
_sigtramp -> mono_conc_hashtable_lookup -> interp_runtime_invoke
          -> mono_runtime_invoke_checked -> start_wrapper -> _pthread_start
```

It is not tied to any particular app code — a handful of BCL calls are enough.

## The trigger

Several thread-pool threads spin a tight `await Task.Yield()` loop that each re-run a
`ClientWebSocket.ConnectAsync` against a **real remote host**, while one task aborts that
socket every `SSA_ABORT_MS` ms so the workers keep reconnecting. The pool injects fresh
threads to service the continuations, and each fresh thread's first work item runs through
`start_wrapper -> interp_runtime_invoke` — the frame where the race hits.

Requirements, established empirically:

- **`UseInterpreter=true`** (set in the `.csproj`). The fault is in the interpreter's SSA pass.
- **A real remote host is required.** A loopback / local server does *not* reproduce, even a
  full WebSocket server that completes the 101 handshake and then drops the socket.
- **The WebSocket handshake is not needed.** The host can answer 404/400/502 — the client's
  `ConnectAsync` still exercises the code path.
- **TLS is not needed.** Plain `ws://` reproduces just as well as `wss://`.

Crash time scales with round-trip latency to the host (fewer round-trips/sec ⇒ slower):
a ~2 ms host crashes in ~3 s, a ~9 ms host in ~12 s, a ~17 ms host in ~15–20 s, and a
~50 ms host may not crash on a practical timescale.

## Layout

| File | What it is |
| --- | --- |
| `SsaRepro.iOS/Spin.cs` | The whole trigger: N concurrent ws-spin loops against the host + an abort loop. |
| `SsaRepro.iOS/SceneDelegate.cs` | Creates a window, then calls `Spin.Start()`. |
| `SsaRepro.iOS/{AppDelegate,Main}.cs`, `Info.plist` | Boilerplate. |

## Requirements

- macOS with Xcode, and .NET 10 SDK with the iOS workload
- An iOS **simulator** (x64 RID used below; arm64 also works on Apple Silicon)

## Run it

```
dotnet build SsaRepro.iOS -c Debug -r iossimulator-x64
xcrun simctl boot <UDID>                       # if not already booted
xcrun simctl install <UDID> SsaRepro.iOS/bin/Debug/net10.0-ios/iossimulator-x64/SsaRepro.iOS.app
xcrun simctl launch --console-pty <UDID> com.example.ssarepro
```

Wait for a `Got a SIGSEGV` line on the console (a few seconds to a minute, depending on the
host's latency). A genuine crash report also lands in `~/Library/Logs/DiagnosticReports/`
(`SsaRepro.iOS-*.ips`, containing `KERN_INVALID_ADDRESS at 0x0000000100000019` and the
`interp_runtime_invoke -> start_wrapper` stack).

> **Do not launch under a debugger.** With the Mono soft debugger attached (e.g. an IDE's
> default *Debug* launch, which passes `mlaunch … -monodevelop-port …`) the race does **not**
> fire and the app just spins forever. Observed 2026-11-23: the same binary that crashes in
> ~13 s when launched with a plain `simctl launch --console-pty` never crashes under an IDE
> debugger. In Rider use **Run** (Shift+F10), not **Debug** (F5). The architecture (arm64 vs
> x64 sim) and the host do not matter for this — only the debugger does.

## Knobs

All are environment variables on the app (e.g. `xcrun simctl spawn <UDID> launchctl setenv K V`).

| Env var | Default | Meaning |
| --- | --- | --- |
| `SSA_WS_SERVER` | `static.tildacdn.com` | Remote host. The URL built is `<scheme>://<host>/ws/`. |
| `SSA_WS_SCHEME` | `wss` | `wss` or `ws`. |
| `SSA_SPIN_WORKERS` | `8` | Number of concurrent ws-spin loops. |
| `SSA_ABORT_MS` | `2000` | Abort the socket every N ms so workers reconnect (`0` = never). |

The default host is a public CDN host chosen only because it is remote and low-latency; any
real remote host works.

## Workaround

Setting

```
MONO_INTERPRETER_OPTIONS=-ssa
```

disables the SSA pass and avoids the crash.
