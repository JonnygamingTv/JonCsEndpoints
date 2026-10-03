global using System;
global using System.IO;
global using System.Linq;
global using System.Threading.Tasks;
global using System.Threading;
global using Microsoft.AspNetCore.Http;
global using System.Net.Http;

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebServer;

/// <summary>
/// Piper TTS front-end — proxies to the Python sidecar over a Unix domain socket.
/// All disk caching, CORS, OpenAI-compat shaping, and voice listing live here.
/// The Python sidecar handles model loading + synthesis; we never spawn processes.
/// </summary>
public class Is_CsScript
{
    // ── Config ─────────────────────────────────────────────────────────────
    const string SocketPath = "/run/piper-tts/piper.sock";
    const string CacheDir = "/opt/piper-cache";
    const string DefaultVoice = "en_US-ryan-high";
    const int MaxTextLength = 4000;

    // ── Long-lived HttpClient over the Unix socket ──────────────────────────
    // SocketsHttpHandler is the correct type for ConnectCallback in .NET 5+.
    // The fake base address is required by HttpClient but never used for routing.
    static readonly HttpClient _piperClient = BuildUnixSocketClient(SocketPath);

    static HttpClient BuildUnixSocketClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            // Each "connection" is really just a new UDS stream — no TCP stack at all
            ConnectCallback = async (ctx, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint(socketPath);
                await socket.ConnectAsync(endpoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
            // Keep connections alive across requests — eliminates reconnect overhead
            PooledConnectionLifetime = TimeSpan.FromMinutes(30),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8,
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    // ── Disk cache ──────────────────────────────────────────────────────────
    static readonly ConcurrentDictionary<string, string> _cache = new();

    // ── Per-text semaphore to avoid stampeding the sidecar with identical
    //    concurrent requests for the same audio (dog-pile prevention) ────────
    static readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new();
    const string _domainPrefix = "ttsjonhostingcom";
    /*
    static readonly string _domainPrefix = Path.GetFileName(
        Path.GetDirectoryName(typeof(Is_CsScript).Assembly.Location)!);*/
    static string ToKey(string p) => Startup.BackendDir + _domainPrefix + p;

    // ───────────────────────────────────────────────────────────────────────
    static Is_CsScript()
    {
        Console.WriteLine($"[TTS] Loading front-end on {_domainPrefix}");
        Directory.CreateDirectory(CacheDir);

        // Index any audio already on disk from previous runs
        foreach (var f in Directory.EnumerateFiles(CacheDir, "*.mp3")
                          .Concat(Directory.EnumerateFiles(CacheDir, "*.wav")))
        {
            string key = Path.GetFileNameWithoutExtension(f);
            _cache.TryAdd(key, f);
        }

        // Verify sidecar is up — log warning if not, don't crash
        _ = PingSidecarAsync();

        Startup.AddToFileLead(ToKey("/v1/audio/speech"), HandleSpeech);
        Startup.AddToFileLead(ToKey("/v1/audio/voices"), HandleVoices);
        Startup.AddToFileLead(ToKey("/v1/models"), HandleVoices);
        Startup.AddToFileLead(ToKey("/tts/speak"), HandleSimpleSpeak);
    }

    static async Task PingSidecarAsync()
    {
        try
        {
            var resp = await _piperClient.GetAsync("/health");
            Console.WriteLine(resp.IsSuccessStatusCode
                ? "[TTS] Piper sidecar is up ✓"
                : $"[TTS] Piper sidecar returned {resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TTS] Warning: Piper sidecar not reachable: {ex.Message}");
            Console.Error.WriteLine($"[TTS]   Is 'piper-tts.service' running? Check: systemctl status piper-tts");
        }
    }

    public static Task Run(HttpContext ctx, string path)
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.Headers.Location = "https://tts.jonhosting.com/tts.html";
        return ctx.Response.WriteAsync("Not Found");
    }

    // ── POST /v1/audio/speech ───────────────────────────────────────────────
    static async Task HandleSpeech(HttpContext ctx, string path)
    {
        SetCors(ctx);
        if (ctx.Request.Method == "OPTIONS") { ctx.Response.StatusCode = 204; return; }
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }

        // ── Parse ──────────────────────────────────────────────────────────
        SpeechReq req;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var r = doc.RootElement;
            req = new SpeechReq(
                Input: r.TryGetProperty("input", out var i) ? i.GetString() ?? "" : "",
                Model: r.TryGetProperty("model", out var m) ? m.GetString() ?? DefaultVoice : DefaultVoice,
                Format: r.TryGetProperty("response_format", out var f) ? f.GetString() ?? "mp3" : "mp3",
                Speed: r.TryGetProperty("speed", out var s) ? s.GetDouble() : 1.0
            );
        }
        catch { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("Invalid JSON"); return; }

