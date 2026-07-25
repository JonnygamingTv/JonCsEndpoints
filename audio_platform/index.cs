global using System;
global using System.IO;
global using System.Linq;
global using System.Collections.Generic;
global using System.Threading;
global using System.Threading.Tasks;
global using Microsoft.AspNetCore.Http;
global using Microsoft.Data.Sqlite;
global using System.Net.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebServer;
using SameSiteMode = Microsoft.AspNetCore.Http.SameSiteMode;

public class Is_CsScript
{
    // ── Config ────────────────────────────────────────────────────────────
    const string DataDir = "/mnt/hdd1/music";
    const string AudioDir = DataDir + "/audio";
    const string IconDir = DataDir + "/icons";
    const string TmpDir = DataDir + "/uploads/tmp";
    const string DbPath = DataDir + "/music.db";
    const string FfmpegBin = "ffmpeg";

    const int MaxUploadBytes = 500 * 1024 * 1024; // 500 MB
    const int MaxIconBytes = 5 * 1024 * 1024; // 5 MB
    const int SessionDays = 30;
    const int PlatformCutPct = 15; // 15% cut

    // HMAC key for session tokens — change to a secret value
    static readonly byte[] _hmacKey = Encoding.UTF8.GetBytes("MODIFY_THIS_AS_RANDOM_SECRET_KEY_32B");
    const string _domainPrefix = "musicjonhostingcom";
    /*static readonly string _domainPrefix = Path.GetFileName(
        Path.GetDirectoryName(typeof(Is_CsScript).Assembly.Location)!);*/
    static string ToKey(string p) => Startup.BackendDir + _domainPrefix + p;

    // ── Connection pool ───────────────────────────────────────────────────
    // SQLite with WAL mode — multiple readers, one writer, low latency
    static readonly ConcurrentQueue<SqliteConnection> _dbPool = new();
    const int DbPoolSize = 8;

    // ── Allowed audio extensions ──────────────────────────────────────────
    static readonly HashSet<string> _allowedAudio =
        new() { ".wav", ".mp3", ".flac", ".aiff", ".ogg" };
    static readonly HashSet<string> _allowedIcon =
        new() { ".jpg", ".jpeg", ".png", ".webp" };

    // ── Payment config — set via environment or replace directly ──────────────
    static readonly string PaypalClientId = Environment.GetEnvironmentVariable("PAYPAL_CLIENT_ID") ?? "xxxx";
    static readonly string PaypalSecret = Environment.GetEnvironmentVariable("PAYPAL_SECRET") ?? "xxxx";
    static readonly string PaypalBase = Environment.GetEnvironmentVariable("PAYPAL_SANDBOX") == "1"
                                                ? "https://api-m.sandbox.paypal.com"
                                                : "https://api-m.paypal.com";
    static readonly string StripeSecretKey = Environment.GetEnvironmentVariable("STRIPE_SECRET") ?? "rk_live_xxxx";
    static readonly string StripeWebhookSec = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SEC") ?? "";
    static readonly string CoinbaseApiKey = Environment.GetEnvironmentVariable("COINBASE_API_KEY") ?? "xxxx";
    static readonly string CoinbaseWebhookSec = Environment.GetEnvironmentVariable("COINBASE_WEBHOOK_SEC") ?? "";
    static readonly string SiteBaseUrl = Environment.GetEnvironmentVariable("SITE_BASE_URL") ?? "https://music.jonhosting.com";

    // Shared HttpClient for payment APIs — one per provider, long-lived
    static readonly HttpClient _paypalHttp = new() { BaseAddress = new Uri(PaypalBase) };
    static readonly HttpClient _stripeHttp = new() { BaseAddress = new Uri("https://api.stripe.com") };
    static readonly HttpClient _coinbaseHttp = new() { BaseAddress = new Uri("https://api.commerce.coinbase.com") };
    // Cache PayPal access token — expires in ~9h, refresh when needed
    static string _paypalToken = "";
    static long _paypalTokenExpiry = 0;
    static readonly SemaphoreSlim _paypalTokenLock = new(1, 1);

