#!/usr/bin/env python3
"""
strip_metadata.py - detect and remove app metadata (TikTok / ByteDance "Aweme"
tags and similar) from MP4 / MOV files without re-encoding.

    python strip_metadata.py detect  <file>
    python strip_metadata.py strip   <input> <output> [--strip-sei]
    python strip_metadata.py verify  <input> <output>

Only the Python standard library is used. ffmpeg / ffprobe must be on PATH or
sit next to this script.

Where the tags can live, and what removes them:
  * moov metadata boxes (udta, meta/ilst, keys, ©cmt / ©too, track udta,
    QuickTime "com.*" keys)       -> removed by strip() (container rewrite)
  * top-level uuid / free boxes, data after the last box
                                   -> removed by strip() (ffmpeg writes a new file)
  * the video bitstream itself (H.264 / HEVC SEI user-data NAL units)
                                   -> survive "-c copy"; need --strip-sei
"""

import argparse
import json
import os
import shutil
import struct
import subprocess
import sys

# Byte patterns searched for. Matching is case-insensitive.
SIGNATURES = [
    b"com.ss.iphone.ugc.Aweme",
    b"com.ss.android.ugc",
    b"Aweme",
    b"sec_uid",
    b"bytedance",
    b"vid:v",          # TikTok video-ID comment, e.g. "vid:v2800fgi..."
    b"aigc_info",
    b"tiktok",
    b"musically",
]

# Boxes whose children are boxes. "meta" is special-cased below.
CONTAINERS = {
    b"moov", b"trak", b"mdia", b"minf", b"stbl", b"dinf", b"edts",
    b"udta", b"ilst", b"tref", b"mvex", b"moof", b"traf", b"sinf", b"schi",
}

CHUNK = 8 * 1024 * 1024


# --------------------------------------------------------------------------
# tools
# --------------------------------------------------------------------------

def find_tool(name):
    here = os.path.dirname(os.path.abspath(__file__))
    for cand in (name + ".exe", name):
        local = os.path.join(here, cand)
        if os.path.isfile(local):
            return local
    found = shutil.which(name)
    if not found:
        sys.exit(f"error: {name} not found on PATH or next to this script")
    return found


def run(cmd):
    return subprocess.run(cmd, capture_output=True, text=True,
                          encoding="utf-8", errors="replace")


def ffprobe_json(path):
    r = run([find_tool("ffprobe"), "-v", "error", "-print_format", "json",
             "-show_format", "-show_streams", path])
    if r.returncode != 0:
        sys.exit(f"error: ffprobe could not read {path}:\n{r.stderr.strip()}")
    return json.loads(r.stdout)


# --------------------------------------------------------------------------
# box walking
# --------------------------------------------------------------------------

def read_header(f, pos, end):
    """Return (type, header_size, box_size) for the box at pos, or None."""
    f.seek(pos)
    hdr = f.read(8)
    if len(hdr) < 8:
        return None
    size, btype = struct.unpack(">I4s", hdr)
    hsize = 8
    if size == 1:
        ext = f.read(8)
        if len(ext) < 8:
            return None
        size = struct.unpack(">Q", ext)[0]
        hsize = 16
    elif size == 0:
        size = end - pos
    if size < hsize or pos + size > end:
        return None
    return btype, hsize, size


def meta_child_offset(f, payload_start):
    """MP4 'meta' is a full box (4 bytes version/flags before children);
    QuickTime 'meta' is not. Tell them apart by where the first child's
    type field sits: +4 for QuickTime, +8 for MP4."""
    f.seek(payload_start + 8)
    peek = f.read(4)
    return 4 if peek in (b"hdlr", b"keys", b"ilst") else 0


def walk(f, start, end, path, out):
    """Collect (path, type, offset, size, payload_start) for every box."""
    pos = start
    while pos + 8 <= end:
        h = read_header(f, pos, end)
        if h is None:
            out.append((path + ["<garbage>"], b"????", pos, end - pos, pos))
            return
        btype, hsize, size = h
        name = btype.decode("latin-1")
        out.append((path + [name], btype, pos, size, pos + hsize))
        if btype in CONTAINERS or btype == b"meta":
            child = pos + hsize
            if btype == b"meta":
                child += meta_child_offset(f, pos + hsize)
            walk(f, child, pos + size, path + [name], out)
        pos += size
    if pos < end and not path:
        out.append((["<trailing data>"], b"????", pos, end - pos, pos))


