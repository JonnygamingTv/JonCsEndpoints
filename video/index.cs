using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Net.Http.Headers;
using WebServer;

/// <summary>
/// Video conversion endpoint.
///
/// Flow:
///   POST /video/upload                  → accepts multipart, saves to /opt/video-up/{id}.*
///                                         returns JSON { jobId, filename, subtitleStreams[] }
///   POST /video/convert                 → { jobId, codec, crf, resolution, format, subtitleIndex }
///                                         starts background job, returns { jobId }
///   GET  /video/status/{jobId}          → { state, progress, error? }
///   GET  /video/download/{jobId}/video  → streams the .mp4 / .webm
///   GET  /video/download/{jobId}/vtt    → streams the .vtt (404 if none)
///   GET  /video/info/{jobId}            → ffprobe JSON (subtitle stream list for UI)
///   DELETE /video/job/{jobId}           → clean up files early
/// </summary>
public class Is_CsScript
{
    // ── Config ─────────────────────────────────────────────────────────────
    const string UpDir = "/opt/video-up";
    const string OutDir = "/opt/video-out";
    const long MaxUpload = 8L * 1024 * 1024 * 1024;   // 8 GB
    const long MaxOutput = 8L * 1024 * 1024 * 1024;   // 8 GB
    const string FfmpegBin = "ffmpeg";
    const string FfprobeBin = "ffprobe";

    // ── Job registry ───────────────────────────────────────────────────────
    enum JobState { pending, probing, converting, done, failed }

    class Job
    {
        public ulong Id;
        public double DurationSeconds = 0;
        public string UploadPath = "";   // /opt/video-up/{id}.ext
        public string VideoOutPath = "";   // /opt/video-out/{id}.mp4 etc
        public string VttOutPath = "";   // /opt/video-out/{id}.vtt  (may be empty)
        public JobState State = JobState.pending;
        public string Progress = "";   // e.g. "frame=1234 fps=24 ..."
        public string Error = "";
        public List<SubStream> SubStreams = new();  // populated after probe
        public CancellationTokenSource Cts = new();
    }

    record SubStream(int Index, string Codec, string Language, bool IsImageBased);

    static readonly ConcurrentDictionary<ulong, Job> _jobs = new();
    static ulong _idCounter = 0;
    const string _domainPrefix = "vidjonhostingcom";
    /*
    static readonly string _domainPrefix = Path.GetFileName(
        Path.GetDirectoryName(typeof(Is_CsScript).Assembly.Location)!);*/
    static string ToKey(string p) => Startup.BackendDir + _domainPrefix + p;

    static readonly HashSet<string> allowedExt = new HashSet<string>
            { ".mkv", ".avi", ".mov", ".mp4", ".ts", ".m2ts", ".wmv",
              ".flv", ".webm", ".mpg", ".mpeg", ".m4v", ".3gp", ".ogv" };

    // ───────────────────────────────────────────────────────────────────────
    static Is_CsScript()
    {
        Console.WriteLine($"[VideoConv] Loading on {_domainPrefix}");
        Directory.CreateDirectory(UpDir);
        Directory.CreateDirectory(OutDir);

        ClearEndpoints();

        Startup.AddToFileLead(ToKey("/video/upload"), HandleUpload);
        Startup.AddToFileLead(ToKey("/video/convert"), HandleConvert);
        Startup.AddToFileLead(ToKey("/video/subtitles"), HandleSubtitleOnly);
        Startup.AddToFileLead(ToKey("/video/status"), HandleStatus);
        Startup.AddToFileLead(ToKey("/video/download"), HandleDownload);
        Startup.AddToFileLead(ToKey("/video/job"), HandleDeleteJob);
    }
    ~Is_CsScript() {
        ClearEndpoints();
    }
    static void ClearEndpoints()
    {
        Startup.RemoveFromFileLead(ToKey("/video/upload"));
        Startup.RemoveFromFileLead(ToKey("/video/convert"));
        Startup.RemoveFromFileLead(ToKey("/video/subtitles"));
        Startup.RemoveFromFileLead(ToKey("/video/status"));
        Startup.RemoveFromFileLead(ToKey("/video/download"));
        Startup.RemoveFromFileLead(ToKey("/video/job"));
    }

