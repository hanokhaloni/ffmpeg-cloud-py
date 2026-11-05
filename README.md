# FFmpeg Reliability Benchmark

[![ci](https://github.com/hanokhaloni/ffmpeg-cloud-py/actions/workflows/ci.yml/badge.svg)](https://github.com/hanokhaloni/ffmpeg-cloud-py/actions/workflows/ci.yml)

A console/TUI tool (C#, .NET 8) that tests an FFmpeg **RTSP → HLS + MP4 recording** pipeline end to end on Windows Server, with Linux supported for development.

A process returning exit code 0, a growing file or an updated playlist does not prove that recording works. This tool independently verifies three things:

| Stage | Question | How it is verified |
|---|---|---|
| **Ingestion** | Is FFmpeg receiving and processing the stream in real time? | `-progress pipe:1` watchdog (frames, media time, speed, dropped frames), classified FFmpeg log errors |
| **Production** | Is every HLS segment and every recording file present, complete and valid? | Every sequence number and recording index is accounted for; every file is probed **and fully decoded**; unfinalized MP4s (missing `moov`) are detected |
| **Consumption** | Can another process actually play it? | An independent FFmpeg plays the live HLS output in real time (local path and the real HTTP URL). Recordings are opened, decoded and seeked at 25/50/75/95 % |

**Primary metric: verified media coverage** = decoded and accounted-for media ÷ expected media, reported separately for HLS and recording, with missing / corrupt / unverified seconds broken out.

---

## 1. Quick start (5 minutes, any machine)

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and FFmpeg + FFprobe on `PATH`.

```bash
git clone https://github.com/hanokhaloni/ffmpeg-cloud-py
```
```bash
cd ffmpeg-cloud-py
```
```bash
dotnet run -c Release --project src/FFmpegReliabilityBench.App -- --duration 60
```

This runs a 60-second benchmark against a built-in **synthetic source**: an FFmpeg test pattern at a constant 25 fps with a tone, so no camera is needed. Watch the dashboard. When it finishes, open the `report.html` printed at the end.

Expected result on a healthy machine: HLS and recording coverage **100 %**, overall **WARN**. The WARN comes from checks that cannot run without configuration, such as clock checks, which report UNKNOWN. UNKNOWN is never counted as PASS.

---

## 2. Guide: running it on the Windows Server

### 2.1 Install

1. **FFmpeg:** use the *same build* as production (for example the Gyan 4.4 build). Put `ffmpeg.exe` and `ffprobe.exe` in one folder, e.g. `C:\FFmpeg\bin`. To compare versions later, keep each build in its own folder.
2. **The tool:** either install the .NET 8 SDK on the server and use `dotnet run`, or publish a self-contained build elsewhere and copy the folder to the server (no .NET needed on the server):

   ```bash
   dotnet publish src/FFmpegReliabilityBench.App -c Release -r win-x64 --self-contained -o publish/FRB
   ```

   Copy `publish/FRB`, `config/` and `profiles/` to the server, e.g. to `C:\Tools\FRB`.

### 2.2 Configure

Copy `config\windows-local-ntfs.json` to `config\site.json` (the name used in the examples below) and edit:

| Setting | Example | Notes |
|---|---|---|
| `ffmpeg.executable` / `ffmpeg.ffprobe` | `C:\FFmpeg\bin\ffmpeg.exe` | the build under test |
| `source.url` | `rtsp://192.168.1.10:554/axis-media/media.amp` | **no credentials here** (see 2.3); `synthetic` = test pattern; a file path = looped file |
| `source.transport` | `tcp` / `udp` | RTSP transport |
| `source.expectedFps` | `25` | used for GOP sizing and timestamp-gap detection |
| `storage.hlsDirectory` / `storage.recordingDirectory` | `H:\hls`, `Y:\recording\rec` or `\\NAS01\Video\HLS` | the **real** production destinations. Prefer UNC paths for NAS: mapped drives are usually invisible to services |
| `hls.targetDurationSeconds`, `hls.playlistSize`, `hls.flags` | `2`, `3`, `delete_segments+independent_segments` | same as production; try `+temp_file` |
| `hls.httpUrl` | `http://server/hls/{session}/{stream}/stream.m3u8` | optional: tests the real web server (IIS/proxy, MIME, caching) |
| `recording.segmentDurationSeconds`, `recording.mode` | `5`, `faststart` | modes: `mp4`, `faststart`, `fmp4` |
| `time.enabled`, `time.ntpServer`, `time.onvifUrl` | `true`, your NTP server, `http://camera/onvif/device_service` | ±500 ms requirement (`time.maxClockDifferenceMs`) |
| `benchmark.durationSeconds`, `benchmark.concurrentStreams` | `3600`, `1` | |

Every option is listed in [section 3.3](#33-configuration-reference). Config files can be stacked: `--config site.json --config profiles\fragmented-mp4.json`, and later files win.

### 2.3 Camera credentials

Never put passwords in config files. Store them in Windows Credential Manager **as the account that will run the tool**:

```bash
cmdkey /generic:FFmpegReliabilityBench:192.168.1.10 /user:viewer /pass:secret
```

Alternatively, set the `FRB_RTSP_USER` and `FRB_RTSP_PASSWORD` environment variables. Passwords are masked (`rtsp://viewer:***@…`) in the TUI, logs and reports.

### 2.4 Run as the same account as the production FFmpeg service

Filesystem, SMB and antivirus behaviour depends on the Windows identity. Run the tool as the service account, for example:

```bash
runas /user:DOMAIN\svc-video "C:\Tools\FRB\FFmpegReliabilityBench.App.exe --config C:\Tools\FRB\config\site.json"
```

For unattended or long runs, use a scheduled task set to "Run whether user is logged on or not" with `--no-tui`. The identity used is recorded in test `SYS-015`.

### 2.5 First run: preflight only (nothing is started)

```bash
FFmpegReliabilityBench.App.exe --config config\site.json --preflight-only
```

This checks the OS, CPU, RAM, disk space, path type (local / mapped / UNC), the FFmpeg/FFprobe versions, SHA-256 and available encoders/muxers, Defender status and exclusions, both output directories (create/write/flush/read-back/rename/delete, latency p95/p99, concurrency, locking), the camera (DNS, TCP, RTSP OPTIONS/DESCRIBE, opening the stream) and clocks. Fix every **FAIL** before continuing.

### 2.6 Benchmark run

```bash
FFmpegReliabilityBench.App.exe --config config\site.json --duration 3600
```

The run starts FFmpeg with the profile's command; the exact command and `ArgumentList` are shown on screen `3`. Monitors validate every output while it runs, an independent consumer plays the HLS stream, and at the end FFmpeg is stopped gracefully (`q`), the remaining files are validated and the report is written.

| Key | Screen |
|---|---|
| `1` | overview: per-stream state, speed, segment/file counts, coverage, recent events |
| `2` | every test with status and diagnosis |
| `3` | FFmpeg: command + exact ArgumentList, progress, resources, recent errors |
| `4` | HLS: playlist state, last segments with validation status |
| `5` | recording: file lifecycle (CREATED → … → PLAYBACK VERIFIED) |
| `6` | resources: CPU, memory, free space, TCP retransmissions, NIC errors |
| `0` | event log |
| `S` / `Q` | save a report snapshot / graceful stop (validates and reports) |
| `Ctrl+C` | first press = graceful stop; second = abort |

### 2.7 Reading the results

Open `reports\<session>\report.html`. Then:

1. **Overall / Prerequisites / Runtime**: statuses are never averaged. Any FAIL fails the run; WARN and UNKNOWN degrade it; UNKNOWN is never PASS.
2. **Coverage** (`HLS-006`, `REC-006`): anything below 100 % means media was missing, corrupt or unverifiable. The evidence lists exactly which segments or files.
3. **Recording timeline**: every expected 5-second interval, with its file, duration, status and the stage it reached.
4. **Which layer failed?**

| Symptom | Look at | Likely layer |
|---|---|---|
| `PROC-002` stalls, `PROC-004` speed < 1.0 | `RES-001` CPU, `PROC-005` | CPU / encoder too slow |
| `PROC-003` RTSP errors, `NET-008` retransmissions | `NET-0xx`, FFmpeg log | camera / network |
| `HLS-002` playlist stalls with `PROC` healthy | `FS-0xx` latency, `REC-007` | storage |
| `REC-003` "moov atom missing" | FFmpeg exit state, `FI-001` | process termination before finalization |
| `REC-007` sharing violations (with locking processes listed) | Defender `SYS-014` | AV / indexer / backup / reader. Investigate; do not assume |
| `HLS-007` stalls but generation OK | `HLS-008/009` | consumer path / web server |
| Failures on several streams at once | Correlation section | shared resource (storage, network, CPU) |

Correlations are labelled **HYPOTHESIS**. They show events that happened together, not proof of cause.

### 2.8 Recipes for common investigations

Run each variant for the same duration and compare the reports.

| Question | Command additions |
|---|---|
| Local disk vs NAS? | `--config config\smb-storage.json` vs the local config |
| Camera/network vs infrastructure? | `--source synthetic` (same VM, same storage, no camera) |
| RTSP over TCP vs UDP? | `--transport udp` (or `--config profiles\udp-transport.json`) |
| Conventional MP4 vs faststart vs fragmented MP4? | `--recording-mode mp4` / `faststart` / `fmp4` |
| What happens if FFmpeg is killed? | `--kill-at 22`: compare `mp4` (the in-progress file is lost) with `fmp4` (1 s fragments survive) |
| One encoder instead of two? | `--profile tee` (single encode, `tee` muxer, `onfail=ignore`) |
| Less CPU? | `--profile copy` (camera stream: stream copy) |
| FFmpeg 4.4 vs 7.x? | `--ffmpeg C:\FFmpeg7\bin\ffmpeg.exe --ffprobe C:\FFmpeg7\bin\ffprobe.exe` |
| How many cameras can this VM sustain? | `--streams 2`, then 4, 8, … (`SCALE-001`, `RES-001`) |
| Does the web server deliver HLS correctly? | `--http-url http://server/hls/{session}/{stream}/stream.m3u8` |
| Is the camera clock within ±500 ms? | `--ntp <server> --onvif http://camera/onvif/device_service` |
| Long soak test | `--duration 28800 --no-tui` (scheduled task; exit code 0/1/2/3) |

`--print-command` shows the exact FFmpeg command for any combination without running it.

---

## 3. Reference

### 3.1 Command line

```
FFmpegReliabilityBench.App [mode] [--config file.json ...] [overrides] [--no-tui]
```

| Mode | |
|---|---|
| `--benchmark` (default) | preflight → filesystem → network → time → live run → final validation → report |
| `--preflight-only` | all checks, no FFmpeg run |
| `--filesystem-only` | storage diagnostics only |
| `--hls-only` / `--recording-only` | one output only |
| `--print-command` | print the FFmpeg command / ArgumentList and exit |
| `--report latest` | non-PASS results of the latest report |

Overrides: `--source`, `--transport`, `--fps`, `--duration`, `--profile`, `--streams`, `--kill-at`, `--hls-dir`, `--rec-dir`, `--recording-mode`, `--hls-time`, `--segment-time`, `--hls-flags`, `--http-url`, `--ffmpeg`, `--ffprobe`, `--ntp`, `--onvif`, `--max-clock-ms`, `--reports`, `--full-decode true|false`.

Exit codes: `0` PASS, `1` WARN, `2` FAIL, `3` fatal / cannot run. `--no-tui` is automatic when output is redirected.

### 3.2 Profiles

| Profile | Pipeline | Purpose |
|---|---|---|
| `baseline` | decode once, two independent libx264/AAC encodes → `hls` + `segment` (same structure as a `split=2` production command) | current baseline |
| `copy` | stream copy video, AAC audio | lower CPU (segment boundaries follow the camera GOP) |
| `fmp4` | fMP4 HLS (`init.mp4` + `.m4s`) + fragmented MP4 recording | alternative packaging |
| `tee` | one encode → `tee` muxer, GOP = gcd(HLS, recording durations) | avoid duplicate encoding; output isolation |

Recording modes: `mp4` (conventional), `faststart`, `fmp4` (`frag_keyframe+empty_moov+default_base_moof`, 1 s `frag_duration`, `flush_packets`).
The command adapts to the FFmpeg build it finds. For example, it uses `-stimeout` on 4.x and `-timeout` on 5.0+, and drops `temp_file` and `-stats_period` when unsupported (the change is noted on screen and in the report). `profiles/*.json` are ready-made config overlays.

### 3.3 Configuration reference

Defaults are in `config/default.json`. Keys are case-insensitive.

| Section | Key | Default | Meaning |
|---|---|---|---|
| (root) | `reportDirectory` | `reports` | where `<session>/` report folders go |
| `ffmpeg` | `executable`, `ffprobe` | `ffmpeg`, `ffprobe` | binaries under test |
| | `logLevel` | `info` | FFmpeg log level (lines are prefixed with their level for classification) |
| | `processStallTimeoutSeconds` | `10` | no forward progress for this long = stall |
| | `startupTimeoutSeconds` | `30` | |
| `source` | `url` | `synthetic` | `synthetic`, `rtsp://…`, or a media file (looped in real time) |
| | `transport` | `tcp` | `tcp` / `udp` |
| | `expectedFps` | `25` | |
| | `useReadrate` | `false` | add `-re` to an RTSP input (normally unnecessary) |
| `storage` | `hlsDirectory`, `recordingDirectory` | `out/hls`, `out/rec` | media goes to `<dir>/<session>/<stream>/`; existing files are never touched |
| | `minimumFreeSpaceGb` | `10` | |
| | `smallFileCount`, `segmentFileCount`, `segmentFileSizeMb` | `50`, `10`, `4` | filesystem test workload |
| | `latencyWarnMs`, `latencyFailMs` | `250`, `1000` | p99 thresholds |
| `hls` | `enabled` | `true` | |
| | `targetDurationSeconds`, `playlistSize` | `2`, `3` | `-hls_time`, `-hls_list_size` |
| | `flags`, `deleteThreshold` | `delete_segments+independent_segments`, `6` | `-hls_flags`, `-hls_delete_threshold` (keeps deleted segments around long enough to validate) |
| | `durationToleranceSeconds`, `maxSegmentAgeSeconds`, `minSegmentSizeBytes` | `0.5`, `8`, `1024` | validation thresholds |
| | `decodeSegments`, `playbackTest` | `true`, `true` | full decode of every segment; independent real-time consumer |
| | `httpUrl` | empty | HTTP consumption test (`{session}`, `{stream}` placeholders) |
| `recording` | `enabled`, `segmentDurationSeconds`, `mode` | `true`, `5`, `faststart` | |
| | `fullDecode`, `seekProbes` | `true`, `3` | full vs 2 s quick decode; seek tests per file |
| | `stableSeconds`, `durationToleranceSeconds` | `1.5`, `1.5` | |
| `time` | `enabled`, `ntpServer`, `sampleCount`, `maxClockDifferenceMs`, `onvifUrl` | `false`, `pool.ntp.org`, `5`, `500`, empty | |
| `benchmark` | `durationSeconds`, `profile`, `concurrentStreams`, `injectKillAtSeconds` | `3600`, `baseline`, `1`, `0` | |

### 3.4 Test catalogue

| Area | IDs |
|---|---|
| System | SYS-001 OS, 002 architecture, 003 CPU, 004 RAM, 005 disk space (per directory, UNC-aware), 006 disk health (UNKNOWN), 007/008 ffmpeg/ffprobe + SHA-256, 009 encoders, 010 muxers + feature support, 012 path type (FAIL for mapped drives under a service), 013 other FFmpeg processes, 014 Defender + exclusions (diagnose only), 015 identity |
| Filesystem (per directory) | FS-001..009 create/write/durable flush/read/SHA-256 verify/rename/delete/subdir, 010 concurrent 8 writers + 4 readers, 011–014 small-file create/close/rename/delete latency p50/p95/p99, 015 segment-size write+flush, 016 durable-flush latency, 017 read-after-close, 018 sharing semantics |
| Network | NET-001 DNS, 002 TCP, 003 RTSP OPTIONS, 004 DESCRIBE, 005 stream open, 006 connect latency; during the run 008 TCP retransmissions, 009 NIC errors/discards, 011 bandwidth, 012 resets |
| Time | TIME-001 host vs NTP (own SNTP measurement), 002 camera vs host (ONVIF; 1 s resolution, so ±500 ms is never PASS on ONVIF alone), 003 Windows Time; TS-001 PTS/DTS continuity |
| Process | PROC-001 lifetime (crash / graceful / failed graceful stop), 002 forward-progress watchdog, 003 classified log errors with original lines, 004 real-time speed and dropped frames, 005 CPU / memory trend |
| HLS | HLS-001 RFC 8216 validity, 002 advancement, 003 every sequence number accounted for, 004 probe + full decode, 005 duration statistics, 006 coverage, 007 local real-time consumer, 008/009 HTTP consumer; HLS-GEN / HLS-CON summaries |
| Recording | REC-001 cadence, 002 missing intervals, 003 lifecycle validation (finalized, probe, decode), 004 durations, 005 seek tests, 006 coverage, 007 locking after close (with locking processes via Restart Manager) |
| Resources / scale | RES-001 system CPU, 002 memory, 003 the benchmark's own memory, SCALE-001 concurrent streams |
| Fault injection | FI-001 forced termination impact |

### 3.5 Output files (`reports/<session>/`)

`report.html` (summary, timeline, commands, events, hypotheses) · `report.json` (everything, machine-readable) · `results.csv` · `events.csv` · `<stream>.hls-segments.csv` · `<stream>.recording-timeline.csv` · `<stream>.ffmpeg.log` (the complete original FFmpeg log) · `<stream>.consumer.log` · `ffmpeg-capabilities.txt`.
`reports/latest.txt` points to the most recent session. Generated media under `<hlsDirectory>/<session>/` and `<recordingDirectory>/<session>/` is kept for inspection, so delete it yourself.

---

## 4. Development

```bash
dotnet build
```
```bash
dotnet test
```

The tests (52) cover parsers, playlist validation, coverage and health rules, command generation for every profile and FFmpeg version, credential masking, config layering and CLI parsing. Tests that need FFmpeg run real media through the monitors: valid, corrupt, missing and unfinalized files, sequence gaps, fake RTSP and ONVIF servers, and two full orchestrator runs (healthy pipeline with an HTTP consumer, and a forced kill). These tests pass trivially when `ffmpeg`/`ffprobe` are not on `PATH`. They run sequentially in the `ffmpeg` collection.

CI (`.github/workflows/ci.yml`) builds and tests on **Ubuntu and Windows** (Windows uses the Gyan FFmpeg build), then runs a 45-second smoke benchmark and uploads the reports as artifacts.

```
src/FFmpegReliabilityBench.Core   Runner/       FFmpegRunner (process lifecycle, watchdog, graceful 'q' stop), ProgressParser,
                                                FFmpegLogParser, FFprobeRunner, CommandBuilder, Capabilities, CommandText
                                  Validation/   HlsPlaylist, HlsMonitor, RecordingMonitor, PlaybackConsumer, HttpHlsConsumer,
                                                Coverage, TimestampAnalyzer
                                  Diagnostics/  Preflight, Filesystem, Network (+ counters), Time (SNTP, ONVIF, w32tm),
                                                ResourceMonitor, RestartManager, Correlation, SystemInfo
                                  Engine/       Orchestrator (owns all lifecycles), Evaluator (results), BenchState
                                  Reporting/    JSON / CSV / HTML
src/FFmpegReliabilityBench.App    CLI + Spectre.Console TUI (read-only view over BenchState)
tests/FFmpegReliabilityBench.Tests
config/   default, windows-local-ntfs, smb-storage, multi-camera
profiles/ baseline, conventional-mp4, fragmented-mp4, fmp4-hls, stream-copy, tee-single-encode, udp-transport, forced-kill
```

Design rules: the TUI never runs tests or manages FFmpeg; FFmpeg is always started with `ProcessStartInfo.ArgumentList` (never through a shell); stdout and stderr are drained asynchronously; elapsed times use `Stopwatch` (immune to clock changes) and timestamps are UTC.

## 5. Limitations / not implemented yet

- Camera clock via RTCP Sender Reports or overlay OCR is not implemented. Only ONVIF is, and its 1 s resolution means it cannot prove ±500 ms.
- Fault injection covers forced termination only. Pausing FFmpeg, CPU/disk/network load, storage delay, a competing reader and clock changes are not implemented.
- No automatic scalability ramp or FFmpeg version matrix: run once per value and compare the reports.
- Windows disk/SMB performance counters are not sampled. Storage health comes from the FS tests and network health from TCP/NIC counters.
- End-to-end live latency is reported as UNKNOWN, because it needs an embedded reference timestamp.
- The synthetic source is FFmpeg's `lavfi` generator, not an RTSP server. To isolate RTSP itself, point `--source` at a MediaMTX stream.
- No graphs in the HTML report. The time series is in `report.json`.
