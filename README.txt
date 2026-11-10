Quality Optimizer
=================

What it does
------------
Takes a video and makes a new MP4 that keeps its upload quality (the
"RTX Fury" container method - see CLAUDE.md for the full write-up). Your
video is NOT re-encoded; only the container is modified.

How to use
----------
1. Double-click  optimizer.exe
2. Drag a video onto the window, or click "Choose Video..."
3. Choose where to save it.
4. You get a new file named  <yourvideo>_optimized.mp4

Upload that optimized file. Do not edit it inside the app you upload to
(editing there forces a fresh re-encode from a clean copy).

Re-encode at a higher bitrate (optional)
---------------------------------------
Tick "Re-encode video at" and pick 20-30 Mbps before you choose a video. The
video is then re-encoded to H.264 at that bitrate instead of being copied, and
the optimization is applied as usual. It works with Add Song too. It takes
longer, and it cannot add detail the source does not have: a higher bitrate
only helps if the source is low-bitrate or HEVC and you want an H.264 file.
Leave it off to keep your video exactly as it is.
Command line:  optimizer.exe -bitrate 25 "in.mov" "out.mp4"

Add your own song / audio
-------------------------
To put a specific track on your clip, click "Add Song + Optimize...", pick the
VIDEO, then the audio file (mp3/m4a/wav/aac/...). It attaches the audio WITHOUT
re-encoding the video, then optimizes and verifies in one step. The output
length matches your video (audio padded with silence if shorter, trimmed if
longer).

Do this instead of adding a TikTok library sound inside the app: any sound you
add in TikTok forces a re-encode and undoes the optimization. Only use audio
you have the right to use.

How to check the result
-----------------------
The app checks every file for you. Right after it saves an optimized MP4 it
pops up a report with PASS/WARN for each fingerprint. You can also click
"Verify a File..." in the app to check any MP4 you already have. It reports:
  - 1 video + 2 audio tracks
  - junk track has 10x the real audio's packets
  - junk track is undecodable (that is intended)
  - video still decodes with 0 errors (no quality loss)
  - timestamps zeroed
  - trailing 00 00 00 04 00 00 00 00 bytes
  - box layout ftyp, moov, mdat, then the junk bytes
  - movie header: duration "unknown", no movie-level metadata
A file the tool made passes all of them (files from older versions of this
tool fail the box-layout or movie-header check - run the original video
through again); an un-optimized file fails several,
which is how you tell them apart.

The ONLY real-world test is to upload it and confirm the platform serves it
at original quality; that depends on the platform, not the file.

(Command line, if you want it:  optimizer.exe -verify "yourfile.mp4"  prints
the same report. verify.bat does too, but needs PowerShell enabled.)

Remove app tags first (stripper.exe)
------------------------------------
Videos saved from TikTok carry app tags (Aweme, com.ss.iphone.ugc.Aweme,
sec_uid, a "vid:" ID comment). stripper.exe removes them WITHOUT re-encoding:
1. Double-click  stripper.exe
2. Drop the video on it (or "Choose Video...") and pick where to save.
3. You get  <yourvideo>_clean.mp4  (or .mov), checked automatically:
     - no app tags left
     - plays (decodes with 0 errors)
     - video/audio packets identical to the source (nothing re-encoded)
"Check a File..." only scans a file and lists any tags it finds.
If the check says tags are "inside the video data", tick
"Also strip SEI from video stream" and strip again (picture is unchanged).

Run stripper.exe BEFORE optimizer.exe: strip -> optimize -> upload.
Stripping an optimized file removes the optimization.

Command line:  stripper.exe -check "file.mp4"
               stripper.exe [-sei] "in.mp4" "out.mp4"

Requirements
------------
- Windows (uses the built-in .NET Framework - nothing to install).
- ffmpeg.  Either:
    * ffmpeg is already on your PATH (it is, on this machine), OR
    * put ffmpeg.exe and ffprobe.exe in this same folder.
  Get ffmpeg from https://www.gyan.dev/ffmpeg/builds/ if you ever need it.

Command line (optional)
-----------------------
    optimizer.exe  input.mov  output.mp4
Runs with no window and prints a one-line summary.

Rebuild from source (optional)
------------------------------
    build.bat
Compiles src\Optimizer.cs into optimizer.exe and src\Stripper.cs into
stripper.exe using the C# compiler that ships with Windows. No SDK needed.
