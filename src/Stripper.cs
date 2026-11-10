// Metadata Stripper
// Standalone Windows app, companion to the Quality Optimizer. Detects app
// metadata (TikTok / ByteDance "Aweme", sec_uid, vid: comments, ...) in an
// MP4 / MOV and writes a clean copy with it removed. Video and audio are
// stream-copied (-c copy): nothing is re-encoded.
//
// Where the tags can live, and what removes them:
//   * moov metadata (udta, meta/ilst, keys, track udta, handler names)
//       -> removed: ffmpeg rewrites the container with all metadata dropped
//   * extra top-level boxes (uuid, free) and data after the last box
//       -> removed: ffmpeg only writes the boxes it needs
//   * the video bitstream (H.264 / HEVC SEI user data)
//       -> survives a stream copy; the "also strip SEI" option drops SEI NAL
//          units, which changes packet bytes but not the decoded pictures
//
// Run this BEFORE optimizer.exe, never after: the rewrite drops the junk
// track and trailing bytes the optimizer adds.
//
// Target: C# 5 / .NET Framework 4.8 (the in-box csc.exe), same as optimizer.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MetadataStripper
{
    // ---- Signature scan ----------------------------------------------------
    class Hit
    {
        public string Signature;
        public long Offset;
        public string Box;        // innermost box path, e.g. moov/udta/meta/ilst
        public string Context;    // printable bytes around the hit
        public bool InMedia;      // inside mdat (bitstream), not metadata
    }

    class ScanResult
    {
        public List<Hit> Hits = new List<Hit>();
        public List<string> TopLevel = new List<string>();
        public bool LooksOptimized;   // trailing 00 00 00 04 00 00 00 00 junk

        public int MetaCount { get { int n = 0; foreach (Hit h in Hits) if (!h.InMedia) n++; return n; } }
        public int MediaCount { get { return Hits.Count - MetaCount; } }
    }

    static class Scanner
    {
        // Searched case-insensitively.
        public static readonly string[] Signatures = new string[] {
            "com.ss.iphone.ugc.Aweme",
            "com.ss.android.ugc",
            "Aweme",
            "sec_uid",
            "bytedance",
            "vid:v",          // TikTok video-ID comment, e.g. "vid:v2800fgi..."
            "aigc_info",
            "tiktok",
            "musically",
        };

        static readonly HashSet<string> CONT = new HashSet<string>(new string[] {
            "moov", "trak", "mdia", "minf", "stbl", "dinf", "edts",
            "udta", "ilst", "tref", "mvex", "moof", "traf", "sinf", "schi",
        });

        struct BoxSpan { public string Path; public long Start; public long Size; }

        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        static uint RU32(byte[] b, int o)
        {
            return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
        }

        static void Walk(FileStream fs, long start, long end, string path, List<BoxSpan> out_)
        {
            byte[] h = new byte[16];
            long pos = start;
            while (pos + 8 <= end)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                int got = fs.Read(h, 0, 16);
                if (got < 8) break;
                long size = RU32(h, 0);
                string type = Latin1.GetString(h, 4, 4);   // keeps the © in ©cmt
                int hdr = 8;
                if (size == 1)
                {
                    if (got < 16) break;
                    size = ((long)RU32(h, 8) << 32) | RU32(h, 12);
                    hdr = 16;
                }
                else if (size == 0) size = end - pos;
                if (size < hdr || pos + size > end)
                {
                    BoxSpan g; g.Path = path.Length == 0 ? "<trailing data>" : path + "/<garbage>";
                    g.Start = pos; g.Size = end - pos;
                    out_.Add(g);
                    return;
                }
                // ilst children are named by key index (binary), not 4CC
                foreach (char ch in type) if ((ch < 32 || ch > 126) && ch != '©') { type = "#" + RU32(h, 4); break; }
                string p = path.Length == 0 ? type : path + "/" + type;
                BoxSpan b; b.Path = p; b.Start = pos; b.Size = size;
                out_.Add(b);
                if (CONT.Contains(type) || type == "meta")
                {
                    long child = pos + hdr;
                    // MP4 'meta' is a full box (4 bytes version/flags first);
                    // QuickTime 'meta' is not. The first child's type sits at
                    // +4 for QuickTime and +8 for MP4.
                    if (type == "meta")
                    {
                        byte[] peek = new byte[4];
                        fs.Seek(child + 8, SeekOrigin.Begin);
                        if (fs.Read(peek, 0, 4) == 4)
                        {
                            string t = Encoding.ASCII.GetString(peek);
                            if (t == "hdlr" || t == "keys" || t == "ilst") child += 4;
                        }
                    }
                    Walk(fs, child, pos + size, p, out_);
                }
                pos += size;
            }
            if (pos < end && path.Length == 0)
            {
                BoxSpan t; t.Path = "<trailing data>"; t.Start = pos; t.Size = end - pos;
                out_.Add(t);
            }
        }

        public static ScanResult Scan(string file)
        {
            ScanResult r = new ScanResult();
            byte[][] needles = new byte[Signatures.Length][];
            int maxLen = 0;
            for (int i = 0; i < Signatures.Length; i++)
            {
                needles[i] = Encoding.ASCII.GetBytes(Signatures[i].ToLowerInvariant());
                maxLen = Math.Max(maxLen, needles[i].Length);
            }

            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                long len = fs.Length;
                List<BoxSpan> boxes = new List<BoxSpan>();
                Walk(fs, 0, len, "", boxes);
                foreach (BoxSpan b in boxes) if (b.Path.IndexOf('/') < 0) r.TopLevel.Add(b.Path);

                // Whole-file scan in chunks, carrying an overlap so hits that
                // straddle a chunk boundary are still found exactly once.
                const int CHUNK = 8 * 1024 * 1024;
                int overlap = maxLen - 1;
                byte[] buf = new byte[CHUNK + overlap];
                int carry = 0;
                long basePos = 0;   // file offset of buf[0]
                fs.Seek(0, SeekOrigin.Begin);
                HashSet<long> seen = new HashSet<long>();
                while (true)
                {
                    int got = fs.Read(buf, carry, CHUNK);
                    if (got <= 0) break;
                    int n = carry + got;
                    for (int i = 0; i < n; i++)
                    {
                        byte c = Lower(buf[i]);
                        for (int k = 0; k < needles.Length; k++)
                        {
                            byte[] nd = needles[k];
                            if (c != nd[0] || i + nd.Length > n) continue;
                            if (i + nd.Length <= carry) continue;   // already seen last round
                            int j = 1;
                            while (j < nd.Length && Lower(buf[i + j]) == nd[j]) j++;
                            if (j < nd.Length) continue;
                            long at = basePos + i;
                            if (!seen.Add(at)) continue;
                            r.Hits.Add(MakeHit(Signatures[k], at, buf, i, n, boxes));
                        }
                    }
                    int keep = Math.Min(overlap, n);
                    Array.Copy(buf, n - keep, buf, 0, keep);
                    basePos += n - keep;
                    carry = keep;
                }

                // Optimizer outputs end with 00 00 00 04 00 00 00 00 repeats
                // (optionally followed by an empty 8-byte free box).
                if (len >= 24)
                {
                    byte[] t = new byte[24];
                    fs.Seek(len - 24, SeekOrigin.Begin);
                    fs.Read(t, 0, 24);
                    int o = 0;
                    if (RU32(t, 16) == 8 && Encoding.ASCII.GetString(t, 20, 4) == "free") o = 0; else o = 8;
                    r.LooksOptimized = RU32(t, o) == 4 && RU32(t, o + 4) == 0 && RU32(t, o + 8) == 4 && RU32(t, o + 12) == 0;
                }
            }
            return r;
        }

        static byte Lower(byte b) { return (b >= 'A' && b <= 'Z') ? (byte)(b + 32) : b; }

        static Hit MakeHit(string sig, long at, byte[] buf, int i, int n, List<BoxSpan> boxes)
        {
            Hit h = new Hit();
            h.Signature = sig;
            h.Offset = at;
            // innermost (smallest) box containing the hit
            BoxSpan best = new BoxSpan(); bool found = false;
            foreach (BoxSpan b in boxes)
                if (b.Start <= at && at < b.Start + b.Size && (!found || b.Size < best.Size)) { best = b; found = true; }
            h.Box = found ? best.Path : "?";
            h.InMedia = found && best.Path.StartsWith("mdat");
            StringBuilder sb = new StringBuilder();
            for (int k = Math.Max(0, i - 8); k < Math.Min(n, i + 40); k++)
                sb.Append(buf[k] >= 32 && buf[k] < 127 ? (char)buf[k] : '.');
            h.Context = sb.ToString();
            return h;
        }

        public static string Report(string file, ScanResult r)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("File: " + Path.GetFileName(file));
            sb.AppendLine("Top-level boxes: " + String.Join(", ", r.TopLevel.ToArray()));
            if (r.Hits.Count == 0)
            {
                sb.AppendLine("[CLEAN] No app signatures found.");
            }
            else
            {
                if (r.MetaCount > 0)
                {
                    sb.AppendLine("[FOUND] " + r.MetaCount + " in container metadata (removed by Strip):");
                    foreach (Hit h in r.Hits) if (!h.InMedia) sb.AppendLine(Line(h));
                }
                if (r.MediaCount > 0)
                {
                    sb.AppendLine("[FOUND] " + r.MediaCount + " inside the video data (SEI; needs \"also strip SEI\"):");
                    int shown = 0;
                    foreach (Hit h in r.Hits)
                    {
                        if (!h.InMedia) continue;
                        if (++shown > 20) { sb.AppendLine("  ... " + (r.MediaCount - 20) + " more"); break; }
                        sb.AppendLine(Line(h));
                    }
                }
            }
            if (r.LooksOptimized)
                sb.AppendLine("[NOTE] This looks like an optimizer output. Stripping it removes the " +
                              "optimization; strip the original video, then optimize the clean copy.");
            return sb.ToString();
        }

        static string Line(Hit h)
        {
            return "  " + ("'" + h.Signature + "'").PadRight(26) + " @" + h.Offset.ToString().PadLeft(10) +
                   "  " + h.Box + "\n      " + h.Context;
        }
    }

    // ---- ffmpeg driving ----------------------------------------------------
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
            // Close stdin at once so ffmpeg never waits on a console.
            p.StandardInput.Close();
            // Drain stderr on its own thread so a full pipe can't deadlock.
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

        public static bool Available()
        {
            string so, se;
            try { Run(Find("ffmpeg"), "-version", out so, out se); return true; }
            catch { return false; }
        }

        static string Q(string s) { return "\"" + s + "\""; }

        static string Tail(string s, int n)
        {
            if (s == null) return "";
            s = s.Trim();
            return s.Length <= n ? s : s.Substring(s.Length - n);
        }

        static string VideoCodec(string file)
        {
            string so, se;
            Run(Find("ffprobe"), "-v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 " + Q(file), out so, out se);
            return so.Trim();
        }

        // Output path next to the input: <name>_clean.mp4 / .mov
        public static string DefaultOutput(string input)
        {
            string ext = Path.GetExtension(input).ToLowerInvariant();
            if (ext != ".mov" && ext != ".m4v") ext = ".mp4";
            return Path.Combine(Path.GetDirectoryName(input),
                Path.GetFileNameWithoutExtension(input) + "_clean" + ext);
        }

        public static void Strip(string input, string output, bool stripSei, Action<string> status)
        {
            if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
                throw new Exception("The output must be a different file from the input.");

            string args =
                "-nostdin -hide_banner -v error -y -i " + Q(input) +
                // keep video and audio only: drops data / timecode / subtitle tracks
                " -map 0:v? -map 0:a? -c copy" +
                // drop global, per-stream and chapter metadata
                " -map_metadata -1 -map_metadata:s:v -1 -map_metadata:s:a -1 -map_chapters -1" +
                // bitexact: no "encoder=Lavf..." tags added by ffmpeg itself
                " -fflags +bitexact -flags:v +bitexact -flags:a +bitexact" +
                // neutral handler names instead of anything copied from the source
                " -metadata:s:v handler_name=VideoHandler -metadata:s:a handler_name=SoundHandler" +
                " -movflags +faststart";

            if (stripSei)
            {
                // SEI carries no picture data; removing it leaves frames unchanged.
                string codec = VideoCodec(input);
                if (codec == "h264") args += " -bsf:v filter_units=remove_types=6";
                else if (codec == "hevc") args += " -bsf:v \"filter_units=remove_types=39|40\"";
                else status("SEI stripping supports H.264 / HEVC only; skipped for " + codec + ".");
            }

            status("Rewriting container (streams copied, no re-encode)...");
            string so, se;
            int rc = Run(Find("ffmpeg"), args + " " + Q(output), out so, out se);
            if (rc != 0)
            {
                try { if (File.Exists(output)) File.Delete(output); } catch { }
                throw new Exception("ffmpeg could not process this file.\n\n" + Tail(se, 1500));
            }
        }

        static string Md5(string file, string map, bool copy)
        {
            string so, se;
            int rc = Run(Find("ffmpeg"), "-nostdin -v error -i " + Q(file) + " -map " + map +
                         (copy ? " -c copy" : "") + " -f md5 -", out so, out se);
            return rc == 0 ? so.Trim() : "";
        }

        static bool HasStream(string file, string sel)
        {
            string so, se;
            Run(Find("ffprobe"), "-v error -select_streams " + sel + " -show_entries stream=index -of csv=p=0 " + Q(file), out so, out se);
            return so.Trim().Length > 0;
        }

        static int CountLines(string s)
        {
            int c = 0;
            foreach (string ln in (s ?? "").Replace("\r", "").Split('\n')) if (ln.Trim().Length > 0) c++;
            return c;
        }

        // Checks a stripped file against its source. Returns the report.
        public static string Verify(string input, string output, out bool allPass)
        {
            StringBuilder sb = new StringBuilder();
            allPass = true;

            ScanResult sr = Scanner.Scan(output);
            bool clean = sr.Hits.Count == 0;
            sb.AppendLine((clean ? "[PASS] " : "[WARN] ") + "No app signatures".PadRight(24) +
                (clean ? "none found in output" : sr.Hits.Count + " still present (see Check a File)"));
            allPass &= clean;

            string so, se;
            Run(Find("ffmpeg"), "-nostdin -v error -i " + Q(output) + " -map 0:v? -map 0:a? -f null -", out so, out se);
            int errs = CountLines(se);
            sb.AppendLine((errs == 0 ? "[PASS] " : "[WARN] ") + "Plays (full decode)".PadRight(24) +
                errs + " decode errors (expected: 0)");
            allPass &= errs == 0;

            // Packet checksums: equal means the stream was copied bit for bit.
            // With SEI stripped the video packets differ, so compare decoded frames.
            string[] kinds = { "v", "a" };
            string[] names = { "Video", "Audio" };
            for (int i = 0; i < 2; i++)
            {
                if (!HasStream(input, kinds[i])) continue;
                string a = Md5(input, "0:" + kinds[i], true), b = Md5(output, "0:" + kinds[i], true);
                if (a.Length > 0 && a == b)
                {
                    sb.AppendLine("[PASS] " + (names[i] + " untouched").PadRight(24) + "packets identical to source");
                }
                else if (kinds[i] == "v")
                {
                    bool same = Md5(input, "0:v:0", false) == Md5(output, "0:v:0", false);
                    sb.AppendLine((same ? "[PASS] " : "[WARN] ") + "Video untouched".PadRight(24) +
                        (same ? "packets differ (SEI removed), decoded frames identical" : "decoded frames DIFFER"));
                    allPass &= same;
                }
                else
                {
                    sb.AppendLine("[WARN] " + "Audio untouched".PadRight(24) + "packets differ from source");
                    allPass = false;
                }
            }
            return sb.ToString();
        }
    }

    // ---- GUI ----------------------------------------------------------------
    class MainForm : Form
    {
        Label title, instr, status;
        Panel drop;
        Button choose, checkBtn;
        CheckBox seiBox;
        ProgressBar bar;
        TextBox report;

        public MainForm()
        {
            Text = "Metadata Stripper";
            Width = 560; Height = 580;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 24, 28);
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            title = new Label();
            title.Text = "Metadata Stripper";
            title.ForeColor = Color.White;
            title.Font = new Font("Segoe UI", 18F, FontStyle.Bold);
            title.SetBounds(24, 18, 500, 36);
            Controls.Add(title);

            instr = new Label();
            instr.Text = "Drop a video to remove app tags (Aweme, sec_uid, vid:, ...). No re-encode.\n" +
                         "Output (*_clean.mp4) is checked automatically. Run this BEFORE the optimizer.";
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
            choose.SetBounds(24, 244, 180, 38);
            choose.FlatStyle = FlatStyle.Flat;
            choose.ForeColor = Color.White;
            choose.BackColor = Color.FromArgb(0, 120, 215);
            choose.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            choose.Click += OnChoose;
            Controls.Add(choose);

            checkBtn = new Button();
            checkBtn.Text = "Check a File...";
            checkBtn.SetBounds(214, 244, 150, 38);
            checkBtn.FlatStyle = FlatStyle.Flat;
            checkBtn.ForeColor = Color.White;
            checkBtn.BackColor = Color.FromArgb(70, 70, 78);
            checkBtn.Font = new Font("Segoe UI", 10F);
            checkBtn.Click += OnCheck;
            Controls.Add(checkBtn);

            seiBox = new CheckBox();
            seiBox.Text = "Also strip SEI from video stream";
            seiBox.ForeColor = Color.FromArgb(180, 180, 190);
            seiBox.Font = new Font("Segoe UI", 9F);
            seiBox.SetBounds(24, 290, 500, 22);
            Controls.Add(seiBox);

            bar = new ProgressBar();
            bar.SetBounds(24, 316, 500, 12);
            bar.Style = ProgressBarStyle.Marquee;
            bar.Visible = false;
            Controls.Add(bar);

            status = new Label();
            status.Text = "";
            status.ForeColor = Color.FromArgb(160, 200, 160);
            status.Font = new Font("Segoe UI", 9.5F);
            status.SetBounds(24, 332, 500, 20);
            Controls.Add(status);

            report = new TextBox();
            report.Multiline = true;
            report.ReadOnly = true;
            report.ScrollBars = ScrollBars.Both;
            report.WordWrap = false;
            report.BackColor = Color.FromArgb(18, 18, 22);
            report.ForeColor = Color.FromArgb(200, 210, 200);
            report.Font = new Font("Consolas", 9F);
            report.BorderStyle = BorderStyle.FixedSingle;
            report.SetBounds(24, 356, 500, 172);
            Controls.Add(report);

            if (!Tools.Available())
            {
                SetStatus("ffmpeg was not found. Put ffmpeg.exe and ffprobe.exe next to this program, or install ffmpeg and add it to PATH.", true);
                choose.Enabled = false;
                checkBtn.Enabled = false;
            }
        }

        static string VideoFilter = "Video files|*.mp4;*.mov;*.m4v|All files|*.*";

        void OnDragEnter(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        void OnDragDrop(object s, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0) Start(files[0]);
        }

        void OnChoose(object s, EventArgs e)
        {
            OpenFileDialog d = new OpenFileDialog();
            d.Filter = VideoFilter;
            if (d.ShowDialog() == DialogResult.OK) Start(d.FileName);
        }

        void OnCheck(object s, EventArgs e)
        {
            OpenFileDialog d = new OpenFileDialog();
            d.Filter = VideoFilter;
            if (d.ShowDialog() != DialogResult.OK) return;
            string file = d.FileName;
            Busy(true);
            SetStatus("Scanning " + Path.GetFileName(file) + " ...", false);
            SetReport("");
            Thread t = new Thread(delegate()
            {
                try
                {
                    ScanResult r = Scanner.Scan(file);
                    ShowCheck(Path.GetFileName(file), Scanner.Report(file, r), r.Hits.Count == 0);
                }
                catch (Exception ex) { Fail(ex.Message); }
            });
            t.IsBackground = true; t.SetApartmentState(ApartmentState.STA); t.Start();
        }

        void Start(string input)
        {
            string outPath = Tools.DefaultOutput(input);
            SaveFileDialog sd = new SaveFileDialog();
            sd.FileName = Path.GetFileName(outPath);
            sd.InitialDirectory = Path.GetDirectoryName(input);
            sd.Filter = Path.GetExtension(outPath) == ".mov" ? "QuickTime video|*.mov" : "MP4 video|*.mp4;*.m4v";
            if (sd.ShowDialog() != DialogResult.OK) return;
            outPath = sd.FileName;
            bool sei = seiBox.Checked;

            Busy(true);
            SetStatus("Scanning source...", false);
            SetReport("");

            Thread t = new Thread(delegate()
            {
                try
                {
                    string before = Scanner.Report(input, Scanner.Scan(input));
                    Tools.Strip(input, outPath, sei, delegate(string m) { SetStatus(m, false); });
                    SetStatus("Verifying output...", false);
                    bool allPass;
                    string rep = Tools.Verify(input, outPath, out allPass);
                    Done(outPath, "SOURCE\n" + before + "\nOUTPUT\n" + rep, allPass);
                }
                catch (Exception ex) { Fail(ex.Message); }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void Done(string outPath, string rep, bool allPass)
        {
            if (InvokeRequired) { Invoke(new Action<string, string, bool>(Done), outPath, rep, allPass); return; }
            Busy(false);
            SetReport(rep);
            SetStatus((allPass ? "Clean and verified." : "Saved, but some checks did not pass - see below.") + "  " + outPath, !allPass);
            if (MessageBox.Show((allPass ? "Saved and verified - all checks passed.\n\n" : "Saved, but some checks did not pass.\n\n") +
                outPath + "\n\nOpen the containing folder?",
                "Metadata Stripper", MessageBoxButtons.YesNo,
                allPass ? MessageBoxIcon.Information : MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                try { Process.Start("explorer.exe", "/select,\"" + outPath + "\""); }
                catch { }
            }
        }

        void ShowCheck(string fileName, string rep, bool clean)
        {
            if (InvokeRequired) { Invoke(new Action<string, string, bool>(ShowCheck), fileName, rep, clean); return; }
            Busy(false);
            SetReport(rep);
            SetStatus(fileName + ": " + (clean ? "no app signatures." : "app signatures found."), !clean);
        }

        void Fail(string msg)
        {
            if (InvokeRequired) { Invoke(new Action<string>(Fail), msg); return; }
            Busy(false);
            SetStatus("Failed.", true);
            MessageBox.Show(msg, "Metadata Stripper - error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        void Busy(bool on)
        {
            if (InvokeRequired) { Invoke(new Action<bool>(Busy), on); return; }
            bar.Visible = on;
            choose.Enabled = !on;
            checkBtn.Enabled = !on;
            seiBox.Enabled = !on;
        }

        void SetReport(string text)
        {
            if (report.InvokeRequired) { report.Invoke(new Action<string>(SetReport), text); return; }
            report.Text = (text == null ? "" : text.Replace("\r", "").Replace("\n", "\r\n"));
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
            // Detect only:  stripper.exe -check <file>   (exit 1 if signatures found)
            if (args.Length >= 1 && (args[0] == "-check" || args[0] == "/check"))
            {
                if (args.Length < 2) { Console.Error.WriteLine("usage: stripper.exe -check <file>"); Environment.Exit(2); }
                ScanResult r = Scanner.Scan(args[1]);
                Console.WriteLine(Scanner.Report(args[1], r));
                Environment.Exit(r.Hits.Count == 0 ? 0 : 1);
            }

            // Strip + verify:  stripper.exe [-sei] <in> <out>
            bool sei = false;
            List<string> rest = new List<string>();
            foreach (string a in args) { if (a == "-sei" || a == "/sei") sei = true; else rest.Add(a); }
            if (rest.Count >= 2)
            {
                try
                {
                    Console.WriteLine("SOURCE");
                    Console.WriteLine(Scanner.Report(rest[0], Scanner.Scan(rest[0])));
                    Tools.Strip(rest[0], rest[1], sei, delegate(string m) { Console.WriteLine(m); });
                    bool allPass;
                    Console.WriteLine("\nOUTPUT  " + rest[1]);
                    Console.WriteLine(Tools.Verify(rest[0], rest[1], out allPass));
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
