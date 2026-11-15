# Quality Optimizer — Master Project File

This repository contains **(a)** a reverse-engineering writeup of the RTXFury
"Quality Method" for TikTok uploads and **(b)** a small standalone Windows app
that implements that method: you give it a video, it hands back an optimized
MP4 that keeps its upload quality.

This file is the project's master `CLAUDE.md`. The app is the deliverable; the
research notes below (sections 1–10) are the specification it is built from.

---

## The app

> **Current method (2026-11-09): use the clone engine.** TikTok stopped honoring
> the original junk-track method around 2026-11-08. The **clone engine**
> (`-clone` flag / the GUI "New TikTok method" checkbox, now on by default) is
> **confirmed working** by upload — it re-encodes to H.264 (1080p by default)
> and reproduces RTXFury v2's two-audio-track + two-sample-description
> structure. **4K is confirmed too (2026-11-11):** with "Allow up to 4K" /
> `-4k`, two 2160×3840 uploads at 60 and 59.94 fps were served raw at full
> resolution and their original frame rate. See "New clone engine" below. The
> junk-track method described first is kept for reference but no longer works
> on TikTok. **End-gap fix: keep "Off - v3".** The `keep` layout was re-encoded
> by TikTok (2026-11-14) and `front` breaks local playback.
>
> **Note (2026-11-14): the surgery may not be the lever.** Two current
> @someplace505 posts were served `original` (one 4K H.264, one 1440p 10-bit HDR
> HEVC) from clean, trick-free files; a plain upload of ours came back `normal`.
> See "How other accounts keep quality" below. The clone engine stays the
> confirmed method only because no trick-free upload has yet been served
> `original` on this account.
>
> **Update (2026-11-15): RTXFury itself dropped the surgery.** A new RTXFury
> output (`rtx-optimized-*.mp4`) is a clean faststart remux — video
> stream-copied (no re-encode), 1 video + 1 audio, brand isom, moov-first, no
> clone/tail/two-description — structurally identical to @someplace505's served
> originals. The app now has a **`-passthrough` / "Clean passthrough" mode** that
> reproduces it, so a clean-remux upload can be A/B'd against the clone and a
> plain upload on the same account. NOT yet upload-confirmed here. See "RTXFury
> pivot" below.
>
> **Correction (2026-11-15, later): the newest RTXFury output is NOT fully
> clean — it keeps a stealth junk audio tail.** A newer sample
> (`rtx-optimized-dd39.mp4`) is the same clean faststart remux (video
> stream-copied byte-for-byte, 1 video + 1 audio, moov-first, brand isom) BUT
> its **last 4 audio frames are overwritten with a fixed 15-byte undecodable
> constant and relocated into a trailing 68-byte `mdat`** after the main one (5
> AAC decode errors on the tail). The audio frame count and timeline are
> preserved (no end gap), so the file still parses as a clean 1v+1a file. So
> RTXFury did NOT drop the surgery; it made it stealthier — a corrupt audio tail
> in the real track instead of the old separate junk track. **The passthrough
> engine now reproduces this by default** (`Mp4.AddJunkAudioTail`); the earlier
> "fully clean" read came from the older `ca17` sample, which lacked the tail.
> Still NOT upload-confirmed. See "RTXFury pivot" below.

**What it does.** Takes an input video and writes a new MP4 that carries the
container signature studied below — a third audio track whose tail is
undecodable, plus trailing junk bytes after `mdat`. The point is to make a
platform's server-side transcoder fail and fall back to serving the raw upload
("original" quality) instead of a re-encode. The **video stream is never
re-encoded** when the source is already compatible; it is stream-copied, so
there is no quality loss from this tool.

**Files**

| Path | What it is |
|------|------------|
| `optimizer.exe` | The app. Double-click to run. ~30 KB, no install. |
| `ffmpeg.exe`, `ffprobe.exe` | Bundled (static gyan.dev build) so the folder is portable. |
| `verify.bat` / `verify.ps1` | Drag an MP4 on to check it carries the fingerprints. |
| `src/Optimizer.cs` | Full source (GUI + MP4 surgery + ffmpeg driving). |
| `stripper.exe` / `src/Stripper.cs` | Companion app: detects and removes app tags (Aweme, `sec_uid`, `vid:`) with `-c copy`. Run it **before** the optimizer. |
| `strip_metadata.py` | Python version of the stripper (needs Python; none installed here). |
| `build.bat` | Rebuilds `optimizer.exe` and `stripper.exe` from source. |
| `tiktok-passthrough.user.js` | **Experimental** Tampermonkey userscript: strips TikTok's cloud-edit/canvas flags + `post_type 2→3` on the web publish call. For A/B testing passthrough; not part of the app. See 2026-11-14 notes. |
| `extension/` | **Browser extension (MV3)** that recreates the Editing News two-part system: intercepts the TikTok web upload and swaps in an `optimizer.exe -clone` output via a native-messaging host (`extension/host/clone_host.exe`), plus the userscript's flag-strip. Local only; no backend. See "Browser extension" below and `extension/README.md`. |
| `README.txt` | Short end-user instructions. |
| `CLAUDE.md` | This file. |

**Run it (no Claude Code needed).**
1. Double-click `optimizer.exe`.
2. Drop a video onto the window, or click **Choose Video…**.
3. Pick where to save; you get `<name>_optimized.mp4`.
4. It auto-verifies the result and shows a PASS/WARN report. **Verify a File…**
   re-checks any MP4 on demand.
5. **Add Song + Optimize…** attaches an external audio file (e.g. a downloaded
   MP3) to the video — video stream-copied, no re-encode — then optimizes and
   verifies. Use this instead of a TikTok library sound, which re-encodes.
6. **Re-encode video at [20–30] Mbps** (off by default) re-encodes the video to
   H.264 High (libx264, preset medium, ABR at the chosen rate, peaks to 1.25×,
   `yuv420p`) instead of stream-copying it. It applies to both buttons. The
   container surgery is unchanged. Raising the bitrate adds no detail beyond the
   source and costs one generation of encoding, so leave it off unless you want
   an H.264 upload at a fixed high rate (e.g. from an HEVC or low-bitrate
   source). On a 5 s 1900×3378 60 fps HEVC clip, 25 Mbps gave 26.6 Mbps and 20
   gave 21.1, and all 8 checks passed. This mode has not been tested by upload.
7. **New TikTok method (re-encode 1080p H.264 + clone audio)** (off by default)
   switches to the clone engine instead of the junk method, for TikTok uploads
   since ~2026-11 where the junk method stopped working. See "New clone engine"
   below. When checked, it governs both buttons and ignores the legacy re-encode
   box. **Allow up to 4K** under it raises the clone engine's cap from
   1080×1920 to 2160×3840 (upload-confirmed 2026-11-11). Pair it with
   **Set video bitrate** at a high rate: the confirmed 4K uploads came out at
   44–48 Mbps.