        if (string.IsNullOrWhiteSpace(req.Input))
        { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("Missing 'input'"); return; }
        if (req.Input.Length > MaxTextLength)
        { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync($"Max {MaxTextLength} chars"); return; }

        string fmt = req.Format.ToLowerInvariant() is "wav" ? "wav" : "mp3";

        // ── Cache check ────────────────────────────────────────────────────
        string cacheKey = CacheKey(req.Model, fmt, req.Speed, req.Input);
        string cachePath = Path.Combine(CacheDir, cacheKey + "." + fmt);

        if (!_cache.ContainsKey(cacheKey) && File.Exists(cachePath))
            _cache.TryAdd(cacheKey, cachePath);

        if (!_cache.TryGetValue(cacheKey, out string? audioPath))
        {
            // Dog-pile guard: only one coroutine synthesises a given key at a time
            var sem = _keyLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                // Re-check after acquiring — another request may have just finished
                if (!_cache.TryGetValue(cacheKey, out audioPath))
                {
                    audioPath = await SynthAndCache(req, fmt, cachePath);
                    if (audioPath != null) _cache.TryAdd(cacheKey, audioPath);
                }
            }
            finally
            {
                sem.Release();
                _keyLocks.TryRemove(cacheKey, out _);
            }
        }

        if (audioPath == null || !File.Exists(audioPath))
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync("Synthesis failed — check sidecar logs");
            return;
        }

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = fmt == "wav" ? "audio/wav" : "audio/mpeg";
        // Add cache hint so browsers don't re-request identical audio
        ctx.Response.Headers["Cache-Control"] = "public, max-age=86400";
        await using var fs = File.OpenRead(audioPath);
        await fs.CopyToAsync(ctx.Response.Body);
    }

    // ── Forward to sidecar, save result to disk ────────────────────────────
    static async Task<string?> SynthAndCache(SpeechReq req, string fmt, string cachePath)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                input = req.Input,
                model = req.Model,
                response_format = fmt,
                speed = req.Speed,
            });

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _piperClient.PostAsync("/v1/audio/speech", content);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"[TTS] Sidecar error {response.StatusCode}: {err}");
                return null;
            }

            byte[] audio = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(cachePath, audio);
            return cachePath;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TTS] SynthAndCache exception: {ex.Message}");
            return null;
        }
    }

    // ── GET /v1/audio/voices — proxy straight through, no caching needed ───
    static async Task HandleVoices(HttpContext ctx, string path)
    {
        SetCors(ctx);
        try
        {
            var resp = await _piperClient.GetAsync("/v1/models");
            ctx.Response.StatusCode = (int)resp.StatusCode;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(await resp.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsync($"Sidecar unavailable: {ex.Message}");
        }
    }

    // ── GET /tts/speak?text=...&voice=...&speed=...&format=... ─────────────
    static async Task HandleSimpleSpeak(HttpContext ctx, string path)
    {
        SetCors(ctx);
        var q = ctx.Request.Query;
        string text = q["text"].FirstOrDefault() ?? "";
        string voice = q["voice"].FirstOrDefault() ?? DefaultVoice;
        string format = q["format"].FirstOrDefault() ?? "mp3";
        double speed = double.TryParse(q["speed"].FirstOrDefault(), out var s) ? s : 1.0;

        // Re-use HandleSpeech by injecting a JSON body
        var json = JsonSerializer.Serialize(new { input = text, model = voice, response_format = format, speed });
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        ctx.Request.Method = "POST";
        await HandleSpeech(ctx, path);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    static void SetCors(HttpContext ctx)
    {
        ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
        ctx.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type,Authorization";
    }

    static string CacheKey(string voice, string fmt, double speed, string text)
    {
        string raw = $"{voice}|{fmt}|{speed:F2}|{text}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLower();
    }

    record SpeechReq(string Input, string Model, string Format, double Speed);
}