def search(f, start, length, needles):
    """Yield (needle, absolute_offset) for each case-insensitive hit."""
    lowered = [n.lower() for n in needles]
    overlap = max(len(n) for n in needles) - 1
    pos, carry = start, b""
    end = start + length
    f.seek(start)
    while pos < end:
        data = f.read(min(CHUNK, end - pos))
        if not data:
            break
        buf = (carry + data).lower()
        base = pos - len(carry)
        for n in lowered:
            i = buf.find(n)
            while i != -1:
                # skip hits fully inside the carried-over overlap (already seen)
                if i + len(n) > len(carry):
                    yield n, base + i
                i = buf.find(n, i + 1)
        carry = data[-overlap:] if overlap else b""
        pos += len(data)


def context(f, offset, width=48):
    f.seek(max(0, offset - 8))
    raw = f.read(width)
    return "".join(chr(b) if 32 <= b < 127 else "." for b in raw)


# --------------------------------------------------------------------------
# 1. detect
# --------------------------------------------------------------------------

def detect_signatures(file_path, quiet=False):
    """Scan an MP4/MOV for app signatures.

    Returns a dict:
      found      -> True if any signature exists anywhere in the file
      metadata   -> hits inside moov / metadata boxes / extra boxes
                    (removed by strip_metadata)
      bitstream  -> hits inside mdat (SEI user data in the video stream;
                    removed only with --strip-sei)
      tags       -> ffprobe's view of format and stream tags
    """
    size = os.path.getsize(file_path)
    boxes = []
    with open(file_path, "rb") as f:
        walk(f, 0, size, [], boxes)

        # Hits are attributed to the innermost box that contains them.
        leaves = sorted(boxes, key=lambda b: b[3])
        metadata, bitstream, seen = [], [], set()
        for path, btype, off, bsize, _ in boxes:
            if len(path) != 1:
                continue  # scan each top-level box once
            for needle, at in search(f, off, bsize, SIGNATURES):
                if at in seen:
                    continue
                seen.add(at)
                owner = next((b for b in leaves if b[2] <= at < b[2] + b[3]),
                              None)
                where = "/".join(owner[0]) if owner else "?"
                hit = {"signature": needle.decode(), "offset": at,
                       "box": where, "context": context(f, at)}
                (bitstream if path[0] == "mdat" else metadata).append(hit)

    probe = ffprobe_json(file_path)
    tags = {"format": probe.get("format", {}).get("tags", {})}
    for s in probe.get("streams", []):
        tags[f"stream {s['index']} ({s.get('codec_type')})"] = s.get("tags", {})

    result = {
        "file": file_path,
        "found": bool(metadata or bitstream),
        "metadata": metadata,
        "bitstream": bitstream,
        "top_level_boxes": [b[0][0] for b in boxes if len(b[0]) == 1],
        "tags": tags,
    }
    if not quiet:
        print_report(result)
    return result


def print_report(r):
    print(f"File: {r['file']}")
    print(f"Top-level boxes: {', '.join(r['top_level_boxes'])}")
    print("Tags (ffprobe):")
    for scope, t in r["tags"].items():
        print(f"  {scope}: {t if t else '-'}")
    if not r["found"]:
        print("RESULT: no app signatures found.")
        return
    if r["metadata"]:
        print(f"Signatures in container metadata ({len(r['metadata'])}):")
        for h in r["metadata"]:
            print(f"  {h['signature']!r:28} @ {h['offset']:>10}  in {h['box']}"
                  f"   {h['context']}")
    if r["bitstream"]:
        print(f"Signatures inside the media data ({len(r['bitstream'])}) - "
              "probably SEI user data in the video stream:")
        for h in r["bitstream"][:20]:
            print(f"  {h['signature']!r:28} @ {h['offset']:>10}   {h['context']}")
        if len(r["bitstream"]) > 20:
            print(f"  ... {len(r['bitstream']) - 20} more")
        print("  These survive a plain stream copy; use --strip-sei to drop them.")
    print("RESULT: app signatures FOUND.")


# --------------------------------------------------------------------------
# 2. strip
# --------------------------------------------------------------------------

def sei_filter(probe):
    """Bitstream filter that removes SEI NAL units for the video codec.
    SEI carries no picture data, so the decoded frames are unchanged."""
    codecs = {s.get("codec_name") for s in probe.get("streams", [])
              if s.get("codec_type") == "video"}
    if "h264" in codecs:
        return "filter_units=remove_types=6"
    if "hevc" in codecs:
        return "filter_units=remove_types=39|40"
    return None