8. **End-gap fix** (clone engine only; default **Off - v3**) picks where the
   clone engine puts its 16 silent tail frames. v3 leaves ~0.4 s of silence with
   no picture at the end of every file. The other two layouts remove the gap
   but are not usable: **keep** was re-encoded by TikTok (2026-11-14) and
   **front** loses its audio in a local player. Use v3. See "End gap
   (2026-11-13)" below.
9. **Clean passthrough (RTXFury 2026-11)** (off by default; mutually exclusive
   with "New TikTok method") switches to the **passthrough engine**: no video
   re-encode when the source is already H.264/HEVC + AAC-LC — it stream-copies
   both and re-muxes to a clean faststart file (brand isom, moov first), then
   reproduces the newest RTXFury output (`dd39`) by overwriting the **last 4
   audio frames with a 15-byte undecodable constant in a trailing `mdat`** (count
   and duration preserved, so no end gap). HE-AAC audio is re-encoded to AAC-LC
   (where RTXFury rejects it). For an A/B against the clone and a plain upload.
   NOT upload-tested on this account. See "RTXFury pivot" below.

**Requirements.** `ffmpeg.exe` and `ffprobe.exe` must be on `PATH` (already
installed on this machine via WinGet) **or** sitting next to `optimizer.exe`.
The app itself needs only the .NET Framework that ships with Windows.

**Headless / scripting.** No window; prints a report and exits 0 only if all
checks pass:
- `optimizer.exe <input> <output.mp4>` — optimize + verify.
- `optimizer.exe -mux <video> <audio> <output.mp4>` — attach audio, then optimize + verify.
- `optimizer.exe -verify <file.mp4>` — just run the checks.
- Prefix either convert form with `-bitrate <Mbps>` to re-encode the video at
  that rate, e.g. `optimizer.exe -bitrate 25 in.mov out.mp4`.
- Prefix any convert form with `-clone` to use the **clone engine** instead of
  the junk method (see "New clone engine" below): re-encode to H.264 (1080p by
  default) + duplicate audio track. `optimizer.exe -clone in.mov out.mp4`,
  `optimizer.exe -clone -mux video.mp4 song.mp3 out.mp4`,
  `optimizer.exe -clone -verify out.mp4`. After `-clone`, add `-4k` to cap
  at 2160×3840 instead of 1080×1920 (upload-confirmed), or `-native` to keep
  the source resolution (not tested by upload above 4K).
- With `-clone`, add `-tail v3|keep|front` to pick the tail layout (default
  `v3`; see "End gap (2026-11-13)"). With `-verify`, `-tail keep|front` turns
  the end-gap line into a real check.
- Prefix any convert form with `-passthrough` (or `-clean`) for RTXFury's NEW
  method (2026-11, `dd39`): a clean faststart remux **plus a junk audio tail**.
  Video is stream-copied when already H.264/HEVC (zero re-encode), audio copied
  if AAC-LC else re-encoded to AAC-LC; brand isom, moov first; then the last 4
  audio frames become a 15-byte undecodable constant in a trailing `mdat` (count
  and duration preserved). Add `-force264` to convert HEVC → H.264.
  `optimizer.exe -passthrough in.mov out.mp4`,
  `optimizer.exe -passthrough -verify out.mp4`,
  `optimizer.exe -passthrough -mux video.mp4 song.mp3 out.mp4`.
- Flag order: `-bitrate N`, `-clone` or `-passthrough`, `-force264`, `-4k`/`-native`, `-tail X`, then the
  command, e.g. `optimizer.exe -bitrate 45 -clone -4k in.mov out.mp4` or
  `optimizer.exe -clone -tail keep -mux video.mp4 song.mp3 out.mp4`.
  Note: the exe is a GUI-subsystem
  binary, so its console output is not visible when launched from PowerShell;
  rely on the exit code (0 = all checks passed) or the GUI.

**Metadata Stripper (`stripper.exe`).** Same look and portability as the
optimizer. It scans the whole file for app signatures, reports the box each
one is in (or `mdat` for tags in H.264/HEVC SEI), then re-muxes the file with
ffmpeg, keeping only video and audio and dropping all global, stream and chapter
metadata. It uses bitexact flags so ffmpeg adds no tags of its own. It then
verifies three things: no signatures are left, the file decodes with 0 errors,
and the packet MD5s match the source. The optional SEI strip
(`filter_units`) changes the video packets, so in that case the check compares
decoded-frame MD5s instead. It flags files that already carry the optimizer's
trailing junk, because stripping them undoes the optimization.
Headless: `stripper.exe -check <file>` (exit 1 if tags found) and
`stripper.exe [-sei] <in> <out>`.

