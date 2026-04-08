using Microsoft.AspNetCore.Http;
using System.Text.Json;
using WebServer;
public class Is_CsScript
{
    private const string HostFilePath = "/home/jon/h/";

    private enum QueryProtocol { Source, Minecraft, SAMP, FiveM, Terraria }

    private static readonly Dictionary<string, (int BasePort, QueryProtocol Protocol)> Games = new()
    {
        { "minecraft", (25562, QueryProtocol.Minecraft) },
        { "unturned",  (27012, QueryProtocol.Source) },
        { "gmod",      (27012, QueryProtocol.Source) },
        { "csgo",      (27012, QueryProtocol.Source) },
        { "rust",      (27012, QueryProtocol.Source) },
        { "svencoop",  (27012, QueryProtocol.Source) },
        { "beammp",    (27012, QueryProtocol.Source) },
        { "fivem",     (27012, QueryProtocol.FiveM) },
        { "samp",      ( 7774, QueryProtocol.SAMP) },
        { "scpsl",     ( 7774, QueryProtocol.Source) },
        { "ark",       ( 7774, QueryProtocol.Source) },
        { "sotf",      ( 7774, QueryProtocol.Source) },
        { "terraria", ( 7774, QueryProtocol.Terraria) },
    };

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(4500) };

    public static async Task Run(HttpContext context, string path)
    {
        // context.Response.Headers["Cache-Control"] = "max-age=10";
        context.Response.Headers.CacheControl = "max-age=10";
        context.Response.ContentType = "text/plain";

        if (!int.TryParse(context.Request.Query["h"], out int hostId))
        {
            await context.Response.WriteAsync("");
            return;
        }

        string? gameFilter = context.Request.Query["g"];
        if (!string.IsNullOrEmpty(gameFilter))
        {
            if (gameFilter == "Garry's Mod") gameFilter = "gmod";
            gameFilter = gameFilter.ToLowerInvariant();
        }

        string ip = "192.168.1.193";
        string game = "unturned";
        string token = "";
        int port;
        (int BasePort, QueryProtocol Protocol) gameInfo = default;

        try
        {
            string content = await File.ReadAllTextAsync(HostFilePath + hostId);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            if (root.TryGetProperty("ip", out var ipProp))
                ip = ipProp.GetString() ?? ip;

            token = root.TryGetProperty("token", out var tokenProp) ? tokenProp.GetString() ?? "" : "";
            string g = root.TryGetProperty("g", out var gProp) ? gProp.GetString() ?? "unturned" : "unturned";
            int pnum = root.TryGetProperty("pnum", out var pnumProp) ? pnumProp.GetInt32() : 0;

            if (!Games.TryGetValue(g, out gameInfo))
            {
                await context.Response.WriteAsync("");
                return;
            }
            game = g;
            port = gameInfo.BasePort + 3 * pnum;
        }
        catch
        {
            await context.Response.WriteAsync("");
            return;
        }

        if (!string.IsNullOrEmpty(gameFilter) && game != gameFilter)
        {
            await context.Response.WriteAsync("");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(9500));
        try
        {
            int players = gameInfo.Protocol switch
            {
                QueryProtocol.Source => await QuerySourceAsync(ip, port, cts.Token),
                QueryProtocol.Minecraft => await QueryMinecraftAsync(ip, port, cts.Token),
                QueryProtocol.SAMP => await QuerySampAsync(ip, port, cts.Token),
                QueryProtocol.FiveM => await QueryFiveMAsync(ip, port, cts.Token),
                QueryProtocol.Terraria => string.IsNullOrEmpty(token)
    ? throw new Exception("No token")
    : await QueryTerrariaAsync(ip, port, token, cts.Token),
                _ => throw new Exception("Unknown protocol")
            };
            await context.Response.WriteAsync(players.ToString());
        }
        catch
        {
            await context.Response.WriteAsync("");
        }
    }

    // ---- Source / A2S_INFO
    private static async Task<int> QuerySourceAsync(string ip, int port, CancellationToken ct)
    {
        int[] portsToTry = { port, port + 1 };

        foreach (int tryPort in portsToTry)
        {
            try
            {
                int result = await TryQuerySourcePort(ip, tryPort, ct);
                if (result >= 0) return result;
            }
            catch { }
        }

        throw new Exception("A2S_INFO failed on all ports");
    }

    private static async Task<int> TryQuerySourcePort(string ip, int port, CancellationToken ct)
    {
        using var udp = new System.Net.Sockets.UdpClient();
        udp.Client.ReceiveTimeout = 2000;

        byte[] challenge = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }; // default no challenge

        for (int attempt = 0; attempt < 3; attempt++)
        {
            // Build A2S_INFO packet — append challenge if we have one
            bool hasChallenge = attempt > 0;
            byte[] payload = System.Text.Encoding.UTF8.GetBytes("Source Engine Query\0");
            byte[] request = new byte[5 + payload.Length + (hasChallenge ? 4 : 0)];

            request[0] = 0xFF; request[1] = 0xFF; request[2] = 0xFF; request[3] = 0xFF;
            request[4] = 0x54;
            payload.CopyTo(request, 5);

            if (hasChallenge)
                challenge.CopyTo(request, 5 + payload.Length);

            await udp.SendAsync(request, request.Length, ip, port).WaitAsync(ct);
            var result = await udp.ReceiveAsync(ct);
            byte[] data = result.Buffer;

            if (data.Length < 5) continue;

            byte type = data[4];

            if (type == 0x41)
            {
                // Server sent challenge key — resend with it
                challenge = data[5..9];
                continue;
            }

            if (type == 0x49)
            {
                // Valid A2S_INFO response — parse player count
                int offset = 6; // skip 4-byte header + type + protocol
                for (int i = 0; i < 4; i++) // skip name, map, folder, game strings
                {
                    while (offset < data.Length && data[offset] != 0x00) offset++;
                    offset++;
                }
                offset += 2; // skip appID
                if (offset >= data.Length) throw new Exception("Response too short");
                return data[offset];
            }
        }

        throw new Exception("Too many challenge retries");
    }

    // ---- Minecraft Server List Ping (1.7+)
    private static async Task<int> QueryMinecraftAsync(string ip, int port, CancellationToken ct)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(ip, port, ct);
        var stream = tcp.GetStream();

        // Handshake packet
        using var ms = new MemoryStream();
        WriteVarInt(ms, 0x00);          // packet ID
        WriteVarInt(ms, 47);            // protocol version
        WriteString(ms, ip);            // server address
        ms.Write(BitConverter.GetBytes((ushort)port).Reverse().ToArray()); // port big-endian
        WriteVarInt(ms, 1);             // next state: status

        byte[] handshake = ms.ToArray();
        WriteVarInt(stream, handshake.Length);
        await stream.WriteAsync(handshake, ct);

        // Status request
        WriteVarInt(stream, 1);         // length
        WriteVarInt(stream, 0x00);      // packet ID

        await stream.FlushAsync(ct);

        // Read response length
        int length = ReadVarInt(stream);
        byte[] response = new byte[length];
        await stream.ReadExactlyAsync(response, ct);

        // Parse JSON from response — skip packet ID varint, then string length varint
        using var rms = new MemoryStream(response);
        ReadVarInt(rms);                // packet ID
        int strLen = ReadVarInt(rms);
        byte[] jsonBytes = new byte[strLen];
        rms.Read(jsonBytes);

        using var doc = JsonDocument.Parse(jsonBytes);
        return doc.RootElement
            .GetProperty("players")
            .GetProperty("online")
            .GetInt32();
    }

    // ---- SA-MP query protocol
    private static async Task<int> QuerySampAsync(string ip, int port, CancellationToken ct)
    {
        var ipBytes = System.Net.IPAddress.Parse(ip).GetAddressBytes();
        byte portLow = (byte)(port & 0xFF);
        byte portHigh = (byte)(port >> 8 & 0xFF);

        byte[] request = new byte[11];
        request[0] = (byte)'S'; request[1] = (byte)'A'; request[2] = (byte)'M'; request[3] = (byte)'P';
        ipBytes.CopyTo(request, 4);
        request[8] = portLow; request[9] = portHigh;
        request[10] = (byte)'i'; // info query

        using var udp = new System.Net.Sockets.UdpClient();
        udp.Client.ReceiveTimeout = 4500;
        await udp.SendAsync(request, request.Length, ip, port).WaitAsync(ct);
        var result = await udp.ReceiveAsync(ct);
        byte[] data = result.Buffer;

        // Response: 11 byte header + password(1) + players(2) + maxplayers(2) + ...
        if (data.Length < 14) throw new Exception("SAMP response too short");
        return BitConverter.ToInt16(data, 12); // current players at offset 12
    }

    // ---- FiveM HTTP query
    private static async Task<int> QueryFiveMAsync(string ip, int port, CancellationToken ct)
    {
        string url = "http://" + ip + ":" + port + "/players.json";
        string json = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetArrayLength();
    }
    private static async Task<int> QueryTerrariaAsync(string ip, int port, string token, CancellationToken ct)
    {
        string url = "http://" + ip + ":" + port + "/v2/server/status?players=true&token=" + token;
        string json = await Http.GetStringAsync(url, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "200")
            throw new Exception("Invalid Terraria status");
        return root.GetProperty("playercount").GetInt32();
    }

    // ---- Minecraft protocol helpers
    private static void WriteVarInt(Stream s, int value)
    {
        while ((value & 0xFFFFFF80) != 0)
        {
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    private static void WriteString(Stream s, string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        WriteVarInt(s, bytes.Length);
        s.Write(bytes);
    }

    private static int ReadVarInt(Stream s)
    {
        int result = 0, shift = 0;
        byte b;
        do
        {
            int next = s.ReadByte();
            if (next == -1) throw new EndOfStreamException();
            b = (byte)next;
            result |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return result;
    }
}