def strip_metadata(input_path, output_path, strip_sei=False):
    """Rewrite the file with all metadata removed. Video and audio are
    stream-copied (-c copy): no re-encode, no quality change."""
    if os.path.abspath(input_path) == os.path.abspath(output_path):
        sys.exit("error: output must be a different file from input")

    probe = ffprobe_json(input_path)
    cmd = [
        find_tool("ffmpeg"), "-hide_banner", "-v", "error", "-y",
        "-i", input_path,
        # keep only video and audio; drops data / timecode / subtitle tracks
        # (TikTok and iOS sometimes carry metadata tracks)
        "-map", "0:v?", "-map", "0:a?",
        "-c", "copy",
        # drop global, per-stream and chapter metadata
        "-map_metadata", "-1",
        "-map_metadata:s:v", "-1",
        "-map_metadata:s:a", "-1",
        "-map_chapters", "-1",
        # bitexact: stop ffmpeg adding its own "encoder=Lavf..." tags
        "-fflags", "+bitexact",
        "-flags:v", "+bitexact",
        "-flags:a", "+bitexact",
        # neutral handler names instead of anything copied from the source
        "-metadata:s:v", "handler_name=VideoHandler",
        "-metadata:s:a", "handler_name=SoundHandler",
        "-movflags", "+faststart",
    ]
    if strip_sei:
        bsf = sei_filter(probe)
        if bsf:
            cmd += ["-bsf:v", bsf]
        else:
            print("warning: --strip-sei only supports H.264 / HEVC; skipped")
    cmd.append(output_path)

    r = run(cmd)
    if r.returncode != 0:
        sys.exit(f"error: ffmpeg failed:\n{r.stderr.strip()}")
    print(f"Wrote {output_path}")
    return output_path


# --------------------------------------------------------------------------
# 3. verify
# --------------------------------------------------------------------------

def stream_md5(path, selector):
    """MD5 of the raw packets of one stream type (no decode)."""
    r = run([find_tool("ffmpeg"), "-v", "error", "-i", path,
             "-map", f"0:{selector}?", "-c", "copy", "-f", "md5", "-"])
    return r.stdout.strip() if r.returncode == 0 else None


def decode_errors(path):
    r = run([find_tool("ffmpeg"), "-v", "error", "-i", path,
             "-map", "0:v?", "-map", "0:a?", "-f", "null", "-"])
    return [l for l in r.stderr.splitlines() if l.strip()]


def verify(input_path, output_path):
    ok = True
    print("== signatures in output ==")
    res = detect_signatures(output_path)
    if res["found"]:
        ok = False

    print("\n== playable (full decode) ==")
    errs = decode_errors(output_path)
    if errs:
        ok = False
        print(f"FAIL: {len(errs)} decode error line(s), first: {errs[0]}")
    else:
        print("PASS: decodes with 0 errors")

    print("\n== lossless (packet checksums, input vs output) ==")
    for sel, label in (("v", "video"), ("a", "audio")):
        a, b = stream_md5(input_path, sel), stream_md5(output_path, sel)
        if a == b:
            print(f"PASS: {label} packets identical ({b})")
        else:
            # Expected for video when --strip-sei was used: the SEI NAL units
            # are gone, so the packet bytes differ while the frames do not.
            print(f"DIFF: {label} packets differ ({a} vs {b})")
            if label == "video":
                same = frame_md5(input_path) == frame_md5(output_path)
                print(f"      decoded frames {'identical' if same else 'DIFFER'}")
                ok = ok and same
            else:
                ok = False

    print("\nOVERALL:", "PASS" if ok else "FAIL")
    return ok


def frame_md5(path):
    r = run([find_tool("ffmpeg"), "-v", "error", "-i", path,
             "-map", "0:v:0", "-f", "md5", "-"])
    return r.stdout.strip()


# --------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawTextHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    d = sub.add_parser("detect", help="report app signatures in a file")
    d.add_argument("file")
    d.add_argument("--json", action="store_true", help="print JSON instead")

    s = sub.add_parser("strip", help="write a copy with metadata removed")
    s.add_argument("input")
    s.add_argument("output")
    s.add_argument("--strip-sei", action="store_true",
                   help="also drop SEI NAL units from H.264/HEVC video")
    s.add_argument("--no-verify", action="store_true")

    v = sub.add_parser("verify", help="check a stripped file against its source")
    v.add_argument("input")
    v.add_argument("output")

    a = ap.parse_args()
    if a.cmd == "detect":
        r = detect_signatures(a.file, quiet=a.json)
        if a.json:
            print(json.dumps(r, indent=2))
        sys.exit(1 if r["found"] else 0)
    if a.cmd == "strip":
        strip_metadata(a.input, a.output, a.strip_sei)
        if not a.no_verify:
            print()
            sys.exit(0 if verify(a.input, a.output) else 1)
    if a.cmd == "verify":
        sys.exit(0 if verify(a.input, a.output) else 1)


if __name__ == "__main__":
    main()
