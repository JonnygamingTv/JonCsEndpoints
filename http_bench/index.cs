global using System;
global using System.Linq;
global using System.Collections.Generic;
global using System.Threading.Tasks;
global using Microsoft.AspNetCore.Http;
global using System.Net.Http;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using WebServer;

public class Is_CsScript
{
    // ── Concurrency ───────────────────────────────────────────────────────────
    private static readonly SemaphoreSlim _sem = new SemaphoreSlim(1, 1);

    // ── Verification client ───────────────────────────────────────────────────
    // SocketsHttpHandler is leaner than HttpClientHandler for a single-purpose client.
    private static readonly HttpClient _verifyClient = new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect              = false,
        EnableMultipleHttp2Connections = false,
        ConnectTimeout                 = TimeSpan.FromSeconds(5),
    })
    {
        Timeout                  = TimeSpan.FromSeconds(5),
        DefaultRequestVersion    = System.Net.HttpVersion.Version11, // no need for H2/H3 on a txt probe
        DefaultVersionPolicy     = HttpVersionPolicy.RequestVersionOrLower,
    };

    // ── SSE wire bytes ────────────────────────────────────────────────────────
    // Pre-encode the fixed SSE prefix/suffix so SendEvent never allocates a
    // format string — it writes three segments directly to the pipe.
    private static readonly byte[] _ssePrefix = Encoding.UTF8.GetBytes("data: ");
    private static readonly byte[] _sseSuffix = Encoding.UTF8.GetBytes("\n\n");

    // ── Blocked IPv4 ranges (RFC1918 / loopback / reserved) ──────────────────
    // Pre-computed at class init — zero allocation at check time.
    private static readonly (uint lo, uint hi)[] _blockedRanges =
    {
        (Ip("0.0.0.0"),     Ip("0.255.255.255")),
        (Ip("10.0.0.0"),    Ip("10.255.255.255")),
        (Ip("100.64.0.0"),  Ip("100.127.255.255")),
        (Ip("127.0.0.0"),   Ip("127.255.255.255")),
        (Ip("169.254.0.0"), Ip("169.254.255.255")),
        (Ip("172.16.0.0"),  Ip("172.31.255.255")),
        (Ip("192.168.0.0"), Ip("192.168.255.255")),
        (Ip("198.18.0.0"),  Ip("198.19.255.255")),
        (Ip("224.0.0.0"),   Ip("255.255.255.255")),
    };

    // ── Entry point ───────────────────────────────────────────────────────────
    public static async Task Run(HttpContext ctx, string path)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        // ── Parse & clamp parameters ──────────────────────────────────────────
        string? rawUrl = ctx.Request.Query["url"].FirstOrDefault();
        if (string.IsNullOrEmpty(rawUrl))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync("Missing ?url=", ctx.RequestAborted);
            return;
        }

        int duration = Clamp(ParseInt(ctx.Request.Query["d"],   10), 1,    60);
        int threads  = Clamp(ParseInt(ctx.Request.Query["t"],    8), 2,     8);
        int conns    = Clamp(ParseInt(ctx.Request.Query["c"], 1050), 10, 1100);

        // ── Sanitize URL ──────────────────────────────────────────────────────
        // Returns the already-parsed Uri to avoid re-parsing safeUrl downstream.
        if (!TrySanitizeUrl(rawUrl, out Uri? parsedUri, out string safeUrl, out string? urlError))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsync(urlError!, ctx.RequestAborted);
            return;
        }
        ctx.Response.Headers.CacheControl         = "max-age=5";
        // ── Verify /jonhostbench.txt ──────────────────────────────────────────
        // Must return 2xx AND body must be exactly "1" (trimmed).
        // Prevents redirect pages (e.g. InfinityFree's captcha) from passing.
        string verifyUrl = $"{parsedUri!.GetLeftPart(UriPartial.Authority)}/jonhostbench.txt";
        bool verified;
        try
        {
            using var verifyResp = await _verifyClient.GetAsync(verifyUrl, ctx.RequestAborted);
            if (verifyResp.IsSuccessStatusCode)
            {
                // Read at most 8 bytes — "1" is 1 byte, anything longer is wrong.
                using var stream = await verifyResp.Content.ReadAsStreamAsync(ctx.RequestAborted);
                byte[] buf = new byte[8];
                int read = await stream.ReadAsync(buf.AsMemory(0, 8), ctx.RequestAborted);
                string body = System.Text.Encoding.UTF8.GetString(buf, 0, read).Trim();
                verified = body == "1";
            }
            else { verified = false; }
        }
        catch { verified = false; }

        if (!verified)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync(
                $"Verification failed: {verifyUrl} must exist, return 2xx, and contain only \"1\".\n" +
                 "Create that file with the content 1 on your server to authorize benchmarking.",
                ctx.RequestAborted);
            return;
        }

        // ── Semaphore — non-blocking ──────────────────────────────────────────
        if (!await _sem.WaitAsync(0, ctx.RequestAborted))
        {
            ctx.Response.StatusCode = StatusCodes.Status423Locked;
            await ctx.Response.WriteAsync("A benchmark is already running. Try again shortly.", ctx.RequestAborted);
            return;
        }

        // ── SSE response headers ──────────────────────────────────────────────
        ctx.Response.StatusCode  = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl         = "no-cache";
        ctx.Response.Headers.Connection           = "keep-alive";
        ctx.Response.Headers["X-Accel-Buffering"] = "no"; // disable nginx proxy buffering

        // Write directly to the PipeWriter to avoid Stream indirection.
        var pipe  = ctx.Response.BodyWriter;
        var abort = ctx.RequestAborted;

        // Three fixed writes per event: prefix | UTF8(payload) | suffix.
        // Only unavoidable alloc is the UTF8 byte array for the payload string.
        async ValueTask SendEvent(string data)
        {
            await pipe.WriteAsync(_ssePrefix, abort);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(data), abort);
            await pipe.WriteAsync(_sseSuffix, abort);
            await pipe.FlushAsync(abort);
        }

        try
        {
            await SendEvent($"[bench] Starting: wrk -t{threads} -c{conns} -d{duration}s {safeUrl}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(abort);
            cts.CancelAfter(TimeSpan.FromSeconds(duration + 15)); // hard ceiling

            var psi = new ProcessStartInfo
            {
                FileName               = "/usr/bin/taskset",
                Arguments              = $"-c 0-7 wrk -t{threads} -c{conns} -d{duration}s {safeUrl}",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            try { proc.PriorityClass = ProcessPriorityClass.Idle; } catch { }

            async Task Relay(System.IO.StreamReader reader)
            {
                string? line;
                while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
                {
                    if (!string.IsNullOrEmpty(line)) // cheaper than IsNullOrWhiteSpace
                        await SendEvent(line);
                }
            }

            await Task.WhenAll(
                Relay(proc.StandardOutput),
                Relay(proc.StandardError));

            await proc.WaitForExitAsync(cts.Token);
            await SendEvent($"[bench] Done (exit {proc.ExitCode}).");
            await SendEvent("[done]"); // frontend sentinel
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { await SendEvent($"[error] {ex.Message}"); } catch { }
        }
        finally
        {
            _sem.Release();
        }
    }

    // ── URL sanitization ──────────────────────────────────────────────────────
    static bool TrySanitizeUrl(string raw, out Uri? parsedUri, out string safe, out string? error)
    {
        parsedUri = null;
        safe  = "";
        error = null;

        raw = raw.Trim();

        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
        {
            error = "Invalid URL.";
            return false;
        }

        if (uri.Scheme != "http" && uri.Scheme != "https")
        {
            error = "Only http:// and https:// URLs are allowed.";
            return false;
        }

        try
        {
            IPAddress[] addresses = Dns.GetHostAddresses(uri.Host);
            if (addresses.Length == 0)
            {
                error = "Could not resolve host.";
                return false;
            }

            foreach (var addr in addresses)
            {
                if (addr.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    if (IPAddress.IsLoopback(addr) || addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal)
                    {
                        error = "Target resolves to a private/reserved address.";
                        return false;
                    }
                    continue;
                }

                // GetAddressBytes() avoids ToString() + string.Split('.') per address.
                byte[] b = addr.GetAddressBytes();
                uint ip = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

                foreach (var (lo, hi) in _blockedRanges)
                {
                    if (ip >= lo && ip <= hi)
                    {
                        error = "Target resolves to a private/reserved address.";
                        return false;
                    }
                }
            }
        }
        catch
        {
            error = "Could not resolve host.";
            return false;
        }

        var builder = new UriBuilder(uri) { UserName = "", Password = "", Fragment = "" };
        parsedUri = builder.Uri;
        safe = parsedUri.AbsoluteUri;
        return true;
    }

    // ── Micro-helpers ─────────────────────────────────────────────────────────

    // Only called at static init — allocation here is fine.
    static uint Ip(string ip)
    {
        byte[] b = IPAddress.Parse(ip).GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    static int ParseInt(Microsoft.Extensions.Primitives.StringValues sv, int fallback)
        => int.TryParse(sv.FirstOrDefault(), out int v) ? v : fallback;

    static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
