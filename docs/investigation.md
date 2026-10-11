# Large instances, face tracking, OSC and networking

The main finding is that **remote avatar face updates are not incoming raw OSC packets**. A local tracking application sends OSC to its own VRChat client; that client applies avatar parameters and replicates applicable state through game networking. A room full of tracked avatars therefore stresses avatar processing/rendering and game networking, not one shared local OSC receive queue.

These findings are a static, build-specific reconstruction, not an official configurable tuning guide. The exact investigated GameAssembly SHA-256 is in `Profile.Hash`; another build must be investigated again. Compiled fallbacks do not prove the remotely supplied runtime configuration. The monitor did not obtain live internal fields on the investigated protected client: ordinary and elevated read/module enumeration attempts returned access denied. It deliberately does not work around that restriction.

| Observed value | Interpretation and limits of evidence |
| --- | --- |
| OSC packet buffer: 4096 bytes | One receive buffer/datagram size; not socket backlog, messages/second, or a remote-player allowance. |
| OSC callback array: 16, grows by 16 | Expandable allocation, not a fixed queue maximum. |
| OSC argument allocation: 32 | Arguments in a message, not number of face parameters or players. |
| OSC outgoing error cutoff: greater than 32 | Send-path failures, not incoming rate limiting. |
| `clientBPSCeiling`: 24576 fallback, clamp 2048–65536 | Remote configuration; internal rate-unit interpretation not independently verified. |
| `clientReservedPlayerBPS`: 7168 fallback, clamp 0–ceiling | Remote-configured reservation, not a per-player incoming OSC budget. |
| `maxQueuedCommands`: 100 fallback, clamp 20–4096 | Outgoing pressure threshold; not the local OSC receive queue. |
| `clientMaxDatagrams`: 10 | Outgoing send calls per eligible pass; not an incoming packets/second limit. |
| `clientSentCountAllowance`: 15 | Photon resend setting. |
| `clientDisconnectTimeout`: 10000 ms | Disconnect timeout. |

The native export can provide useful historical observations, but its displayed units need interpretation and its maxima are observed maxima. In the inspected implementation, `gpu_frame_time` brackets camera callbacks with wall-clock timing; it is not hardware GPU execution duration. The monitor instead uses [PresentMon's CPU/GPU metrics](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/README-ConsoleApplication.md), parsed by column name. [The pinned CSV implementation](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/PresentMon/CsvOutput.cpp) defines the v2 column names and millisecond timestamps.

For diagnosis, compare CPU busy, CPU interval (including wait), GPU duration, process network rates, and local OSC event rate over the same period. A high network rate alone is not proof that face tracking is responsible: asset downloads and API traffic contribute. GPU durations can overlap CPU work and may measure the desktop swap chain. Unavailable events must remain unknown rather than becoming zero. The monitor's 80% warning level is its own presentation policy, not a discovered VRChat limit.

Useful controlled comparisons are the same instance/camera with local tracking enabled versus disabled, and equivalent avatar visibility settings. No packet flood or modification of other players' clients is required. This tool reports evidence; it does not expose remote configuration as local tuning controls.