    public static Task Run(HttpContext ctx, string path)
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.Headers.Location = "https://vid.jonhosting.com/video.html";
        return ctx.Response.WriteAsync("Not Found");
    }

    // ── POST /video/upload ─────────────────────────────────────────────────
    // Accepts multipart/form-data with a single "file" field.
    static async Task HandleUpload(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }

        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature != null) sizeFeature.MaxRequestBodySize = MaxUpload;

        string? contentType = ctx.Request.ContentType;
        if (contentType == null || !contentType.Contains("multipart/form-data"))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("Expected multipart/form-data");
            return;
        }

        // Pull boundary from Content-Type header
        string? boundary = null;
        foreach (var segment in contentType.Split(';'))
        {
            var trimmed = segment.Trim();
            if (trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                boundary = trimmed["boundary=".Length..].Trim('"');
                break;
            }
        }
        if (string.IsNullOrEmpty(boundary))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("Missing multipart boundary");
            return;
        }

        var reader = new MultipartReader(boundary, ctx.Request.Body);
        MultipartSection? section;

        ulong id = Interlocked.Increment(ref _idCounter);
        string? uploadPath = null;
        string? originalName = null;
        long bytesWritten = 0;

        try
        {
            while ((section = await reader.ReadNextSectionAsync(ctx.RequestAborted)) != null)
            {
                // Only care about the "file" field
                if (!ContentDispositionHeaderValue.TryParse(
                        section.Headers?["Content-Disposition"].FirstOrDefault(), out var cd))
                    continue;
                if (!cd.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!cd.Name.Equals("file", StringComparison.OrdinalIgnoreCase))
                    continue;

                originalName = cd.FileName.Value ?? cd.FileNameStar.Value ?? "upload";
                string origExt = Path.GetExtension(originalName).ToLowerInvariant();
                if (!allowedExt.Contains(origExt))
                {
                    ctx.Response.StatusCode = 415;
                    await ctx.Response.WriteAsync($"Unsupported file type: {origExt}");
                    return;
                }

                uploadPath = Path.Combine(UpDir, $"{id}{origExt}");

                // Write directly from network stream → disk, no /tmp, no intermediate buffer
                await using (var fs = new FileStream(
                    uploadPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1 << 17,      // 128 KB disk write buffer — matches typical HDD block size
                    useAsync: true))
                {
                    byte[] buf = new byte[1 << 17];
                    int read;
                    while ((read = await section.Body.ReadAsync(buf, ctx.RequestAborted)) > 0)
                    {
                        bytesWritten += read;
                        if (bytesWritten > MaxUpload)
                        {
                            ctx.Response.StatusCode = 413;
                            await ctx.Response.WriteAsync("File exceeds size limit");
                            return;   // fs disposed by using, finally block cleans up file
                        }
                        await fs.WriteAsync(buf.AsMemory(0, read), ctx.RequestAborted);
                    }
                }
                break; // only one file field expected
            }
        }
        catch (OperationCanceledException)
        {
            ctx.Response.StatusCode = 499;
            return;
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync($"Upload error: {ex.Message}");
            return;
        }
        finally
        {
            // Clean up partial file on any early return after path was assigned
            if (uploadPath != null && bytesWritten > 0 && ctx.Response.StatusCode != 200
                && File.Exists(uploadPath))
                TryDelete(uploadPath);
        }

        if (uploadPath == null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("No file field found in form");
            return;
        }

        // Probe and register job exactly as before
        var job = new Job { Id = id, UploadPath = uploadPath, State = JobState.probing };
        _jobs[id] = job;

        // Replace ProbeSubtitleStreams call in HandleUpload with this:
        var (subStreams, duration) = await ProbeStreams(uploadPath);
        job.SubStreams = subStreams;
        job.DurationSeconds = duration;
        job.State = JobState.pending;

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            jobId = id,
            originalName,
            durationSeconds = job.DurationSeconds,
            subtitleStreams = subStreams.Select(s => new
            {
                index = s.Index,
                codec = s.Codec,
                language = s.Language,
                imageBased = s.IsImageBased,
            })
        }));
    }

    // ── POST /video/convert ────────────────────────────────────────────────
    // Body: { jobId, codec, crf, resolution, format, subtitleIndex }
    //   codec:         "h264" | "hevc" | "av1"
    //   crf:           integer, codec-appropriate range (validated per codec)
    //   resolution:    "source" | "3840x2160" | "1920x1080" | "1280x720" | "854x480"
    //   format:        "mp4" | "webm"
    //   subtitleIndex: -1 = none, else the stream index from probe
    static async Task HandleConvert(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }

        ConvertRequest req;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var r = doc.RootElement;
            req = new ConvertRequest(
                JobId: r.GetProperty("jobId").GetUInt64(),
                Codec: r.TryGetProperty("codec", out var c) ? c.GetString() ?? "h264" : "h264",
                Crf: r.TryGetProperty("crf", out var cr) ? cr.GetInt32() : 23,
                Resolution: r.TryGetProperty("resolution", out var rs) ? rs.GetString() ?? "source" : "source",
                Format: r.TryGetProperty("format", out var f) ? f.GetString() ?? "mp4" : "mp4",
                SubtitleIndex: r.TryGetProperty("subtitleIndex", out var si) ? si.GetInt32() : -1,
                Preset: r.TryGetProperty("preset", out var pr) ? pr.GetString() ?? "medium" : "medium",
                TrimStart: r.TryGetProperty("trimStart", out var ts) ? ts.GetDouble() : 0,
                TrimEnd: r.TryGetProperty("trimEnd", out var te) ? te.GetDouble() : 0,
                Rotation: r.TryGetProperty("rotation", out var rot) ? rot.GetString() ?? "none" : "none",
                BurnInSubs: r.TryGetProperty("burnInSubs", out var bis) && bis.GetBoolean(),
                AudioBitrate: r.TryGetProperty("audioBitrate", out var ab) ? ab.GetString() ?? "192k" : "192k"
            );
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync($"Invalid request: {ex.Message}");
            return;
        }

        if (!_jobs.TryGetValue(req.JobId, out var job))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("Job not found");
            return;
        }
        if (job.State == JobState.converting)
        {
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsync("Job already converting");
            return;
        }

        // Validate & clamp params
        string codec = req.Codec.ToLower() switch { "hevc" => "hevc", "av1" => "av1", _ => "h264" };
        string rotation = req.Rotation switch
        {
            "cw90" or "ccw90" or "180" => req.Rotation,
            _ => "none"
        };
        string format = req.Format.ToLower() == "webm" ? "webm" : "mp4";
        // CRF ranges: h264 0–51, hevc 0–51, av1(SVT) 0–63
        int crfMin = 0, crfMax = codec == "av1" ? 63 : 51;
        int crf = Math.Clamp(req.Crf, crfMin, crfMax);

        // webm only supports AV1 or VP9 — force av1 if webm selected with h264/hevc
        if (format == "webm" && codec == "h264") codec = "av1";
        if (format == "webm" && codec == "hevc") codec = "av1";

        string ext = format == "webm" ? ".webm" : ".mp4";
        job.VideoOutPath = Path.Combine(OutDir, $"{job.Id}{ext}");
        job.VttOutPath = Path.Combine(OutDir, $"{job.Id}.vtt");
        job.State = JobState.converting;
        job.Progress = "Starting…";

        // Fire and forget — conversion runs in background
        _ = Task.Run(() => RunConversion(job, codec, crf, req.Resolution, format,
                                 req.SubtitleIndex, req.Preset,
                                 req.TrimStart, req.TrimEnd,
                                 req.Rotation, req.BurnInSubs, req.AudioBitrate));

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { jobId = job.Id }));
    }
    
    /// <summary>Subtitle-exclusive endpoint.</summary>
    static async Task HandleSubtitleOnly(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }

        SubtitleRequest req;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var r = doc.RootElement;
            req = new SubtitleRequest(
                JobId: r.GetProperty("jobId").GetUInt64(),
                SubtitleIndex: r.TryGetProperty("subtitleIndex", out var si) ? si.GetInt32() : 0
            );
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync($"Invalid request: {ex.Message}");
            return;
        }

        if (!_jobs.TryGetValue(req.JobId, out var job))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("Job not found");
            return;
        }
        if (job.State == JobState.converting)
        {
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsync("Already processing");
            return;
        }

        var subStream = job.SubStreams.FirstOrDefault(s => s.Index == req.SubtitleIndex);
        if (subStream == null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("Subtitle stream index not found");
            return;
        }
        if (subStream.IsImageBased)
        {
            ctx.Response.StatusCode = 422;
            await ctx.Response.WriteAsync("Image-based subtitle tracks (PGS/VOBSUB) cannot be extracted to VTT — use 'burn into video' instead");
            return;
        }

        job.VttOutPath = Path.Combine(OutDir, $"{job.Id}.vtt");
        job.State = JobState.converting;
        job.Progress = "Extracting subtitles…";

        _ = Task.Run(async () =>
        {
            try
            {
                await ExtractSubtitles(job.UploadPath, req.SubtitleIndex, job.VttOutPath, job.Cts.Token);
                job.State = JobState.done;
                job.Progress = "Complete";
            }
            catch (OperationCanceledException)
            {
                job.State = JobState.failed;
                job.Error = "Cancelled";
            }
            catch (Exception ex)
            {
                job.State = JobState.failed;
                job.Error = ex.Message;
            }
        });

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { jobId = job.Id }));
    }

    record SubtitleRequest(ulong JobId, int SubtitleIndex);
    // ── GET /video/status/{jobId} ──────────────────────────────────────────
    static async Task HandleStatus(HttpContext ctx, string path)
    {
        // path suffix: /video/status/12345
        ulong id = ParseTrailingId(ctx);
        if (!_jobs.TryGetValue(id, out var job))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("{}");
            return;
        }
        ctx.Response.Headers.CacheControl = "max-age=1";
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            jobId = job.Id,
            state = job.State.ToString(),
            progress = job.Progress,
            error = job.Error,
            hasVtt = File.Exists(job.VttOutPath),
        }));
    }

    // ── GET /video/download/{jobId}/video  or  /vtt ───────────────────────
    static async Task HandleDownload(HttpContext ctx, string path)
    {
        ctx.Response.Headers.CacheControl = "max-age=60";
        string reqPath = ctx.Request.Path.Value ?? "";
        // /video/download/{id}/video  or  /video/download/{id}/vtt
        var parts = reqPath.TrimEnd('/').Split('/');
        if (parts.Length < 2 || !ulong.TryParse(parts[^2], out ulong id))
        {
            ctx.Response.StatusCode = 400; return;
        }
        string which = parts[^1].ToLower(); // "video" or "vtt"

        if (!_jobs.TryGetValue(id, out var job) || job.State != JobState.done)
        {
            ctx.Response.StatusCode = job?.State == JobState.converting ? 202 : 404;
            await ctx.Response.WriteAsync(job?.State == JobState.converting ? "Still processing" : "Not found");
            return;
        }

        string filePath = which == "vtt" ? job.VttOutPath : job.VideoOutPath;
        string mimeType = which == "vtt" ? "text/vtt"
                         : job.VideoOutPath.EndsWith(".webm") ? "video/webm" : "video/mp4";

        if (!File.Exists(filePath))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync(which == "vtt" ? "No subtitles extracted" : "Output file missing");
            return;
        }

        var info = new FileInfo(filePath);
        string download = Path.GetFileName(filePath);
        string attachm = "attachment; filename=\"" + download + "\"";
        long len = info.Length;
        ctx.Response.Headers.ContentDisposition = attachm;
        ctx.Response.Headers.ContentLength = len;
        ctx.Response.ContentType = mimeType;
        await using var fs = File.OpenRead(filePath);
        await fs.CopyToAsync(ctx.Response.Body);
    }

    // ── DELETE /video/job/{jobId} ──────────────────────────────────────────
    static async Task HandleDeleteJob(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "DELETE") { ctx.Response.StatusCode = 405; return; }
        ulong id = ParseTrailingId(ctx);
        if (!_jobs.TryRemove(id, out var job))
        {
            ctx.Response.StatusCode = 404; return;
        }
        job.Cts.Cancel();
        TryDelete(job.UploadPath);
        TryDelete(job.VideoOutPath);
        TryDelete(job.VttOutPath);
        ctx.Response.StatusCode = 204;
        await Task.CompletedTask;
    }

    // ── Conversion logic ───────────────────────────────────────────────────
    static readonly SemaphoreSlim _conversionSem = new SemaphoreSlim(4, 4); // max 4 concurrent encodes
    static async Task RunConversion(Job job, string codec, int crf,
                                string resolution, string format,
                                int subtitleIndex, string preset,
                                double trimStart, double trimEnd, string rotation, bool burnInSubs, string audioBitrate)
    {
        job.Progress = "Queued — waiting for a free slot…";
        await _conversionSem.WaitAsync(job.Cts.Token);
        try
        {
            // ── Step 1: extract subtitles ──────────────────────────────────
            if (subtitleIndex >= 0)
            {
                var subStream = job.SubStreams.FirstOrDefault(s => s.Index == subtitleIndex);
                if (subStream != null && !subStream.IsImageBased)
                {
                    // Text subs: always extract .vtt sidecar regardless of burn-in mode
                    job.Progress = "Extracting subtitle sidecar…";
                    await ExtractSubtitles(job.UploadPath, subtitleIndex, job.VttOutPath, job.Cts.Token);
                }
                else if (subStream?.IsImageBased == true && !burnInSubs)
                {
                    job.Progress = "Note: image-based subtitle track — can only be burned in, not extracted. Continuing…";
                }
                // Image + burnInSubs: handled entirely in EncodeVideo via overlay, no sidecar
            }

            // ── Step 2: video encode ───────────────────────────────────────
            job.Progress = "Encoding video…";
            await EncodeVideo(job, codec, crf, resolution, format, preset, trimStart, trimEnd, rotation, burnInSubs, subtitleIndex, audioBitrate, job.Cts.Token);

            // ── Step 3: output size guard ──────────────────────────────────
            if (File.Exists(job.VideoOutPath))
            {
                long size = new FileInfo(job.VideoOutPath).Length;
                if (size > MaxOutput)
                {
                    File.Delete(job.VideoOutPath);
                    job.State = JobState.failed;
                    job.Error = $"Output exceeded size safety limit ({size / 1_073_741_824.0:F2} GB). Try a higher CRF or lower resolution.";
                    return;
                }
            }

            job.State = JobState.done;
            job.Progress = "Complete";
        }
        catch (OperationCanceledException)
        {
            job.State = JobState.failed;
            job.Error = "Cancelled";
            TryDelete(job.VideoOutPath);
            TryDelete(job.VttOutPath);
        }
        catch (Exception ex)
        {
            job.State = JobState.failed;
            job.Error = ex.Message;
        }
        finally
        {
            _conversionSem.Release();
        }
    }

    static async Task ExtractSubtitles(string inputPath, int streamIndex,
                                        string vttPath, CancellationToken ct)
    {
        // ffmpeg extracts and converts text subs to VTT in one step.
        // -map 0:{streamIndex} -c:s webvtt
        string args = $"-y -i \"{inputPath}\" -fs {MaxOutput} -map 0:{streamIndex} -c:s webvtt \"{vttPath}\"";
        await RunFfmpeg(args, onProgress: null, ct);
    }

    static async Task EncodeVideo(Job job, string codec, int crf,
                                   string resolution, string format,
                                   string preset, double trimStart, double trimEnd,
                                   string rotation, bool burnInSubs, int subtitleIndex,
                                   string audioBitrate, CancellationToken ct)
    {
        var subStream = subtitleIndex >= 0
            ? job.SubStreams.FirstOrDefault(s => s.Index == subtitleIndex)
            : null;

        bool imageSubBurnIn = burnInSubs && subStream?.IsImageBased == true;
        bool textSubBurnIn = burnInSubs && subStream?.IsImageBased == false;

        // Build the video filter list (scale → text-sub burn-in → rotate)
        var filters = new List<string>();
        if (resolution != "source")
            filters.Add(BuildScaleFilter(resolution));

        if (textSubBurnIn)
        {
            string escapedPath = job.UploadPath
                .Replace("\\", "\\\\")
                .Replace(":", "\\:")
                .Replace("'", "\\'");
            filters.Add($"subtitles='{escapedPath}':si={GetSubtitleTrackIndex(job, subtitleIndex)}");
        }

        switch (rotation)
        {
            case "cw90": filters.Add("transpose=1"); break;
            case "ccw90": filters.Add("transpose=2"); break;
            case "180": filters.Add("hflip,vflip"); break;
            case "vflip": filters.Add("vflip"); break;
            case "hflip": filters.Add("hflip"); break;
        }

        // ── Build filter argument ──────────────────────────────────────────────
        // Image subs need -filter_complex with overlay; everything else uses -vf
        string filterArg;
        if (imageSubBurnIn)
        {
            // [0:v] → optional scale+rotate chain → overlay with [0:subIdx]
            // filter_complex chains: [0:v]scale=-2:720[scaled];[scaled][0:3]overlay[out]
            // Output label [out] is then mapped with -map [out]
            var fc = new StringBuilder();
            string inLabel = "0:v";
            string curLabel = "v0";
            int labelN = 0;

            if (filters.Count > 0)
            {
                // Chain scale/rotate filters before overlay
                fc.Append($"[{inLabel}]{string.Join(",", filters)}[{curLabel}]");
                labelN++;
            }
            else
            {
                // No other filters — feed video directly into overlay
                // Alias it so overlay syntax is consistent
                fc.Append($"[{inLabel}]null[{curLabel}]");
            }

            string outLabel = $"v{labelN}";
            fc.Append($";[{curLabel}][0:{subtitleIndex}]overlay=shortest=1[{outLabel}]");

            filterArg = $"-filter_complex \"{fc}\" -map \"[{outLabel}]\" -map 0:a?";
        }
        else
        {
            filterArg = filters.Count > 0
                ? $"-vf \"{string.Join(",", filters)}\" -map 0:v:0 -map 0:a?"
                : "-map 0:v:0 -map 0:a?";
        }

        string trimArg = "";
        if (trimStart > 0) trimArg += $" -ss {trimStart.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";
        if (trimEnd > 0) trimArg += $" -to {trimEnd.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}";

        string safePreset = preset.ToLower() switch
        {
            "ultrafast" or "superfast" or "veryfast" or "faster" or
            "fast" or "medium" or "slow" or "slower" or "veryslow" => preset.ToLower(),
            _ => "medium"
        };

        string videoArgs = codec switch
        {
            "hevc" => $"-c:v libx265 -crf {crf} -preset {safePreset} -tag:v hvc1",
            "av1" => format == "webm"
                      ? $"-c:v libaom-av1 -crf {crf} -b:v 0 -cpu-used {MapAv1Preset(safePreset)} -row-mt 1"
                      : $"-c:v libsvtav1 -crf {crf} -preset {MapAv1Preset(safePreset)} -svtav1-params tune=0",
            _ => $"-c:v libx264 -crf {crf} -preset {safePreset} -pix_fmt yuv420p",
        };

        // Validated audio bitrate — whitelist to avoid injection
        string safeAudioBitrate = audioBitrate switch
        {
            "96k" or "128k" or "160k" or "192k" or "224k" or "256k" or "320k" => audioBitrate,
            _ => "192k"
        };
        string audioArgs = format == "webm"
            ? $"-c:a libopus -b:a {safeAudioBitrate}"
            : $"-c:a aac -b:a {safeAudioBitrate}";

        string extraArgs = format == "mp4" ? "-movflags +faststart" : "";

        string args = $"-y -i \"{job.UploadPath}\" -fs {MaxOutput + 1}" +
                      $"{trimArg} {videoArgs} {audioArgs} {extraArgs}" +
                      $" {filterArg}" +
                      $" \"{job.VideoOutPath}\"";

        await RunFfmpeg(args, onProgress: line => { job.Progress = line; }, ct);

        string filePath = job.VideoOutPath;
        string download = Path.GetFileName(filePath);
        string attachm = "attachment; filename=\"" + download + "\"";
        string mimeType = job.VideoOutPath.EndsWith(".webm") ? "video/webm" : "video/mp4";
        var info = new FileInfo(filePath);
        // Startup.CacheFileInfo(filePath); goes through symlinks and finds the correct data,
        // but otherwise, CacheFileInfo essentially does this:
        Startup.FileIndex[filePath] = new long[] { ((DateTimeOffset)info.LastWriteTimeUtc).ToUnixTimeSeconds(), info.Length };
        
        Startup.AddToFileLead(ToKey($"/video/download/{job.Id}/video"), (context, path) => { // Endpoint, /video/download/x/video, will no longer lead to HandleDownload in future requests. Also supports ranged streaming.
            context.Response.Headers.CacheControl = "max-age=60";
            context.Response.Headers.ContentDisposition = attachm;
            context.Response.ContentType = mimeType;
            return Startup.DefHandle(context, filePath);
        });
    }

    /// <summary>
    /// Map named presets → SVT-AV1 numeric presets (0=slowest/best, 13=fastest)
    /// </summary>
    static int MapAv1Preset(string preset) => preset switch
    {
        "veryslow" => 2,
        "slower" => 3,
        "slow" => 4,
        "medium" => 6,
        "fast" => 8,
        "faster" => 9,
        "veryfast" => 10,
        "superfast" => 11,
        "ultrafast" => 12,
        _ => 6
    };

    // ── ffprobe: enumerate subtitle streams ────────────────────────────────
    static int GetSubtitleTrackIndex(Job job, int absoluteStreamIndex)
    {
        int si = 0;
        foreach (var s in job.SubStreams)
        {
            if (s.Index == absoluteStreamIndex) return si;
            si++;
        }
        return 0;
    }
    static async Task<(List<SubStream> subs, double duration)> ProbeStreams(string inputPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfprobeBin,
            // show_streams for subs, show_format for duration
            Arguments = $"-v error -of json -show_streams -select_streams s -show_format \"{inputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var subs = new List<SubStream>();
        double duration = 0;
        try
        {
            using var proc = new Process { StartInfo = psi };
            proc.Start();
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            // Duration from format section (most reliable source)
            if (root.TryGetProperty("format", out var fmt) &&
                fmt.TryGetProperty("duration", out var dur))
                double.TryParse(dur.GetString(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out duration);

            // Subtitle streams
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    int idx = s.TryGetProperty("index", out var i) ? i.GetInt32() : -1;
                    string codecName = s.TryGetProperty("codec_name", out var cn) ? cn.GetString()! : "unknown";
                    string lang = "";
                    if (s.TryGetProperty("tags", out var tags) &&
                        tags.TryGetProperty("language", out var lp))
                        lang = lp.GetString() ?? "";

                    bool isImage = codecName is "dvd_subtitle" or "hdmv_pgs_subtitle" or "dvb_subtitle"
                                || (s.TryGetProperty("width", out _) && s.TryGetProperty("height", out _));

                    subs.Add(new SubStream(idx, codecName, lang, isImage));
                }
            }
        }
        catch { }

        return (subs, duration);
    }
    // ── Generic ffmpeg runner with progress capture ────────────────────────
    static async Task RunFfmpeg(string args, Action<string>? onProgress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegBin,
            // -progress pipe:2 sends machine-readable progress to stderr;
            // -stats_period 2 updates every 2 seconds (reduces noise)
            Arguments = $"-progress pipe:2 -stats_period 2 {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi }; // PriorityClass = ProcessPriorityClass.Idle
        proc.Start();
        try { proc.PriorityClass = ProcessPriorityClass.Idle; }
        catch { /* process may have already exited for very short jobs */ }
        // Read progress lines from stderr asynchronously
        // Replace the stderrTask block inside RunFfmpeg:
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            // Accumulate one progress block (ends with "progress=continue" or "progress=end")
            string frame = "", fps = "", outTime = "", speed = "";
            while ((line = await proc.StandardError.ReadLineAsync()) != null)
            {
                if (line.StartsWith("frame=")) frame = line[6..];
                else if (line.StartsWith("fps=")) fps = line[4..];
                else if (line.StartsWith("out_time=")) outTime = line[9..];  // HH:MM:SS.μs
                else if (line.StartsWith("speed=")) speed = line[6..];
                else if (line.StartsWith("progress="))
                {
                    // End of block — emit a single formatted string
                    if (outTime.Length > 0)
                        outTime = outTime[..Math.Min(8, outTime.Length)]; // trim to HH:MM:SS
                    onProgress?.Invoke($"frame={frame}  fps={fps}  time={outTime}  speed={speed}");
                }
            }
        }, ct);

        // Register cancellation → kill process
        await using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { }
        });

        await proc.WaitForExitAsync(ct);
        await stderrTask;

        ct.ThrowIfCancellationRequested();

        if (proc.ExitCode != 0)
            throw new Exception($"ffmpeg exited with code {proc.ExitCode}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    static string BuildScaleFilter(string resolution)
    {
        // resolution is just the height now, e.g. "1080", "720"
        if (int.TryParse(resolution, out int h) && h > 0)
            return $"scale=-2:{h}:flags=lanczos";
        return "";
    }

    static ulong ParseTrailingId(HttpContext ctx)
    {
        string reqPath = ctx.Request.Path.Value ?? "";
        var parts = reqPath.TrimEnd('/').Split('/');
        return ulong.TryParse(parts[^1], out ulong id) ? id : 0;
    }

    static void TryDelete(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            try { File.Delete(path); } catch { }
    }

    record ConvertRequest(ulong JobId, string Codec, int Crf, string Resolution,
                      string Format, int SubtitleIndex, string Preset,
                      double TrimStart, double TrimEnd, string Rotation,
                      bool BurnInSubs, string AudioBitrate);
}
