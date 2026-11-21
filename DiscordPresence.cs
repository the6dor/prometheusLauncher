using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Prometheus.Launcher
{
    /// <summary>
    /// "Playing Prometheus" in Discord while a game runs: game name and picture, creator, time played, and buttons
    /// to the game page. Talks to the local Discord app over its IPC pipe; does nothing if Discord isn't running.
    /// </summary>
    internal static class DiscordPresence
    {
        // Discord application "Prometheus" (developer portal). Its art asset "logo" is Assets/flame-256.png.
        private const string AppId = "1573786511739060255";

        // Each step goes to %LOCALAPPDATA%Prometheuslogsdiscord.log, so a "nothing shows" report can be checked.
        internal static void Log(string line)
        {
            try
            {
                var dir = Path.Combine(Installer.InstallDir, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "discord.log"), DateTime.Now.ToString("s") + " " + line + Environment.NewLine);
            }
            catch (Exception e)
            {
                // Started with low permissions (some browsers do), AppData isn't writable; LocalLow always is.
                try
                {
                    var low = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "Prometheus");
                    Directory.CreateDirectory(low);
                    File.AppendAllText(Path.Combine(low, "discord.log"), DateTime.Now.ToString("s") + " (" + e.GetType().Name + ") " + line + Environment.NewLine);
                }
                catch (Exception) { }
            }
        }

        public static void ShowUntilExit(Process game, string ticket)
        {
            Log("game " + game.Id + " started");
            if (AppId.Length == 0) return;
            try
            {
                var placeId = PlaceIdFromTicket(ticket);
                if (placeId == null) { Log("no placeId in ticket"); return; }
                var activity = BuildActivity(placeId.Value);
                using (var pipe = Connect())
                {
                    if (pipe == null) { Log("Discord is not running"); return; }
                    Send(pipe, 0, new Dictionary<string, object> { ["v"] = 1, ["client_id"] = AppId });
                    Log("handshake: " + Read(pipe)); // READY, or an error such as an unknown app id
                    Send(pipe, 1, new Dictionary<string, object>
                    {
                        ["cmd"] = "SET_ACTIVITY",
                        ["args"] = new Dictionary<string, object> { ["pid"] = game.Id, ["activity"] = activity },
                        ["nonce"] = Guid.NewGuid().ToString(),
                    });
                    Log("set activity: " + Read(pipe));
                    game.WaitForExit();
                    Log("game closed"); // Discord clears the activity when this pipe closes
                }
            }
            catch (Exception e)
            {
                Log("skipped: " + e); // never get in the way of playing
            }
        }

        /// <summary>The join ticket is a JWT whose payload carries "placeId"; the signature doesn't matter here.</summary>
        internal static long? PlaceIdFromTicket(string ticket)
        {
            var parts = ticket?.Split('.');
            if (parts == null || parts.Length < 2) return null;
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var m = Regex.Match(json, "\"placeId\"\\s*:\\s*(\\d+)");
            return m.Success ? long.Parse(m.Groups[1].Value) : (long?)null;
        }

        private static Dictionary<string, object> BuildActivity(long placeId)
        {
            var baseUrl = Installer.BaseUrl;
            var json = new JavaScriptSerializer();
            string name = "a Prometheus game", builder = null, image = null;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PrometheusLauncher");
                try
                {
                    var places = json.Deserialize<List<Dictionary<string, object>>>(
                        http.GetStringAsync($"{baseUrl}/apisite/games/v1/games/multiget-place-details?placeIds={placeId}").Result);
                    if (places.Count > 0) { name = places[0]["name"] as string ?? name; builder = places[0]["builder"] as string; }
                    var thumbs = json.Deserialize<Dictionary<string, object>>(
                        http.GetStringAsync($"{baseUrl}/apisite/thumbnails/v1/assets?assetIds={placeId}&format=png&size=420x420").Result);
                    if (thumbs["data"] is System.Collections.ArrayList data && data.Count > 0 && data[0] is Dictionary<string, object> t && t["imageUrl"] is string url)
                        image = url.StartsWith("/") ? baseUrl + url : url;
                }
                catch (Exception e) { Trace.WriteLine("Game info for Discord failed: " + e.Message); }
            }
            var gamePage = $"{baseUrl}/games/{placeId}/";
            var activity = new Dictionary<string, object>
            {
                ["details"] = Limit(name),
                ["timestamps"] = new Dictionary<string, object> { ["start"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                ["assets"] = new Dictionary<string, object>
                {
                    ["large_image"] = image ?? "logo",
                    ["large_text"] = Limit(name),
                    ["small_image"] = "logo",
                    ["small_text"] = "Prometheus",
                },
                ["buttons"] = new[]
                {
                    new Dictionary<string, object> { ["label"] = "Join game", ["url"] = gamePage },
                    new Dictionary<string, object> { ["label"] = "View on Prometheus", ["url"] = baseUrl + "/" },
                },
            };
            // shortcut: no live player count yet (2021 servers don't report it); shows the creator instead.
            if (builder != null) activity["state"] = Limit("by " + builder);
            return activity;
        }

        // Discord rejects text fields over 128 characters (and under 2).
        private static string Limit(string s) => s.Length > 128 ? s.Substring(0, 125) + "..." : s.PadRight(2);

        private static NamedPipeClientStream Connect()
        {
            for (var i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut);
                try { pipe.Connect(200); return pipe; }
                catch (Exception) { pipe.Dispose(); }
            }
            return null;
        }

        private static void Send(Stream pipe, int opcode, object payload)
        {
            var body = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(payload));
            pipe.Write(BitConverter.GetBytes(opcode), 0, 4);
            pipe.Write(BitConverter.GetBytes(body.Length), 0, 4);
            pipe.Write(body, 0, body.Length);
            pipe.Flush();
        }

        private static string Read(Stream pipe)
        {
            var header = new byte[8];
            ReadExactly(pipe, header);
            var body = new byte[BitConverter.ToInt32(header, 4)];
            ReadExactly(pipe, body);
            return Encoding.UTF8.GetString(body);
        }

        private static void ReadExactly(Stream s, byte[] buffer)
        {
            for (var n = 0; n < buffer.Length;)
            {
                var r = s.Read(buffer, n, buffer.Length - n);
                if (r == 0) throw new EndOfStreamException("Discord closed the connection");
                n += r;
            }
        }
    }
}