**Build from source.**
```bat
build.bat
```
It compiles `src\Optimizer.cs` with the in-box C# compiler
(`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) — no SDK required.

## How the output is produced (implementation)

The app runs two stages (see `src/Optimizer.cs`):

1. **Normalize** — `ffmpeg` re-muxes the input to a clean H.264 + AAC MP4 with
   `moov` written last. Streams are copied (`-c copy`); it only re-encodes if
   the source codec is not MP4-compatible, or the user turned on the bitrate
   re-encode (then the video goes to libx264 at that rate and the audio is still
   copied when it can be). If the source has no audio, a silent
   AAC track is added (the method needs a real audio track to clone).
2. **Container surgery** (pure byte work, no re-encode) — the MP4 box tree is
   parsed and rewritten to add a third audio track built from the real one:
   - its first *N* samples point at the **same** audio bytes (not duplicated);
   - it gets **9 × N** extra 8-byte samples, each `00 00 00 04 00 00 00 00`,
     living in a trailing block straight after `mdat`, outside any box;
   - total samples = **10 × N**; the extra run has duration 1 tick each;
   - the track has **no edit list**;
   - every `mvhd` / `tkhd` / `mdhd` creation & modification time is zeroed;
   - `mvhd` becomes version 1 with duration **unknown** (`ff ff ff ff ff ff ff ff`);
   - the movie-level `udta` is dropped (ffmpeg's `encoder` tag and any source
     comment, e.g. a TikTok `vid:` ID);
   - each track's `hdlr` loses 4 of its 12 reserved bytes (41-byte box), so
     parsers read the names as `oHandler` / `dHandler`;
   - the file is written in the reference layout (section 5.2):
     `ftyp`, `moov`, `mdat`, junk. Moving `moov` in front of `mdat` shifts all
     media data by the same amount, so every track's chunk offsets are shifted
     by that delta; the `free` box ffmpeg writes is dropped.

Earlier builds wrote `moov` last (`ftyp, free, mdat, moov, junk`), which does
not match the reference; files made by them fail the box-layout check and
should be re-made. Verified on a 720p30, a 2160×3840 60 fps ~24 Mbps, a
no-audio and an Add Song sample: video and real-audio packets are
byte-identical to the old layout's, both decode with 0 errors, the junk track
yields decode errors on every junk packet, `nb_read_packets` on it is exactly
`10 × N`, and the file still parses and plays.

## Verifying an output

The app verifies every file it makes and shows the report automatically; the
**Verify a File…** button re-checks any MP4. On the command line,
`optimizer.exe -verify "file.mp4"` prints the same report and exits 0 only if
all checks pass. (`verify.bat` / `verify.ps1` do the same but need PowerShell
enabled — the built-in checks avoid that dependency.) Each reports PASS/WARN
for the fingerprints below and whether the video is intact:

| Check | Optimized file | Plain file |
|-------|----------------|------------|
| 1 video + 2 audio tracks | PASS | WARN |
| junk track = 10x real audio packets | PASS | WARN |
| junk track breaks decoders | PASS | WARN |
| video decodes with 0 errors (no quality loss) | PASS | PASS |
| timestamps zeroed | PASS | depends |
| trailing `00 00 00 04 00 00 00 00` | PASS | WARN |
| box layout `ftyp, moov, mdat, junk` | PASS | WARN |
| movie header: `mvhd` v1, duration unknown, no movie `udta` | PASS | WARN |

The trailing-bytes check looks past one empty 8-byte `free` box at the very
end, which real RTXFury outputs have; a real RTXFury output passes all eight.

A file this tool produced passes all of them. The only real-world confirmation
is uploading it and seeing the platform serve "original" quality (section 6);
that is a property of the platform, not something checkable from the file.

## Notes / limits

- It intentionally does **not** stamp the third-party `©too` "RTX Fury"
  signature, which is that tool's branding. After the changes below, that tag
  is the only remaining structural difference from a real RTXFury output.
- Whether a platform serves the original depends on that platform's own
  server-side behaviour, which cannot be observed from the file (section 6).
- The optimizer must be the LAST step. Any in-app edit on TikTok — trimming,
  filters, text, or adding a library/trending sound — forces a fresh re-encode
  from a clean source (section 6), which strips the junk track and recompresses
  the video. To use specific music, attach it beforehand with **Add Song**
  (video is stream-copied) rather than a TikTok sound.

## Compared with a real RTXFury output (2026-10-24)

**A/B upload, same account, same source** (a sample clip: HEVC 1080p30
4.8 Mbps, HE-AACv2), posted about a minute apart:

| | RTXFury output | This tool (moov-first build) |
|---|---|---|
| `encodedType` / `videoQuality` | original / original | normal / lowest |
| Served | the upload, 1080p HEVC, 6,880,332 B | TikTok re-encode, 540p H.264, ~508 kbps |
| Re-encode versions | none at 4 min | 540p already there at 92 s |

So the account is not the cause; the file is. A box-by-box diff of the RTXFury
file against ours (everything else, including all sample tables and `mdat`,
was byte-identical) found:

1. `mvhd` version 1 with duration all `ff` ("unknown"); ours had v0, 11.3 s.
   ffmpeg ignores it and derives the duration from the tracks; a pipeline that
   trusts the header would see 2^64−1. Strongest suspect for the trigger.
2. No movie-level `udta`; ours carried `encoder=Lavf…` and the source's
   `comment=vid:v2800fgi…` (a ByteDance video ID carried over from the source).
3. `hdlr` boxes 41 bytes: RTXFury drops 4 of the 12 *reserved* bytes and keeps
   the full `VideoHandler` string; spec parsers therefore read `oHandler`
   (section 5.3's guess that the name is clipped is wrong about which bytes).
4. `©too` "RTX Fury Quality Method … v21.2" in the video track's `udta`.
5. An empty 8-byte `free` box after the junk (the section 5 sample had none).
6. Video `mdhd` duration 1,024 ticks shorter (ffmpeg's remux recomputes it).

The app now matches 1–3. 4 is deliberately left out; 5 and 6 are not needed
(the section 5 sample worked without 5; 6 is a remux rounding difference).

**Confirmed by upload (same account, same source):** the build with 1–3 was
served `original` / `original`, 1080p HEVC, 6,880,180 B (exactly the uploaded
file's size), with no re-encode versions at 176 s, past the point where the
moov-first build had already been re-encoded. So the `©too` tag is not needed.
Which of 1–3 is the actual trigger is still untested (1 is the likeliest).

## Stopped working on TikTok (2026-11-08)

Uploads made with the same `optimizer.exe` were served raw on 10-26 (p91) and
10-29 (p93); for both, the served size equals the uploaded file's size. Both
are still served raw on 11-08. On 11-08, every upload was re-encoded to 540p
H.264 at 0.2–1.1 Mbps (`normal` / `lowest`):

- p94 and p96: same box structure and header box sizes as p93; all 8 checks
  pass. p94 had no caption.
- p95: run through the optimizer twice (see below).
- **p93 re-uploaded unchanged** (a re-upload test, no caption): re-encoded,
  540p, 1.1 Mbps. The same file was served raw on 10-29.

So the change is not in the file or the caption. That same day, RTXFury's how-to
page showed a banner saying its "Original & FFMPEG" engines are being fixed and
telling users to use a temporary "RTX Engine (720P 60FPS)" instead.
INFERENCE: TikTok changed its server-side handling between 10-29 and 11-08, and
the current container signature no longer triggers the fallback. Whether that
applies to every account or only this one is untested.

Other notes from this check:
- A post served raw can be labelled `normal` / `normal` rather than
  `original` / `original`. The reliable signals are an empty `bitrateInfo`
  and a served `size` equal to the uploaded file's size.
- Running the optimizer on its own output trims the video `hdlr` a second time
  (41 → 37 bytes). It should refuse files that already carry the trailing junk.

## New clone engine (2026-11-09)

A real new RTXFury output (a sample made with RTXFury's own patcher) **was
served `original` / `original` by TikTok** (served size 22,519,291 B = the
uploaded file exactly, empty `bitrateInfo`). So the new
RTXFury method works where the old junk method now fails. A box diff of that
file against our junk output shows it shares **none** of the old fingerprints:

- video **re-encoded** to H.264 (1080p, ~15.7 Mbps, `h264_nvenc`); not stream-copied
- a second audio track that is a **valid duplicate** of the first (no junk, 1× not 10×)
- moov **last**; full `VideoHandler`/`SoundHandler` names (not clipped)
- `mvhd` v0 with a real duration (not v1/unknown); movie-level `udta` present
- no RTX Fury `©too` tag
- tail frames are valid 6-byte AAC inside a second small `mdat` (not junk outside all boxes)

So the new trigger is almost certainly **a valid duplicate audio track in an
otherwise normal, well-formed H.264 file** — the one feature common to both the
old-working and new-working files (three streams: 1 video + 2 audio), now done
with valid frames instead of junk.

**The `-clone` engine** (`Tools.BuildClone` in `src/Optimizer.cs`, GUI checkbox
"New TikTok method") works in two stages:
1. ffmpeg builds a clean single-audio base — re-encode video to H.264 **Main,
   `-bf 0`** (tries `h264_nvenc`, falls back to libx264), downscale to fit
   1080×1920 (default), 2160×3840 (`-4k`) or not at all (`-native`),
   re-encode audio to AAC-LC, `-map_metadata -1` to drop the source `vid:` ID,
   moov written last.
2. `Mp4.BuildCloneContainer` does byte surgery (moov is last, so the main mdat
   never moves): it adds a second audio track that **shares the first track's
   bytes** (clones its sample tables), and appends a run of 16 silent 6-byte
   AAC frames in a **second `mdat`** that both audio tracks reference. The audio
   edit lists are dropped so ffmpeg (and, we assume, TikTok) demuxes the tail
   instead of trimming it; `mdhd`/`tkhd`/`mvhd` durations are extended to cover
   it. This mirrors the real RTXFury output (shared-bytes clone + second-mdat
   tail), which is itself a well-formed version of the old junk-track idea.

`VerifyClone` checks 1 video + 2 audio, both audio tracks decode with 0 errors
(valid, not junk), equal packet counts, video decodes clean and is H.264 within
2160×3840, and the container parses. It also reports the **end gap** (file
duration minus video duration): `[INFO]` for v3, a real PASS/WARN (≤ 0.100 s)
for the no-gap layouts. "Verify a File…" in the GUI now uses these clone checks
when "New TikTok method" is ticked (it used to always run the junk-method checks).

The current engine reproduces the **two-sample-description quirk**: the real
audio track carries a 2nd `esds` (an mp4a sample entry lifted verbatim from a
working RTXFury file, `AUDIO_DESC2`, whose AudioSpecificConfig differs from the
normal one) and tags the tail's second 8 frames with it, while the clone track
tags the same bytes with description 1 — a codec-config mismatch between the two
tracks. The tail is split into two 8-frame chunks to allow this.

Still **not** copied: the stale-moov `free` stub at the front (a faststart
leftover; almost certainly not the trigger). That is the only remaining
structural difference from the RTX file.

**Status (2026-11-09): CONFIRMED WORKING by upload.** Three hypotheses tried:
- **v1 (clean dual-encode)** — two *independent* identical audio tracks, no
  shared bytes, no tail — upload test A: **re-encoded** to 540p. So a
  plain duplicate audio track is NOT the trigger.
- **v2 (shared-bytes clone + second-mdat tail)** — matched the RTX structure
  except the sample-description quirk — upload test B: **re-encoded** to
  540p. So the shared-bytes clone + tail alone is NOT enough.
- **v3 (v2 + the two-sample-description mismatch)** — upload test C:
  served **`original` / `original`**, 1080p H.264, size 15,952,974 B (exactly
  the uploaded file), empty `bitrateInfo`, no re-encode. **This is the current
  engine.**

**The trigger is the two-sample-description mismatch** (`AUDIO_DESC2` + the
`[…,8,2]` tail tag on the real track vs `[…,8,1]` on the clone). v1→v2→v3 isolate
it: the only thing added between the re-encoded v2 and the served-original v3 was
that mismatch. The shared-bytes clone and second-mdat tail are necessary scaffolding
(v1 without them failed) but not sufficient without the mismatch (v2 failed).

The stale-moov `free` stub is the only RTXFury feature still not copied, and v3
proves it is not needed.

**4K confirmed by upload (2026-11-11).** Two clone-engine outputs made with
"Allow up to 4K" were posted on the same account about 2.5 minutes apart. Both
were served raw:

| | p98 ferry sunset sea gulls | p99 umbrella walk rainy bridge |
|---|---|---|
| Source | HEVC 2160×3840, 60 fps, 17.3 Mbps | HEVC 2160×3840, 59.94 fps (2997/50), 3.3 Mbps |
| Upload | H.264 Main L5.2, NVENC, no B-frames, 2160×3840, 60 fps, 44.5 Mbps, 52,484,251 B | same, 59.94 fps, 48.2 Mbps, 49,808,465 B |
| `encodedType` / `videoQuality` | original / original | original / original |
| `definition` | 2160p | 2160p |
| `bitrateInfo` | empty | empty |
| Served file | 52,484,251 B, SHA-256 identical to the upload, 60 fps, 563 frames | 49,808,465 B, SHA-256 identical to the upload, 59.94 fps, 493 frames |

So the two-sample-description trigger does not depend on resolution, frame
rate (60 and 59.94 both come back unchanged, not resampled) or a bitrate of up
to about 48 Mbps. Caveat: the page data was read about 2 minutes (p99) and 4
minutes (p98) after posting. That is past the 92 s point at which an earlier
failed build already had its re-encode. A later re-check was blocked by
TikTok's bot-check page at the time. **Re-checked 2026-11-15 01:56 UTC, about 3
days after posting: both are still `original` / `original`**, 2160p, with
the served size equal to the upload and empty `bitrateInfo`. p97 (nanjing first
snow, 4K v3, posted 11-10) is also still raw. The junk-method posts from 10-26
and 10-29 were still raw 10+ days later.

The clone engine re-encodes (one generation of loss), so set a high bitrate.
It caps at 1080p by default; for a 4K source, turn on "Allow up to 4K" (`-4k`),
which is now upload-confirmed.

Correction (2026-11-13): v1 also lacked the mismatch, so v1 failing does not
show that the shared-bytes clone and the tail are needed. What v1→v2→v3 proves
is only that the full v3 works and that adding the mismatch to v2 made it work.

## End gap (2026-11-13)

**Symptom (user report):** clone-engine posts end with a short black (or frozen)
screen and silence before the loop, on TikTok and in local players. Posts made
with the old junk method loop seamlessly.

**Cause (VERIFIED by rebuilding the engine on Linux and measuring).** v3 appends
16 silent AAC frames after the song. An AAC frame always decodes to 1024
samples, so that is 16 × 1024 / 44,100 = 0.372 s of real audio output. v3 also
(a) deletes the audio edit lists, so the 1024-sample encoder delay is played
(audio ~23 ms late) instead of skipped, and (b) stretches tkhd/mvhd over the
tail. The video track is untouched, so the file runs past the last video frame.
Neither (a) nor (b) was copied from RTXFury (the code comment said "we
assume"); the October RTXFury file kept the edit list on its real audio track.

| File (6.000 s, 1080×1920 60 fps test clip) | File ends | Gap after last frame |
|---|---|---|
| Plain re-encode, no surgery | 6.016 s | ~0 |
| v3 | 6.411 s | 0.411 s |
| v3 + Add Song | 6.385 s | 0.385 s |
| v3, source audio 0.5 s longer than its video | 6.901 s | 0.901 s |

In Chromium, playing the last second of video to the `ended` event takes
0.43 s longer for v3 than for the plain file; Chromium holds the last frame,
other players show black. Shortening the tail's timestamps (1 tick per frame)
does **not** help: decoders still output 1024 samples per frame and Chromium
plays them.

**The two no-gap layouts** (`Mp4.TailLayout`, GUI "End-gap fix", `-tail`):

- **Keep edit lists** (`keep`) = v3 minus (a) and (b). Same tail, same
  two-description mismatch; mdhd still covers the tail. Playback stops at the
  end of the song. Trade-off: ffmpeg's demuxer then drops the tail frames that
  carry description 2 (262 of 277 packets read), so a reader that works like
  ffmpeg never reaches the config switch. If TikTok's trigger needs that switch
  to be read, this layout gets re-encoded.
- **Silent frames at start** (`front`): the tail sits before the song, inside
  the edit list's skipped start (media_time = 16 × 1024 + encoder delay). ffmpeg
  still demuxes and decodes all 277 packets (marked discard), the real track
  still switches to description 2 and back while the clone track does not, and
  nothing plays after the song. Differs from v3 in tail position, so it is the
  bigger structural change.

Both no-gap layouts also fix the 23 ms audio lag. Measured on the test clip
(C# build, run under Mono): v3 output is byte-identical to the previous build
on three inputs; `keep` and `front` pass all clone checks with an end gap of
0.016 s (0.000 s with Add Song or a no-audio source), Chromium end delay within
0.03 s of the plain file, and 0 decode errors on both audio tracks.

**Status (2026-11-14): neither layout is usable. Keep v3 as the default.**

**`keep`: re-encoded by upload.** p102 (gexian sky lanterns fireworks edit),
`keep` layout with "Allow up to 4K": H.264 2160×3840, 60 fps, 45.3 Mbps,
62,896,981 B. Posted 2026-11-15 01:50:56 UTC; page data read about 4.5 min
later:

| | Uploaded p102 (`keep`) | Served |
|---|---|---|
| `encodedType` / `videoQuality` | | normal / lowest |
| `definition` / size on screen | 2160×3840 | 540p, 576×1024 |
| Bitrate / size | 45.3 Mbps / 62,896,981 B | 1.1 Mbps / 1,546,924 B |
| `bitrateInfo` | | one gear, `lowest_540_0` |

ffprobe on p102 reads 479 of the 495 packets on each audio track: all 16 tail
frames, including the 8 tagged with description 2, are cut by the edit list.
(The v3 p98 reads all 423 of 423.) This fits the risk named above: if TikTok
reads the file the way ffmpeg does, a `keep` file never shows it the
config switch. INFERENCE: TikTok's reader must actually reach the
description-2 frames, so the tail has to be inside the played timeline.

Control, read the same day: p98 and p99 (v3, posted 11-12 02:54 / 02:57 UTC)
are still `original` / `original`, 2160p, served size equal to the upload,
empty `bitrateInfo`, about 3 days after posting. p97 (nanjing first snow, also a
4K v3 clone, 55,010,915 B, posted 11-10 04:18 UTC) is `original` too, as is the
10-29 junk-method p93. Not ruled out: no v3 file has been uploaded since 11-12,
so a new TikTok change between 11-12 and 11-15 would also explain p102. The
next v3 upload settles it.

**`front`: broken locally (user report, 2026-11-14).** In a local player it
plays once with no audio and then stops, so it was not uploaded. INFERENCE
(not reproduced here): the real audio track now switches sample description
(1 → 2 → 1) before the song starts, and a player that cannot change audio
config mid-track (Windows Media Foundation is the likely one) drops the audio
for the whole song. In v3 the switch comes after the song, so it does no harm.

**What is left for a gap fix.** The tail has to stay after the song and be
played (v3 timing). Two ideas keep v3's audio structure:
- trim the song by the tail's length (~0.39 s) during the base encode, so the
  file ends with the last video frame. The picture plays to the end; the last
  ~0.39 s is silent. A v3 file whose source audio is shorter than its video
  already looks like this, so it is the smallest change.
- stretch the last video frame over the tail, so the picture holds instead of
  going black. Silence stays, and the video track changes.
Neither is built or tested.

**Sound ID (inconclusive).** Raw-served junk-method posts: 10-26 (p91) got an
identified sound, 10-29 (p93) did not. Same structure, different outcome, so
sound identification does not show anything about the file; it is probably
down to the song itself (or timing).

## How other accounts keep quality — it is not our surgery (2026-11-14)

Pulled and dissected three current same-day posts (yt-dlp; served size equals
TikTok's reported size, so these are byte-exact served containers):

| Post | Account | How posted | TikTok label | Served |
|------|---------|-----------|--------------|--------|
| V1 `7708099…308` | @someplace505 | editingnews | `original`/`original` | 4K H.264 2160×3840, 26 Mbps |
| V2 `7708031…798` | @someplace505 | editingnews | `original`/`original` | 1440×2560 **10-bit HDR HEVC**, 14.5 Mbps |
| V3 `7708221…709` | @sylvanism_ (our test) | plain web upload, HD on | `normal`/`normal` | 720p H.264 + HE-AACv2, re-encoded |

**The served originals carry NONE of our fingerprints (VERIFIED).** V1 and V2 are
ordinary `ftyp·moov·free·mdat` faststart files: 1 video + 1 audio, full
`VideoHandler`/`SoundHandler` names (45-byte hdlr), real `mvhd` duration, movie
`udta` present — no junk track, no clone, no second `mdat`, no two-sample-
description, no trailing junk, no silent tail. A box-by-box diff against our
confirmed-working p98 v3 clone shows *ours* has three deliberate additions (the
clone audio track, the second-`mdat` tail with the audio edit list stripped, the
two `mp4a` sample descriptions) that theirs lack — yet both are served raw. So the
container surgery is **not** what earns original serving, and it is exactly what
causes the end-gap (their clean files have no tail and loop seamlessly).

- V2's served file carries TikTok's own `©too` JSON with **`te_is_reencode:0`**
  and `hdr_bit:10` — TikTok itself saying it did not re-encode a 10-bit HDR HEVC.
- V1 kept the uploader's plain `©too = Lavf60.16.100` (an ffmpeg mux).
- `encodedType`/`videoQuality` read `original` for V1 (empty `bitrateInfo`) and V2
  (single `original_1440_0` gear); V3 reads `normal` (`normal_720_0`).
- Page data exposes **no account `region`** (checked all ~33 author keys); served
  CDN hosts are `v16/v19-webapp-prime.tiktok.com` — a quality tier, not a country.

**A served `vid:` tag is TikTok's re-encode stamp, not a stripper miss (VERIFIED).**
Only the re-encoded V3 carries `vid:`/`vid_md5`/`encoder`, in TikTok's server
`keys`/`mdta` layout muxed by ByteDance `Lavf58.76.100`. V1/V2 (original) have
none. Our stripper was run on V3's served file: it detects the `vid:` and removes
it cleanly (0 decode errors, packets identical to source, re-check reports CLEAN).
TikTok writes a fresh `vid:` whenever it re-encodes, so a served `vid:` means
"this was re-encoded", and stripping the source cannot prevent it.

## editingnews is an upload-time extension, not a file method (2026-11-14)

editingnews.com ships as the Chrome extension **"Editing News X re:TikTok"** /
"TikTok Enhancer" — it acts at upload time on tiktok.com. Its own package could
not be pulled (Chrome listing unreachable / likely removed; not installed
locally), but the free sibling add-on **"Maska TikTok Upload Method"** (Firefox
AMO, downloadable) lays the whole category's method bare, and it is **client-side
with no backend**:

1. a `window.fetch` hook that catches TikTok's publish request and strips the
   server-edit flags — deletes `vedit_segment_info`/`canvas_config`/`draft`, sets
   `cloud_edit_is_use_video_canvas=false`, changes `post_type` 2→3;
2. a light MP4 metadata inject (an `©ART` brand tag — V2's `©ART="editingnews.com"`
   is the same move; Maska's is `"Tiktok: Maskafr"`);
3. shoving the file into TikTok's `input[type=file]` via the native setter.

Maska hard-caps uploads at 1080p (it blocks 1440p/4K); editingnews allows 4K/HDR,
so it is a more refined version of the same idea.

**Live capture of TikTok Studio's own upload (2026-11-14, signed in; nothing was
posted — the publish call was intercepted and blocked).** Every flag above exists
in TikTok's current uploader JS, and the real publish payload (`POST
/tiktok/web/project/post/v1/`) for a plain 1440×2560 upload shows:

- **no "serve original / no-transcode" field anywhere** — quality is a server-side
  decision, not a client-settable flag;
- `cloud_edit_is_use_video_canvas` is **already `false`** and there is **no
  `canvas_config`** — so the Maska flag-strip is a **no-op** unless you used
  TikTok's in-page editor; the only real change it makes is `post_type` 2→3,
  effect on the server transcode **unproven**;
- adding a TikTok library sound forces a re-encode on its own.

**INFERENCE.** The flag-strip is copyable but very likely **insufficient** on its
own: there is no client switch for original serving, and this account re-encodes
clean high-res already. editingnews's real edge is most plausibly **account /
region eligibility** (± a file-prep step), which a userscript cannot supply. The
one open empirical test is whether `post_type 2→3` alone flips the server to
passthrough: A/B a clean clip (no added sound) with vs without the userscript and
read `encodedType`. `tiktok-passthrough.user.js` (new, experimental, not wired
into the app) reproduces the flag-strip for that test.

## Browser extension — upload interceptor (built 2026-11-15)

`extension/` recreates the Editing News **two-part** architecture (the one the
`tiktok-quality` / EditingSource repos describe) but drives our **confirmed**
clone engine instead of the dead junk method:

1. **File interception + swap.** `content-intercept.js` (MV3 content script,
   isolated world) catches the video on the TikTok web uploader — `change` on the
   file `<input>` and `drop`, both capture-phase at `document_start`, before
   TikTok reads it — and swaps the optimized bytes back into the input.
2. **Flag-strip.** `inject-flagstrip.js` (MAIN world) is the userscript's
   `fetch`/XHR publish-payload rewrite (`post_type 2→3`, drop `canvas_config` /
   `vedit_segment_info`), toggled from the popup.

**Why a native host, not a pure-JS in-page patcher.** Our confirmed method
RE-ENCODES to H.264 (needs ffmpeg), which can't run cheaply in a page. So the
extension does NOT port the atom/ghost-frame surgery to JS — that is the junk
method TikTok killed on 11-08. Instead `background.js` (service worker) pipes the
bytes over Chrome native messaging to `host/clone_host.exe` (C#, built by
`install_host.ps1` with the in-box csc, referencing `System.Web.Extensions.dll`
for JSON), which shells out to the existing `optimizer.exe -clone` in the exact
confirmed flag order and streams the patched MP4 back. The popup exposes
resolution / bitrate / tail / flag-strip plus a native-host health check.

Native-messaging detail: host→browser messages are capped at 1 MB, so the file is
chunked both ways (512 KiB raw per frame), and the host emits `{t:"progress"}`
heartbeats during the encode so the MV3 service worker is not idle-killed on a
long 4K job. The extension fails **open** — if optimizing errors, the original is
uploaded, so a post always goes out.

**Install:** load `extension/` unpacked, copy the extension ID, run
`install_host.ps1 -ExtensionId <id>`, reload. Full steps in `extension/README.md`.

**Status (2026-11-15): host↔optimizer path VERIFIED locally; NOT yet tested by
upload.** A `clone_host.exe --run` self-test on a generated 3 s 640×360 clip ran
`optimizer.exe -clone`, exited 0 (all clone checks passed) in 1.5 s, and produced
a 1 video + 2 audio file that `-clone -verify` passes. The browser half (content
script interception, native-messaging bridge, file swap) is written and compiles
but has NOT been run against a live TikTok page here, and nothing has been posted.

**Caveat — this does not settle whether the surgery is the lever.** Per the
2026-11-14 notes above, clean trick-free files are being served `original`, so the
file method may no longer be what earns it. The extension is built so the
flag-strip can be A/B tested alone (popup: turn "Optimize uploaded file" off,
leave "Strip cloud-edit flags" on) — the one open empirical test — without giving
up the confirmed file method when you want it.

## RTXFury pivoted off the surgery — clean-remux "passthrough" (2026-11-15)

A new RTXFury output (`rtx-optimized-ca17.mp4`, from "RTX Server Optimize"),
reverse-engineered against its source (`americangirl.mp4`, H.264 1440×1080 +
AAC-LC stereo): **RTXFury has abandoned the clone surgery.** The new output is a
clean faststart remux:

- **video STREAM-COPIED** — the H.264 elementary stream is byte-identical to the
  input, and the bitrate (13,837,567 bps), frame count (316) and the source
  `Mainconcept` handler name are all preserved. No re-encode, zero loss. (It
  re-encodes HEVC → H.264; `americangirl` was already H.264, so it was copied.)
- **1 video + 1 audio** (box tree: 2 trak, 1 mp4a, 1 esds, 1 mdat) — no clone
  track, no two-sample-description, no second `mdat`, no trailing junk, no RTX
  `©too` tag.
- audio stream-copied (824 → 823 frames, last one trimmed to align), brand
  **mp42 → isom** (+ `isomiso2avc1mp41`), `moov` moved to the front
  (`ftyp · moov · free · mdat`), `mvhd` v0 real duration, `Lavf60` mux.
- this also explains "fixes sound detection": it now REQUIRES AAC-LC input and
  rejects HE-AAC (e.g. p77), because it copies the audio instead of re-encoding.

This is the **same clean profile as @someplace505's served originals** (1 video
+ 1 audio, full handlers, no tricks). Three signals now agree — our flag-strip
test came back `normal`, @someplace505's clean files are served `original`, and
RTXFury (which invented the surgery) has dropped it — so the current play is most
likely a **clean, non-re-encoded H.264/AAC-LC faststart file**, with eligibility
decided server-side.

**New mode: `-passthrough` / "Clean passthrough".** The app reproduces this with
ffmpeg (`Tools.BuildPassthrough` in `src/Optimizer.cs`): stream-copy the video
when it is already H.264/HEVC (else re-encode to H.264; `-force264` forces it for
HEVC), copy the audio when it is already AAC-LC (else re-encode to AAC-LC, so
HE-AAC sources work where RTXFury rejects them), `-movflags +faststart`, brand
isom, `-map_metadata 0`. No container surgery. `Tools.VerifyPassthrough` checks 1
video + 1 audio, video intact (H.264/HEVC, 0 decode errors), audio AAC-LC, moov
before mdat, single mdat / no junk, brand isom. GUI: the "Clean passthrough"
checkbox (mutually exclusive with "New TikTok method"); CLI: `-passthrough` /
`-clean` / `-force264`.

Verified locally 2026-11-15: on an H.264 + AAC-LC clip the output video is
byte-identical to the source (stream-copied), the layout is
`ftyp · moov · free · mdat`, and all `VerifyPassthrough` checks pass; the clone
engine still verifies clean (no regression).

**Status: NOT upload-tested on this account.** The point is the A/B — clean
passthrough vs the clone surgery vs a plain upload, same account, reading
`encodedType`. If passthrough is served `original`, it beats the clone (no
re-encode, no end-gap); if it comes back `normal` like the earlier clean uploads
(@sylvanism_, @papercheques), the lever is account/region eligibility, which no
file method supplies. The clone engine stays the confirmed method until this A/B
says otherwise.

### The newest RTXFury kept a junk audio tail (2026-11-15, `dd39`) — VERIFIED

A newer real RTXFury output, `rtx-optimized-dd39.mp4` (from source
`exclusive.mp4`, H.264 Main 1440×1080 4:3 + AAC-LC), shows the "fully clean"
read above was premature. It is still a clean faststart remux — the **video
elementary stream is byte-identical to the source** (same MD5, 260 packets, no
re-encode), 1 video + 1 audio, brand isom, moov moved to front with the
stale-moov `free` stub kept — but it carries one deliberate piece of surgery the
`ca17` sample did not:

- **Box layout:** `ftyp · moov · free(=stale moov) · free(8) · mdat(main) ·
  mdat(68)`. The source ended cleanly at its single `mdat`, so RTXFury **added**
  the trailing 68-byte `mdat`.
- **The trailing `mdat` = the last 4 audio frames, corrupted.** Source and
  output both have **678 audio packets** (count preserved). In the output the
  last 4 packets are **15 bytes each, at the trailing mdat**, each the fixed
  constant `a0 98 00 40 01 28 00 64 00 00 03 50 00 00 38`. Decoding the audio
  track yields **5 errors** — the tail is undecodable. ffprobe still reads all
  678 packets (the audio edit list is kept and covers them), so the file parses
  as a clean 1v+1a file; only a decoder that actually demuxes the tail fails.
- **No end gap:** frame count and timeline are unchanged, so unlike the clone
  v3 the file ends with the last video frame (format duration = source).

So the mechanism is a stealthier revival of the dead junk-track idea: instead of
a whole third track of junk (which the served-`original` clean files lack), it
poisons the last few frames of the *real* audio track and relocates them to a
trailing `mdat`. This is the one structural feature that separates a
served-`original` RTXFury file from a plain clean remux.

**App change — passthrough now reproduces it (`Mp4.AddJunkAudioTail`).** After
the clean ffmpeg remux, the passthrough engine rewrites the audio track to one
sample per chunk, sets the last 4 stsz entries to 15, appends a trailing `mdat`
of 4 × the 15-byte constant, and points those 4 samples at it (moov is at the
front, so the resized moov's chunk offsets are re-derived; `stts`, the edit list
and sample groups are untouched, so count and duration are preserved). It fails
**open** — if the surgery throws, the clean file is kept, so a post always has a
valid file. `VerifyPassthrough` now expects the tail: "Audio junk tail (breaks
decoders)" wants > 0 decode errors and "Trailing junk mdat" wants a second
`mdat` after the main one; "Audio is AAC-LC" checks codec/profile only.

Verified locally 2026-11-15 on the user's upscaled 4K source
(`exclusive_2x_2880x2160_prob-3.mp4`, H.264 Constrained Baseline 2880×2160 4:3):
output video stream-copied byte-for-byte (MD5 match), 677→677 audio packets,
last 4 = 15 bytes in a **68-byte trailing `mdat` byte-identical to `dd39`'s**, 5
decode errors on the tail, format duration = source (no end gap), all 7
`VerifyPassthrough` checks pass. Still NOT upload-tested. The earlier `ca17`
passthrough (truly clean, no tail) is superseded by this.

**Caveat (resolution/profile confound).** The user's "doesn't work" file is
2880×2160 (4:3) **Constrained Baseline**, upscaled 2×; the working `dd39` is
1440×1080 **Main**. If a fixed-with-tail 4K upload still comes back `normal`, the
lever may be the resolution/profile or account/region eligibility, not the tail.
The junk tail was unambiguously missing from the old passthrough output and
present in every served-`original` RTXFury file, so it is the right first fix,
but only an upload reading `encodedType` settles it.

---

> Everything below is the original reverse-engineering writeup, kept verbatim as
> the specification for the app above.

# RTXFury: Reverse-Engineering Notes

Compiled: 2026-10-24
Labels used in this document:

* VERIFIED: observed directly in the file or in TikTok's page data.
* SOURCE: claimed by a third party (cited).
* INFERENCE: reasoning from the evidence; not proven.

## Sample and method

Sample: one public TikTok post whose served file carries the tool's signature
(see 5.1). The poster is a private individual and is not identified here.

Steps:

1. Fetched the post's public web page and read TikTok's embedded page data
   (the **UNIVERSAL_DATA_FOR_REHYDRATION** JSON block).
2. Downloaded the stream TikTok serves to viewers (27,400,402 bytes).
3. Probed it with ffprobe (format, streams, tags, dispositions).
4. Walked the MP4 box tree with a small Python script: box offsets and sizes,
   hdlr names, mvhd/tkhd/mdhd timestamps, edit lists.
5. Expanded each track's sample tables (stsz, stco, stsc, stts) to map every
   sample to its byte offset and size.
6. Decoded each track separately with ffmpeg and counted errors.
7. Searched the start of the video stream for embedded encoder strings
   (x264, Lavc, NVENC, VideoToolbox, etc.).

## 1. TikTok-side evidence (VERIFIED)

From TikTok's page data for the post:

```
encodedType   original
videoQuality  original
definition    2160p
width/height  2160 x 3840
codecType     h264
bitrateInfo   one entry only: "original_2160_0", 27,400,402 bytes
```

Normal TikTok posts list several re-encoded quality levels. This one lists a
single "original" gear.

The served file still contains the tool's signature tag and the junk track.
A TikTok re-encode, or even a remux, would have rewritten both. So the served
file is the uploaded container, not a TikTok-produced one.

## 5. File analysis (VERIFIED)

### 5.1 Container and signature

```
Brand            isom, minor 512, compatible isomiso2avc1mp41
Duration         9.218 s
Overall bitrate  23,779,020 bps
Streams          3 (1 video, 2 audio)
Encoder tag      "RTX Fury Quality Method - https://www.rtxfury.xyz/ - v21.2"
```

The encoder tag is stored as a ©too atom inside the video track's udta box.
Muxers normally put it in the movie-level udta, so the placement is itself a
fingerprint.

### 5.2 Box layout

```
ftyp
moov
  mvhd                (version 1, created=0, modified=0)
  trak #1  video      hdlr name "oHandler", edit list, udta/meta/ilst/©too
  trak #2  audio      hdlr name "dHandler", edit list, sgpd/sbgp
  trak #3  audio      hdlr name "dHandler", NO edit list, sgpd/sbgp
mdat                  ends at byte 27,371,810
[28,592 bytes of trailing data outside any box]
```

### 5.3 Track names

ffmpeg's default handler names are "VideoHandler" and "SoundHandler". This
file has "oHandler" and "dHandler", the last 8 characters of each. The hdlr
boxes are 41 bytes instead of the 45 ffmpeg writes.
INFERENCE: the file started as an ffmpeg-muxed MP4 and was then rewritten by
the tool's own box writer, which drops 4 bytes of the name (bug or
deliberate; can't tell which).

### 5.4 Timestamps

Every creation and modification time (mvhd, tkhd, mdhd) is 0. The tool wipes
them. No device model, location or date metadata exists in the file.

### 5.5 Video track (track 1)

```
Codec         H.264 High, level 5.2, yuv420p, BT.709
Size / rate   2160 x 3840, 60 fps (553 frames)
Bitrate       23,601,347 bps
Timescale     1/1,000,000 (frame durations alternate 16,667 / 16,666)
Keyframes     5
B-frames      none (has_b_frames=0), refs=1
Encoder SEI   none found
Decode        0 errors
```

INFERENCE: no B-frames and 1 reference frame is typical of a hardware or
phone export, not ffmpeg/x264 defaults. This reflects the poster's export,
and is consistent with the site's claim that the tool does not re-encode
video.

### 5.6 Real audio track (track 2)

```
AAC-LC, 44.1 kHz stereo, ~128 kbps
397 samples, 1 sample per chunk
stts: 249 x 1024, 1 x 1025, 147 x 1024
Decode: 0 errors
```

### 5.7 Junk audio track (track 3)

```
AAC-LC, 44.1 kHz stereo (same sample description as track 2)
3,970 samples in 398 chunks
stsc: chunks 1-397 hold 1 sample each; chunk 398 holds 3,573 samples
stts: 249 x 1024, 1 x 1025, 147 x 1024, then 3,573 x 1
```

Sample mapping:

* 397 samples have exactly the same byte offset and size as track 2's
  samples. Track 3 points at the real audio; the bytes are not duplicated.
* The other 3,573 samples are 8 bytes each, at consecutive offsets starting
  at byte 27,371,810, i.e. after the end of mdat, outside any box.
* Each junk sample contains the same bytes: 00 00 00 04 00 00 00 00.
* Each junk sample lasts 1 tick (1/44,100 s); together they add ~81 ms.

Formula: junk count = 9 x real count (397 x 9 = 3,573; total 3,970 =
10 x 397). INFERENCE: the tool generates this from a fixed template.

Decoding track 3 with ffmpeg produces exactly 3,573 errors:
"Input buffer exhausted before END element found"
"Error submitting packet to decoder: Invalid data found when processing input"

## 6. How the bypass probably works (INFERENCE)

TikTok re-encodes uploads into several quality levels. When it processes
this file, the third track holds thousands of undecodable packets whose data
lies outside the normal box structure. The most likely explanation is that
TikTok's processing job fails or rejects the file, and the platform falls
back to serving the raw upload, labelled "original".

Supporting evidence: the "original" labels, the single quality level, and the
tool's own instruction not to edit in TikTok (an in-app edit would trigger a
fresh encode from a clean source).

Not proven: TikTok's server-side behaviour cannot be observed directly.
Also unknown is which change triggers it: the junk track itself, the junk
sitting outside mdat, the missing edit list on track 3, the clipped handler
names, or a combination.

## Fingerprints (for identifying files made by this tool)

* ©too tag "RTX Fury Quality Method - https://www.rtxfury.xyz/ - v21.2",
  inside the video track's udta
* hdlr names "oHandler" / "dHandler" (41-byte hdlr boxes)
* All mvhd/tkhd/mdhd timestamps zeroed
* A third audio track with 10x the real audio's sample count, no edit list,
  and a final chunk of 9x-count 8-byte samples
* Trailing data after mdat, outside any box, of repeated
  00 00 00 04 00 00 00 00

## 10. Sources

* RTXFury homepage: https://www.rtxfury.xyz/
* RTXFury how-to: https://www.rtxfury.xyz/howto
* RTXFury optimizer: https://www.rtxfury.xyz/patcher
* @rtx_fury tutorial: https://www.tiktok.com/@rtx_fury/video/7678384856761453846
* TikHaze upload guide: https://tikhaze.com/guides/upload-tiktok-high-quality-no-compression
