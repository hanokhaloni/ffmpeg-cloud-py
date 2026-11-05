# StreamBench design

StreamBench (FFmpeg Stream & Recording Reliability Benchmark) is an end-to-end reliability tester for FFmpeg RTSP → HLS + MP4 pipelines on Windows Server. It verifies **ingestion**, **production** and **consumption** independently. The primary metric is **verified media coverage**: decoded, accounted-for media ÷ expected media. Statuses are PASS / WARN / FAIL / UNKNOWN, and UNKNOWN is never PASS.

Sections 1–36 of the original design (system, filesystem, network, time, process monitoring, HLS, recording, profiles, resources, fault injection, reporting) are implemented as described in the [README](../README.md). This document records the **mandatory** requirements added in sections 37–45 and how each one is implemented.

> **The TUI and the multi-stream lab are first-class deliverables, not optional polish.**
> The default launch provides a bashtop/btop-like full-screen dashboard with resource history graphs, a selectable stream/process table, prerequisite status, incidents and selected-command details.
> `StreamBench lab` works with defaults: 4 independent RTSP publishers, 1 consumer per publisher, 10-minute duration, isolated local output directories, managed HTTP serving, and runtime HLS/recording validation.
> MediaMTX is used as a managed RTSP relay (or an explicitly configured external relay). StreamBench itself owns and supervises the publishers and benchmark commands. The PowerShell script is only a launcher.
> A scrolling log console is not an acceptable substitute for the dashboard, and a script that launches unmanaged FFmpeg processes is not an acceptable substitute for the in-application orchestrator.

---

## 37. Full-screen TUI, not a scrolling console

