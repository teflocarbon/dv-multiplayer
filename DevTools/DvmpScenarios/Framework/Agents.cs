using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Dvmp.Scenarios
{
    /// <summary>One game's dev agent, found the way dvmp finds it: its discovery file in
    /// %LOCALAPPDATA%\dvmp\agents (DvmpAgent/Discovery.cs) and its /v1/status.</summary>
    internal sealed class GameAgent
    {
        private static readonly HttpClient http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        public int Pid { get; private set; }
        public int Port { get; private set; }
        private string token;
        public string Role { get; private set; }
        public string LaunchId { get; private set; }
        public bool Dedicated { get; private set; }
        /// <summary>The scenario assembly this game has loaded, by hash, for this launch.</summary>
        public string LoadedHash { get; set; }

        public static string AgentsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dvmp", "agents");

        /// <summary>The session's games that dvmp launched from the install it points at, with a role.
        /// A game the user started, or one from another install, is never touched.</summary>
        public static async Task<List<GameAgent>> DiscoverAsync(string session, string gameDir)
        {
            List<GameAgent> found = new();
            if (!Directory.Exists(AgentsDir)) return found;
            foreach (string file in Directory.GetFiles(AgentsDir, "agent-*.json"))
            {
                JObject info;
                try { info = JObject.Parse(File.ReadAllText(file)); }
                catch { continue; }
                string[] args = info["args"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                if (Array.IndexOf(args, "-dvmp-role") < 0) continue;
                int s = Array.IndexOf(args, "-dvmp-session");
                if ((s >= 0 && s + 1 < args.Length ? args[s + 1] : "default") != session) continue;
                int pid = info.Value<int>("pid");
                if (!FromInstall(pid, gameDir)) continue;
                GameAgent agent = new() { Pid = pid, Port = info.Value<int>("port"), token = info.Value<string>("token"), Dedicated = args.Contains("-mp-dedicated") };
                try
                {
                    JObject status = await agent.CallAsync("/v1/status", new JObject(), TimeSpan.FromSeconds(10));
                    agent.Role = status.Value<string>("role");
                    agent.LaunchId = status.Value<string>("launchId");
                    found.Add(agent);
                }
                catch (HarnessException) { }
            }
            return found;
        }

        private static bool FromInstall(int pid, string gameDir)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (p.HasExited) return false;
                string exe = p.MainModule?.FileName;
                return exe == null || string.Equals(Path.GetFullPath(Path.GetDirectoryName(exe)).TrimEnd('\\'),
                    Path.GetFullPath(gameDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }   // gone
            catch { return true; }                          // can't read its path: as dvmp does
        }

        public async Task<JObject> CallAsync(string path, JObject body, TimeSpan timeout)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, $"http://127.0.0.1:{Port}{path}")
            {
                Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Task<HttpResponseMessage> send = http.SendAsync(request);
            if (await Task.WhenAny(send, Task.Delay(timeout)) != send)
                throw new HarnessException($"{Role ?? "game"} #{Pid}: no answer to {path} within {timeout.TotalSeconds:0} s");
            HttpResponseMessage response;
            try { response = await send; }
            catch (HttpRequestException e) { throw new HarnessException($"{Role ?? "game"} #{Pid}: {e.InnerException?.Message ?? e.Message}"); }
            string text = await response.Content.ReadAsStringAsync();
            JObject json;
            try { json = JObject.Parse(text); }
            catch { throw new HarnessException($"{Role ?? "game"} #{Pid}: HTTP {(int)response.StatusCode} from {path}: {text}"); }
            if (!response.IsSuccessStatusCode)
                throw new AgentRefused((int)response.StatusCode, json.Value<string>("error") ?? text);
            return json;
        }
    }

    /// <summary>The agent answered with an error status (404: not loaded, 409: stale install, ...).</summary>
    internal sealed class AgentRefused(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }
}