    static async Task<string> GetPaypalToken()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_paypalToken.Length > 0 && now < _paypalTokenExpiry - 60)
            return _paypalToken;

        await _paypalTokenLock.WaitAsync();
        try
        {
            // Re-check after acquiring lock
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (_paypalToken.Length > 0 && now < _paypalTokenExpiry - 60)
                return _paypalToken;

            string creds = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{PaypalClientId}:{PaypalSecret}"));
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/oauth2/token");
            req.Headers.Add("Authorization", $"Basic {creds}");
            req.Content = new StringContent("grant_type=client_credentials",
                Encoding.UTF8, "application/x-www-form-urlencoded");
            using var resp = await _paypalHttp.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            _paypalToken = doc.RootElement.GetProperty("access_token").GetString()!;
            _paypalTokenExpiry = now + doc.RootElement.GetProperty("expires_in").GetInt64();
            return _paypalToken;
        }
        finally { _paypalTokenLock.Release(); }
    }

    // ── POST /m/pay/paypal/create?credits={amount} ────────────────────────────
    // credits = amount in cents to purchase
    static async Task HandlePaypalCreate(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        long uid = GetUserId(ctx);
        if (uid == 0) { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 401, "not logged in"); return; }

        if (!int.TryParse(ctx.Request.Query["credits"].FirstOrDefault(), out int credits)
            || credits < 100 || credits > 100_000_00) // min $1, max $100k
        { await JsonErr(ctx, 400, "invalid credits amount"); return; }

        string token = await GetPaypalToken();
        decimal dollars = credits / 100m;

        var orderBody = JsonSerializer.Serialize(new
        {
            intent = "CAPTURE",
            purchase_units = new[]
            {
            new {
                amount = new {
                    currency_code = "USD",
                    value = dollars.ToString("F2")
                },
                description = $"{credits} MusicStore credits"
            }
        },
            application_context = new
            {
                return_url = $"{SiteBaseUrl}/m/pay/paypal/capture",
                cancel_url = $"{SiteBaseUrl}/"
            }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v2/checkout/orders");
        req.Headers.Add("Authorization", $"Bearer {token}");
        req.Content = new StringContent(orderBody, Encoding.UTF8, "application/json");
        using var resp = await _paypalHttp.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        { await JsonErr(ctx, 502, "PayPal order creation failed"); return; }

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        string orderId = doc.RootElement.GetProperty("id").GetString()!;
        string approveUrl = doc.RootElement.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "approve")
            .GetProperty("href").GetString()!;

        // Store pending order
        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"INSERT INTO payment_orders (id, user_id, provider, credits, created_at)
                            VALUES (@id, @u, 'paypal', @c, @t)";
            cmd.Parameters.AddWithValue("@id", orderId);
            cmd.Parameters.AddWithValue("@u", uid);
            cmd.Parameters.AddWithValue("@c", credits);
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
        finally { ReturnDb(db); }

        await JsonOk(ctx, new { orderId, approveUrl });
    }

    // ── GET /m/pay/paypal/capture?token={orderId} ─────────────────────────────
    // PayPal redirects here after user approves — capture and credit account
    static async Task HandlePaypalCapture(HttpContext ctx, string path)
    {
        string? orderId = ctx.Request.Query["token"].FirstOrDefault();
        if (string.IsNullOrEmpty(orderId))
        { ctx.Response.Redirect("/paypal?pay=cancelled"); return; }

        string ppToken = await GetPaypalToken();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"/v2/checkout/orders/{orderId}/capture");
        req.Headers.Add("Authorization", $"Bearer {ppToken}");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await _paypalHttp.SendAsync(req);

        if (!resp.IsSuccessStatusCode)
        { ctx.Response.Redirect("/paypal?pay=failed"); return; }

        var db = RentDb();
        try
        {
            using var tx = db.BeginTransaction();
            try
            {
                using var qCmd = db.CreateCommand();
                qCmd.Transaction = tx;
                qCmd.CommandText = "SELECT user_id, credits, status FROM payment_orders WHERE id=@id";
                qCmd.Parameters.AddWithValue("@id", orderId);
                using var rdr = qCmd.ExecuteReader();
                if (!rdr.Read() || rdr.GetString(2) != "pending")
                { rdr.Close(); tx.Rollback(); ctx.Response.Redirect("/paypal?pay=already"); return; }
                long userId = rdr.GetInt64(0);
                int credits = rdr.GetInt32(1);
                rdr.Close();

                using var upOrder = db.CreateCommand();
                upOrder.Transaction = tx;
                upOrder.CommandText = "UPDATE payment_orders SET status='completed' WHERE id=@id";
                upOrder.Parameters.AddWithValue("@id", orderId);
                upOrder.ExecuteNonQuery();

                using var upUser = db.CreateCommand();
                upUser.Transaction = tx;
                upUser.CommandText = "UPDATE users SET credits=credits+@c WHERE id=@u";
                upUser.Parameters.AddWithValue("@c", credits);
                upUser.Parameters.AddWithValue("@u", userId);
                upUser.ExecuteNonQuery();

                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
        }
        finally { ReturnDb(db); }

        ctx.Response.Redirect("/paypal?pay=success");
    }
    // ── POST /m/pay/stripe/create?credits={amount} ────────────────────────────
    static async Task HandleStripeCreate(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        long uid = GetUserId(ctx);
        if (uid == 0) { await JsonErr(ctx, 401, "not logged in"); return; }

        if (!int.TryParse(ctx.Request.Query["credits"].FirstOrDefault(), out int credits)
            || credits < 100 || credits > 100_000_00)
        { await JsonErr(ctx, 400, "invalid credits amount"); return; }

        // Stripe Checkout session via form-encoded POST
        var formData = new Dictionary<string, string>
        {
            ["mode"] = "payment",
            ["line_items[0][quantity]"] = "1",
            ["line_items[0][price_data][currency]"] = "usd",
            ["line_items[0][price_data][unit_amount]"] = credits.ToString(),
            ["line_items[0][price_data][product_data][name]"] = $"{credits} MusicStore Credits",
            ["success_url"] = $"{SiteBaseUrl}/m/pay/stripe/return?session_id={{CHECKOUT_SESSION_ID}}",
            ["cancel_url"] = $"{SiteBaseUrl}/stripe?pay=cancelled",
            ["metadata[user_id]"] = uid.ToString(),
            ["metadata[credits]"] = credits.ToString(),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/checkout/sessions");
        req.Headers.Add("Authorization",
            $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(StripeSecretKey + ":"))}");
        req.Content = new FormUrlEncodedContent(formData);
        using var resp = await _stripeHttp.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        { await JsonErr(ctx, 502, "Stripe session creation failed"); return; }

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        string sessionId = doc.RootElement.GetProperty("id").GetString()!;
        string checkoutUrl = doc.RootElement.GetProperty("url").GetString()!;

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"INSERT INTO payment_orders (id, user_id, provider, credits, created_at)
                            VALUES (@id, @u, 'stripe', @c, @t)";
            cmd.Parameters.AddWithValue("@id", sessionId);
            cmd.Parameters.AddWithValue("@u", uid);
            cmd.Parameters.AddWithValue("@c", credits);
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
        finally { ReturnDb(db); }

        await JsonOk(ctx, new { sessionId, checkoutUrl });
    }

    // ── POST /m/pay/stripe/webhook ────────────────────────────────────────────
    // Stripe posts here on payment completion — verify sig, credit account
    static async Task HandleStripeWebhook(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }

        // Read raw body for signature verification
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        byte[] body = ms.ToArray();
        string payload = Encoding.UTF8.GetString(body);

        string? sigHeader = ctx.Request.Headers["Stripe-Signature"].FirstOrDefault();
        if (!VerifyStripeSignature(payload, sigHeader, StripeWebhookSec))
        { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("bad signature"); return; }

        using var doc = JsonDocument.Parse(payload);
        string eventType = doc.RootElement.GetProperty("type").GetString() ?? "";
        if (eventType != "checkout.session.completed")
        { ctx.Response.StatusCode = 200; return; } // acknowledge but ignore

        var sessionObj = doc.RootElement.GetProperty("data").GetProperty("object");
        string sessionId = sessionObj.GetProperty("id").GetString()!;

        await CreditFromOrder(sessionId);
        ctx.Response.StatusCode = 200;
    }
    static async Task HandleStripeReturn(HttpContext ctx, string path)
    {
        long uid = GetUserId(ctx);
        if (uid == 0) { ctx.Response.Redirect("/?pay=login"); return; }

        string? sessionId = ctx.Request.Query["session_id"].FirstOrDefault();
        if (string.IsNullOrEmpty(sessionId))
        { ctx.Response.Redirect("/?pay=cancelled"); return; }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.stripe.com/v1/checkout/sessions/{sessionId}");
            req.Headers.Add("Authorization",
                $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(StripeSecretKey + ":"))}");
            using var resp = await _stripeHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            { ctx.Response.Redirect("/?pay=failed"); return; }

            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync());
            string paymentStatus = doc.RootElement
                .GetProperty("payment_status")
                .GetString() ?? "";

            if (paymentStatus == "paid")
            {
                await CreditFromOrder(sessionId);
                ctx.Response.Redirect("/?pay=success");
            }
            else
            {
                ctx.Response.Redirect("/?pay=failed");
            }
        }
        catch
        {
            ctx.Response.Redirect("/?pay=failed");
        }
    }

    static bool VerifyStripeSignature(string payload, string? header, string secret)
    {
        if (string.IsNullOrEmpty(header)) return false;
        // header format: t=timestamp,v1=sig
        string? timestamp = null, signature = null;
        foreach (var part in header.Split(','))
        {
            if (part.StartsWith("t=")) timestamp = part[2..];
            if (part.StartsWith("v1=")) signature = part[3..];
        }
        if (timestamp == null || signature == null) return false;

        string signed = $"{timestamp}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        byte[] expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(signed));
        byte[] actual;
        try { actual = Convert.FromHexString(signature); }
        catch { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
    // ── POST /m/pay/coinbase/create?credits={amount} ──────────────────────────
    static async Task HandleCoinbaseCreate(HttpContext ctx, string path)
    {
        /*if (ctx.Request.Method != "POST") {*/ ctx.Response.StatusCode = 405; return;// }
        long uid = GetUserId(ctx);
        if (uid == 0) { await JsonErr(ctx, 401, "not logged in"); return; }

        if (!int.TryParse(ctx.Request.Query["credits"].FirstOrDefault(), out int credits)
            || credits < 100 || credits > 100_000_00)
        { await JsonErr(ctx, 400, "invalid credits amount"); return; }

        decimal dollars = credits / 100m;
        var chargeBody = JsonSerializer.Serialize(new
        {
            name = "MusicStore Credits",
            description = $"{credits} credits",
//            customer_name = uid.ToString(),
//            customer_email = GetUserMail(uid),
            pricing_type = "fixed_price",
            local_price = new { amount = dollars.ToString("F2"), currency = "USD" },
            metadata = new { user_id = uid.ToString(), credits = credits.ToString() },
            redirect_url = $"{SiteBaseUrl}/m/pay/coinbase/return",
            cancel_url = $"{SiteBaseUrl}/crypto?pay=cancelled",
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "/charges");
        req.Headers.Add("X-CC-Api-Key", CoinbaseApiKey);
        req.Headers.Add("X-CC-Version", "2018-03-22");
        req.Content = new StringContent(chargeBody, Encoding.UTF8, "application/json");
        using var resp = await _coinbaseHttp.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        { await JsonErr(ctx, 502, "Coinbase charge creation failed"); return; }

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        var data = doc.RootElement.GetProperty("data");
        string chargeId = data.GetProperty("id").GetString()!;
        string hostedUrl = data.GetProperty("hosted_url").GetString()!;

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"INSERT INTO payment_orders (id, user_id, provider, credits, created_at)
                            VALUES (@id, @u, 'coinbase', @c, @t)";
            cmd.Parameters.AddWithValue("@id", chargeId);
            cmd.Parameters.AddWithValue("@u", uid);
            cmd.Parameters.AddWithValue("@c", credits);
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
        finally { ReturnDb(db); }

        await JsonOk(ctx, new { chargeId, hostedUrl });
    }

    // ── POST /m/pay/coinbase/webhook ──────────────────────────────────────────
    static async Task HandleCoinbaseWebhook(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }

        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        byte[] body = ms.ToArray();
        string payload = Encoding.UTF8.GetString(body);

        // Coinbase uses HMAC-SHA256 hex signature in X-CC-Webhook-Signature
        string? sig = ctx.Request.Headers["X-CC-Webhook-Signature"].FirstOrDefault();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(CoinbaseWebhookSec));
        byte[] expected = hmac.ComputeHash(body);
        byte[] actual;
        try { actual = Convert.FromHexString(sig ?? ""); }
        catch { ctx.Response.StatusCode = 400; return; }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("bad signature"); return; }

        using var doc = JsonDocument.Parse(payload);
        var evt = doc.RootElement.GetProperty("event");
        string eventType = evt.GetProperty("type").GetString() ?? "";

        // Only credit on confirmed payment
        if (eventType != "charge:confirmed" && eventType != "charge:resolved")
        { ctx.Response.StatusCode = 200; return; }

        string chargeId = evt.GetProperty("data").GetProperty("id").GetString()!;
        await CreditFromOrder(chargeId);
        ctx.Response.StatusCode = 200;
    }

    static async Task HandleCoinbaseReturn(HttpContext ctx, string path)
    {
        long uid = GetUserId(ctx);
        if (uid == 0) { ctx.Response.Redirect("/?pay=login"); return; }

        string? chargeId = ctx.Request.Query["id"].FirstOrDefault();
        if (string.IsNullOrEmpty(chargeId))
        { ctx.Response.Redirect("/?pay=cancelled"); return; }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.commerce.coinbase.com/charges/{chargeId}");
            req.Headers.Add("X-CC-Api-Key", CoinbaseApiKey);
            req.Headers.Add("X-CC-Version", "2018-03-22");
            using var resp = await _coinbaseHttp.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            { ctx.Response.Redirect("/?pay=failed"); return; }

            using var doc = await JsonDocument.ParseAsync(
                await resp.Content.ReadAsStreamAsync());
            string status = doc.RootElement
                .GetProperty("data")
                .GetProperty("status")
                .GetString() ?? "";

            // COMPLETED = fully confirmed on-chain
            // RESOLVED  = manually marked resolved by merchant
            if (status is "COMPLETED" or "RESOLVED")
            {
                await CreditFromOrder(chargeId);
                ctx.Response.Redirect("/?pay=success");
            }
            else
            {
                // PENDING = payment detected but not confirmed yet
                // NEW / UNRESOLVED / EXPIRED / CANCELED
                ctx.Response.Redirect($"/?pay=pending&id={chargeId}&provider=coinbase");
            }
        }
        catch
        {
            ctx.Response.Redirect("/?pay=failed");
        }
    }

    // ── Shared credit-granting logic used by all webhook/capture handlers ─────
    static async Task CreditFromOrder(string orderId)
    {
        var db = RentDb();
        try
        {
            using var tx = db.BeginTransaction();
            try
            {
                using var qCmd = db.CreateCommand();
                qCmd.Transaction = tx;
                qCmd.CommandText = "SELECT user_id, credits, status FROM payment_orders WHERE id=@id";
                qCmd.Parameters.AddWithValue("@id", orderId);
                using var rdr = qCmd.ExecuteReader();
                if (!rdr.Read() || rdr.GetString(2) != "pending")
                { rdr.Close(); tx.Rollback(); return; } // already processed or unknown
                long userId = rdr.GetInt64(0);
                int credits = rdr.GetInt32(1);
                rdr.Close();

                using var upOrder = db.CreateCommand();
                upOrder.Transaction = tx;
                upOrder.CommandText = "UPDATE payment_orders SET status='completed' WHERE id=@id";
                upOrder.Parameters.AddWithValue("@id", orderId);
                upOrder.ExecuteNonQuery();

                using var upUser = db.CreateCommand();
                upUser.Transaction = tx;
                upUser.CommandText = "UPDATE users SET credits=credits+@c WHERE id=@u";
                upUser.Parameters.AddWithValue("@c", credits);
                upUser.Parameters.AddWithValue("@u", userId);
                upUser.ExecuteNonQuery();

                tx.Commit();
                Console.WriteLine($"[Music] Credited {credits} to user {userId} via order {orderId}");
            }
            catch { tx.Rollback(); throw; }
        }
        finally { ReturnDb(db); }
    }

    // ─────────────────────────────────────────────────────────────────────
    static Is_CsScript()
    {
        Directory.CreateDirectory(AudioDir);
        Directory.CreateDirectory(IconDir);
        Directory.CreateDirectory(TmpDir);

        InitDb();

        // Pre-fill connection pool
        for (int i = 0; i < DbPoolSize; i++)
            _dbPool.Enqueue(OpenDb());

        // In static Is_CsScript(), after InitDb():
        _ = Task.Run(() =>
        {
            var db = RentDb();
            try
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT id, has_wav FROM audio WHERE active=1";
                using var rdr = cmd.ExecuteReader();
                int count = 0;
                while (rdr.Read())
                {
                    long id = rdr.GetInt64(0);
                    bool hw = rdr.GetInt32(1) == 1;
                    string mp3 = Path.Combine(AudioDir, id.ToString(), "stream.mp3")
                                     .Replace(Path.DirectorySeparatorChar, '/');
                    // Only register if file actually exists (active=-1 transcodes may be in DB)
                    if (File.Exists(mp3))
                    {
                        RegisterAudioFile(mp3);
                        if (hw)
                        {
                            string wav = Path.Combine(AudioDir, id.ToString(), "original.wav")
                                             .Replace(Path.DirectorySeparatorChar, '/');
                            if (File.Exists(wav)) RegisterAudioFile(wav);
                        }
                        RegisterStreamRoutes(id, hw);
                        count++;
                    }
                }
                Console.WriteLine($"[Music] Registered routes for {count} existing tracks.");
            }
            finally { ReturnDb(db); }
        });

        // Register routes
        Startup.AddToFileLead(ToKey("/m/register"), HandleRegister);
        Startup.AddToFileLead(ToKey("/m/login"), HandleLogin);
        Startup.AddToFileLead(ToKey("/m/logout"), HandleLogout);
        Startup.AddToFileLead(ToKey("/m/me"), HandleMe);
        Startup.AddToFileLead(ToKey("/m/upload"), HandleUpload);
        Startup.AddToFileLead(ToKey("/m/audio/edit"), HandleAudioEdit);
        Startup.AddToFileLead(ToKey("/m/audio/delete"), HandleAudioDelete);
        Startup.AddToFileLead(ToKey("/m/purchase"), HandlePurchase);
        Startup.AddToFileLead(ToKey("/m/stream"), HandleStream);
        Startup.AddToFileLead(ToKey("/m/download"), HandleDownload);
        Startup.AddToFileLead(ToKey("/m/browse"), HandleBrowse);
        Startup.AddToFileLead(ToKey("/m/icon"), HandleIcon);
        Startup.AddToFileLead(ToKey("/m/mod/revoke"), HandleModRevoke);
        Startup.AddToFileLead(ToKey("/m/mod/transfer"), HandleModTransfer);
        Startup.AddToFileLead(ToKey("/m/mod/promote"), HandleModPromote);

        Startup.AddToFileLead(ToKey("/m/mod/name-requests"), HandleModNameRequests);
        Startup.AddToFileLead(ToKey("/m/mod/user"), HandleModUserLookup);

        Startup.AddToFileLead(ToKey("/m/artist/name-request"), HandleArtistNameRequest);
        Startup.AddToFileLead(ToKey("/m/mod/set-artist-name"), HandleModSetArtistName);
        Startup.AddToFileLead(ToKey("/m/track"), HandleTrackInfo);
        Startup.AddToFileLead(ToKey("/m/library/add"), HandleLibraryAdd);
        Startup.AddToFileLead(ToKey("/m/pay/paypal/create"), HandlePaypalCreate);
        Startup.AddToFileLead(ToKey("/m/pay/paypal/capture"), HandlePaypalCapture);
        Startup.AddToFileLead(ToKey("/m/pay/stripe/create"), HandleStripeCreate);
        // Startup.AddToFileLead(ToKey("/m/pay/stripe/webhook"), HandleStripeWebhook);
        Startup.AddToFileLead(ToKey("/m/pay/coinbase/create"), HandleCoinbaseCreate);
        // Startup.AddToFileLead(ToKey("/m/pay/coinbase/webhook"), HandleCoinbaseWebhook);

        Startup.AddToFileLead(ToKey("/m/pay/stripe/return"), HandleStripeReturn);
        Startup.AddToFileLead(ToKey("/m/pay/coinbase/return"), HandleCoinbaseReturn);

        Startup.AddToFileLead(ToKey("/m/my-tracks"), HandleMyTracks);

        Startup.AddToFileLead(ToKey("/m/pay/coinbase/status"), async (ctx, path) =>
        {
            long uid = GetUserId(ctx);
            if (uid == 0) { await JsonErr(ctx, 401, "not logged in"); return; }
            string? chargeId = ctx.Request.Query["id"].FirstOrDefault();
            if (string.IsNullOrEmpty(chargeId)) { await JsonErr(ctx, 400, "id required"); return; }

            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.commerce.coinbase.com/charges/{chargeId}");
            req.Headers.Add("X-CC-Api-Key", CoinbaseApiKey);
            req.Headers.Add("X-CC-Version", "2018-03-22");
            using var resp = await _coinbaseHttp.SendAsync(req);
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            string status = doc.RootElement.GetProperty("data").GetProperty("status").GetString() ?? "";

            if (status is "COMPLETED" or "RESOLVED")
            {
                await CreditFromOrder(chargeId);
                await JsonOk(ctx, new { status = "complete" });
            }
            else
            {
                await JsonOk(ctx, new { status = status.ToLower() }); // "pending", "new", "expired" etc.
            }
        });

        Console.WriteLine("[Music] Backend loaded.");
    }

    public static Task Run(HttpContext ctx, string path)
    {
        ctx.Response.StatusCode = 404;
        return ctx.Response.WriteAsync("Not found");
    }

    // ── DB helpers ────────────────────────────────────────────────────────
    static void InitDb()
    {
        using var conn = OpenDb();
        string sql = File.ReadAllText(DataDir + "/init.sql");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static SqliteConnection OpenDb()
    {
        var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // WAL mode: readers don't block writers, writers don't block readers
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    static SqliteConnection RentDb()
    {
        if (_dbPool.TryDequeue(out var conn) && conn.State == System.Data.ConnectionState.Open)
            return conn;
        return OpenDb();
    }

    static void ReturnDb(SqliteConnection conn)
    {
        if (_dbPool.Count < DbPoolSize)
            _dbPool.Enqueue(conn);
        else
            conn.Dispose();
    }

    // ── Auth helpers ──────────────────────────────────────────────────────
    static string HashPassword(string password)
    {
        // Argon2id via libsodium would be ideal; using PBKDF2 as a portable fallback
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, 200_000,
            HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
    }

    static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2) return false;
        byte[] salt = Convert.FromBase64String(parts[0]);
        byte[] expected = Convert.FromBase64String(parts[1]);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, 200_000,
            HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    static string GenerateSessionToken(long userId)
    {
        long expiry = DateTimeOffset.UtcNow.AddDays(SessionDays).ToUnixTimeSeconds();
        string payload = $"{userId}:{expiry}";
        using var hmac = new HMACSHA256(_hmacKey);
        byte[] sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return payload + ":" + Convert.ToBase64String(sig);
    }

    static (long userId, bool valid) ValidateToken(string token)
    {
        var parts = token.Split(':');
        if (parts.Length != 3) return (0, false);
        string payload = $"{parts[0]}:{parts[1]}";
        using var hmac = new HMACSHA256(_hmacKey);
        byte[] expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        byte[] actual;
        try { actual = Convert.FromBase64String(parts[2]); }
        catch { return (0, false); }
        if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return (0, false);
        if (!long.TryParse(parts[1], out long expiry)) return (0, false);
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry) return (0, false);
        if (!long.TryParse(parts[0], out long userId)) return (0, false);
        return (userId, true);
    }

    // Returns userId or 0 if not authenticated
    static long GetUserId(HttpContext ctx)
    {
        string? token = ctx.Request.Cookies["msess"]
                     ?? ctx.Request.Headers["Authorization"].FirstOrDefault()?.Replace("Bearer ", "");
        if (string.IsNullOrEmpty(token)) return 0;
        var (userId, valid) = ValidateToken(token);
        return valid ? userId : 0;
    }
    static string? GetUserMail(long id)
    {
        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT email FROM users WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", id);
            var result = cmd.ExecuteScalar();
            return Convert.ToString(result);
        }
        finally { ReturnDb(db); }
    }

    static bool IsModDb(long userId)
    {
        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT is_mod FROM users WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", userId);
            var result = cmd.ExecuteScalar();
            return result != null && Convert.ToInt32(result) == 1;
        }
        finally { ReturnDb(db); }
    }

    // ── JSON write helpers ─────────────────────────────────────────────────
    static Task JsonOk(HttpContext ctx, object data)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(data));
    }

    static Task JsonErr(HttpContext ctx, int status, string msg)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        // Short field name 'e' for error — bandwidth saving on hot error paths
        return ctx.Response.WriteAsync($"{{\"e\":\"{msg}\"}}");
    }

    // ── POST /music/register ──────────────────────────────────────────────
    // { email, password, artistName? }
    static async Task HandleRegister(HttpContext ctx, string path)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        ctx.Response.Headers.CacheControl = "max-age=1";

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
        var r = doc.RootElement;

        string? email = r.TryGetProperty("email", out var e) ? e.GetString() : null;
        string? password = r.TryGetProperty("password", out var p) ? p.GetString() : null;
        string? artistName = r.TryGetProperty("artistName", out var an) ? an.GetString() : null;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        { await JsonErr(ctx, 400, "email and password required"); return; }

        if (password.Length < 8)
        { await JsonErr(ctx, 400, "password min 8 chars"); return; }

        if (!email.Contains('@') || email.Length > 254)
        { await JsonErr(ctx, 400, "invalid email"); return; }

        if (artistName is { Length: < 2 or > 40 })
        { await JsonErr(ctx, 400, "artist name 2-40 chars"); return; }

        string hash = HashPassword(password);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
        INSERT INTO users (email, artist_name, pass_hash, created_at)
        VALUES (@e, @a, @h, @t)
        RETURNING id;
        """;

            cmd.Parameters.Add("@e", SqliteType.Text).Value = email.ToLowerInvariant();
            cmd.Parameters.Add("@a", SqliteType.Text).Value = (object?)artistName ?? DBNull.Value;
            cmd.Parameters.Add("@h", SqliteType.Text).Value = hash;
            cmd.Parameters.Add("@t", SqliteType.Integer).Value = now;

            long userId;
            try
            { userId = (long)(cmd.ExecuteScalar() ?? throw new Exception("No ID returned")); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            { await JsonErr(ctx, 409, "email or artist name already taken"); return; }

            string token = GenerateSessionToken(userId);
            ctx.Response.Cookies.Append("msess", token, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddDays(SessionDays),
                Path = "/"
            });

            await JsonOk(ctx, new { ok = true, userId });
        }
        finally
        {
            ReturnDb(db);
        }
    }

    // ── POST /music/login ─────────────────────────────────────────────────
    static async Task HandleLogin(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        ctx.Response.Headers.CacheControl = "max-age=1";

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var r = doc.RootElement;
        string? email = r.TryGetProperty("email", out var e) ? e.GetString() : null;
        string? password = r.TryGetProperty("password", out var p) ? p.GetString() : null;
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
        { await JsonErr(ctx, 400, "missing fields"); return; }

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id, pass_hash, artist_name FROM users WHERE email=@e";
            cmd.Parameters.AddWithValue("@e", email.ToLowerInvariant());
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read() || !VerifyPassword(password, rdr.GetString(1)))
            { await JsonErr(ctx, 401, "invalid credentials"); return; }

            long userId = rdr.GetInt64(0);
            string artistName = rdr.IsDBNull(2) ? "" : rdr.GetString(2);
            string token = GenerateSessionToken(userId);

            ctx.Response.Cookies.Append("msess", token, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddDays(SessionDays),
                Path = "/"
            });
            await JsonOk(ctx, new { ok = true, userId, artistName });
        }
        finally { ReturnDb(db); }
    }

    // ── POST /music/logout ────────────────────────────────────────────────
    static Task HandleLogout(HttpContext ctx, string path)
    {
        ctx.Response.Headers.CacheControl = "max-age=1";
        ctx.Response.Cookies.Delete("msess");
        ctx.Response.StatusCode = 204;
        return Task.CompletedTask;
    }

    // ── GET /music/me ─────────────────────────────────────────────────────
    // /me array format: [email, artistName, credits, isMod, owned[]]
    // Schema version in first element so clients can detect format changes
    // [0]=email [1]=artistName [2]=credits [3]=isMod(0/1) [4]=owned[]

    static readonly JsonEncodedText _meSchema = JsonEncodedText.Encode("s");

    static async Task HandleMe(HttpContext ctx, string path)
    {
        long uid = GetUserId(ctx);
        if (uid == 0)
        {
            ctx.Response.Headers.CacheControl = "max-age=5";
            await JsonErr(ctx, 401, "not logged in");
            return;
        }

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
            SELECT u.email, u.artist_name, u.credits, u.is_mod,
                   GROUP_CONCAT(p.audio_id) as owned
            FROM users u
            LEFT JOIN purchases p ON p.user_id = u.id
            WHERE u.id = @id
            GROUP BY u.id";
            cmd.Parameters.AddWithValue("@id", uid);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read())
            {
                ctx.Response.Headers.CacheControl = "max-age=5";
                await JsonErr(ctx, 404, "user not found");
                return;
            }

            string email = rdr.GetString(0);
            string artistName = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
            int credits = rdr.GetInt32(2);
            int isMod = rdr.GetInt32(3); // 0/1 — saves bool serialization
            string ownedRaw = rdr.IsDBNull(4) ? "" : rdr.GetString(4);

            // Write manually with Utf8JsonWriter — zero reflection, zero intermediate objects
            ctx.Response.Headers.CacheControl = "max-age=30";
            ctx.Response.ContentType = "application/json";

            var writer = new Utf8JsonWriter(ctx.Response.BodyWriter);
            writer.WriteStartArray();
            writer.WriteNumberValue(uid);
            writer.WriteStringValue(email);
            writer.WriteStringValue(artistName);
            writer.WriteNumberValue(credits);
            writer.WriteNumberValue(isMod);
            writer.WriteStartArray();
            if (!string.IsNullOrEmpty(ownedRaw))
            {
                // Parse CSV of longs without allocating a string[]
                ReadOnlySpan<char> span = ownedRaw.AsSpan();
                while (!span.IsEmpty)
                {
                    int comma = span.IndexOf(',');
                    ReadOnlySpan<char> token = comma < 0 ? span : span[..comma];
                    if (long.TryParse(token, out long oid))
                        writer.WriteNumberValue(oid);
                    span = comma < 0 ? ReadOnlySpan<char>.Empty : span[(comma + 1)..];
                }
            }
            writer.WriteEndArray();
            writer.WriteEndArray();
            await writer.FlushAsync(ctx.RequestAborted);
        }
        finally { ReturnDb(db); }
    }

    // ── POST /music/upload ────────────────────────────────────────────────
    // multipart: file=<audio>, title=, description=, price=
    static async Task HandleUpload(HttpContext ctx, string path)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        long uid = GetUserId(ctx);
        if (uid == 0)
        {
            ctx.Response.Headers.CacheControl = "max-age=30";
            await JsonErr(ctx, 401, "not logged in");
            return;
        }
        ctx.Response.Headers.CacheControl = "max-age=1";
        var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature != null)
            sizeFeature.MaxRequestBodySize = MaxUploadBytes;

        string? contentType = ctx.Request.ContentType;
        if (contentType is null || contentType.IndexOf("multipart/form-data", StringComparison.OrdinalIgnoreCase) < 0)
        { await JsonErr(ctx, 400, "multipart required"); return; }

        int bIndex = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (bIndex < 0)
        { await JsonErr(ctx, 400, "missing boundary"); return; }

        string boundary = contentType[(bIndex + 9)..].Trim('"');

        string? title = null;
        string? description = null;
        int price = 0;

        string? tmpPath = null;
        string? origExt = null;
        long bytesWritten = 0;

        var reader = new MultipartReader(boundary, ctx.Request.Body);

        byte[] buffer = new byte[1 << 17];

        try
        {
            MultipartSection? section;

            while ((section = await reader.ReadNextSectionAsync(ctx.RequestAborted)) != null)
            {
                var cdHeader = section.Headers?["Content-Disposition"].FirstOrDefault();
                if (!ContentDispositionHeaderValue.TryParse(cdHeader, out var cd))
                    continue;

                string fieldName = cd.Name.Value ?? string.Empty;

                if (fieldName == "file" && tmpPath is null)
                {
                    string fn = cd.FileName.Value ?? "upload";

                    origExt = Path.GetExtension(fn).ToLowerInvariant();

                    if (!_allowedAudio.Contains(origExt))
                    { await JsonErr(ctx, 415, $"unsupported audio format: {origExt}"); return; }

                    tmpPath = Path.Combine(TmpDir, Guid.NewGuid().ToString("N") + origExt);

                    await using var fs = new FileStream(
                        tmpPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: buffer.Length,
                        useAsync: true);

                    int read;
                    while ((read = await section.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
                    {
                        bytesWritten += read;

                        if (bytesWritten > MaxUploadBytes)
                        { await JsonErr(ctx, 413, "file too large"); return; }

                        await fs.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                    }
                }
                else
                {
                    using var sr = new StreamReader(section.Body);
                    string value = (await sr.ReadToEndAsync()).Trim();

                    switch (fieldName)
                    {
                        case "title":
                            title = value.Length > 100 ? value[..100] : value;
                            break;

                        case "description":
                            description = value.Length > 2000 ? value[..2000] : value;
                            break;

                        case "price":
                            int.TryParse(value, out price);
                            break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmpPath);
            ctx.Response.StatusCode = 499;
            return;
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            await JsonErr(ctx, 500, ex.Message);
            return;
        }

        if (tmpPath is null || string.IsNullOrWhiteSpace(title))
        {
            TryDelete(tmpPath);
            await JsonErr(ctx, 400, "file and title required");
            return;
        }

        price = Math.Max(0, price);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool hasWav = origExt == ".wav";

        long audioId;

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
        INSERT INTO audio (owner_id, title, description, price, has_wav, created_at)
        VALUES (@o, @t, @d, @p, @w, @n)
        RETURNING id;
        """;

            cmd.Parameters.Add("@o", SqliteType.Integer).Value = uid;
            cmd.Parameters.Add("@t", SqliteType.Text).Value = title;
            cmd.Parameters.Add("@d", SqliteType.Text).Value = description ?? "";
            cmd.Parameters.Add("@p", SqliteType.Integer).Value = price;
            cmd.Parameters.Add("@w", SqliteType.Integer).Value = hasWav ? 1 : 0;
            cmd.Parameters.Add("@n", SqliteType.Integer).Value = now;

            audioId = (long)(cmd.ExecuteScalar() ?? throw new Exception("No id returned"));
        }
        finally
        {
            ReturnDb(db);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string audioDir = Path.Combine(AudioDir, audioId.ToString());
                Directory.CreateDirectory(audioDir);

                if (hasWav)
                {
                    string wavDest = Path.Combine(audioDir, "original.wav");
                    File.Move(tmpPath!, wavDest, true);

                    string mp3Dest = Path.Combine(audioDir, "stream.mp3");
                    await TranscodeToMp3(wavDest, mp3Dest);

                    RegisterAudioFile(mp3Dest);
                    RegisterAudioFile(wavDest);
                }
                else
                {
                    string mp3Dest = Path.Combine(audioDir, "stream.mp3");
                    await TranscodeToMp3(tmpPath!, mp3Dest);

                    File.Delete(tmpPath!);
                    RegisterAudioFile(mp3Dest);
                }
                RegisterStreamRoutes(audioId, hasWav);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Music] Transcode failed for audio {audioId}: {ex.Message}");
                TryDelete(tmpPath);

                var db2 = RentDb();
                try
                {
                    using var cmd = db2.CreateCommand();
                    cmd.CommandText = "UPDATE audio SET active=-1 WHERE id=@id";
                    cmd.Parameters.Add("@id", SqliteType.Integer).Value = audioId;
                    cmd.ExecuteNonQuery();
                }
                finally
                {
                    ReturnDb(db2);
                }
            }
        });

        await JsonOk(ctx, new { ok = true, audioId });
    }

    static async Task TranscodeToMp3(string input, string output)
    {
        // -q:a 0 = VBR V0, highest quality MP3 (~245kbps average)
        // -map_metadata 0 preserves tags
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegBin,
            Arguments = $"-y -i \"{input}\" -c:a libmp3lame -q:a 0 -map_metadata 0 \"{output}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        try { proc.PriorityClass = ProcessPriorityClass.Idle; } catch { }
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new Exception($"ffmpeg exited {proc.ExitCode}");
    }

    static void RegisterAudioFile(string filePath)
    {
        var info = new FileInfo(filePath);
        string normalized = filePath.Replace(Path.DirectorySeparatorChar, '/');
        Startup.FileIndex[normalized] = new long[]
        {
            ((DateTimeOffset)info.LastWriteTimeUtc).ToUnixTimeSeconds(),
            info.Length
        };
    }

    // ── POST /music/audio/edit ────────────────────────────────────────────
    // { audioId, title?, description?, price? }
    static async Task HandleAudioEdit(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        long uid = GetUserId(ctx);
        if (uid == 0) { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 401, "not logged in"); return; }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var r = doc.RootElement;
        if (!r.TryGetProperty("audioId", out var aid)) { await JsonErr(ctx, 400, "audioId required"); return; }
        long audioId = aid.GetInt64();

        var db = RentDb();
        try
        {
            // Verify ownership (or mod)
            using var check = db.CreateCommand();
            check.CommandText = "SELECT owner_id FROM audio WHERE id=@id AND active=1";
            check.Parameters.AddWithValue("@id", audioId);
            var ownerId = check.ExecuteScalar();
            if (ownerId == null) { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 404, "audio not found"); return; }
            if (Convert.ToInt64(ownerId) != uid && !IsModDb(uid))
            { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 403, "forbidden"); return; }

            // Build update dynamically — only set provided fields
            var sets = new List<string>();
            var cmd = db.CreateCommand();

            if (r.TryGetProperty("title", out var t) && !string.IsNullOrWhiteSpace(t.GetString()))
            { sets.Add("title=@t"); cmd.Parameters.AddWithValue("@t", t.GetString()![..Math.Min(t.GetString()!.Length, 100)]); }
            if (r.TryGetProperty("description", out var d))
            { sets.Add("description=@d"); cmd.Parameters.AddWithValue("@d", d.GetString() ?? ""); }
            if (r.TryGetProperty("price", out var pr))
            { sets.Add("price=@p"); cmd.Parameters.AddWithValue("@p", Math.Max(0, pr.GetInt32())); }

            if (sets.Count == 0) { await JsonErr(ctx, 400, "nothing to update"); return; }

            cmd.CommandText = $"UPDATE audio SET {string.Join(',', sets)} WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", audioId);
            cmd.ExecuteNonQuery();
            cmd.Dispose();
            ctx.Response.Headers.CacheControl = "max-age=1";
            await JsonOk(ctx, new { ok = true });
        }
        finally { ReturnDb(db); }
    }

    // ── POST /music/purchase ──────────────────────────────────────────────
    // { audioId }
    static async Task HandlePurchase(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        long uid = GetUserId(ctx);
        if (uid == 0) { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 401, "not logged in"); return; }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        if (!doc.RootElement.TryGetProperty("audioId", out var aid))
        { await JsonErr(ctx, 400, "audioId required"); return; }
        long audioId = aid.GetInt64();

        var db = RentDb();
        try
        {
            // Get audio price + owner
            using var qCmd = db.CreateCommand();
            qCmd.CommandText = "SELECT price, owner_id FROM audio WHERE id=@id AND active=1";
            qCmd.Parameters.AddWithValue("@id", audioId);
            using var qRdr = qCmd.ExecuteReader();
            if (!qRdr.Read()) { await JsonErr(ctx, 404, "audio not found"); return; }
            int price = qRdr.GetInt32(0);
            long ownerId = qRdr.GetInt64(1);
            qRdr.Close();

            // Check not already owned
            using var ownCheck = db.CreateCommand();
            ownCheck.CommandText = "SELECT 1 FROM purchases WHERE user_id=@u AND audio_id=@a";
            ownCheck.Parameters.AddWithValue("@u", uid);
            ownCheck.Parameters.AddWithValue("@a", audioId);
            if (ownCheck.ExecuteScalar() != null)
            { ctx.Response.Headers.CacheControl = "max-age=600"; await JsonErr(ctx, 409, "already purchased"); return; }

            // Free track — just record purchase
            if (price == 0)
            {
                using var freeCmd = db.CreateCommand();
                freeCmd.CommandText = @"
                    INSERT OR IGNORE INTO purchases (user_id, audio_id, paid_cents, purchased_at)
                    VALUES (@u, @a, 0, @t)";
                freeCmd.Parameters.AddWithValue("@u", uid);
                freeCmd.Parameters.AddWithValue("@a", audioId);
                freeCmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                freeCmd.ExecuteNonQuery();
                ctx.Response.Headers.CacheControl = "max-age=10";
                await JsonOk(ctx, new { ok = true }); return;
            }

            // Check buyer has enough credits
            using var credCmd = db.CreateCommand();
            credCmd.CommandText = "SELECT credits FROM users WHERE id=@u";
            credCmd.Parameters.AddWithValue("@u", uid);
            int credits = Convert.ToInt32(credCmd.ExecuteScalar() ?? 0);
            if (credits < price) { await JsonErr(ctx, 402, "insufficient credits"); return; }

            // Transaction: deduct from buyer, credit artist (minus platform cut), record purchase
            int platformCut = (int)Math.Round(price * PlatformCutPct / 100.0);
            int artistRevenue = price - platformCut;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using var tx = db.BeginTransaction();
            try
            {
                void Exec(string sql, Action<SqliteCommand> setup)
                {
                    using var c = db.CreateCommand();
                    c.Transaction = tx;
                    c.CommandText = sql;
                    setup(c);
                    c.ExecuteNonQuery();
                }

                Exec("UPDATE users SET credits=credits-@p WHERE id=@u",
                     c => { c.Parameters.AddWithValue("@p", price); c.Parameters.AddWithValue("@u", uid); });
                Exec("UPDATE users SET credits=credits+@r WHERE id=@o",
                     c => { c.Parameters.AddWithValue("@r", artistRevenue); c.Parameters.AddWithValue("@o", ownerId); });
                Exec("INSERT OR IGNORE INTO purchases (user_id,audio_id,paid_cents,purchased_at) VALUES(@u,@a,@p,@t)",
                     c => {
                         c.Parameters.AddWithValue("@u", uid); c.Parameters.AddWithValue("@a", audioId);
                         c.Parameters.AddWithValue("@p", price); c.Parameters.AddWithValue("@t", now);
                     });

                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
            ctx.Response.Headers.CacheControl = "max-age=10";
            await JsonOk(ctx, new { ok = true, spent = price, artistRevenue });
        }
        finally { ReturnDb(db); }
    }

    // ── GET /music/stream?id={audioId} ────────────────────────────────────
    // Streams the MP3 — purchased users + owner only (free tracks: anyone)
    static async Task HandleStream(HttpContext ctx, string path)
    {
        long uid = GetUserId(ctx);
        if (!long.TryParse(ctx.Request.Query["id"].FirstOrDefault(), out long audioId))
        { ctx.Response.Headers.CacheControl = "max-age=86400"; await JsonErr(ctx, 400, "id required"); return; }

        var db = RentDb();
        bool authorized = false;
        bool free = false;
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT price, owner_id FROM audio WHERE id=@id AND active=1";
            cmd.Parameters.AddWithValue("@id", audioId);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read()) { ctx.Response.Headers.CacheControl = "max-age=600"; await JsonErr(ctx, 404, "not found"); return; }
            int price = rdr.GetInt32(0);
            long ownerId = rdr.GetInt64(1);
            rdr.Close();

            free = price == 0;
            if (free || uid == ownerId) { authorized = true; }
            else if (uid != 0)
            {
                using var pCmd = db.CreateCommand();
                pCmd.CommandText = "SELECT 1 FROM purchases WHERE user_id=@u AND audio_id=@a";
                pCmd.Parameters.AddWithValue("@u", uid);
                pCmd.Parameters.AddWithValue("@a", audioId);
                authorized = pCmd.ExecuteScalar() != null;
            }
        }
        finally { ReturnDb(db); }

        if (!authorized) { ctx.Response.Headers.CacheControl = "max-age=30"; await JsonErr(ctx, 403, "purchase required"); return; }

        string mp3 = Path.Combine(AudioDir, audioId.ToString(), "stream.mp3")
                         .Replace(Path.DirectorySeparatorChar, '/');
        if (!File.Exists(mp3)) { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 404, "audio not ready"); return; }

        // Long cache — audio files are immutable once processed
        ctx.Response.Headers.CacheControl = "private, max-age=604800";
        ctx.Response.ContentType = "audio/mpeg";
        await Startup.DefHandle(ctx, mp3);

        // Increment play count async — fire and forget, don't block response
        _ = Task.Run(() =>
        {
            var db2 = RentDb();
            try
            {
                using var cmd = db2.CreateCommand();
                cmd.CommandText = "UPDATE audio SET plays=plays+1 WHERE id=@id";
                cmd.Parameters.AddWithValue("@id", audioId);
                cmd.ExecuteNonQuery();
            }
            finally { ReturnDb(db2); }
        });
    }

    // ── GET /music/download?id={audioId}&fmt={mp3|wav} ────────────────────
    static async Task HandleDownload(HttpContext ctx, string path)
    {
        long uid = GetUserId(ctx);
        if (uid == 0) { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 401, "not logged in"); return; }
        if (!long.TryParse(ctx.Request.Query["id"].FirstOrDefault(), out long audioId))
        { ctx.Response.Headers.CacheControl = "max-age=600"; await JsonErr(ctx, 400, "id required"); return; }
        string fmt = ctx.Request.Query["fmt"].FirstOrDefault()?.ToLower() == "wav" ? "wav" : "mp3";

        var db = RentDb();
        bool authorized = false;
        bool hasWav = false;
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT price, owner_id, has_wav FROM audio WHERE id=@id AND active=1";
            cmd.Parameters.AddWithValue("@id", audioId);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read()) { ctx.Response.Headers.CacheControl = "max-age=300"; await JsonErr(ctx, 404, "not found"); return; }
            int price = rdr.GetInt32(0);
            long ownerId = rdr.GetInt64(1);
            hasWav = rdr.GetInt32(2) == 1;
            rdr.Close();

            if (uid == ownerId) authorized = true;
            else if (price == 0) authorized = true;
            else
            {
                using var pCmd = db.CreateCommand();
                pCmd.CommandText = "SELECT 1 FROM purchases WHERE user_id=@u AND audio_id=@a";
                pCmd.Parameters.AddWithValue("@u", uid);
                pCmd.Parameters.AddWithValue("@a", audioId);
                authorized = pCmd.ExecuteScalar() != null;
            }
        }
        finally { ReturnDb(db); }

        if (!authorized) { ctx.Response.Headers.CacheControl = "max-age=30"; await JsonErr(ctx, 403, "purchase required"); return; }

        if (fmt == "wav" && !hasWav)
        { await JsonErr(ctx, 404, "wav not available for this track"); return; }

        string fileName = fmt == "wav" ? "original.wav" : "stream.mp3";
        string filePath = Path.Combine(AudioDir, audioId.ToString(), fileName)
                              .Replace(Path.DirectorySeparatorChar, '/');

        if (!File.Exists(filePath)) { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 404, "file not ready"); return; }

        ctx.Response.Headers.CacheControl = "private, max-age=604800";
        ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{audioId}.{fmt}\"";
        ctx.Response.ContentType = fmt == "wav" ? "audio/wav" : "audio/mpeg";
        await Startup.DefHandle(ctx, filePath);
    }

    // ── GET /music/browse?page=0&sort=new|top|promoted ───────────────────
    static Task HandleBrowse(HttpContext ctx, string path)
    {
        int page = int.TryParse(ctx.Request.Query["page"].FirstOrDefault(), out var pg) ? Math.Max(0, pg) : 0;
        string sort = ctx.Request.Query["sort"].FirstOrDefault() switch
        {
            "top" => "top",
            "promoted" => "promoted",
            _ => "new"
        };
        const int PageSize = 24;

        string orderBy = sort switch
        {
            "top" => "plays DESC",
            "promoted" => "promoted DESC, promoted_until DESC",
            _ => "a.created_at DESC"
        };

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            // JOIN instead of correlated subquery — one pass, no per-row subselect
            cmd.CommandText = $@"
            SELECT a.id, a.title, u.artist_name, a.price, a.plays, a.has_wav, a.promoted
            FROM audio a
            LEFT JOIN users u ON u.id = a.owner_id
            WHERE a.active=1
            ORDER BY {orderBy}
            LIMIT {PageSize} OFFSET @off";
            cmd.Parameters.AddWithValue("@off", page * PageSize);

            ctx.Response.Headers.CacheControl = "public, max-age=30";
            ctx.Response.ContentType = "application/json";

            // Write directly to pipe — no List<>, no anonymous objects, no reflection
            // Browse array format: [page, sort, [[id,title,artist,price,plays,hasWav,promo],...]]
            var w = new Utf8JsonWriter(ctx.Response.BodyWriter);
            w.WriteStartArray();
            w.WriteNumberValue(page);
            w.WriteStringValue(sort);
            w.WriteStartArray(); // items
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                w.WriteStartArray();
                w.WriteNumberValue(rdr.GetInt64(0));                          // id
                w.WriteStringValue(rdr.GetString(1));                         // title
                w.WriteStringValue(rdr.IsDBNull(2) ? "" : rdr.GetString(2)); // artist
                w.WriteNumberValue(rdr.GetInt32(3));                          // price
                w.WriteNumberValue(rdr.GetInt64(4));                          // plays
                w.WriteNumberValue(rdr.GetInt32(5));                          // hasWav 0/1
                w.WriteNumberValue(rdr.GetInt32(6));                          // promo 0/1
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndArray();
            return w.FlushAsync(ctx.RequestAborted);
        }
        finally { ReturnDb(db); }
    }

    // ── GET /music/icon?id={audioId} ──────────────────────────────────────
    static async Task HandleIcon(HttpContext ctx, string path)
    {
        if (!long.TryParse(ctx.Request.Query["id"].FirstOrDefault(), out long audioId))
        { ctx.Response.StatusCode = 400; return; }

        string iconPath = Path.Combine(IconDir, audioId + ".jpg")
                              .Replace(Path.DirectorySeparatorChar, '/');
        if (!File.Exists(iconPath))
        {
            // Redirect to default icon
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Location = "/m/default-icon.jpg";
            return;
        }

        ctx.Response.Headers.CacheControl = "public, max-age=2592000"; // 30 days
        ctx.Response.ContentType = "image/jpeg";
        await Startup.DefHandle(ctx, iconPath);
    }

    // ── POST /music/mod/revoke ────────────────────────────────────────────
    // { audioId, refund: bool }
    static async Task HandleModRevoke(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        long uid = GetUserId(ctx);
        if (uid == 0 || !IsModDb(uid)) { await JsonErr(ctx, 403, "mod only"); return; }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var r = doc.RootElement;
        if (!r.TryGetProperty("audioId", out var aid)) { await JsonErr(ctx, 400, "audioId required"); return; }
        long audioId = aid.GetInt64();
        bool doRefund = r.TryGetProperty("refund", out var rf) && rf.GetBoolean();

        var db = RentDb();
        try
        {
            using var tx = db.BeginTransaction();
            try
            {
                // Revoke
                using var revCmd = db.CreateCommand();
                revCmd.Transaction = tx;
                revCmd.CommandText = "UPDATE audio SET active=0 WHERE id=@id";
                revCmd.Parameters.AddWithValue("@id", audioId);
                revCmd.ExecuteNonQuery();

                if (doRefund)
                {
                    // Refund all purchasers — return paid_cents to their credits
                    using var refCmd = db.CreateCommand();
                    refCmd.Transaction = tx;
                    refCmd.CommandText = @"
                        UPDATE users SET credits=credits+p.paid_cents
                        FROM purchases p
                        WHERE users.id=p.user_id AND p.audio_id=@a";
                    refCmd.Parameters.AddWithValue("@a", audioId);
                    refCmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
            ctx.Response.Headers.CacheControl = "max-age=1";
            await JsonOk(ctx, new { ok = true, refunded = doRefund });
        }
        finally { ReturnDb(db); }
    }

    // ── POST /music/mod/transfer ──────────────────────────────────────────
    // { audioId, toUserId }
    static async Task HandleModTransfer(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        long uid = GetUserId(ctx);
        if (uid == 0 || !IsModDb(uid)) { await JsonErr(ctx, 403, "mod only"); return; }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var r = doc.RootElement;
        if (!r.TryGetProperty("audioId", out var aid) ||
            !r.TryGetProperty("toUserId", out var tuid))
        { await JsonErr(ctx, 400, "audioId and toUserId required"); return; }

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE audio SET owner_id=@t WHERE id=@a";
            cmd.Parameters.AddWithValue("@t", tuid.GetInt64());
            cmd.Parameters.AddWithValue("@a", aid.GetInt64());
            int rows = cmd.ExecuteNonQuery();
            if (rows == 0) { await JsonErr(ctx, 404, "audio not found"); return; }
            ctx.Response.Headers.CacheControl = "max-age=1";
            await JsonOk(ctx, new { ok = true });
        }
        finally { ReturnDb(db); }
    }

    // ── POST /music/mod/promote ───────────────────────────────────────────
    // { audioId, days } — sets paid front-page promotion
    static async Task HandleModPromote(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        long uid = GetUserId(ctx);
        if (uid == 0 || !IsModDb(uid)) { await JsonErr(ctx, 403, "mod only"); return; }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var r = doc.RootElement;
        if (!r.TryGetProperty("audioId", out var aid)) { await JsonErr(ctx, 400, "audioId required"); return; }
        int days = r.TryGetProperty("days", out var d) ? Math.Clamp(d.GetInt32(), 1, 365) : 7;

        long until = DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeSeconds();
        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE audio SET promoted=1, promoted_until=@u WHERE id=@a";
            cmd.Parameters.AddWithValue("@u", until);
            cmd.Parameters.AddWithValue("@a", aid.GetInt64());
            cmd.ExecuteNonQuery();
            ctx.Response.Headers.CacheControl = "max-age=1";
            await JsonOk(ctx, new { ok = true, until });
        }
        finally { ReturnDb(db); }
    }

    // ── POST /music/audio/delete ──────────────────────────────────────────
    // Owner can delete their own audio (soft delete — keeps purchase records)
    static async Task HandleAudioDelete(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        long uid = GetUserId(ctx);
        if (uid == 0) { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 401, "not logged in"); return; }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        if (!doc.RootElement.TryGetProperty("audioId", out var aid))
        { await JsonErr(ctx, 400, "audioId required"); return; }
        long audioId = aid.GetInt64();

        var db = RentDb();
        try
        {
            using var check = db.CreateCommand();
            check.CommandText = "SELECT owner_id FROM audio WHERE id=@id";
            check.Parameters.AddWithValue("@id", audioId);
            var owner = check.ExecuteScalar();
            if (owner == null) { ctx.Response.Headers.CacheControl = "max-age=30"; await JsonErr(ctx, 404, "not found"); return; }
            if (Convert.ToInt64(owner) != uid && !IsModDb(uid))
            { await JsonErr(ctx, 403, "forbidden"); return; }

            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE audio SET active=0 WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", audioId);
            cmd.ExecuteNonQuery();
            ctx.Response.Headers.CacheControl = "max-age=1";
            await JsonOk(ctx, new { ok = true });
        }
        finally { ReturnDb(db); }
    }
    // ── GET /m/mod/name-requests — pending name change queue ─────────────────
    static async Task HandleModNameRequests(HttpContext ctx, string path)
    {
        long uid = GetUserId(ctx);
        if (uid == 0 || !IsModDb(uid)) { await JsonErr(ctx, 403, "mod only"); return; }

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"SELECT id, user_id, new_name, requested_at
                            FROM name_requests WHERE status='pending'
                            ORDER BY requested_at ASC";
            ctx.Response.Headers.CacheControl = "max-age=10";
            ctx.Response.ContentType = "application/json";
            var w = new Utf8JsonWriter(ctx.Response.BodyWriter);
            w.WriteStartArray();
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                w.WriteStartArray();
                w.WriteNumberValue(rdr.GetInt64(0));  // request id
                w.WriteNumberValue(rdr.GetInt64(1));  // user id
                w.WriteStringValue(rdr.GetString(2)); // requested name
                w.WriteNumberValue(rdr.GetInt64(3));  // timestamp
                w.WriteEndArray();
            }
            w.WriteEndArray();
            await w.FlushAsync(ctx.RequestAborted);
        }
        finally { ReturnDb(db); }
    }

    // ── GET /m/mod/user?id={userId} ───────────────────────────────────────────
    static async Task HandleModUserLookup(HttpContext ctx, string path)
    {
        long uid = GetUserId(ctx);
        if (uid == 0 || !IsModDb(uid)) { await JsonErr(ctx, 403, "mod only"); return; }
        if (!long.TryParse(ctx.Request.Query["id"].FirstOrDefault(), out long targetId))
        { await JsonErr(ctx, 400, "id required"); return; }

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
            SELECT u.id, u.email, u.artist_name, u.credits, u.is_mod,
                   (SELECT COUNT(*) FROM audio WHERE owner_id=u.id AND active=1),
                   u.created_at
            FROM users u WHERE u.id=@id";
            cmd.Parameters.AddWithValue("@id", targetId);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read()) { await JsonErr(ctx, 404, "user not found"); return; }

            ctx.Response.Headers.CacheControl = "max-age=10";
            ctx.Response.ContentType = "application/json";
            var w = new Utf8JsonWriter(ctx.Response.BodyWriter);
            w.WriteStartArray();
            w.WriteNumberValue(rdr.GetInt64(0));
            w.WriteStringValue(rdr.GetString(1));
            w.WriteStringValue(rdr.IsDBNull(2) ? "" : rdr.GetString(2));
            w.WriteNumberValue(rdr.GetInt32(3));
            w.WriteNumberValue(rdr.GetInt32(4));
            w.WriteNumberValue(rdr.GetInt64(5)); // track count
            w.WriteNumberValue(rdr.GetInt64(6)); // created_at
            w.WriteEndArray();
            await w.FlushAsync(ctx.RequestAborted);
        }
        finally { ReturnDb(db); }
    }

    // Optimized endpoints
    // Called after transcoding completes successfully, inside the Task.Run:
    static void RegisterStreamRoutes(long audioId, bool hasWav)
    {
        string mp3Path = Path.Combine(AudioDir, audioId.ToString(), "stream.mp3")
                            .Replace(Path.DirectorySeparatorChar, '/');
        string wavPath = Path.Combine(AudioDir, audioId.ToString(), "original.wav")
                            .Replace(Path.DirectorySeparatorChar, '/');

        Startup.AddToFileLead(ToKey($"/m/stream/{audioId}"), async (ctx, path) =>
        {
            long uid = GetUserId(ctx);
            if (!await CheckStreamAuth(ctx, uid, audioId)) return;
            ctx.Response.Headers.CacheControl = "private, max-age=604800";
            ctx.Response.ContentType = "audio/mpeg";
            await Startup.DefHandle(ctx, mp3Path);
            _ = Task.Run(() => IncrementPlays(audioId));
        });

        Startup.AddToFileLead(ToKey($"/m/download/{audioId}/mp3"), async (ctx, path) =>
        {
            long uid = GetUserId(ctx);
            if (!await CheckDownloadAuth(ctx, uid, audioId, false)) return;
            ctx.Response.Headers.CacheControl = "private, max-age=604800";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{audioId}.mp3\"";
            ctx.Response.ContentType = "audio/mpeg";
            await Startup.DefHandle(ctx, mp3Path);
        });

        if (hasWav)
        {
            Startup.AddToFileLead(ToKey($"/m/download/{audioId}/wav"), async (ctx, path) =>
            {
                long uid = GetUserId(ctx);
                if (!await CheckDownloadAuth(ctx, uid, audioId, true)) return;
                ctx.Response.Headers.CacheControl = "private, max-age=604800";
                ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{audioId}.wav\"";
                ctx.Response.ContentType = "audio/wav";
                await Startup.DefHandle(ctx, wavPath);
            });
        }
    }

    // Auth helpers extracted so lambdas stay lean:
    static async Task<bool> CheckStreamAuth(HttpContext ctx, long uid, long audioId)
    {
        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT price, owner_id FROM audio WHERE id=@id AND active=1";
            cmd.Parameters.AddWithValue("@id", audioId);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read())
            {
                ctx.Response.Headers.CacheControl = "max-age=600";
                await JsonErr(ctx, 404, "not found");
                return false;
            }
            int price = rdr.GetInt32(0);
            long ownerId = rdr.GetInt64(1);
            rdr.Close();

            if (price == 0 || uid == ownerId) return true;
            if (uid == 0) { await JsonErr(ctx, 403, "purchase required"); return false; }

            using var pCmd = db.CreateCommand();
            pCmd.CommandText = "SELECT 1 FROM purchases WHERE user_id=@u AND audio_id=@a";
            pCmd.Parameters.AddWithValue("@u", uid);
            pCmd.Parameters.AddWithValue("@a", audioId);
            if (pCmd.ExecuteScalar() != null) return true;

            ctx.Response.Headers.CacheControl = "max-age=30";
            await JsonErr(ctx, 403, "purchase required");
            return false;
        }
        finally { ReturnDb(db); }
    }

    static async Task<bool> CheckDownloadAuth(HttpContext ctx, long uid, long audioId, bool wav)
    {
        if (uid == 0) { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 401, "not logged in"); return false; }
        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT price, owner_id, has_wav FROM audio WHERE id=@id AND active=1";
            cmd.Parameters.AddWithValue("@id", audioId);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read()) { await JsonErr(ctx, 404, "not found"); return false; }
            int price = rdr.GetInt32(0);
            long ownerId = rdr.GetInt64(1);
            bool hasWav = rdr.GetInt32(2) == 1;
            rdr.Close();

            if (wav && !hasWav) { await JsonErr(ctx, 404, "wav not available"); return false; }
            if (uid == ownerId || price == 0) return true;

            using var pCmd = db.CreateCommand();
            pCmd.CommandText = "SELECT 1 FROM purchases WHERE user_id=@u AND audio_id=@a";
            pCmd.Parameters.AddWithValue("@u", uid);
            pCmd.Parameters.AddWithValue("@a", audioId);
            if (pCmd.ExecuteScalar() != null) return true;

            await JsonErr(ctx, 403, "purchase required");
            return false;
        }
        finally { ReturnDb(db); }
    }

    static void IncrementPlays(long audioId)
    {
        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE audio SET plays=plays+1 WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", audioId);
            cmd.ExecuteNonQuery();
        }
        finally { ReturnDb(db); }
    }

    // ── GET /m/track?id={audioId} — public track info page data ──────────────
    static Task HandleTrackInfo(HttpContext ctx, string path)
    {
        if (!long.TryParse(ctx.Request.Query["id"].FirstOrDefault(), out long audioId))
        { return JsonErr(ctx, 400, "id required"); }

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
            SELECT a.id, a.title, a.description, a.price, a.plays,
                   a.has_wav, a.created_at, a.active,
                   u.artist_name, u.id as owner_id
            FROM audio a
            JOIN users u ON u.id = a.owner_id
            WHERE a.id = @id";
            cmd.Parameters.AddWithValue("@id", audioId);
            using var rdr = cmd.ExecuteReader();
            if (!rdr.Read() || rdr.GetInt32(7) != 1)
            { ctx.Response.Headers.CacheControl = "max-age=60"; return JsonErr(ctx, 404, "not found"); }

            ctx.Response.Headers.CacheControl = "public, max-age=60";
            ctx.Response.ContentType = "application/json";

            // Array format: [id, title, desc, price, plays, hasWav, createdAt, artistName, ownerId]
            var w = new Utf8JsonWriter(ctx.Response.BodyWriter);
            w.WriteStartArray();
            w.WriteNumberValue(rdr.GetInt64(0));
            w.WriteStringValue(rdr.GetString(1));
            w.WriteStringValue(rdr.GetString(2));
            w.WriteNumberValue(rdr.GetInt32(3));
            w.WriteNumberValue(rdr.GetInt64(4));
            w.WriteNumberValue(rdr.GetInt32(5)); // hasWav 0/1
            w.WriteNumberValue(rdr.GetInt64(6)); // createdAt unix
            w.WriteStringValue(rdr.IsDBNull(8) ? "" : rdr.GetString(8));
            w.WriteNumberValue(rdr.GetInt64(9));
            w.WriteEndArray();
            return w.FlushAsync(ctx.RequestAborted);
        }
        finally { ReturnDb(db); }
    }

    // ── POST /m/library/add — add free track to library without playing ───────
    static async Task HandleLibraryAdd(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        long uid = GetUserId(ctx);
        if (uid == 0) { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 401, "not logged in"); return; }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        if (!doc.RootElement.TryGetProperty("audioId", out var aid))
        { await JsonErr(ctx, 400, "audioId required"); return; }
        long audioId = aid.GetInt64();

        var db = RentDb();
        try
        {
            using var check = db.CreateCommand();
            check.CommandText = "SELECT price FROM audio WHERE id=@id AND active=1";
            check.Parameters.AddWithValue("@id", audioId);
            var priceObj = check.ExecuteScalar();
            if (priceObj == null) { await JsonErr(ctx, 404, "not found"); return; }
            if (Convert.ToInt32(priceObj) != 0) { await JsonErr(ctx, 402, "track is not free"); return; }

            using var ins = db.CreateCommand();
            ins.CommandText = @"INSERT OR IGNORE INTO purchases (user_id, audio_id, paid_cents, purchased_at)
                            VALUES (@u, @a, 0, @t)";
            ins.Parameters.AddWithValue("@u", uid);
            ins.Parameters.AddWithValue("@a", audioId);
            ins.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            ins.ExecuteNonQuery();
            ctx.Response.Headers.CacheControl = "max-age=1";
            await JsonOk(ctx, new { ok = true });
        }
        finally { ReturnDb(db); }
    }

    // ── POST /m/artist/name-request ───────────────────────────────────────────
    // { newName } — submits for mod approval
    static async Task HandleArtistNameRequest(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        long uid = GetUserId(ctx);
        if (uid == 0) { ctx.Response.Headers.CacheControl = "max-age=10"; await JsonErr(ctx, 401, "not logged in"); return; }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        string? newName = doc.RootElement.TryGetProperty("newName", out var nn) ? nn.GetString() : null;
        if (string.IsNullOrWhiteSpace(newName) || newName.Length < 2 || newName.Length > 40)
        { await JsonErr(ctx, 400, "name must be 2-40 chars"); return; }

        var db = RentDb();
        try
        {
            // Check no pending request already exists
            using var check = db.CreateCommand();
            check.CommandText = "SELECT 1 FROM name_requests WHERE user_id=@u AND status='pending'";
            check.Parameters.AddWithValue("@u", uid);
            if (check.ExecuteScalar() != null)
            { await JsonErr(ctx, 409, "name change request already pending"); return; }

            using var ins = db.CreateCommand();
            ins.CommandText = @"INSERT INTO name_requests (user_id, new_name, requested_at)
                            VALUES (@u, @n, @t)";
            ins.Parameters.AddWithValue("@u", uid);
            ins.Parameters.AddWithValue("@n", newName.Trim());
            ins.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            ins.ExecuteNonQuery();
            ctx.Response.Headers.CacheControl = "max-age=1";
            await JsonOk(ctx, new { ok = true, msg = "Request submitted for mod approval" });
        }
        finally { ReturnDb(db); }
    }

    // ── POST /m/mod/set-artist-name ───────────────────────────────────────────
    // { requestId, approve: bool } — mod approves/rejects name change request
    static async Task HandleModSetArtistName(HttpContext ctx, string path)
    {
        if (ctx.Request.Method != "POST") { ctx.Response.StatusCode = 405; return; }
        long uid = GetUserId(ctx);
        if (uid == 0 || !IsModDb(uid)) { await JsonErr(ctx, 403, "mod only"); return; }

        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        var r = doc.RootElement;
        if (!r.TryGetProperty("requestId", out var rid)) { await JsonErr(ctx, 400, "requestId required"); return; }
        bool approve = r.TryGetProperty("approve", out var ap) && ap.GetBoolean();
        long requestId = rid.GetInt64();

        var db = RentDb();
        try
        {
            using var qCmd = db.CreateCommand();
            qCmd.CommandText = "SELECT user_id, new_name FROM name_requests WHERE id=@id AND status='pending'";
            qCmd.Parameters.AddWithValue("@id", requestId);
            using var rdr = qCmd.ExecuteReader();
            if (!rdr.Read()) { await JsonErr(ctx, 404, "request not found or already handled"); return; }
            long targetUser = rdr.GetInt64(0);
            string newName = rdr.GetString(1);
            rdr.Close();

            using var tx = db.BeginTransaction();
            try
            {
                using var statusCmd = db.CreateCommand();
                statusCmd.Transaction = tx;
                statusCmd.CommandText = "UPDATE name_requests SET status=@s WHERE id=@id";
                statusCmd.Parameters.AddWithValue("@s", approve ? "approved" : "rejected");
                statusCmd.Parameters.AddWithValue("@id", requestId);
                statusCmd.ExecuteNonQuery();

                if (approve)
                {
                    using var nameCmd = db.CreateCommand();
                    nameCmd.Transaction = tx;
                    nameCmd.CommandText = "UPDATE users SET artist_name=@n WHERE id=@u";
                    nameCmd.Parameters.AddWithValue("@n", newName);
                    nameCmd.Parameters.AddWithValue("@u", targetUser);
                    try { nameCmd.ExecuteNonQuery(); }
                    catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                    {
                        tx.Rollback();
                        ctx.Response.Headers.CacheControl = "max-age=600";
                        await JsonErr(ctx, 409, "artist name already taken");
                        return;
                    }
                }
                tx.Commit();
            }
            catch { tx.Rollback(); throw; }

            ctx.Response.Headers.CacheControl = "max-age=1";
            await JsonOk(ctx, new { ok = true, approved = approve });
        }
        finally { ReturnDb(db); }
    }
    // ── GET /m/my-tracks — returns artist's own uploads ───────────────────────
    static Task HandleMyTracks(HttpContext ctx, string path)
    {
        ctx.Response.Headers.CacheControl = "max-age=10";
        long uid = GetUserId(ctx);
        if (uid == 0) { return JsonErr(ctx, 401, "not logged in"); }

        var db = RentDb();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
            SELECT id, title, price, plays, has_wav, active, created_at, description
            FROM audio WHERE owner_id=@u ORDER BY created_at DESC";
            cmd.Parameters.AddWithValue("@u", uid);

            ctx.Response.ContentType = "application/json";

            // Array of arrays: [id, title, price, plays, hasWav, active, createdAt, desc]
            var w = new Utf8JsonWriter(ctx.Response.BodyWriter);
            w.WriteStartArray();
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                w.WriteStartArray();
                w.WriteNumberValue(rdr.GetInt64(0));
                w.WriteStringValue(rdr.GetString(1));
                w.WriteNumberValue(rdr.GetInt32(2));
                w.WriteNumberValue(rdr.GetInt64(3));
                w.WriteNumberValue(rdr.GetInt32(4)); // hasWav
                w.WriteNumberValue(rdr.GetInt32(5)); // active
                w.WriteNumberValue(rdr.GetInt64(6)); // createdAt
                w.WriteStringValue(rdr.GetString(7)); // desc
                w.WriteEndArray();
            }
            w.WriteEndArray();
            return w.FlushAsync(ctx.RequestAborted);
        }
        finally { ReturnDb(db); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    static void TryDelete(string? p)
    {
        if (!string.IsNullOrEmpty(p)) try { File.Delete(p); } catch { }
    }
}
