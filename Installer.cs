using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace Prometheus.Launcher
{
    /// <summary>Per-user install (no administrator rights): %LOCALAPPDATA%\Prometheus.</summary>
    internal static class Installer
    {
        public const string Scheme = "prometheus-player";
        private const string DefaultBaseUrl = "https://www.prmths.lol";

        public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prometheus");
        private static string InstalledExe => Path.Combine(InstallDir, "PrometheusLauncher.exe");

        /// <summary>
        /// Website the launcher talks to. A base-url.txt next to the launcher overrides it (local testing only);
        /// it is never taken from a Play link.
        /// </summary>
        public static string BaseUrl
        {
            get
            {
                foreach (var dir in new[] { Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath), InstallDir })
                {
                    var file = Path.Combine(dir, "base-url.txt");
                    if (File.Exists(file)) return File.ReadAllText(file).Trim().TrimEnd('/');
                }
                return DefaultBaseUrl;
            }
        }

        /// <summary>Copy the launcher into the install folder and point prometheus-player: links at it.</summary>
        public static void InstallSelf()
        {
            Directory.CreateDirectory(InstallDir);
            var self = System.Windows.Forms.Application.ExecutablePath;
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(self, InstalledExe, true); }
                catch (IOException) { /* an older copy is running; the next install replaces it */ }
                var devBase = Path.Combine(Path.GetDirectoryName(self), "base-url.txt");
                if (File.Exists(devBase)) File.Copy(devBase, Path.Combine(InstallDir, "base-url.txt"), true);
            }

            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme))
            {
                key.SetValue("", "URL:Prometheus Player");
                key.SetValue("URL Protocol", "");
                using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + InstalledExe + "\",0");
                using (var cmd = key.CreateSubKey(@"shell\open\command")) cmd.SetValue("", "\"" + InstalledExe + "\" \"%1\"");
            }
        }

        /// <summary>
        /// Replace the installed launcher with the website's copy when they differ; takes effect on the next start.
        /// Windows lets a running program be renamed, so the current one moves aside to .old and is deleted later.
        /// Any failure leaves the installed launcher as it was.
        /// </summary>
        public static void UpdateSelf()
        {
            var old = InstalledExe + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch (Exception) { /* still running; next time */ }
            try
            {
                byte[] latest;
                using (var http = NewClient(BaseUrl))
                    latest = http.GetByteArrayAsync(BaseUrl + "/client/PrometheusLauncher.exe").Result;
                // Only a Windows program ("MZ" header) of a sane size replaces the launcher.
                if (latest.Length < 16 * 1024 || latest.Length > 16 * 1024 * 1024 || latest[0] != 'M' || latest[1] != 'Z') return;
                if (File.Exists(InstalledExe) && File.ReadAllBytes(InstalledExe).SequenceEqual(latest)) return;
                var fresh = InstalledExe + ".new";
                File.WriteAllBytes(fresh, latest);
                if (File.Exists(InstalledExe))
                {
                    if (File.Exists(old)) return; // the previous update's old copy is still running; next time
                    File.Move(InstalledExe, old);
                }
                File.Move(fresh, InstalledExe);
            }
            catch (Exception e) { System.Diagnostics.Trace.WriteLine("Launcher update skipped: " + e.Message); }
        }

        private class Manifest
        {
            public string version { get; set; }
            public string file { get; set; }
            public string sha256 { get; set; }
            public long size { get; set; }
        }

        private static HttpClient NewClient(string baseUrl)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler();
            // Local testing uses a self-signed certificate on 127.0.0.1; the real site keeps full certificate checks.
            var host = new Uri(baseUrl).Host;
            if (host == "127.0.0.1" || host == "localhost")
                handler.ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true;
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PrometheusLauncher/0.2");
            return http;
        }

        /// <summary>
        /// Make sure the newest player for this year is installed; returns its PrometheusPlayer.exe.
        /// Downloads {base}/client/{year}/manifest.json, then the zip it names, and checks its SHA-256 before unpacking.
        /// If the site can't be reached, an already installed version is used.
        /// </summary>
        public static async Task<string> EnsureClient(string year, Action<string, float?> report)
        {
            var baseUrl = BaseUrl;
            var yearDir = Path.Combine(InstallDir, "Versions", year);
            Directory.CreateDirectory(yearDir);
            report("Checking for updates ...", null);

            Manifest manifest;
            using (var http = NewClient(baseUrl))
            {
                try
                {
                    var json = await http.GetStringAsync($"{baseUrl}/client/{year}/manifest.json");
                    manifest = new JavaScriptSerializer().Deserialize<Manifest>(json);
                }
                catch (Exception) when (Installed(yearDir) is string fallback)
                {
                    return fallback;
                }
                if (manifest?.version == null || manifest.file == null || manifest.sha256 == null || manifest.version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new Exception("The Prometheus download information is broken. Try again later.");

                var target = Path.Combine(yearDir, manifest.version);
                if (File.Exists(Path.Combine(target, ".complete"))) return PlayerExe(target);

                // Download with progress.
                var zip = Path.Combine(yearDir, manifest.version + ".zip.part");
                using (var response = await http.GetAsync($"{baseUrl}/client/{year}/files/{Uri.EscapeDataString(manifest.file)}", HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? manifest.size;
                    using (var input = await response.Content.ReadAsStreamAsync())
                    using (var output = File.Create(zip))
                    {
                        var buffer = new byte[1 << 16];
                        long done = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, read);
                            done += read;
                            if (total > 0) report($"Downloading Prometheus {year} ... {done * 100 / total}%", (float)done / total);
                        }
                    }
                }

                report("Checking the download ...", null);
                string hash;
                using (var sha = SHA256.Create())
                using (var f = File.OpenRead(zip))
                    hash = string.Concat(sha.ComputeHash(f).Select(b => b.ToString("x2")));
                if (!string.Equals(hash, manifest.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(zip);
                    throw new Exception("The download was damaged (its fingerprint doesn't match). Try again.");
                }

                report("Installing ...", null);
                var temp = target + ".tmp";
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, temp));
                File.Delete(zip);
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(temp, target);
                File.WriteAllText(Path.Combine(target, ".complete"), hash);

                // Remove older versions (skip any that are still running).
                foreach (var old in Directory.GetDirectories(yearDir).Where(d => !string.Equals(d, target, StringComparison.OrdinalIgnoreCase)))
                    try { Directory.Delete(old, true); } catch (Exception) { }
                return PlayerExe(target);
            }
        }

        // Discord spots "RobloxPlayerBeta.exe" and shows "Playing ROBLOX" next to our own presence; a copy under
        // our name avoids that. The file is byte-for-byte the same, so its published fingerprint still matches.
        private static string PlayerExe(string versionDir)
        {
            // The client reads its website address from AppSettings.xml. Rewritten whenever it differs, so an install
            // made against another address (such as a local test server) can't stay stuck on it.
            var settings = Path.Combine(versionDir, "AppSettings.xml");
            var wanted = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<Settings>\r\n\t<ContentFolder>Content</ContentFolder>\r\n\t<BaseUrl>" + BaseUrl + "</BaseUrl>\r\n</Settings>\r\n";
            if (!File.Exists(settings) || File.ReadAllText(settings) != wanted) File.WriteAllText(settings, wanted);
            var ours = Path.Combine(versionDir, "PrometheusPlayer.exe");
            if (!File.Exists(ours)) File.Copy(Path.Combine(versionDir, "RobloxPlayerBeta.exe"), ours);
            return ours;
        }

        private static string Installed(string yearDir) =>
            Directory.GetDirectories(yearDir)
                .Where(d => File.Exists(Path.Combine(d, ".complete")))
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Select(PlayerExe)
                .FirstOrDefault();
    }
}
