// TikTok Quality Optimizer
// Standalone Windows app. Takes an input video and writes an "optimized" MP4
// that preserves upload quality by applying the container method documented in
// CLAUDE.md (section 5-6): a third audio track whose tail is undecodable junk,
// plus trailing junk bytes, which makes the receiving platform fall back to
// serving the raw upload instead of a re-encode.
//
// Pipeline:
//   1. ffmpeg normalizes the input to a clean H.264 + AAC MP4 (video stream is
//      copied, never re-encoded, whenever the source is already compatible).
//   2. This program parses the MP4 box tree, adds the junk audio track, zeroes
//      all creation/modification timestamps, gives the movie header an unknown
//      duration, drops movie-level metadata, clips the handler names, and
//      rewrites the file in the reference layout: ftyp, moov, mdat, then the
//      junk bytes after mdat.
//   3. It then verifies the result against the CLAUDE.md fingerprints.
//
// Target: C# 5 / .NET Framework 4.8 (the in-box csc.exe), so it compiles and
// runs on a stock Windows machine with no runtime to install. ffmpeg/ffprobe
// are expected on PATH or next to this exe.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace QualityOptimizer
{
    // ---- Big-endian byte helpers -------------------------------------------
    static class BE
    {
        public static uint RU32(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }
        public static ulong RU64(byte[] b, int o)
        {
            return ((ulong)RU32(b, o) << 32) | RU32(b, o + 4);
        }
        public static void WU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
        public static void WU64(byte[] b, int o, ulong v)
        {
            WU32(b, o, (uint)(v >> 32)); WU32(b, o + 4, (uint)v);
        }
        public static string Ascii(byte[] b, int o, int n)
        {
            return Encoding.ASCII.GetString(b, o, n);
        }
    }

    // ---- Minimal MP4 box tree ----------------------------------------------
    class Box
    {
        public string Type;
        public bool Container;
        public byte[] Payload;        // leaf: bytes after the 8-byte header
        public List<Box> Kids;        // container children
        public long Size;             // computed on serialize

        static readonly HashSet<string> CONT =
            new HashSet<string>(new string[] { "moov", "trak", "mdia", "minf", "stbl" });

        public static Box ParseBox(byte[] d, ref int o)
        {
            int start = o;
            long size = BE.RU32(d, o);
            string type = BE.Ascii(d, o + 4, 4);
            int hdr = 8;
            if (size == 1) { size = (long)BE.RU64(d, o + 8); hdr = 16; }
            else if (size == 0) { size = d.Length - o; }

            Box b = new Box();
            b.Type = type;
            if (CONT.Contains(type))
            {
                b.Container = true;
                b.Kids = new List<Box>();
                int p = start + hdr;
                int boxEnd = start + (int)size;
                while (p < boxEnd)
                {
                    int q = p;
                    Box c = ParseBox(d, ref q);
                    b.Kids.Add(c);
                    p = q;
                }
            }
            else
            {
                int plen = (int)(size - hdr);
                b.Payload = new byte[plen];
                Array.Copy(d, start + hdr, b.Payload, 0, plen);
            }
            o = start + (int)size;
            return b;
        }

        public long ComputeSize()
        {
            if (Container)
            {
                long p = 0;
                foreach (Box k in Kids) p += k.ComputeSize();
                Size = 8 + p;
            }
            else
            {
                Size = 8 + Payload.Length;
            }
            return Size;
        }

        public void Write(MemoryStream ms)
        {
            byte[] h = new byte[8];
            BE.WU32(h, 0, (uint)Size);
            byte[] t = Encoding.ASCII.GetBytes(Type);
            Array.Copy(t, 0, h, 4, 4);
            ms.Write(h, 0, 8);
            if (Container) { foreach (Box k in Kids) k.Write(ms); }
            else ms.Write(Payload, 0, Payload.Length);
        }

        public Box Clone()
        {
            Box b = new Box();
            b.Type = Type;
            b.Container = Container;
            if (Container)
            {
                b.Kids = new List<Box>();
                foreach (Box k in Kids) b.Kids.Add(k.Clone());
            }
            else
            {
                b.Payload = (byte[])Payload.Clone();
            }
            return b;
        }

        public Box Kid(string t)
        {
            if (Kids == null) return null;
            foreach (Box k in Kids) if (k.Type == t) return k;
            return null;
        }
    }

    // ---- The container transform -------------------------------------------
    static class Mp4
    {
        // A top-level box: where it starts and its full size (header included).
        struct Top { public string Type; public int Start; public long Size; }

        static List<Top> TopLevel(byte[] d)
        {
            List<Top> r = new List<Top>();
            int o = 0;
            while (o + 8 <= d.Length)
            {
                long size = BE.RU32(d, o);
                string type = BE.Ascii(d, o + 4, 4);
                if (size == 1) size = (long)BE.RU64(d, o + 8);
                else if (size == 0) size = d.Length - o;
                if (size < 8 || o + size > d.Length) break;
                Top t; t.Type = type; t.Start = o; t.Size = size;
                r.Add(t);
                o += (int)size;
            }
            return r;
        }

        // Add delta to every chunk offset (stco or co64) of every track.
        static void ShiftChunkOffsets(Box moov, long delta)
        {
            foreach (Box trak in moov.Kids)
            {
                if (trak.Type != "trak") continue;
                Box stbl = trak.Kid("mdia").Kid("minf").Kid("stbl");
                Box stco = stbl.Kid("stco");
                Box co64 = stbl.Kid("co64");
                if (stco != null)
                {
                    byte[] p = stco.Payload;
                    int n = (int)BE.RU32(p, 4);
                    for (int i = 0; i < n; i++)
                    {
                        long v = BE.RU32(p, 8 + i * 4) + delta;
                        if (v < 0 || v > uint.MaxValue)
                            throw new Exception("File too large for 32-bit offsets.");
                        BE.WU32(p, 8 + i * 4, (uint)v);
                    }
                }
                if (co64 != null)
                {
                    byte[] p = co64.Payload;
                    int n = (int)BE.RU32(p, 4);
                    for (int i = 0; i < n; i++)
                        BE.WU64(p, 8 + i * 8, (ulong)((long)BE.RU64(p, 8 + i * 8) + delta));
                }
            }
        }

        static string HandlerType(Box trak)
        {
            Box mdia = trak.Kid("mdia");
            if (mdia == null) return "";
            Box hdlr = mdia.Kid("hdlr");
            if (hdlr == null || hdlr.Payload.Length < 12) return "";
            return BE.Ascii(hdlr.Payload, 8, 4);
        }

        static uint TrackId(Box trak)
        {
            Box tkhd = trak.Kid("tkhd");
            byte[] p = tkhd.Payload;
            int off = (p[0] == 1) ? 20 : 12;
            return BE.RU32(p, off);
        }

        static void SetTrackId(Box trak, uint id)
        {
            Box tkhd = trak.Kid("tkhd");
            byte[] p = tkhd.Payload;
            int off = (p[0] == 1) ? 20 : 12;
            BE.WU32(p, off, id);
        }

        // mvhd -> version 1 with duration "unknown" (all ones), as in a real
        // RTXFury output. Creation/modification times end up 0.
        static void MvhdUnknownDuration(Box mvhd)
        {
            byte[] p = mvhd.Payload;
            if (p[0] == 1) { BE.WU64(p, 24, ulong.MaxValue); return; }
            byte[] n = new byte[p.Length + 12];
            n[0] = 1; n[1] = p[1]; n[2] = p[2]; n[3] = p[3];
            Array.Copy(p, 12, n, 20, 4);                // timescale
            BE.WU64(n, 24, ulong.MaxValue);             // duration
            Array.Copy(p, 20, n, 32, p.Length - 20);    // rate .. next_track_ID
            mvhd.Payload = n;
        }

        // Drop 4 of the hdlr's 12 reserved bytes, as a real RTXFury output does
        // (41-byte hdlr). The name bytes stay, but a parser that skips the full
        // 12 reserved bytes reads "VideoHandler" as "oHandler".
        static void ClipHandlerName(Box trak)
        {
            Box hdlr = trak.Kid("mdia").Kid("hdlr");
            byte[] p = hdlr.Payload;
            if (p.Length < 28) return;
            byte[] n = new byte[p.Length - 4];
            Array.Copy(p, 0, n, 0, 20);                 // version/flags, pre_defined, type, 8 reserved
            Array.Copy(p, 24, n, 20, p.Length - 24);    // name
            hdlr.Payload = n;
        }

        static void ZeroTimes(Box b)
        {
            if (b.Container) { foreach (Box k in b.Kids) ZeroTimes(k); return; }
            if (b.Type == "mvhd" || b.Type == "tkhd" || b.Type == "mdhd")
            {
                byte[] p = b.Payload;
                if (p[0] == 1) { BE.WU64(p, 4, 0); BE.WU64(p, 12, 0); }
                else { BE.WU32(p, 4, 0); BE.WU32(p, 8, 0); }
            }
        }

        public static byte[] Transform(byte[] data, out string note)
        {
            int ftypI = -1, moovI = -1, mdatI = -1;
            List<Top> top = TopLevel(data);
            for (int i = 0; i < top.Count; i++)
            {
                if (top[i].Type == "ftyp" && ftypI < 0) ftypI = i;
                else if (top[i].Type == "moov" && moovI < 0) moovI = i;
                else if (top[i].Type == "mdat")
                {
                    if (mdatI >= 0) throw new Exception("More than one mdat box; re-mux the file first.");
                    mdatI = i;
                }
            }
            if (moovI < 0) throw new Exception("No moov box found; not a valid MP4.");
            if (ftypI < 0 || mdatI < 0) throw new Exception("Missing ftyp or mdat box; re-mux the file first.");
            Top ftypT = top[ftypI], mdatT = top[mdatI];
            int mq = top[moovI].Start;
            Box moov = Box.ParseBox(data, ref mq);

            Box audioTrak = null;
            uint maxId = 0;
            foreach (Box k in moov.Kids)
            {
                if (k.Type != "trak") continue;
                uint id = TrackId(k);
                if (id > maxId) maxId = id;
                if (audioTrak == null && HandlerType(k) == "soun") audioTrak = k;
            }
            if (audioTrak == null)
                throw new Exception("No audio track found after normalization.");

            Box srcStbl = audioTrak.Kid("mdia").Kid("minf").Kid("stbl");
            Box sStts = srcStbl.Kid("stts");
            Box sStsc = srcStbl.Kid("stsc");
            Box sStsz = srcStbl.Kid("stsz");
            Box sStco = srcStbl.Kid("stco");
            Box sCo64 = srcStbl.Kid("co64");
            bool co64 = (sCo64 != null);
            Box sChunk = co64 ? sCo64 : sStco;
            if (sStts == null || sStsc == null || sStsz == null || sChunk == null)
                throw new Exception("Audio sample tables incomplete.");

            byte[] zp = sStsz.Payload;
            uint uniform = BE.RU32(zp, 4);
            int N = (int)BE.RU32(zp, 8);
            uint[] sizes = new uint[N];
            if (uniform != 0) { for (int i = 0; i < N; i++) sizes[i] = uniform; }
            else { for (int i = 0; i < N; i++) sizes[i] = BE.RU32(zp, 12 + i * 4); }
            if (N <= 0) throw new Exception("Audio track has no samples.");

            int junk = 9 * N;
            int total = N + junk;

            byte[] cp = sChunk.Payload;
            int chunkCount = (int)BE.RU32(cp, 4);
            int entW = co64 ? 8 : 4;

            Box t3 = audioTrak.Clone();
            if (t3.Kids != null) t3.Kids.RemoveAll(delegate(Box x) { return x.Type == "edts"; });
            uint newId = maxId + 1;
            SetTrackId(t3, newId);

            Box d3 = t3.Kid("mdia").Kid("minf").Kid("stbl");

            byte[] nStsz = new byte[12 + total * 4];
            BE.WU32(nStsz, 0, 0);
            BE.WU32(nStsz, 4, 0);
            BE.WU32(nStsz, 8, (uint)total);
            for (int i = 0; i < N; i++) BE.WU32(nStsz, 12 + i * 4, sizes[i]);
            for (int i = 0; i < junk; i++) BE.WU32(nStsz, 12 + (N + i) * 4, 8);
            d3.Kid("stsz").Payload = nStsz;

            int sEntN = (int)BE.RU32(sStts.Payload, 4);
            byte[] nStts = new byte[8 + (sEntN + 1) * 8];
            BE.WU32(nStts, 0, 0);
            BE.WU32(nStts, 4, (uint)(sEntN + 1));
            Array.Copy(sStts.Payload, 8, nStts, 8, sEntN * 8);
            BE.WU32(nStts, 8 + sEntN * 8, (uint)junk);
            BE.WU32(nStts, 8 + sEntN * 8 + 4, 1);
            d3.Kid("stts").Payload = nStts;

            int cEntN = (int)BE.RU32(sStsc.Payload, 4);
            byte[] nStsc = new byte[8 + (cEntN + 1) * 12];
            BE.WU32(nStsc, 0, 0);
            BE.WU32(nStsc, 4, (uint)(cEntN + 1));
            Array.Copy(sStsc.Payload, 8, nStsc, 8, cEntN * 12);
            int so2 = 8 + cEntN * 12;
            BE.WU32(nStsc, so2, (uint)(chunkCount + 1));
            BE.WU32(nStsc, so2 + 4, (uint)junk);
            BE.WU32(nStsc, so2 + 8, 1);
            d3.Kid("stsc").Payload = nStsc;

            byte[] nChunk = new byte[8 + (chunkCount + 1) * entW];
            BE.WU32(nChunk, 0, 0);
            BE.WU32(nChunk, 4, (uint)(chunkCount + 1));
            Array.Copy(cp, 8, nChunk, 8, chunkCount * entW);
            Box d3chunk = co64 ? d3.Kid("co64") : d3.Kid("stco");
            d3chunk.Payload = nChunk;

            int ai = moov.Kids.IndexOf(audioTrak);
            moov.Kids.Insert(ai + 1, t3);

            Box mvhd = moov.Kid("mvhd");
            BE.WU32(mvhd.Payload, mvhd.Payload.Length - 4, newId + 1);
            ZeroTimes(moov);

            // Match the movie header of a real RTXFury output (CLAUDE.md, "Compared
            // with a real RTXFury output"): unknown movie duration, no movie-level
            // metadata (drops ffmpeg's encoder tag and source comments such as a
            // TikTok "vid:" ID), clipped handler names.
            MvhdUnknownDuration(mvhd);
            moov.Kids.RemoveAll(delegate(Box x) { return x.Type == "udta"; });
            foreach (Box k in moov.Kids) if (k.Type == "trak") ClipHandlerName(k);

            // Reference layout (CLAUDE.md 5.2): ftyp, moov, mdat, then the junk
            // straight after mdat. Putting moov in front moves all the media
            // data by the same amount, so every chunk offset shifts by delta.
            long moovSize = moov.ComputeSize();
            long mdatStart = ftypT.Size + moovSize;
            ShiftChunkOffsets(moov, mdatStart - mdatT.Start);
            long junkOffset = mdatStart + mdatT.Size;
            if (!co64 && junkOffset > uint.MaxValue)
                throw new Exception("File too large for 32-bit offsets.");

            int lastOff = 8 + chunkCount * entW;
            if (co64) BE.WU64(nChunk, lastOff, (ulong)junkOffset);
            else BE.WU32(nChunk, lastOff, (uint)junkOffset);

            MemoryStream ms = new MemoryStream((int)(junkOffset + junk * 8L));
            ms.Write(data, ftypT.Start, (int)ftypT.Size);
            moov.Write(ms);
            ms.Write(data, mdatT.Start, (int)mdatT.Size);
            byte[] one = new byte[] { 0, 0, 0, 4, 0, 0, 0, 0 };
            for (int i = 0; i < junk; i++) ms.Write(one, 0, 8);

            note = String.Format(
                "audio samples: {0}  ->  junk track with {1} samples ({2} junk), +{3} trailing bytes",
                N, total, junk, junk * 8);
            return ms.ToArray();
        }

        // ---- Clone engine surgery (RTXFury v2) -----------------------------
        // One valid 6-byte AAC-LC frame (44.1 kHz stereo); the real RTXFury
        // tail is 16 of these. Reused verbatim so it decodes under the normal
        // sample description.
        static readonly byte[] SILENT_AAC = new byte[] { 0x21, 0x10, 0x04, 0x60, 0x8c, 0x1c };
        const int TAIL_FRAMES = 16;

        // The real RTXFury output's SECOND audio sample description (a whole
        // mp4a/esds box, 113 bytes, lifted verbatim from a working file). Its
        // AudioSpecificConfig differs from the normal one; the real audio track
        // tags its tail frames with this while the clone track tags the same
        // bytes with description 1 — a codec-config mismatch between the two
        // tracks, the suspected trigger.
        static readonly byte[] AUDIO_DESC2 = new byte[] {
            0x00, 0x00, 0x00, 0x71, 0x6d, 0x70, 0x34, 0x61, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00,
            0xac, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00, 0x39, 0x65, 0x73, 0x64, 0x73, 0x00, 0x00, 0x00, 0x00,
            0x03, 0x80, 0x80, 0x80, 0x28, 0x00, 0x02, 0x00, 0x04, 0x80, 0x80, 0x80, 0x1a, 0x40, 0x15, 0x00,
            0x00, 0x00, 0x00, 0x02, 0xf2, 0x31, 0x00, 0x02, 0xf2, 0x31, 0x05, 0x80, 0x80, 0x80, 0x08, 0x12,
            0x00, 0x05, 0x00, 0x40, 0x00, 0x20, 0x00, 0x06, 0x80, 0x80, 0x80, 0x01, 0x02, 0x00, 0x00, 0x00,
            0x14, 0x62, 0x74, 0x72, 0x74, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0xf2, 0x31, 0x00, 0x02, 0xf2,
            0x31 };

        // Append AUDIO_DESC2 as a second entry in an audio track's stsd.
        static void AddSecondAudioDesc(Box trak)
        {
            Box stsd = trak.Kid("mdia").Kid("minf").Kid("stbl").Kid("stsd");
            byte[] p = stsd.Payload;
            uint count = BE.RU32(p, 4);
            byte[] np = new byte[p.Length + AUDIO_DESC2.Length];
            Array.Copy(p, 0, np, 0, p.Length);
            Array.Copy(AUDIO_DESC2, 0, np, p.Length, AUDIO_DESC2.Length);
            BE.WU32(np, 4, count + 1);
            stsd.Payload = np;
        }

        // Append the TAIL_FRAMES-frame tail (each `sampleSize` bytes, duration
        // `dur`) at `tailOffset` as TWO chunks of half the frames each, so the
        // second half can carry a different sample description. The first half
        // uses description 1; the second half uses `secondHalfDesc`.
        static void AppendTail(Box trak, long tailOffset, uint sampleSize, uint dur, int secondHalfDesc)
        {
            Box stbl = trak.Kid("mdia").Kid("minf").Kid("stbl");
            Box stsz = stbl.Kid("stsz");
            Box stts = stbl.Kid("stts");
            Box stsc = stbl.Kid("stsc");
            Box stco = stbl.Kid("stco");
            Box co64 = stbl.Kid("co64");
            if (stsz == null || stts == null || stsc == null || (stco == null && co64 == null))
                throw new Exception("Audio sample tables incomplete for tail append.");
            int half = TAIL_FRAMES / 2;

            // stsz: +TAIL_FRAMES entries of sampleSize (must be non-uniform)
            byte[] zp = stsz.Payload;
            if (BE.RU32(zp, 4) != 0) throw new Exception("Uniform stsz unexpected on audio track.");
            int zc = (int)BE.RU32(zp, 8);
            byte[] nz = new byte[zp.Length + TAIL_FRAMES * 4];
            Array.Copy(zp, 0, nz, 0, zp.Length);
            BE.WU32(nz, 8, (uint)(zc + TAIL_FRAMES));
            for (int i = 0; i < TAIL_FRAMES; i++) BE.WU32(nz, zp.Length + i * 4, sampleSize);
            stsz.Payload = nz;

            // stts: +1 run [TAIL_FRAMES, dur]
            byte[] tp = stts.Payload;
            int tc = (int)BE.RU32(tp, 4);
            byte[] nt = new byte[tp.Length + 8];
            Array.Copy(tp, 0, nt, 0, tp.Length);
            BE.WU32(nt, 4, (uint)(tc + 1));
            BE.WU32(nt, tp.Length, (uint)TAIL_FRAMES);
            BE.WU32(nt, tp.Length + 4, dur);
            stts.Payload = nt;

            // stco/co64: +2 chunk offsets (the two tail halves)
            bool is64 = (co64 != null);
            Box chunkBox = is64 ? co64 : stco;
            byte[] cp = chunkBox.Payload;
            int cc = (int)BE.RU32(cp, 4);
            long off2 = tailOffset + (long)half * sampleSize;
            byte[] nc = new byte[cp.Length + (is64 ? 16 : 8)];
            Array.Copy(cp, 0, nc, 0, cp.Length);
            BE.WU32(nc, 4, (uint)(cc + 2));
            if (is64) { BE.WU64(nc, cp.Length, (ulong)tailOffset); BE.WU64(nc, cp.Length + 8, (ulong)off2); }
            else
            {
                if (off2 > uint.MaxValue) throw new Exception("File too large for 32-bit offsets.");
                BE.WU32(nc, cp.Length, (uint)tailOffset); BE.WU32(nc, cp.Length + 4, (uint)off2);
            }
            chunkBox.Payload = nc;

            // stsc: +[cc+1, half, desc 1] [cc+2, half, secondHalfDesc]
            byte[] sp = stsc.Payload;
            int sc = (int)BE.RU32(sp, 4);
            byte[] ns = new byte[sp.Length + 24];
            Array.Copy(sp, 0, ns, 0, sp.Length);
            BE.WU32(ns, 4, (uint)(sc + 2));
            BE.WU32(ns, sp.Length, (uint)(cc + 1)); BE.WU32(ns, sp.Length + 4, (uint)half); BE.WU32(ns, sp.Length + 8, 1);
            BE.WU32(ns, sp.Length + 12, (uint)(cc + 2)); BE.WU32(ns, sp.Length + 16, (uint)half); BE.WU32(ns, sp.Length + 20, (uint)secondHalfDesc);
            stsc.Payload = ns;
        }

        // Where the 16-frame tail sits, and what it does to playback.
        //   V3            The upload-confirmed layout (CLAUDE.md, v3): tail appended
        //                 after the song, audio edit lists dropped and track/movie
        //                 durations stretched over it. Players then play the tail:
        //                 ~0.4 s of silence with no picture at the end of every file.
        //   KeepEditLists V3 minus those two additions: same tail, same mismatch,
        //                 but the audio edit lists and track/movie durations stay as
        //                 ffmpeg wrote them, so playback stops at the end of the
        //                 song. ffmpeg-style readers then skip the tail frames.
        //                 NOT upload-tested.
        //   Front         Tail placed BEFORE the song and hidden inside the edit
        //                 list's encoder-delay skip: readers still receive and
        //                 decode every tail frame (then discard the output), and
        //                 nothing plays after the song. NOT upload-tested.
        public enum TailLayout { V3, KeepEditLists, Front }

        // Extend a track's media duration (mdhd) by `tailMedia` ticks so it
        // covers every sample, tail included. Returns the media timescale.
        static uint ExtendMediaDuration(Box trak, long tailMedia)
        {
            byte[] m = trak.Kid("mdia").Kid("mdhd").Payload;
            if (m[0] == 1) { BE.WU64(m, 24, BE.RU64(m, 24) + (ulong)tailMedia); return BE.RU32(m, 20); }
            BE.WU32(m, 16, BE.RU32(m, 16) + (uint)tailMedia);
            return BE.RU32(m, 12);
        }

        // V3 only: also stretch the presentation duration (tkhd) over the tail.
        // With the edit lists gone, this is what makes ffmpeg and players play
        // the appended tail (and what creates the silent end).
        static void ExtendTrackDuration(Box trak, long tailMedia, uint movieTs)
        {
            uint mts = ExtendMediaDuration(trak, tailMedia);
            ulong tailMovie = (mts == 0) ? 0 : (ulong)tailMedia * movieTs / mts;
            byte[] q = trak.Kid("tkhd").Payload;
            if (q[0] == 1) BE.WU64(q, 28, BE.RU64(q, 28) + tailMovie);
            else BE.WU32(q, 20, BE.RU32(q, 20) + (uint)tailMovie);
        }

        // Give a track a one-entry edit list (play the whole track from media
        // time 0) if it has none. ffmpeg normally writes one for AAC already.
        static void EnsureEditList(Box trak)
        {
            if (trak.Kid("edts") != null) return;
            byte[] q = trak.Kid("tkhd").Payload;
            bool v1 = (q[0] == 1);
            byte[] e = new byte[v1 ? 36 : 28];
            BE.WU32(e, 0, (uint)e.Length);
            e[4] = (byte)'e'; e[5] = (byte)'l'; e[6] = (byte)'s'; e[7] = (byte)'t';
            e[8] = (byte)(v1 ? 1 : 0);
            BE.WU32(e, 12, 1);                                      // entry_count
            if (v1) { BE.WU64(e, 16, BE.RU64(q, 28)); BE.WU64(e, 24, 0); BE.WU32(e, 32, 0x00010000); }
            else { BE.WU32(e, 16, BE.RU32(q, 20)); BE.WU32(e, 20, 0); BE.WU32(e, 24, 0x00010000); }
            Box edts = new Box();
            edts.Type = "edts"; edts.Container = false; edts.Payload = e;   // edts is kept as a leaf (its payload is the elst box)
            trak.Kids.Insert(trak.Kids.IndexOf(trak.Kid("tkhd")) + 1, edts);
        }

        // Front layout: move every edit's start later by `ticks` media ticks, so
        // the prepended tail falls inside the skipped start (like encoder delay).
        static void ShiftEditStart(Box trak, long ticks)
        {
            Box edts = trak.Kid("edts");
            if (edts == null) throw new Exception("Audio track has no edit list.");
            byte[] e = edts.Payload;                                // the elst box: size, 'elst', ver/flags, count, entries
            if (e.Length < 16 || BE.Ascii(e, 4, 4) != "elst") throw new Exception("Unexpected audio edit list.");
            bool v1 = (e[8] == 1);
            int n = (int)BE.RU32(e, 12), w = v1 ? 20 : 12;
            for (int i = 0; i < n; i++)
            {
                int o = 16 + i * w + (v1 ? 8 : 4);                  // media_time
                if (v1)
                {
                    long mt = (long)BE.RU64(e, o);
                    if (mt != -1) BE.WU64(e, o, (ulong)(mt + ticks));
                }
                else
                {
                    int mt = (int)BE.RU32(e, o);
                    if (mt != -1) BE.WU32(e, o, (uint)(mt + ticks));
                }
            }
        }

        // Front layout: insert the TAIL_FRAMES-frame tail (each `sampleSize`
        // bytes, 1024 ticks) at the START of the track as two chunks of half the
        // frames each, mirroring AppendTail: the first half uses description 1,
        // the second half `secondHalfDesc`.
        static void PrependTail(Box trak, long tailOffset, uint sampleSize, int secondHalfDesc)
        {
            Box stbl = trak.Kid("mdia").Kid("minf").Kid("stbl");
            Box stsz = stbl.Kid("stsz");
            Box stts = stbl.Kid("stts");
            Box stsc = stbl.Kid("stsc");
            Box stco = stbl.Kid("stco");
            Box co64 = stbl.Kid("co64");
            if (stsz == null || stts == null || stsc == null || (stco == null && co64 == null))
                throw new Exception("Audio sample tables incomplete for tail insert.");
            if (stbl.Kid("stss") != null || stbl.Kid("ctts") != null)
                throw new Exception("Audio track has sync/composition tables; the front layout does not support this file.");
            int half = TAIL_FRAMES / 2;

            // stsz: TAIL_FRAMES entries of sampleSize, then the existing sizes
            byte[] zp = stsz.Payload;
            if (BE.RU32(zp, 4) != 0) throw new Exception("Uniform stsz unexpected on audio track.");
            int zc = (int)BE.RU32(zp, 8);
            byte[] nz = new byte[zp.Length + TAIL_FRAMES * 4];
            Array.Copy(zp, 0, nz, 0, 12);
            BE.WU32(nz, 8, (uint)(zc + TAIL_FRAMES));
            for (int i = 0; i < TAIL_FRAMES; i++) BE.WU32(nz, 12 + i * 4, sampleSize);
            Array.Copy(zp, 12, nz, 12 + TAIL_FRAMES * 4, zp.Length - 12);
            stsz.Payload = nz;

            // stts: a first run [TAIL_FRAMES, 1024], then the existing runs
            byte[] tp = stts.Payload;
            int tc = (int)BE.RU32(tp, 4);
            byte[] nt = new byte[tp.Length + 8];
            Array.Copy(tp, 0, nt, 0, 8);
            BE.WU32(nt, 4, (uint)(tc + 1));
            BE.WU32(nt, 8, (uint)TAIL_FRAMES);
            BE.WU32(nt, 12, 1024);
            Array.Copy(tp, 8, nt, 16, tp.Length - 8);
            stts.Payload = nt;

            // stco/co64: the two tail chunk offsets first
            bool is64 = (co64 != null);
            Box chunkBox = is64 ? co64 : stco;
            byte[] cp = chunkBox.Payload;
            int cc = (int)BE.RU32(cp, 4), ew = is64 ? 8 : 4;
            long off2 = tailOffset + (long)half * sampleSize;
            byte[] nc = new byte[cp.Length + 2 * ew];
            Array.Copy(cp, 0, nc, 0, 8);
            BE.WU32(nc, 4, (uint)(cc + 2));
            if (is64) { BE.WU64(nc, 8, (ulong)tailOffset); BE.WU64(nc, 16, (ulong)off2); }
            else
            {
                if (off2 > uint.MaxValue) throw new Exception("File too large for 32-bit offsets.");
                BE.WU32(nc, 8, (uint)tailOffset); BE.WU32(nc, 12, (uint)off2);
            }
            Array.Copy(cp, 8, nc, 8 + 2 * ew, cp.Length - 8);
            chunkBox.Payload = nc;

            // stsc: [1, half, desc 1] [2, half, secondHalfDesc], then the existing
            // entries with their first_chunk moved up by 2
            byte[] sp = stsc.Payload;
            int sc = (int)BE.RU32(sp, 4);
            byte[] ns = new byte[sp.Length + 24];
            Array.Copy(sp, 0, ns, 0, 8);
            BE.WU32(ns, 4, (uint)(sc + 2));
            BE.WU32(ns, 8, 1); BE.WU32(ns, 12, (uint)half); BE.WU32(ns, 16, 1);
            BE.WU32(ns, 20, 2); BE.WU32(ns, 24, (uint)half); BE.WU32(ns, 28, (uint)secondHalfDesc);
            for (int i = 0; i < sc; i++)
            {
                int s = 8 + i * 12, d = 32 + i * 12;
                BE.WU32(ns, d, BE.RU32(sp, s) + 2);
                Array.Copy(sp, s + 4, ns, d + 4, 8);
            }
            stsc.Payload = ns;

            // sbgp (ffmpeg's AAC 'roll' group): the tail frames join the first
            // group, so the real frames keep their own group membership
            Box sbgp = stbl.Kid("sbgp");
            if (sbgp != null)
            {
                byte[] gp = sbgp.Payload;
                int hdr = (gp[0] == 1) ? 16 : 12;                   // ver/flags, grouping_type, [param], entry_count
                int ec = (int)BE.RU32(gp, hdr - 4);
                uint gi = (ec > 0) ? BE.RU32(gp, hdr + 4) : 0;
                byte[] ng = new byte[gp.Length + 8];
                Array.Copy(gp, 0, ng, 0, hdr);
                BE.WU32(ng, hdr - 4, (uint)(ec + 1));
                BE.WU32(ng, hdr, (uint)TAIL_FRAMES);
                BE.WU32(ng, hdr + 4, gi);
                Array.Copy(gp, hdr, ng, hdr + 8, gp.Length - hdr);
                sbgp.Payload = ng;
            }
        }

        static ulong TkhdDuration(Box trak)
        {
            byte[] q = trak.Kid("tkhd").Payload;
            return q[0] == 1 ? BE.RU64(q, 28) : (ulong)BE.RU32(q, 20);
        }

        // Turn a clean single-audio H.264 base (moov LAST) into the RTXFury v2
        // structure: a second audio track that shares the first's bytes, plus a
        // tail of silent frames in a second mdat referenced by both tracks. The
        // main mdat is untouched, so no chunk offsets need shifting. `layout`
        // picks where the tail sits (see TailLayout).
        public static byte[] BuildCloneContainer(byte[] data, TailLayout layout)
        {
            List<Top> top = TopLevel(data);
            int moovI = -1;
            for (int i = 0; i < top.Count; i++) if (top[i].Type == "moov") moovI = i;
            if (moovI < 0) throw new Exception("No moov box found; not a valid MP4.");
            if (moovI != top.Count - 1) throw new Exception("moov is not the last box; re-mux the file first.");
            Top moovT = top[moovI];
            int mq = moovT.Start;
            Box moov = Box.ParseBox(data, ref mq);

            Box audio = null; uint maxId = 0;
            foreach (Box k in moov.Kids)
            {
                if (k.Type != "trak") continue;
                uint id = TrackId(k); if (id > maxId) maxId = id;
                if (audio == null && HandlerType(k) == "soun") audio = k;
            }
            if (audio == null) throw new Exception("No audio track found after normalization.");

            Box mvhd = moov.Kid("mvhd");
            byte[] mp = mvhd.Payload;
            uint movieTs = (mp[0] == 1) ? BE.RU32(mp, 20) : BE.RU32(mp, 12);

            // The no-gap layouts keep the audio edit list; make sure there is
            // one before cloning, so the clone track gets the same one.
            if (layout != TailLayout.V3) EnsureEditList(audio);

            // Clone the audio track; the clone shares the real audio bytes
            // (it keeps the source track's chunk offsets).
            Box t3 = audio.Clone();
            uint newId = maxId + 1;
            SetTrackId(t3, newId);
            int ai = moov.Kids.IndexOf(audio);
            moov.Kids.Insert(ai + 1, t3);

            // V3 only: drop the audio edit lists. ffmpeg's AAC edit list
            // (encoder-delay skip) would otherwise trim the appended tail out of
            // the demuxed stream. Side effects: audio plays ~23 ms late, and the
            // tail plays as ~0.4 s of silence after the last video frame.
            if (layout == TailLayout.V3)
            {
                audio.Kids.RemoveAll(delegate(Box x) { return x.Type == "edts"; });
                t3.Kids.RemoveAll(delegate(Box x) { return x.Type == "edts"; });
            }

            // A second mdat right after the main data holds the shared tail.
            // The real audio track gets a second sample description and tags the
            // tail's second half with it; the clone track tags the same bytes
            // with description 1 (the RTXFury codec-config mismatch). All three
            // layouts keep this mismatch; only the tail's position and timing differ.
            long mdat2Start = moovT.Start;
            long tailOffset = mdat2Start + 8;
            long tailMedia = (long)TAIL_FRAMES * 1024;
            AddSecondAudioDesc(audio);
            if (layout == TailLayout.Front)
            {
                PrependTail(audio, tailOffset, (uint)SILENT_AAC.Length, 2);
                PrependTail(t3, tailOffset, (uint)SILENT_AAC.Length, 1);
                ShiftEditStart(audio, tailMedia);
                ShiftEditStart(t3, tailMedia);
            }
            else
            {
                AppendTail(audio, tailOffset, (uint)SILENT_AAC.Length, 1024, 2);
                AppendTail(t3, tailOffset, (uint)SILENT_AAC.Length, 1024, 1);
            }

            if (layout == TailLayout.V3)
            {
                ExtendTrackDuration(audio, tailMedia, movieTs);
                ExtendTrackDuration(t3, tailMedia, movieTs);
            }
            else
            {
                // Media durations cover the tail; presentation (tkhd/mvhd) stays
                // at the song's length, as ffmpeg wrote it.
                ExtendMediaDuration(audio, tailMedia);
                ExtendMediaDuration(t3, tailMedia);
            }

            // Movie duration = longest track (now the audio tracks with the tail).
            ulong maxDur = 0;
            foreach (Box k in moov.Kids)
                if (k.Type == "trak") { ulong d = TkhdDuration(k); if (d > maxDur) maxDur = d; }
            if (mp[0] == 1) BE.WU64(mp, 24, maxDur); else BE.WU32(mp, 16, (uint)maxDur);

            BE.WU32(mvhd.Payload, mvhd.Payload.Length - 4, newId + 1); // next_track_ID

            moov.ComputeSize();
            int tailBytes = TAIL_FRAMES * SILENT_AAC.Length;
            MemoryStream ms = new MemoryStream((int)(moovT.Start + 8 + tailBytes + moov.Size));
            ms.Write(data, 0, moovT.Start);                     // ftyp, free, main mdat
            byte[] mh = new byte[8];
            BE.WU32(mh, 0, (uint)(8 + tailBytes));
            mh[4] = (byte)'m'; mh[5] = (byte)'d'; mh[6] = (byte)'a'; mh[7] = (byte)'t';
            ms.Write(mh, 0, 8);
            for (int i = 0; i < TAIL_FRAMES; i++) ms.Write(SILENT_AAC, 0, SILENT_AAC.Length);
            moov.Write(ms);
            return ms.ToArray();
        }
    }

    // ---- A single verification check ---------------------------------------
    class VCheck
    {
        public string Name; public bool Pass; public string Detail;
        public bool Info;   // informational line: shown as [INFO], never fails the verdict
        public VCheck(string n, bool p, string d) { Name = n; Pass = p; Detail = d; }
    }

    // ---- ffmpeg / ffprobe driving + verification ---------------------------
    static class Tools
    {
        public static string Find(string name)
        {
            string local = Path.Combine(Application.StartupPath, name + ".exe");
            if (File.Exists(local)) return local;
            return name; // rely on PATH
        }

        static int Run(string exe, string args, out string so, out string se)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            Process p = Process.Start(psi);
            // Close stdin at once: a GUI process has no console, and ffmpeg would
            // otherwise block forever waiting on stdin. EOF lets it proceed.
            p.StandardInput.Close();
            // Drain stderr on its own thread so a full pipe can't deadlock the
            // stdout read (ffmpeg writes almost everything to stderr).
            StringBuilder errBuf = new StringBuilder();
            Thread et = new Thread(delegate()
            {
                try
                {
                    string line;
                    while ((line = p.StandardError.ReadLine()) != null) errBuf.AppendLine(line);
                }
                catch { }
            });
            et.IsBackground = true;
            et.Start();
            so = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            et.Join(2000);
            se = errBuf.ToString();
            return p.ExitCode;
        }

        static bool HasAudio(string ffprobe, string input)
        {
            string so, se;
            try
            {
                int rc = Run(ffprobe, "-v error -select_streams a -show_entries stream=index -of csv=p=0 \"" + input + "\"", out so, out se);
                return rc == 0 && so.Trim().Length > 0;
            }
            catch { return true; }
        }

        // 0 = stream-copy the video (the default, no quality loss). Otherwise
        // the video is re-encoded to H.264 at this average bitrate in Mbps.
        public static int ReencodeMbps = 0;

        // ---- "Clone" engine (RTXFury v2, 2026-11) --------------------------
        // The junk-track method above stopped working on TikTok around
        // 2026-11-08. The current RTXFury output is a well-formed file: the
        // video is re-encoded to H.264 (capped at 1080p) and the audio to
        // AAC-LC, with a SECOND audio track that duplicates the first. No junk
        // track, no trailing bytes, no header tricks. This engine reproduces
        // that structure with ffmpeg alone (no container surgery).
        public static int CloneMbps = 15;              // video bitrate for the re-encode
        public static string CloneMaxBox = "1080:1920"; // scale box W:H (confirmed 1080p); "2160:3840" for 4K; "" keeps source res
        public static Mp4.TailLayout CloneTail = Mp4.TailLayout.V3; // V3 = upload-confirmed; the others remove the end gap (untested)

        public static string TailName(Mp4.TailLayout t)
        {
            if (t == Mp4.TailLayout.KeepEditLists) return "keep edit lists (no end gap)";
            if (t == Mp4.TailLayout.Front) return "silent frames at start (no end gap)";
            return "v3";
        }

        static string CloneScale()
        {
            // Fit inside the box, keep aspect, never upscale, even dims.
            return CloneMaxBox.Length > 0
                ? " -vf scale=" + CloneMaxBox + ":force_original_aspect_ratio=decrease:force_divisible_by=2"
                : "";
        }

        static string CloneVideoArgs(bool nvenc)
        {
            // Match the real RTXFury output: H.264 Main profile, no B-frames.
            int k = CloneMbps * 1000;
            string enc = nvenc
                ? "-c:v h264_nvenc -preset p5 -profile:v main -bf 0"
                : "-c:v libx264 -preset medium -profile:v main -bf 0";
            return enc + " -pix_fmt yuv420p -b:v " + k + "k -maxrate " + (k * 6 / 5) + "k -bufsize " + (k * 2) + "k";
        }

        // Build the two-audio-track clone directly with ffmpeg and return a temp
        // mp4 path. audioIn, when non-null, is an external track to use instead
        // of the source audio (Add Song). Tries the GPU encoder first, then
        // falls back to libx264 if no NVIDIA GPU is present.
        public static string BuildClone(string videoIn, string audioIn, Action<string> status)
        {
            string ffmpeg = Find("ffmpeg");
            string ffprobe = Find("ffprobe");
            string tmp = Path.Combine(Path.GetTempPath(), "qopt_" + Guid.NewGuid().ToString("N") + ".mp4");
            string so, se;

            // ffmpeg produces a clean single-audio H.264 base (moov last); the
            // second, byte-sharing audio track + tail is added by surgery below.
            string inputs, maps;
            if (audioIn != null)
            {
                inputs = "-i \"" + videoIn + "\" -i \"" + audioIn + "\"";
                maps = "-map 0:v:0 -map 1:a:0 -af apad -shortest";
            }
            else if (HasAudio(ffprobe, videoIn))
            {
                inputs = "-i \"" + videoIn + "\"";
                maps = "-map 0:v:0 -map 0:a:0";
            }
            else
            {
                inputs = "-i \"" + videoIn + "\" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100";
                maps = "-map 0:v:0 -map 1:a:0 -shortest";
            }
            string audioArgs = "-c:a aac -b:a 192k -ar 44100";

            string lastErr = ""; bool built = false;
            for (int attempt = 0; attempt < 2 && !built; attempt++)
            {
                bool nvenc = (attempt == 0);
                status(nvenc
                    ? "Re-encoding to 1080p H.264 (GPU)..."
                    : "Re-encoding to 1080p H.264...");
                string args = "-nostdin -y " + inputs + " " + maps + " -map_metadata -1" +
                              CloneScale() + " " + CloneVideoArgs(nvenc) + " " + audioArgs + " \"" + tmp + "\"";
                int rc = Run(ffmpeg, args, out so, out se);
                if (rc == 0 && File.Exists(tmp) && new FileInfo(tmp).Length > 0) { built = true; break; }
                lastErr = se;
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
            }
            if (!built)
                throw new Exception("ffmpeg could not build the clone base.\n\n" + Tail(lastErr, 1500));

            // Container surgery: add the byte-sharing clone track + second-mdat tail.
            status("Cloning the audio track (shared bytes + tail)...");
            byte[] baseData = File.ReadAllBytes(tmp);
            byte[] outData = Mp4.BuildCloneContainer(baseData, CloneTail);
            File.WriteAllBytes(tmp, outData);
            return tmp;
        }

        // Video codec arguments for the first attempt: copy, or the bitrate encode.
        static string VideoArgs()
        {
            return ReencodeMbps > 0 ? BitrateArgs() : "-c:v copy";
        }

        // Video codec arguments when the source cannot be stream-copied.
        static string FallbackVideoArgs()
        {
            return ReencodeMbps > 0 ? BitrateArgs() : "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p";
        }

        // H.264 High at an average of ReencodeMbps, peaks allowed to 1.25x.
        static string BitrateArgs()
        {
            int k = ReencodeMbps * 1000;
            return "-c:v libx264 -preset medium -profile:v high -pix_fmt yuv420p" +
                   " -b:v " + k + "k -maxrate " + (k * 5 / 4) + "k -bufsize " + (k * 2) + "k";
        }

        static string VideoStatus()
        {
            return ReencodeMbps > 0
                ? "Re-encoding video at " + ReencodeMbps + " Mbps (this can take a while)..."
                : "Re-muxing (copying streams, no re-encode)...";
        }

        // Normalize to a clean MP4 (moov last). Returns the temp path.
        public static string Normalize(string input, Action<string> status)
        {
            string ffmpeg = Find("ffmpeg");
            string ffprobe = Find("ffprobe");
            string tmp = Path.Combine(Path.GetTempPath(), "qopt_" + Guid.NewGuid().ToString("N") + ".mp4");
            string so, se;
            int rc;
            bool audio = HasAudio(ffprobe, input);

            if (audio)
            {
                status(VideoStatus());
                rc = Run(ffmpeg, "-nostdin -y -i \"" + input + "\" -map 0:v:0 -map 0:a:0 " + VideoArgs() + " -c:a copy \"" + tmp + "\"", out so, out se);
                if (rc != 0)
                {
                    status(ReencodeMbps > 0 ? "Audio needs a compatible encode; re-encoding..." : "Source needs a compatible encode; converting video...");
                    rc = Run(ffmpeg, "-nostdin -y -i \"" + input + "\" -map 0:v:0 -map 0:a:0 " + FallbackVideoArgs() + " -c:a aac -b:a 192k \"" + tmp + "\"", out so, out se);
                }
            }
            else
            {
                status(ReencodeMbps > 0 ? "No audio track; adding a silent one. " + VideoStatus() : "No audio track; adding a silent one...");
                rc = Run(ffmpeg, "-nostdin -y -i \"" + input + "\" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -map 0:v:0 -map 1:a:0 " + VideoArgs() + " -c:a aac -shortest \"" + tmp + "\"", out so, out se);
                if (rc != 0 && ReencodeMbps == 0)
                {
                    rc = Run(ffmpeg, "-nostdin -y -i \"" + input + "\" -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -map 0:v:0 -map 1:a:0 " + FallbackVideoArgs() + " -c:a aac -shortest \"" + tmp + "\"", out so, out se);
                }
            }
            if (rc != 0)
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                throw new Exception("ffmpeg could not process this file.\n\n" + Tail(se, 1500));
            }
            return tmp;
        }

        // Attach an external audio file (e.g. a downloaded MP3) to the video,
        // replacing the video's own audio. The VIDEO is stream-copied (no
        // re-encode, no quality loss); only the new audio is encoded to AAC.
        // Output length matches the video: the audio is padded with silence if
        // shorter, trimmed if longer. Returns a clean temp MP4 (moov last).
        public static string MuxAudio(string video, string audio, Action<string> status)
        {
            string ffmpeg = Find("ffmpeg");
            string tmp = Path.Combine(Path.GetTempPath(), "qopt_" + Guid.NewGuid().ToString("N") + ".mp4");
            string so, se;
            status(ReencodeMbps > 0 ? "Attaching audio. " + VideoStatus() : "Attaching audio (video copied, no re-encode)...");
            int rc = Run(ffmpeg, "-nostdin -y -i \"" + video + "\" -i \"" + audio + "\" -map 0:v:0 -map 1:a:0 " + VideoArgs() + " -c:a aac -b:a 256k -af apad -shortest \"" + tmp + "\"", out so, out se);
            if (rc != 0 && ReencodeMbps == 0)
            {
                status("Video needs a compatible encode; converting video...");
                rc = Run(ffmpeg, "-nostdin -y -i \"" + video + "\" -i \"" + audio + "\" -map 0:v:0 -map 1:a:0 " + FallbackVideoArgs() + " -c:a aac -b:a 256k -af apad -shortest \"" + tmp + "\"", out so, out se);
            }
            if (rc != 0)
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                throw new Exception("ffmpeg could not attach that audio file.\n\n" + Tail(se, 1500));
            }
            return tmp;
        }

        static string Tail(string s, int n)
        {
            if (s == null) return "";
            s = s.Trim();
            return s.Length <= n ? s : s.Substring(s.Length - n);
        }

        public static bool Available()
        {
            string ffmpeg = Find("ffmpeg");
            string so, se;
            try { Run(ffmpeg, "-version", out so, out se); return true; }
            catch { return false; }
        }

        static string[] SplitLines(string s)
        {
            if (s == null) return new string[0];
            return s.Replace("\r", "").Split('\n');
        }
        static int CountLines(string s)
        {
            int c = 0;
            foreach (string ln in SplitLines(s)) if (ln.Trim().Length > 0) c++;
            return c;
        }
        static int ParseInt(string s)
        {
            int v;
            if (int.TryParse((s == null ? "" : s).Trim(), out v)) return v;
            return 0;
        }
        // ffprobe always prints a dot decimal; parse it the same on every Windows locale.
        static double ParseDouble(string s)
        {
            double v;
            if (double.TryParse((s == null ? "" : s).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        // Run the same eight checks verify.bat uses, but entirely in-process.
        public static List<VCheck> Verify(string file)
        {
            string ffprobe = Find("ffprobe");
            string ffmpeg = Find("ffmpeg");
            List<VCheck> r = new List<VCheck>();
            string so, se;
            string q = "\"" + file + "\"";

            // 1. stream layout: 1 video + 2 audio
            Run(ffprobe, "-v error -show_entries stream=codec_type -of csv=p=0 " + q, out so, out se);
            int nv = 0, na = 0;
            foreach (string ln in SplitLines(so))
            {
                if (ln.Contains("video")) nv++;
                else if (ln.Contains("audio")) na++;
            }
            r.Add(new VCheck("Stream layout", nv == 1 && na == 2,
                nv + " video, " + na + " audio (want 1 video + 2 audio)"));

            // 2. junk track = 10x the real audio's packets
            Run(ffprobe, "-v error -select_streams a:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 " + q, out so, out se);
            int ra = ParseInt(so);
            Run(ffprobe, "-v error -select_streams a:1 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 " + q, out so, out se);
            int ja = ParseInt(so);
            r.Add(new VCheck("Junk track = 10x real audio", ra > 0 && ja == 10 * ra,
                "real a:0 = " + ra + " packets, junk a:1 = " + ja + " packets"));

            // 3. junk track is undecodable
            int je = 0;
            if (ja > 0) { Run(ffmpeg, "-nostdin -v error -i " + q + " -map 0:a:1 -f null -", out so, out se); je = CountLines(se); }
            r.Add(new VCheck("Junk track breaks decoders", ja > 0 && je > 0,
                je + " decode-error lines on the junk track (expected: many)"));

            // 4. video decodes cleanly (no quality loss from this tool)
            Run(ffmpeg, "-nostdin -v error -i " + q + " -map 0:v:0 -f null -", out so, out se);
            int ve = CountLines(se);
            r.Add(new VCheck("Video decodes clean", ve == 0,
                ve + " video decode errors (expected: 0)"));

            // 5. timestamps zeroed
            Run(ffprobe, "-v error -show_entries format_tags=creation_time:stream_tags=creation_time -of default=nw=1 " + q, out so, out se);
            string ct = so.Trim();
            r.Add(new VCheck("Timestamps zeroed", ct.Length == 0,
                ct.Length == 0 ? "no creation_time tags" : "found: " + ct));

            // 6. trailing 00 00 00 04 00 00 00 00 pattern (a real RTXFury output
            //    ends with an empty 8-byte free box after it; look past that)
            bool ok6 = false; string hex = "";
            try
            {
                using (FileStream fs = File.OpenRead(file))
                {
                    long len = fs.Length;
                    if (len >= 8)
                    {
                        byte[] t8 = new byte[8];
                        fs.Seek(len - 8, SeekOrigin.Begin);
                        fs.Read(t8, 0, 8);
                        if (BE.RU32(t8, 0) == 8 && BE.Ascii(t8, 4, 4) == "free") len -= 8;
                    }
                    int n = (int)Math.Min(64, len);
                    fs.Seek(len - n, SeekOrigin.Begin);
                    byte[] buf = new byte[n];
                    fs.Read(buf, 0, n);
                    byte[] pat = new byte[] { 0, 0, 0, 4, 0, 0, 0, 0 };
                    ok6 = true;
                    for (int i = 0; i < n; i++) { if (buf[i] != pat[i % 8]) { ok6 = false; break; } }
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < Math.Min(8, n); i++) sb.Append(buf[i].ToString("x2") + " ");
                    hex = sb.ToString().Trim();
                }
            }
            catch { ok6 = false; }
            r.Add(new VCheck("Trailing junk bytes", ok6, "last bytes: " + hex + " ..."));

            // 7. top-level layout matches the reference: ftyp, moov, mdat, junk
            bool ok7 = false; string order = "";
            long moovAt = -1, moovLen = 0;
            try
            {
                using (FileStream fs = File.OpenRead(file))
                {
                    List<string> types = new List<string>();
                    long o = 0, len = fs.Length;
                    byte[] h = new byte[16];
                    while (o + 8 <= len)
                    {
                        fs.Seek(o, SeekOrigin.Begin);
                        fs.Read(h, 0, 16);
                        long size = BE.RU32(h, 0);
                        if (size == 1) size = (long)BE.RU64(h, 8);
                        else if (size == 0) size = len - o;
                        if (size < 8 || o + size > len) { types.Add("junk"); break; }
                        types.Add(BE.Ascii(h, 4, 4));
                        if (moovAt < 0 && BE.Ascii(h, 4, 4) == "moov") { moovAt = o; moovLen = size; }
                        o += size;
                    }
                    order = String.Join(", ", types.ToArray());
                    ok7 = (order == "ftyp, moov, mdat, junk");
                }
            }
            catch { ok7 = false; }
            r.Add(new VCheck("Box layout", ok7, order + " (want ftyp, moov, mdat, junk)"));

            // 8. movie header matches a real RTXFury output: mvhd version 1 with
            //    unknown duration, and no movie-level metadata (udta)
            bool ok8 = false; string mh = "no moov box";
            try
            {
                if (moovAt >= 0 && moovLen < 64 * 1024 * 1024)
                {
                    byte[] m = new byte[moovLen];
                    using (FileStream fs = File.OpenRead(file))
                    {
                        fs.Seek(moovAt, SeekOrigin.Begin);
                        fs.Read(m, 0, m.Length);
                    }
                    int ver = -1; bool unknown = false, udta = false;
                    int o = 8;
                    while (o + 8 <= m.Length)
                    {
                        int size = (int)BE.RU32(m, o);
                        string t = BE.Ascii(m, o + 4, 4);
                        if (size < 8 || o + size > m.Length) break;
                        if (t == "mvhd") { ver = m[o + 8]; unknown = (ver == 1 && BE.RU64(m, o + 32) == ulong.MaxValue); }
                        if (t == "udta") udta = true;
                        o += size;
                    }
                    ok8 = unknown && !udta;
                    mh = "mvhd v" + ver + (unknown ? ", duration unknown" : ", duration set") +
                         (udta ? ", has movie metadata" : ", no movie metadata");
                }
            }
            catch { ok8 = false; }
            r.Add(new VCheck("Movie header", ok8, mh));

            return r;
        }

        // Checks for a "clone" output: a well-formed H.264 file with two valid
        // audio tracks, the second a duplicate of the first. (The eight junk
        // checks above do not apply to this method.) requireNoGap makes the
        // end-gap line a real check (for the no-gap tail layouts); otherwise it
        // is informational, since the v3 layout always leaves a ~0.4 s gap.
        public static List<VCheck> VerifyClone(string file, bool requireNoGap)
        {
            string ffprobe = Find("ffprobe");
            string ffmpeg = Find("ffmpeg");
            List<VCheck> r = new List<VCheck>();
            string so, se;
            string q = "\"" + file + "\"";

            // 1. 1 video + 2 audio
            Run(ffprobe, "-v error -show_entries stream=codec_type -of csv=p=0 " + q, out so, out se);
            int nv = 0, na = 0;
            foreach (string ln in SplitLines(so)) { if (ln.Contains("video")) nv++; else if (ln.Contains("audio")) na++; }
            r.Add(new VCheck("Stream layout", nv == 1 && na == 2, nv + " video, " + na + " audio (want 1 video + 2 audio)"));

            // 2. video is H.264
            Run(ffprobe, "-v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 " + q, out so, out se);
            string vcodec = so.Trim();
            r.Add(new VCheck("Video is H.264", vcodec == "h264", "video codec: " + (vcodec.Length == 0 ? "?" : vcodec)));

            // 3. resolution within 1080x1920
            Run(ffprobe, "-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 " + q, out so, out se);
            string[] wh = so.Trim().Split(',');
            int w = wh.Length > 0 ? ParseInt(wh[0]) : 0, h = wh.Length > 1 ? ParseInt(wh[1]) : 0;
            long lo = Math.Min(w, h), hi = Math.Max(w, h);
            string resNote = w + "x" + h + (hi <= 1920 ? " (1080p; confirmed served original)" : " (above 1080p; NOT upload-tested)");
            r.Add(new VCheck("Resolution within 4K", hi > 0 && hi <= 3840 && lo <= 2160, resNote));

            // 4. video decodes clean (no corruption from the re-encode)
            Run(ffmpeg, "-nostdin -v error -i " + q + " -map 0:v:0 -f null -", out so, out se);
            int ve = CountLines(se);
            r.Add(new VCheck("Video decodes clean", ve == 0, ve + " video decode errors (expected: 0)"));

            // 5. both audio tracks decode clean (valid, not junk)
            Run(ffmpeg, "-nostdin -v error -i " + q + " -map 0:a:0 -f null -", out so, out se);
            int a0 = CountLines(se);
            Run(ffmpeg, "-nostdin -v error -i " + q + " -map 0:a:1 -f null -", out so, out se);
            int a1 = CountLines(se);
            r.Add(new VCheck("Both audio tracks valid", a0 == 0 && a1 == 0, "a:0 " + a0 + " errors, a:1 " + a1 + " errors (expected: 0, 0)"));

            // 6. the two audio tracks are duplicates (same packet count)
            Run(ffprobe, "-v error -select_streams a:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 " + q, out so, out se);
            int p0 = ParseInt(so);
            Run(ffprobe, "-v error -select_streams a:1 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 " + q, out so, out se);
            int p1 = ParseInt(so);
            r.Add(new VCheck("Second audio clones the first", p0 > 0 && p0 == p1, "a:0 = " + p0 + " packets, a:1 = " + p1 + " packets"));

            // 7. valid container: ftyp, moov and mdat all present and the tree parses
            bool ok7 = false; string order = "";
            try
            {
                using (FileStream fs = File.OpenRead(file))
                {
                    List<string> types = new List<string>();
                    long o = 0, len = fs.Length; byte[] hh = new byte[16]; bool bad = false;
                    while (o + 8 <= len)
                    {
                        fs.Seek(o, SeekOrigin.Begin); fs.Read(hh, 0, 16);
                        long size = BE.RU32(hh, 0);
                        if (size == 1) size = (long)BE.RU64(hh, 8);
                        else if (size == 0) size = len - o;
                        if (size < 8 || o + size > len) { bad = true; break; }
                        types.Add(BE.Ascii(hh, 4, 4)); o += size;
                    }
                    order = String.Join(", ", types.ToArray());
                    ok7 = !bad && types.Contains("ftyp") && types.Contains("moov") && types.Contains("mdat");
                }
            }
            catch { ok7 = false; }
            r.Add(new VCheck("Valid MP4 container", ok7, order));

            // 8. end gap: how long the file keeps running after the last video
            //    frame (silence + black or frozen picture before the loop)
            Run(ffprobe, "-v error -show_entries format=duration -of csv=p=0 " + q, out so, out se);
            double fdur = ParseDouble(so);
            Run(ffprobe, "-v error -select_streams v:0 -show_entries stream=duration -of csv=p=0 " + q, out so, out se);
            double vdur = ParseDouble(so);
            bool known = fdur > 0 && vdur > 0;
            double gap = fdur - vdur;
            string gd = known
                ? String.Format(CultureInfo.InvariantCulture, "file runs {0:0.000} s past the last video frame", gap)
                : "could not read the durations";
            if (requireNoGap) gd += " (want 0.100 or less)";
            else if (known && gap > 0.1) gd += " (normal for the v3 layout)";
            VCheck g = new VCheck("End gap", known && gap <= 0.1, gd);
            g.Info = !requireNoGap;
            r.Add(g);

            return r;
        }

        public static string Report(List<VCheck> checks, out bool allPass)
        {
            allPass = true;
            StringBuilder sb = new StringBuilder();
            foreach (VCheck c in checks)
            {
                if (!c.Pass && !c.Info) allPass = false;
                string tag = c.Info ? "[INFO] " : (c.Pass ? "[PASS] " : "[WARN] ");
                sb.AppendLine(tag + c.Name.PadRight(28) + "  " + c.Detail);
            }
            return sb.ToString();
        }
    }

    // ---- GUI ----------------------------------------------------------------
    class MainForm : Form
    {
        Label title, instr, status;
        Panel drop;
        Button choose, addSong, verifyBtn;
        ProgressBar bar;
        TextBox report;
        CheckBox reencode;
        NumericUpDown mbps;
        CheckBox cloneBox;
        CheckBox res4k;
        ComboBox tailMode;

        public MainForm()
        {
            Text = "TikTok Quality Optimizer";
            Width = 560; Height = 730;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 24, 28);
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            title = new Label();
            title.Text = "Quality Optimizer";
            title.ForeColor = Color.White;
            title.Font = new Font("Segoe UI", 18F, FontStyle.Bold);
            title.SetBounds(24, 18, 500, 36);
            Controls.Add(title);

            instr = new Label();
            instr.Text = "Drop a video anywhere on this window, or click Choose Video.\n" +
                         "Add Song attaches an MP3 first. Output (*_optimized.mp4) is checked automatically.";
            instr.ForeColor = Color.FromArgb(180, 180, 190);
            instr.Font = new Font("Segoe UI", 10F);
            instr.SetBounds(24, 60, 500, 44);
            Controls.Add(instr);

            drop = new Panel();
            drop.SetBounds(24, 112, 500, 120);
            drop.BackColor = Color.FromArgb(34, 34, 40);
            drop.BorderStyle = BorderStyle.FixedSingle;
            drop.AllowDrop = true;
            drop.DragEnter += OnDragEnter;
            drop.DragDrop += OnDragDrop;
            Label dl = new Label();
            dl.Text = "Drop video here";
            dl.ForeColor = Color.FromArgb(120, 120, 130);
            dl.Font = new Font("Segoe UI", 13F);
            dl.TextAlign = ContentAlignment.MiddleCenter;
            dl.Dock = DockStyle.Fill;
            drop.Controls.Add(dl);
            Controls.Add(drop);

            choose = new Button();
            choose.Text = "Choose Video...";
            choose.SetBounds(24, 244, 150, 38);
            choose.FlatStyle = FlatStyle.Flat;
            choose.ForeColor = Color.White;
            choose.BackColor = Color.FromArgb(0, 120, 215);
            choose.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            choose.Click += OnChoose;
            Controls.Add(choose);

            addSong = new Button();
            addSong.Text = "Add Song + Optimize...";
            addSong.SetBounds(184, 244, 180, 38);
            addSong.FlatStyle = FlatStyle.Flat;
            addSong.ForeColor = Color.White;
            addSong.BackColor = Color.FromArgb(0, 120, 215);
            addSong.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            addSong.Click += OnAddSong;
            Controls.Add(addSong);

            verifyBtn = new Button();
            verifyBtn.Text = "Verify a File...";
            verifyBtn.SetBounds(374, 244, 150, 38);
            verifyBtn.FlatStyle = FlatStyle.Flat;
            verifyBtn.ForeColor = Color.White;
            verifyBtn.BackColor = Color.FromArgb(70, 70, 78);
            verifyBtn.Font = new Font("Segoe UI", 10F);
            verifyBtn.Click += OnVerify;
            Controls.Add(verifyBtn);

            cloneBox = new CheckBox();
            cloneBox.Text = "New TikTok method (re-encode 1080p H.264 + clone audio)";
            cloneBox.ForeColor = Color.FromArgb(230, 210, 150);
            cloneBox.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
            cloneBox.SetBounds(24, 292, 500, 24);
            cloneBox.Checked = true; // confirmed-working method; the junk method below is dead on TikTok
            Controls.Add(cloneBox);

            Label cloneL = new Label();
            cloneL.Text = "Confirmed working 2026-11; the junk method below no longer works. Re-encodes (one generation).";
            cloneL.ForeColor = Color.FromArgb(150, 150, 160);
            cloneL.Font = new Font("Segoe UI", 8.5F);
            cloneL.SetBounds(42, 316, 490, 18);
            Controls.Add(cloneL);

            res4k = new CheckBox();
            res4k.Text = "Allow up to 4K  (default 1080p — 4K is NOT upload-tested)";
            res4k.ForeColor = Color.FromArgb(200, 200, 210);
            res4k.Font = new Font("Segoe UI", 9F);
            res4k.SetBounds(42, 336, 490, 22);
            Controls.Add(res4k);

            // End-gap fix: where the clone engine puts its 16 silent tail frames.
            // Item order matches Mp4.TailLayout (V3, KeepEditLists, Front).
            Label tailL = new Label();
            tailL.Text = "End-gap fix:";
            tailL.ForeColor = Color.FromArgb(200, 200, 210);
            tailL.Font = new Font("Segoe UI", 9F);
            tailL.SetBounds(42, 363, 86, 20);
            Controls.Add(tailL);

            tailMode = new ComboBox();
            tailMode.DropDownStyle = ComboBoxStyle.DropDownList;
            tailMode.Font = new Font("Segoe UI", 9F);
            tailMode.Items.Add("Off - v3 (upload-confirmed, ~0.4 s silent end)");
            tailMode.Items.Add("Keep edit lists - no end gap (NOT upload-tested)");
            tailMode.Items.Add("Silent frames at start - no end gap (NOT upload-tested)");
            tailMode.SelectedIndex = 0;
            tailMode.SetBounds(130, 360, 394, 24);
            Controls.Add(tailMode);
            cloneBox.CheckedChanged += delegate { tailMode.Enabled = cloneBox.Checked; };

            reencode = new CheckBox();
            reencode.Text = "Set video bitrate";
            reencode.ForeColor = Color.FromArgb(200, 200, 210);
            reencode.Font = new Font("Segoe UI", 9.5F);
            reencode.SetBounds(24, 388, 130, 24);
            reencode.CheckedChanged += delegate { mbps.Enabled = reencode.Checked; };
            Controls.Add(reencode);

            mbps = new NumericUpDown();
            mbps.Minimum = 8; mbps.Maximum = 50; mbps.Value = 15;
            mbps.Font = new Font("Segoe UI", 9.5F);
            mbps.SetBounds(156, 388, 50, 24);
            mbps.Enabled = false;
            Controls.Add(mbps);

            Label mbpsL = new Label();
            mbpsL.Text = "Mbps (default 15; use ~30-40 for 4K)";
            mbpsL.ForeColor = Color.FromArgb(150, 150, 160);
            mbpsL.Font = new Font("Segoe UI", 9F);
            mbpsL.SetBounds(230, 391, 304, 20);
            Controls.Add(mbpsL);

            bar = new ProgressBar();
            bar.SetBounds(24, 420, 500, 12);
            bar.Style = ProgressBarStyle.Marquee;
            bar.Visible = false;
            Controls.Add(bar);

            status = new Label();
            status.Text = "";
            status.ForeColor = Color.FromArgb(160, 200, 160);
            status.Font = new Font("Segoe UI", 9.5F);
            status.SetBounds(24, 438, 500, 20);
            Controls.Add(status);

            report = new TextBox();
            report.Multiline = true;
            report.ReadOnly = true;
            report.ScrollBars = ScrollBars.Vertical;
            report.BackColor = Color.FromArgb(18, 18, 22);
            report.ForeColor = Color.FromArgb(200, 210, 200);
            report.Font = new Font("Consolas", 9F);
            report.BorderStyle = BorderStyle.FixedSingle;
            report.SetBounds(24, 462, 500, 208);
            Controls.Add(report);

            if (!Tools.Available())
            {
                SetStatus("ffmpeg was not found. Put ffmpeg.exe and ffprobe.exe next to this program, or install ffmpeg and add it to PATH.", true);
                choose.Enabled = false;
                addSong.Enabled = false;
                verifyBtn.Enabled = false;
            }
        }

        void OnDragEnter(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        void OnDragDrop(object s, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0) Start(files[0], null);
        }

        void OnChoose(object s, EventArgs e)
        {
            OpenFileDialog d = new OpenFileDialog();
            d.Filter = "Video files|*.mp4;*.mov;*.m4v;*.mkv;*.webm;*.avi;*.flv;*.wmv|All files|*.*";
            if (d.ShowDialog() == DialogResult.OK) Start(d.FileName, null);
        }

        void OnAddSong(object s, EventArgs e)
        {
            OpenFileDialog dv = new OpenFileDialog();
            dv.Title = "Choose the VIDEO";
            dv.Filter = "Video files|*.mp4;*.mov;*.m4v;*.mkv;*.webm;*.avi;*.flv;*.wmv|All files|*.*";
            if (dv.ShowDialog() != DialogResult.OK) return;

            OpenFileDialog da = new OpenFileDialog();
            da.Title = "Choose the AUDIO to attach (mp3, m4a, wav, aac)";
            da.Filter = "Audio files|*.mp3;*.m4a;*.aac;*.wav;*.ogg;*.opus;*.flac|All files|*.*";
            if (da.ShowDialog() != DialogResult.OK) return;

            Start(dv.FileName, da.FileName);
        }

        void OnVerify(object s, EventArgs e)
        {
            OpenFileDialog d = new OpenFileDialog();
            d.Filter = "MP4 video|*.mp4;*.m4v|All files|*.*";
            if (d.ShowDialog() != DialogResult.OK) return;
            string file = d.FileName;
            // Use the checks for whichever method is selected (the clone checks
            // when "New TikTok method" is ticked, else the junk-method checks).
            bool clone = cloneBox.Checked;
            bool noGap = tailMode.SelectedIndex != (int)Mp4.TailLayout.V3;
            Busy(true);
            SetStatus("Verifying " + Path.GetFileName(file) + " ...", false);
            SetReport("");
            Thread t = new Thread(delegate()
            {
                try
                {
                    bool allPass;
                    string rep = Tools.Report(clone ? Tools.VerifyClone(file, noGap) : Tools.Verify(file), out allPass);
                    ShowVerify(Path.GetFileName(file), rep, allPass);
                }
                catch (Exception ex) { Fail(ex.Message); }
            });
            t.IsBackground = true; t.SetApartmentState(ApartmentState.STA); t.Start();
        }

        void Start(string input, string audioPath)
        {
            string dir = Path.GetDirectoryName(input);
            string name = Path.GetFileNameWithoutExtension(input);
            string outPath = Path.Combine(dir, name + "_optimized.mp4");

            SaveFileDialog sd = new SaveFileDialog();
            sd.FileName = Path.GetFileName(outPath);
            sd.InitialDirectory = dir;
            sd.Filter = "MP4 video|*.mp4";
            if (sd.ShowDialog() != DialogResult.OK) return;
            outPath = sd.FileName;

            Tools.ReencodeMbps = reencode.Checked ? (int)mbps.Value : 0;
            Tools.CloneMbps = reencode.Checked ? (int)mbps.Value : 15;
            Tools.CloneMaxBox = res4k.Checked ? "2160:3840" : "1080:1920";
            Tools.CloneTail = (Mp4.TailLayout)tailMode.SelectedIndex;
            bool clone = cloneBox.Checked;
            Busy(true);
            SetStatus("Working...", false);
            SetReport("");

            Thread t = new Thread(delegate()
            {
                string tmp = null;
                try
                {
                    bool allPass;
                    string rep, note;
                    if (clone)
                    {
                        tmp = Tools.BuildClone(input, audioPath, delegate(string m) { SetStatus(m, false); });
                        File.Copy(tmp, outPath, true);
                        note = "re-encoded 1080p H.264, cloned audio track, tail: " + Tools.TailName(Tools.CloneTail);
                        SetStatus("Verifying output...", false);
                        rep = Tools.Report(Tools.VerifyClone(outPath, Tools.CloneTail != Mp4.TailLayout.V3), out allPass);
                        Done(outPath, note, rep, allPass);
                        return;
                    }
                    if (audioPath == null)
                        tmp = Tools.Normalize(input, delegate(string m) { SetStatus(m, false); });
                    else
                        tmp = Tools.MuxAudio(input, audioPath, delegate(string m) { SetStatus(m, false); });
                    SetStatus("Applying quality method...", false);
                    byte[] data = File.ReadAllBytes(tmp);
                    byte[] outData = Mp4.Transform(data, out note);
                    File.WriteAllBytes(outPath, outData);
                    SetStatus("Verifying output...", false);
                    rep = Tools.Report(Tools.Verify(outPath), out allPass);
                    Done(outPath, note, rep, allPass);
                }
                catch (Exception ex)
                {
                    Fail(ex.Message);
                }
                finally
                {
                    try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); }
                    catch { }
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void Done(string outPath, string note, string rep, bool allPass)
        {
            if (InvokeRequired) { Invoke(new Action<string, string, string, bool>(Done), outPath, note, rep, allPass); return; }
            Busy(false);
            SetReport(rep);
            SetStatus((allPass ? "All checks passed." : "Saved, but some checks did not pass - see below.") + "  " + outPath, !allPass);
            string head = allPass
                ? "Saved and verified - all checks passed:\n\n"
                : "Saved, but some checks did not pass:\n\n";
            if (MessageBox.Show(head + rep + "\n" + outPath + "\n\nOpen the containing folder?",
                "Optimizer", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                try { Process.Start("explorer.exe", "/select,\"" + outPath + "\""); }
                catch { }
            }
        }

        void ShowVerify(string fileName, string rep, bool allPass)
        {
            if (InvokeRequired) { Invoke(new Action<string, string, bool>(ShowVerify), fileName, rep, allPass); return; }
            Busy(false);
            SetReport(rep);
            SetStatus(fileName + ": " + (allPass ? "all checks passed." : "some checks did not pass."), !allPass);
            MessageBox.Show((allPass ? "All checks passed:\n\n" : "Some checks did not pass:\n\n") + rep,
                "Verify - " + fileName, MessageBoxButtons.OK,
                allPass ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        void Fail(string msg)
        {
            if (InvokeRequired) { Invoke(new Action<string>(Fail), msg); return; }
            Busy(false);
            SetStatus("Failed.", true);
            MessageBox.Show(msg, "Optimizer - error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        void Busy(bool on)
        {
            if (InvokeRequired) { Invoke(new Action<bool>(Busy), on); return; }
            bar.Visible = on;
            choose.Enabled = !on;
            addSong.Enabled = !on;
            verifyBtn.Enabled = !on;
            reencode.Enabled = !on;
            mbps.Enabled = !on && reencode.Checked;
            cloneBox.Enabled = !on;
            res4k.Enabled = !on;
            tailMode.Enabled = !on && cloneBox.Checked;
        }

        void SetReport(string text)
        {
            if (report.InvokeRequired) { report.Invoke(new Action<string>(SetReport), text); return; }
            report.Text = (text == null ? "" : text.Replace("\n", "\r\n"));
        }

        void SetStatus(string msg, bool error)
        {
            if (status.InvokeRequired) { status.Invoke(new Action<string, bool>(SetStatus), msg, error); return; }
            status.ForeColor = error ? Color.FromArgb(230, 140, 140) : Color.FromArgb(160, 200, 160);
            status.Text = msg;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Optional leading  -bitrate <Mbps>  re-encodes the video at that rate.
            if (args.Length >= 2 && (args[0] == "-bitrate" || args[0] == "/bitrate"))
            {
                int m;
                if (!int.TryParse(args[1], out m) || m < 1 || m > 200)
                {
                    Console.Error.WriteLine("usage: optimizer.exe -bitrate <Mbps> [-mux <video> <audio>] <in> <out>");
                    Environment.Exit(2);
                }
                Tools.ReencodeMbps = m;
                string[] rest = new string[args.Length - 2];
                Array.Copy(args, 2, rest, 0, rest.Length);
                args = rest;
            }

            // Optional leading  -clone  selects the RTXFury v2 engine: re-encode
            // to 1080p H.264 and add a duplicate audio track (no junk method).
            bool clone = false;
            if (args.Length >= 1 && (args[0] == "-clone" || args[0] == "/clone"))
            {
                clone = true;
                string[] rest = new string[args.Length - 1];
                Array.Copy(args, 1, rest, 0, rest.Length);
                args = rest;
            }
            // A leading -bitrate N also sets the clone engine's video bitrate,
            // e.g. optimizer.exe -bitrate 25 -clone in.mov out.mp4
            if (clone && Tools.ReencodeMbps > 0) Tools.CloneMbps = Tools.ReencodeMbps;

            // Optional resolution for the clone engine (default 1080p, confirmed).
            // -4k caps at 2160x3840; -native keeps the source resolution. Both are
            // UNTESTED on TikTok (the proven method is 1080p).
            if (args.Length >= 1 && (args[0] == "-4k" || args[0] == "/4k"))
            {
                Tools.CloneMaxBox = "2160:3840";
                string[] rest = new string[args.Length - 1]; Array.Copy(args, 1, rest, 0, rest.Length); args = rest;
            }
            else if (args.Length >= 1 && (args[0] == "-native" || args[0] == "/native"))
            {
                Tools.CloneMaxBox = "";
                string[] rest = new string[args.Length - 1]; Array.Copy(args, 1, rest, 0, rest.Length); args = rest;
            }

            // Optional tail layout for the clone engine:  -tail v3|keep|front
            // (default v3, the upload-confirmed one; keep/front remove the ~0.4 s
            // silent end and are NOT upload-tested). With -verify, keep/front
            // make the end-gap line a real check.
            if (args.Length >= 2 && (args[0] == "-tail" || args[0] == "/tail"))
            {
                string tm = args[1].ToLowerInvariant();
                if (tm == "v3") Tools.CloneTail = Mp4.TailLayout.V3;
                else if (tm == "keep") Tools.CloneTail = Mp4.TailLayout.KeepEditLists;
                else if (tm == "front") Tools.CloneTail = Mp4.TailLayout.Front;
                else { Console.Error.WriteLine("usage: optimizer.exe -clone [-4k|-native] -tail v3|keep|front ..."); Environment.Exit(2); }
                string[] rest = new string[args.Length - 2]; Array.Copy(args, 2, rest, 0, rest.Length); args = rest;
            }
            bool noGap = Tools.CloneTail != Mp4.TailLayout.V3;

            // Verify only:  Optimizer.exe -verify <file>
            if (args.Length >= 1 && (args[0] == "-verify" || args[0] == "/verify"))
            {
                if (args.Length < 2) { Console.Error.WriteLine("usage: optimizer.exe -verify <file>"); Environment.Exit(2); }
                bool allPass;
                Console.WriteLine(Tools.Report(clone ? Tools.VerifyClone(args[1], noGap) : Tools.Verify(args[1]), out allPass));
                Console.WriteLine(allPass ? "VERDICT: all checks passed." : "VERDICT: some checks did not pass.");
                Environment.Exit(allPass ? 0 : 1);
            }

            // Attach audio, then convert + verify:  Optimizer.exe -mux <video> <audio> <out>
            if (args.Length >= 1 && (args[0] == "-mux" || args[0] == "/mux"))
            {
                if (args.Length < 4) { Console.Error.WriteLine("usage: optimizer.exe -mux <video> <audio> <out>"); Environment.Exit(2); }
                try
                {
                    string note;
                    if (clone)
                    {
                        string tmpc = Tools.BuildClone(args[1], args[2], delegate(string m) { Console.WriteLine(m); });
                        File.Copy(tmpc, args[3], true);
                        if (File.Exists(tmpc)) File.Delete(tmpc);
                        note = "re-encoded 1080p H.264, cloned audio track, tail: " + Tools.TailName(Tools.CloneTail);
                    }
                    else
                    {
                        string tmp = Tools.MuxAudio(args[1], args[2], delegate(string m) { Console.WriteLine(m); });
                        byte[] data = File.ReadAllBytes(tmp);
                        byte[] outData = Mp4.Transform(data, out note);
                        File.WriteAllBytes(args[3], outData);
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                    Console.WriteLine("OK: " + args[3] + "  (" + note + ")");
                    bool allPass;
                    Console.WriteLine(Tools.Report(clone ? Tools.VerifyClone(args[3], noGap) : Tools.Verify(args[3]), out allPass));
                    Console.WriteLine(allPass ? "VERDICT: all checks passed." : "VERDICT: some checks did not pass.");
                    Environment.Exit(allPass ? 0 : 1);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("ERROR: " + ex.Message);
                    Environment.Exit(1);
                }
            }

            // Convert (and verify):  Optimizer.exe <in> <out>
            if (args.Length >= 2)
            {
                try
                {
                    string note;
                    if (clone)
                    {
                        string tmpc = Tools.BuildClone(args[0], null, delegate(string m) { Console.WriteLine(m); });
                        File.Copy(tmpc, args[1], true);
                        if (File.Exists(tmpc)) File.Delete(tmpc);
                        note = "re-encoded 1080p H.264, cloned audio track, tail: " + Tools.TailName(Tools.CloneTail);
                    }
                    else
                    {
                        string tmp = Tools.Normalize(args[0], delegate(string m) { Console.WriteLine(m); });
                        byte[] data = File.ReadAllBytes(tmp);
                        byte[] outData = Mp4.Transform(data, out note);
                        File.WriteAllBytes(args[1], outData);
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                    Console.WriteLine("OK: " + args[1] + "  (" + note + ")");
                    bool allPass;
                    Console.WriteLine(Tools.Report(clone ? Tools.VerifyClone(args[1], noGap) : Tools.Verify(args[1]), out allPass));
                    Console.WriteLine(allPass ? "VERDICT: all checks passed." : "VERDICT: some checks did not pass.");
                    Environment.Exit(allPass ? 0 : 1);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("ERROR: " + ex.Message);
                    Environment.Exit(1);
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