| Requirement | Implementation |
|---|---|
| Default launch: interactive full-screen dashboard | `lab`, `run` and `bench` open the dashboard whenever stdin/stdout are a terminal (alternate screen buffer, hidden cursor). It falls back to headless output when redirected. |
| 4 Hz rendering; collectors run independently | A dedicated UI thread renders at `ui.refreshHz` (default 4). Collectors (monitors, 1 Hz resource sampler, registry) run on their own tasks. |
| Five-minute resource history | The resource sampler runs at 1 Hz, with a ring buffer of `ui.historySeconds` (300) samples for the CPU, app-write and network sparklines. |
| Selection with arrows or `j`/`k` | `KeyMap`; selection is clamped to the visible (filtered/sorted) rows. |
| Sorting: CPU, stream ID, state, latency, validation backlog | `s` cycles the sort key (metrics sort worst-first), `r` reverses it. |
| Filtering: stream, output, incident severity | `/` text filter (stream, source, profile, state), `o` output filter (HLS or REC issues), `v` incident severity. `g` groups rows by source or by profile. |
| Details: selected source, commands, outputs, evidence | `Enter` opens the detail pane: input URL, publisher and consumer commands (redacted), the exact `ArgumentList`, output directories and URLs, coverage, attribution, and recent classified log lines. |
| Command display: redacted, wrapped or horizontally scrollable | COMMAND panel: `←`/`→` (`h`/`l`) scroll it, `w` wraps it. Credentials are always masked. |
| Pause freezes the display only | `Space` stops taking new snapshots. Monitors, recording and validation continue (acceptance test #8). |
| Resize | Each frame reads the terminal size. A size change triggers a full redraw and a new layout without touching collection (acceptance test #7, manual pty test 140→100→60→140 columns). |
| Small terminal | Full layout ≥ 140×42. Compact layout ≥ 100×30, where the selected-stream detail replaces the lower panels. Minimal view below that. |
| Color plus explicit text labels | Every status is spelled out (PASS / WARN / FAIL / UNKN / LATE / BLOCK) and also colored. |
| Compatibility: ASCII fallback, `--no-color`, `--no-tui` | `--ascii` (or `ui.asciiFallback`) maps every glyph and every non-ASCII text character to ASCII. `--no-color` and `NO_COLOR` remove colors. `--no-tui` runs headless. |
| Exit: graceful shutdown, final reconciliation, report | `q` opens a confirmation overlay; `y` requests the ordered shutdown. The dashboard shows shutdown progress and the final verdict, restores the terminal, and prints the summary to the normal screen. |
| No scrolling noise | Child stdout/stderr are always redirected (FFmpeg and MediaMTX logs go to files). Kestrel logging is cleared. Nothing writes to the terminal while the dashboard owns it. |
| Single rendering owner consuming immutable snapshots | `DashboardStateProvider.GetSnapshot()` builds an immutable `DashboardSnapshot`. `Renderer` is a pure function (snapshot, view, size, glyphs) → canvas. `TerminalPresenter` writes only changed rows. |
| Overlays inside the state machine | Quit confirmation and help are overlays handled by `TuiController.Apply`; no nested console prompts. |
| Interfaces | `IDashboardStateProvider`, `ITuiController`, `IUiCommandDispatcher` (`src/FFmpegReliabilityBench.App/Tui/Model.cs`). |
| Renderer replaceable; never compromise supervision | The renderer is custom (no Spectre live display) behind those interfaces. Render exceptions are caught per frame, and the UI runs on its own thread. |

Layout (140×42):

```text
┌ StreamBench ─ LAB: local-4-streams ─ INDEPENDENT · parallel ─ elapsed … / … ─ phase Running ─ CURRENT: WARN ┐
│ CPU bar · RAM bar · Streams n/n · Owned procs · Validation queue                                             │
│ CPU sparkline · App write sparkline · Net sparkline                                                          │
│ Roles: Relay · Publishers(gen) · Consumers · Players · Validators · Total                                     │
├ PREREQUISITES ─────────────────┬ STORAGE / NETWORK ──────────────────────────────────────────────────────────┤
├ STREAM / COMMAND TABLE ────────┴─────────────────────────────────────────────────────────────────────────────┤
│ ID  Source  Kind  Profile  PID  CPU%  FPS  Speed  HLSseq  Age  HLS  REC  Bklg  State                         │
├ SELECTED: … ──────────────────────────────────┬ INCIDENTS ──────────────────────────────────────────────────┤
├ COMMAND [redacted; ←→ scroll, w wrap] ────────┴──────────────────────────────────────────────────────────────┤
│ 1 Overview  2 Streams  3 Checks  4 Storage  5 Logs  6 Lab                                                    │
└ ↑↓/jk Select  Enter Details  / Filter  s Sort … Space Pause UI  ? Help  q Stop & exit ───────────────────────┘
```

Views: 1 Overview, 2 Streams (stream table plus processes by role), 3 Checks (every result with a detail pane), 4 Storage, 5 Logs (event log with severity filter), and 6 Lab (startup state machine, sources/publishers, topology and strategy, and the effective configuration with the origin of every value).

## 38. Built-in multi-stream lab orchestrator

```text
                       StreamBench application
                  owns lifecycle and collects evidence
                                 |
           +---------------------+----------------------+
     RTSP relay             N publishers          M consumers
      MediaMTX                  |                      |
           ^------ RTSP --------+                      |
           +---------------- RTSP ---------------------+
                                                       |
                                              HLS + recordings
                                                       |
                                                HTTP + validators
```

- **Ownership.** Every process is started through `FFmpegRunner` or `ProcessUtil` and registered in `ProcessRegistry` with a role: **Relay, Publisher, Consumer (benchmark), Player, Validator (probe/decode worker), Tool** (capability discovery, fixture generation). CPU, memory and write bytes are sampled per process at 1 Hz and aggregated per role and in total. The dashboard's Streams view lists them; the report lists them per run.
- **Player** (the design's "browser worker") is an FFmpeg-based player. It plays one selected live stream in real time over HTTP and plays sampled completed recordings over HTTP with a seek. A real browser engine is not embedded.
- **Topologies.** Sources × consumers per source × profiles. Labels come from `LabRuntime.Classify`: `INDEPENDENT` (N×1), `FAN-OUT` (1×N, explicitly "readers of ONE source, not N independent cameras"), `MIXED` (N×K), `CAMERAS`, and `MIXED-SOURCES` (synthetic plus cameras).
- **Default:** 4 independent paths with one benchmark consumer per path.

## 39. Working defaults (no JSON edits)

| Setting | Default | Where |
|---|---|---|
| Publishers | 4 | `lab.publishers.count` |
| Consumers per publisher | 1 | `lab.consumers.instancesPerSource` |
| Source | prepared deterministic fixture `720p25-h264-aac` (1280×720, 25 fps, H.264 without B-frames, AAC 48 kHz stereo, keyframe every 1 s, 60 s loop) | `Fixture` |
| RTSP transport / endpoint | TCP / `127.0.0.1:8554` | `lab.relay` |
| Benchmark command | `dual-copy`: HLS (2 s, 6 entries, `temp_file`) + segmented MP4 (5 s, faststart, segment list), stream copy | `CommandTemplates` |
| Duration | 10 minutes | `lab.durationSeconds` |
| Consumer launch stagger | 500 ms | `lab.consumers.staggerMilliseconds` |
| Output | new application-owned run directory `streambench-runs/<session>/` | `lab.runRoot` |
| HTTP | loopback only, port 8088 | `lab.http` |
| Player | one live stream plus every 3rd completed recording per stream | `lab.validation` |
| Validation | probe + full decode of every segment and recording; bounded workers `max(2, cores/2)` | `lab.validation.maxDecodeWorkers` |
| Automatic restart | disabled (diagnostic mode) | n/a |
| Deep storage probe | before streaming (prerequisites phase), never during measurement | n/a |
| Camera clock | not applicable to synthetic sources | n/a |
| Source completeness | UNKNOWN unless ground-truth verification is enabled (not implemented) | n/a |

The fixture is generated once, before measurement, and published with `-c copy`, so four publishers cost almost no CPU. `live-synthetic` mode encodes a test pattern live instead; its encoder load is reported under the Publisher role. Fixture loop transitions are detected from publisher progress and recorded as `Fixture` events. Correlation labels incidents that coincide with a loop boundary as **EXPLAINED (expected)** rather than as camera faults. B-frames are disabled because RTSP carries no DTS, so B-frames force FFmpeg to guess timestamps, which produces spurious "non-monotonic DTS" noise that real cameras (typically without B-frames) don't cause.

## 40. Multiple command profiles in one run

- Templates are argument arrays (`CommandTemplate`). Built-in templates: `dual-copy` (default), `dual-transcode`, `original` (replica of the production command shape), `hls-copy`, `rec-copy`, `rec-fmp4-copy`. Aliases: `controlled-copy`, `controlled-transcode`, `original-command`. Custom templates go under `lab.templates`.
- Allowed placeholders: `{inputUrl} {streamId} {profileId} {instanceId} {runDir} {hlsDir} {recordingDir} {segmentListPath}`. They are expanded inside individual arguments; `{{`/`}}` are literal braces. Unknown placeholders, shells (`cmd.exe`, `powershell`), and output-path collisions across or within instances are rejected **before anything is launched**.
- Every managed consumer gets the instrumentation and capability rules: `-progress pipe:1`, `-stats_period` (when supported), level-tagged logs, a version-appropriate RTSP timeout (`-stimeout` on 4.x, `-timeout` on 5+), and removal of `temp_file` when unsupported.
- Strategies: **parallel** (all instances), **sequential** (one profile group at a time; each group is stopped and reconciled before the next starts), and **staged-load** (cumulative stages, default 1 → 2 → 4 … → all). The default is parallel for one profile. When several profiles are compared and no strategy is given, the default is sequential, so competing configurations are never silently benchmarked against each other. The strategy and the current group or stage are shown in the header and in the Lab view.
- `executableReference` selects the FFmpeg build per template (`executables` map), for version comparisons.

## 41. Lab configuration

`lab` and `ui` sections as specified (see `config/default.json`). Precedence: **built-in defaults < preset < config file(s) < command-line overrides**. Lists are replaced, never merged index by index. `ConfigLoader` records the origin of every effective value (`default`, `preset: …`, `config: file.json`, `command line`). The Lab view and the report show the value and origin for every key.

Presets: `local-4-streams` (default), `fan-out-4`, `mixed-4x3`, `live-synthetic-4`, `profile-compare`, `staged-8`, `cameras`.

## 42. Application commands

```text
StreamBench lab                                            # zero-edit default lab
StreamBench lab --streams 8 --duration 00:30:00
StreamBench lab --streams 4 --consumers-per-stream 3
StreamBench lab --profiles original,dual-copy --execution sequential
StreamBench lab --config .\config\nas-lab.json
StreamBench run --config .\config\camera-group.json        # real cameras / external sources
StreamBench print-commands [lab options]                   # every command, redacted, nothing started
StreamBench presets
```

If dependencies are missing, StreamBench lists the exact missing paths and exits with code 3. Nothing is downloaded or installed. Configurations can mix synthetic and camera sources (`lab.sources[].kind`), and the dashboard shows the kind of every source.

## 43. PowerShell launcher

`scripts/Start-StreamBenchLab.ps1` is a thin launcher. It resolves `app\StreamBench.exe` next to the script, forwards **only explicitly supplied** parameters (`-Streams`, `-ConsumersPerStream`, `-Duration`, `-Profile`, `-Config`, `-Preset`, `-Execution`, `-Headless`, plus any further StreamBench options), and exits with the application's exit code. It never starts FFmpeg or MediaMTX itself. `scripts/Publish-StreamBench.ps1` builds the `dist\` layout the launcher expects (self-contained `app\`, `config\`, `profiles\`, and optionally `tools\mediamtx\` from a local file).

## 44. Startup, readiness and cleanup

The startup state machine is exactly as specified and visible in the Lab view and the report:

```text
Resolve configuration → Validate paths and port availability → Discover capabilities → Run prerequisites → Prepare fixture
→ Start relay → Verify relay readiness → Start publishers → Verify media readiness per source → Start HTTP service
→ Launch consumers (staggered) → Validate first outputs → Steady measurement → Shutdown and reconciliation
```

- An open port is not readiness: each RTSP path must yield **decoded frames** (`ffmpeg … -f null` with frame counting) before its consumers launch.
- A failed publisher blocks only its own consumers. Other streams continue, and the run records a startup failure (LAB-007). `--fail-fast` stops everything at the first mandatory failure.
- If a publisher or the relay dies mid-run, dependent consumers' failures are prefixed with an **upstream attribution** (`[upstream: publisher s02 …]`).
- Busy ports (RTSP, RTP/RTCP, API, HTTP) are reported before anything starts, with the exact `address:port`. Their owners are never touched.
- Cleanup order: stop launching → stop consumers gracefully (`q`) → drain and reconcile outputs → finish bounded validation → stop publishers → stop the managed relay → stop HTTP → persist the report → restore the terminal. Only PIDs started by this run are ever stopped (never `taskkill /IM`). An external relay is never stopped. LAB-009 verifies that no owned process survived.

## 45. Acceptance tests (definition of done)

| # | Criterion | Evidence |
|---|---|---|
| 1 | `StreamBench lab` starts 4 independent publishers and 4 consumers with no JSON edits | `LabAcceptanceTests.Default_lab_…`; manual 10-minute zero-argument run |
| 2 | All managed processes appear by role | same test (snapshot roles: Relay, Publisher, Consumer, Player); Streams view |
| 3 | Selecting a stream shows its exact redacted publisher and consumer commands | same test (detail commands and ArgumentList); renderer tests |
| 4 | Isolated HLS and recording paths per consumer | same test; `CommandTemplateTests.Output_collisions_are_rejected` |
| 5 | A publisher failure affects only dependent consumers | `Publisher_failure_affects_only_its_dependent_consumers` |
| 6 | UI responsive under log floods and validation backlog | `Snapshots_stay_fast_under_log_floods`; `Dashboard_pause_resize_and_log_flood_…` (100k events, a single validation worker) |
| 7 | Terminal resizing doesn't crash collection or supervision | `Every_tab_overlay_and_size_renders_without_throwing`; dashboard test; manual pty test |
| 8 | Pausing the display doesn't pause recording or monitoring | `Pause_freezes_the_display…`; dashboard test |
| 9 | Ctrl+C stops only application-owned processes | `Ctrl_c_stops_only_application_owned_processes` (real SIGINT; a foreign FFmpeg survives) |
| 10 | Busy ports give a clear error; owners are not terminated | `Busy_ports_produce_a_clear_error…`; `Busy_tcp_port_is_reported…` |
| 11 | Generator load reported separately from consumer load | `Generator_load_is_reported_separately…` (LAB-006, live-synthetic) |
| 12 | Headless mode produces the same evidence and verdicts | `Headless_and_dashboard_runs_produce_the_same_evidence` |
| 13 | Profiles run sequentially or concurrently, with the strategy visible | `Fan_out_with_sequential_profiles…`; `Schedules_for_each_execution_strategy` |
| 14 | One source / many consumers is labelled fan-out | `Fan_out_is_never_labelled_as_independent_cameras`; fan-out lab test |
| 15 | The default lab produces HLS HTTP validation and completed-recording playback results | default lab test (HLS-008, HLS-009, LAB-008, REC-008) |

## Known gaps

- Source completeness (ground-truth verification of what the source actually sent) is not implemented, so it is always UNKNOWN.
- The player is FFmpeg-based; no real browser engine is embedded.
- CPU of very short-lived workers is sampled at 1 Hz, so it is a lower bound.
- The Windows-specific paths (Restart Manager, Credential Manager, Defender, w32tm, VT console mode) run in CI on `windows-latest` but have not been run on Windows Server itself.
