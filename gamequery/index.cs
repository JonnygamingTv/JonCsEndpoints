using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
// using WebServer;

public class Is_CsScript
{
    private enum QueryProtocol { Source, Minecraft, SAMP, FiveM, Terraria }
    public class ServerInfo
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? map { get; set; }
        public int numplayers { get; set; }
        public int maxplayers { get; set; }
        public bool Password { get; set; }
        public long Ping { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PlayerInfo>? Players { get; set; }
    }

    public class PlayerInfo
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Name { get; set; }
        public int Score { get; set; }
        public float Time { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly Dictionary<string, (int BasePort, QueryProtocol Protocol)> Games = new(StringComparer.OrdinalIgnoreCase)
    {
        { "minecraft", (25565, QueryProtocol.Minecraft) },
        { "unturned",  (27015, QueryProtocol.Source) },
        { "gmod",      (27015, QueryProtocol.Source) },
        { "csgo",      (27015, QueryProtocol.Source) },
        { "rust",      (27015, QueryProtocol.Source) },
        { "svencoop",  (27015, QueryProtocol.Source) },
        { "beammp",    (27015, QueryProtocol.Source) },
        { "fivem",     (27015, QueryProtocol.FiveM) },
        { "samp",      ( 7777, QueryProtocol.SAMP) },
        { "scpsl",     ( 7777, QueryProtocol.Source) },
        { "ark",       ( 7777, QueryProtocol.Source) },
        { "sotf",      ( 7777, QueryProtocol.Source) },
        { "terraria",  ( 7777, QueryProtocol.Terraria) },
    };

    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(4500) };

    public static async Task Run(HttpContext context, string path)
    {
        context.Response.Headers.CacheControl = "max-age=10";
        context.Response.ContentType = "application/json";
        try {
        string token = "";
        string? ip = context.Request.Query["ip"];
        if (string.IsNullOrEmpty(ip))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("{\"error\":\"Missing IP\"}");
            return;
        }

        string? game = context.Request.Query["g"];
        if (string.IsNullOrEmpty(game))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("{\"error\":\"Missing game\"}");
            return;
        }

        if (game == "Garry's Mod") game = "gmod";

