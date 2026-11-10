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
> **confirmed working** by upload — it re-encodes to 1080p H.264 and reproduces
> RTXFury v2's two-audio-track + two-sample-description structure. See
> "New clone engine" below. The junk-track method described first is kept for
> reference but no longer works on TikTok.

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
   box.

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
  the junk method (see "New clone engine" below): re-encode to 1080p H.264 +
  duplicate audio track. `optimizer.exe -clone in.mov out.mp4`,
  `optimizer.exe -clone -mux video.mp4 song.mp3 out.mp4`,
  `optimizer.exe -clone -verify out.mp4`. Note: the exe is a GUI-subsystem
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
   `-bf 0`** (tries `h264_nvenc`, falls back to libx264), downscale to ≤1080p,
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
(valid, not junk), equal packet counts, video decodes clean and is ≤1080p H.264,
and the container parses.

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

The clone engine re-encodes (one generation of loss) and caps at 1080p — the
price of the new method; a 1080p upload served raw still beats a 4K upload
crushed to 540p.

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
