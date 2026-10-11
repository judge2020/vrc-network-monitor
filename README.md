# VRChat Network Monitor

Windows WinForms telemetry monitor with an ordinary-permission frontend and a temporary elevated capture helper. The frontend stays usable by VR desktop interaction tools. Closing it stops the helper and its captures. No service or startup task is installed.

## Build and run

On Windows x64 with the .NET 9 SDK:

```powershell
.\build.ps1
.\publish\VrcNetworkMonitor.exe
```

The published app requires the .NET 9 Desktop Runtime. Launch normally and approve the helper's UAC prompt. If declined, use **Start helper** to retry. Running the frontend as administrator is rejected. PresentMon 2.6.0 is downloaded by the build script with SHA-256 verification; its license and the TraceEvent license are included.

## Measurements

- Present FPS, CPU frame interval, CPU busy time, and GPU frame duration through PresentMon ETW. Two-second means for the dominant swap chain. These may describe the desktop mirror, not headset or compositor timing.
- CPU frame interval includes wait. GPU duration includes GPU busy and wait. CPU busy is separately selectable; comparing durations alone does not establish a CPU/GPU bottleneck. No arbitrary frame-time maximum is presented as a hardware limit.
- Process CPU utilization (100% equals one logical core) and working-set RAM.
- Process network receive/transmit rates, matching local OSC UDP receive events and largest datagram, plus ETW event loss. Network rates include asset/API traffic. ETW loss is not network packet loss.
- Build-specific internal read-only fields, guarded by an exact GameAssembly hash. Live internal access was denied on the investigated client, including with elevation. These values remain unavailable; the pointer profile has synthetic tests, not verified live reads. No injection, writes, or protection bypass.
- VRChat's manual `stats.json` export in a separate historical viewer. Its measured maximum is not a configured capacity. Its `gpu_frame_time` field is not used as a hardware GPU timer.

## OSC status

Enable OSC in VRChat, check **Send status above avatar**, and select statistics. Broadcast starts off each launch. Messages use `/chatbox/input` on localhost (default port 9000), without notification sound. Unavailable measurements are omitted.

**Average status** selects a rolling arithmetic mean of monitor samples over **1s / 5s / 10s / 15s**. It warms up using available samples, excludes other process IDs, and does not revive currently unavailable measurements. Frame stats already contain PresentMon's two-second aggregation, so averaging them adds smoothing. Live grid values are unaffected.

Messages are limited to 144 characters and **at least five seconds apart**, including across app instances/restarts using a per-user file lock and timestamp gate. Averaging does not change the send interval. Pausing stops capture requests and OSC sends while maintaining helper heartbeats. UDP delivery is not acknowledged.

## Privilege and lifetime

The UI creates a random, current-user-restricted named pipe and launches the same executable with `--daemon` through UAC. Both endpoints verify the other's OS-reported PID; the helper also checks the parent's start time. The elevated client disallows impersonation. Requests contain only a protocol version, OSC port, and pause state, with bounded JSON frames. No arbitrary elevated file, memory, or command API is exposed.

The helper exits on parent death, pipe disconnect, or ten seconds without a request. It disposes its own uniquely named ETW sessions and PresentMon child. Clean shutdown may take several seconds. Nothing is installed persistently. The frontend sends OSC itself without elevation.

## Validation

`build.ps1` runs self-tests for memory guards, OSC encoding and cooldown, CSV timing, IPC framing/validation, native export parsing, and averaging. For a real Windows/UAC lifecycle check:

```powershell
.\publish\VrcNetworkMonitor.exe --helper-check --out "$env:TEMP\vrc-helper-check.json"
```

This collects twelve samples, checks paused heartbeats for eleven seconds, disconnects, and checks helper exit. Diagnostic output can contain local/session details; review it before sharing. `--ui-smoke --out screenshot.png` captures the frontend without starting a helper.

See [investigation findings](docs/investigation.md) for the distinction between local OSC and remote avatar networking. Private session dumps and proprietary binary/disassembly evidence are excluded from this repository.

## Automatic releases

Pushes to the default branch (`main`) build and run self-tests on Windows, then publish a ZIP using GitHub CLI. Each release has a `build-<full commit SHA>` tag targeting the built commit, its subject as the title, and its complete commit message as the notes. Pull requests and other branches only build.

New pushes cancel earlier runs on that branch. The final publishing step queries the current branch head and skips release creation if its commit was superseded; API errors stop publication. Checking the ref and creating a release are separate GitHub API operations, so a push arriving after the check can still race publication. Rerunning a published commit cannot create another release with the same tag.