        (int BasePort, QueryProtocol Protocol) gameInfo = default;
        if (!Games.TryGetValue(game, out gameInfo))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("{\"error\":\"Game not supported\"}");
            return;
        }

        if (!int.TryParse(context.Request.Query["p"], out int port))
            port = gameInfo.BasePort;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(9500));
        try
        {
            ServerInfo info = gameInfo.Protocol switch
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
            await context.Response.WriteAsync(JsonSerializer.Serialize(info, JsonOpts));
        }
        catch
        {
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await context.Response.WriteAsync("{\"error\":\"timeout\"}");
        }
            }catch (Exception e)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("{\"error\":\"" + e.Message.Replace("\"", "'") + "\",\"trace\":\"" + e.StackTrace?.Replace("\"", "'").Replace("\n", " ") + "\"}");
            }
        }

    // ---- Source / A2S_INFO
    private static async Task<ServerInfo> QuerySourceAsync(string ip, int port, CancellationToken ct)
    {
        int[] portsToTry = { port, port + 1 };
        foreach (int tryPort in portsToTry)
        {
            try
            {
                var result = await TryQuerySourcePort(ip, tryPort, ct);
                if (result != null) return result;
            }
            catch { }
        }
        throw new Exception("A2S_INFO failed on all ports");
    }

    private static async Task<ServerInfo?> TryQuerySourcePort(string ip, int port, CancellationToken ct)
    {
        using var udp = new System.Net.Sockets.UdpClient();
        udp.Client.ReceiveTimeout = 2000;

        byte[] challenge = { 0xFF, 0xFF, 0xFF, 0xFF };
        byte[] payload = System.Text.Encoding.UTF8.GetBytes("Source Engine Query\0");

        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool hasChallenge = attempt > 0;
            byte[] request = new byte[5 + payload.Length + (hasChallenge ? 4 : 0)];
            request[0] = 0xFF; request[1] = 0xFF; request[2] = 0xFF; request[3] = 0xFF;
            request[4] = 0x54;
            payload.CopyTo(request, 5);
            if (hasChallenge) challenge.CopyTo(request, 5 + payload.Length);

            long sent = System.Diagnostics.Stopwatch.GetTimestamp();
            await udp.SendAsync(request, request.Length, ip, port).WaitAsync(ct);
            var result = await udp.ReceiveAsync(ct);
            long ping = (System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000 / System.Diagnostics.Stopwatch.Frequency;

            byte[] data = result.Buffer;
            if (data.Length < 5) continue;

            byte type = data[4];

            if (type == 0x41)
            {
                challenge = data[5..9];
                continue;
            }

            if (type == 0x49)
            {
                int offset = 5;
                offset++; // protocol

                string name = ReadNullTermString(data, ref offset);
                string map = ReadNullTermString(data, ref offset);
                ReadNullTermString(data, ref offset); // folder
                ReadNullTermString(data, ref offset); // game

                offset += 2; // appID
                if (offset >= data.Length) throw new Exception("Response too short");

                int numPlayers = data[offset++];
                int maxPlayers = data[offset++];
                offset++; // bots
                offset++; // listentype
                offset++; // environment
                bool password = data[offset++] == 1;

                // Now query players list
                var players = await QuerySourcePlayersAsync(ip, port, ct);

                return new ServerInfo
                {
                    Name = name,
                    map = map,
                    numplayers = numPlayers,
                    maxplayers = maxPlayers,
                    Password = password,
                    Ping = ping,
                    Players = players
                };
            }
        }
        return null;
    }

    private static async Task<List<PlayerInfo>> QuerySourcePlayersAsync(string ip, int port, CancellationToken ct)
    {
        var players = new List<PlayerInfo>();
        try
        {
            using var udp = new System.Net.Sockets.UdpClient();
            udp.Client.ReceiveTimeout = 2000;

            // A2S_PLAYER request with default challenge
            byte[] request = { 0xFF, 0xFF, 0xFF, 0xFF, 0x55, 0xFF, 0xFF, 0xFF, 0xFF };

            await udp.SendAsync(request, request.Length, ip, port).WaitAsync(ct);
            var result = await udp.ReceiveAsync(ct);
            byte[] data = result.Buffer;

            if (data.Length < 6) return players;

            if (data[4] == 0x41) // challenge response
            {
                byte[] challenge = data[5..9];
                request = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x55,
                    challenge[0], challenge[1], challenge[2], challenge[3] };
                await udp.SendAsync(request, request.Length, ip, port).WaitAsync(ct);
                result = await udp.ReceiveAsync(ct);
                data = result.Buffer;
            }

            if (data.Length < 6 || data[4] != 0x44) return players;

            int count = data[5];
            int offset = 6;

            for (int i = 0; i < count && offset < data.Length; i++)
            {
                offset++; // index
                string name = ReadNullTermString(data, ref offset);
                if (offset + 8 > data.Length) break;
                int score = BitConverter.ToInt32(data, offset); offset += 4;
                float time = BitConverter.ToSingle(data, offset); offset += 4;

                players.Add(new PlayerInfo { Name = name, Score = score, Time = time });
            }
        }
        catch { }
        return players;
    }

    private static string ReadNullTermString(byte[] data, ref int offset)
    {
        int start = offset;
        while (offset < data.Length && data[offset] != 0x00) offset++;
        string s = System.Text.Encoding.UTF8.GetString(data, start, offset - start);
        offset++; // skip null terminator
        return s;
    }

    // ---- Minecraft
    private static async Task<ServerInfo> QueryMinecraftAsync(string ip, int port, CancellationToken ct)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        long sent = System.Diagnostics.Stopwatch.GetTimestamp();
        await tcp.ConnectAsync(ip, port, ct);
        var stream = tcp.GetStream();

        using var ms = new MemoryStream();
        WriteVarInt(ms, 0x00);
        WriteVarInt(ms, 47);
        WriteString(ms, ip);
        ms.Write(BitConverter.GetBytes((ushort)port).Reverse().ToArray());
        WriteVarInt(ms, 1);

        byte[] handshake = ms.ToArray();
        WriteVarInt(stream, handshake.Length);
        await stream.WriteAsync(handshake, ct);

        WriteVarInt(stream, 1);
        WriteVarInt(stream, 0x00);
        await stream.FlushAsync(ct);

        int length = ReadVarInt(stream);
        byte[] response = new byte[length];
        await stream.ReadExactlyAsync(response, ct);
        long ping = (System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000 / System.Diagnostics.Stopwatch.Frequency;

        using var rms = new MemoryStream(response);
        ReadVarInt(rms);
        int strLen = ReadVarInt(rms);
        byte[] jsonBytes = new byte[strLen];
        rms.Read(jsonBytes);

        using var doc = JsonDocument.Parse(jsonBytes);
        var root = doc.RootElement;
        var playersEl = root.GetProperty("players");

        var playerList = new List<PlayerInfo>();
        if (playersEl.TryGetProperty("sample", out var sample))
        {
            foreach (var p in sample.EnumerateArray())
                playerList.Add(new PlayerInfo { Name = p.GetProperty("name").GetString() });
        }

        return new ServerInfo
        {
            Name = root.TryGetProperty("description", out var desc)
                ? (desc.ValueKind == JsonValueKind.String ? desc.GetString() : desc.TryGetProperty("text", out var t) ? t.GetString() : null)
                : null,
            numplayers = playersEl.GetProperty("online").GetInt32(),
            maxplayers = playersEl.GetProperty("max").GetInt32(),
            Ping = ping,
            Players = playerList.Count > 0 ? playerList : null
        };
    }

    // ---- SAMP
    private static async Task<ServerInfo> QuerySampAsync(string ip, int port, CancellationToken ct)
    {
        var ipBytes = System.Net.IPAddress.Parse(ip).GetAddressBytes();
        byte portLow = (byte)(port & 0xFF);
        byte portHigh = (byte)(port >> 8 & 0xFF);

        byte[] request = new byte[11];
        request[0] = (byte)'S'; request[1] = (byte)'A'; request[2] = (byte)'M'; request[3] = (byte)'P';
        ipBytes.CopyTo(request, 4);
        request[8] = portLow; request[9] = portHigh;
        request[10] = (byte)'i';

        using var udp = new System.Net.Sockets.UdpClient();
        udp.Client.ReceiveTimeout = 4500;
        long sent = System.Diagnostics.Stopwatch.GetTimestamp();
        await udp.SendAsync(request, request.Length, ip, port).WaitAsync(ct);
        var result = await udp.ReceiveAsync(ct);
        long ping = (System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000 / System.Diagnostics.Stopwatch.Frequency;
        byte[] data = result.Buffer;

        if (data.Length < 14) throw new Exception("SAMP response too short");

        int offset = 11;
        bool password = data[offset++] == 1;
        int players = BitConverter.ToInt16(data, offset); offset += 2;
        int maxPlayers = BitConverter.ToInt16(data, offset); offset += 2;

        // server name (int32 length prefix)
        string name = "";
        if (offset + 4 <= data.Length)
        {
            int nameLen = BitConverter.ToInt32(data, offset); offset += 4;
            if (offset + nameLen <= data.Length)
            {
                name = System.Text.Encoding.UTF8.GetString(data, offset, nameLen);
                offset += nameLen;
            }
        }

        return new ServerInfo
        {
            Name = name,
            numplayers = players,
            maxplayers = maxPlayers,
            Password = password,
            Ping = ping
        };
    }

    // ---- FiveM
    private static async Task<ServerInfo> QueryFiveMAsync(string ip, int port, CancellationToken ct)
    {
        long sent = System.Diagnostics.Stopwatch.GetTimestamp();
        string playersJson = await Http.GetStringAsync("http://" + ip + ":" + port + "/players.json", ct);
        string infoJson = await Http.GetStringAsync("http://" + ip + ":" + port + "/info.json", ct);
        long ping = (System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000 / System.Diagnostics.Stopwatch.Frequency;

        using var playersDoc = JsonDocument.Parse(playersJson);
        using var infoDoc = JsonDocument.Parse(infoJson);

        var playerList = new List<PlayerInfo>();
        foreach (var p in playersDoc.RootElement.EnumerateArray())
        {
            playerList.Add(new PlayerInfo
            {
                Name = p.TryGetProperty("name", out var n) ? n.GetString() : null,
                Score = p.TryGetProperty("ping", out var ping2) ? ping2.GetInt32() : 0
            });
        }

        var vars = infoDoc.RootElement.TryGetProperty("vars", out var v) ? v : default;
        string? serverName = vars.ValueKind == JsonValueKind.Object && vars.TryGetProperty("sv_projectName", out var sn) ? sn.GetString() : null;
        int maxPlayers = vars.ValueKind == JsonValueKind.Object && vars.TryGetProperty("sv_maxClients", out var mc)
            ? (mc.ValueKind == JsonValueKind.String ? int.Parse(mc.GetString()!) : mc.GetInt32())
            : 0;

        return new ServerInfo
        {
            Name = serverName,
            numplayers = playerList.Count,
            maxplayers = maxPlayers,
            Ping = ping,
            Players = playerList.Count > 0 ? playerList : null
        };
    }

    // ---- Terraria
    private static async Task<ServerInfo> QueryTerrariaAsync(string ip, int port, string token, CancellationToken ct)
    {
        long sent = System.Diagnostics.Stopwatch.GetTimestamp();
        string json = await Http.GetStringAsync("http://" + ip + ":" + port + "/v2/server/status?players=true&token=" + token, ct);
        long ping = (System.Diagnostics.Stopwatch.GetTimestamp() - sent) * 1000 / System.Diagnostics.Stopwatch.Frequency;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "200")
            throw new Exception("Invalid Terraria status");

        var playerList = new List<PlayerInfo>();
        if (root.TryGetProperty("players", out var players))
            foreach (var p in players.EnumerateArray())
                playerList.Add(new PlayerInfo { Name = p.TryGetProperty("nickname", out var n) ? n.GetString() : null });

        return new ServerInfo
        {
            Name = root.TryGetProperty("name", out var name) ? name.GetString() : null,
            numplayers = root.GetProperty("playercount").GetInt32(),
            maxplayers = root.TryGetProperty("maxplayers", out var mp) ? mp.GetInt32() : 0,
            Ping = ping,
            Players = playerList.Count > 0 ? playerList : null
        };
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
