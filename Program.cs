using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Prometheus.Launcher
{
    internal static class Program
    {
        // Prometheus "Forge" palette (same values as the website).
        internal static readonly Color Background = Color.FromArgb(0x23, 0x23, 0x21);
        internal static readonly Color Track = Color.FromArgb(0x3d, 0x3d, 0x3a);
        internal static readonly Color Red = Color.FromArgb(0xbc, 0x3d, 0x39);
        internal static readonly Color Text = Color.FromArgb(0xf3, 0xf1, 0xea);

        /// <summary>
        /// No arguments: install (or repair) Prometheus for this Windows user.
        /// "prometheus-player://join?year=2021&amp;ticket=...": opened by the website's Play button.
        /// "--preview file.png": draw the window for design checks.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            DiscordPresence.Log("start: " + string.Join(" ", args).Split(new[] { "ticket=" }, StringSplitOptions.None)[0]);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length == 2 && args[0] == "--preview")
            {
                using (var window = new ConnectingWindow("Connecting to Prometheus ...", _ => Task.FromResult<Process>(null)))
                using (var bmp = new Bitmap(window.Width, window.Height))
                {
                    window.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
                    bmp.Save(args[1]);
                }
                return 0;
            }

            if (args.Length >= 1 && args[0].StartsWith(Installer.Scheme + ":", StringComparison.OrdinalIgnoreCase))
                return Play(args[0]);
            return Install();
        }

        private static int Install()
        {
            Application.Run(new ConnectingWindow("Installing Prometheus ...", async window =>
            {
                Installer.InstallSelf();
                await Installer.EnsureClient("2021", window.Report);
                window.Report("Prometheus is ready", 1f);
                MessageBox.Show(window, "Prometheus is installed. Open a game on the website and press Play.", "Prometheus", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }));
            return 0;
        }

        /// <summary>"prometheus-player://studio?year=2021": install or update that year's Studio, add a Start menu entry, open it.</summary>
        private static int Studio(string year)
        {
            if (year != "2021")
            {
                MessageBox.Show("Studio " + (year ?? "") + " is coming soon.", "Prometheus", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            var opening = new ConnectingWindow("Opening Prometheus Studio ...", async window =>
            {
                Installer.InstallSelf();
                var exe = await Installer.EnsureClient(year + "-studio", window.Report);
                try { Installer.StudioShortcut(year, exe); } catch (Exception e) { DiscordPresence.Log("studio shortcut skipped: " + e.Message); }
                window.Report("Opening Prometheus Studio ...", null);
                return Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = System.IO.Path.GetDirectoryName(exe), UseShellExecute = false });
            });
            Application.Run(opening);
            Installer.UpdateSelf();
            return 0;
        }

        private static int Play(string link)
        {
            DiscordPresence.Log("play: launcher " + System.IO.File.GetLastWriteTime(Application.ExecutablePath).ToString("s") + " from " + Application.ExecutablePath);
            var query = ParseQuery(link);
            query.TryGetValue("ticket", out var ticket);
            query.TryGetValue("year", out var year);
            if (link.StartsWith(Installer.Scheme + "://studio", StringComparison.OrdinalIgnoreCase)) return Studio(year);
            if (string.IsNullOrEmpty(ticket))
            {
                MessageBox.Show("This Play link is incomplete. Press Play on the website again.", "Prometheus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            if (year != "2021")
            {
                MessageBox.Show("This game needs the " + (year ?? "older") + " player, which is coming soon.", "Prometheus", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }

            var connecting = new ConnectingWindow("Connecting to Prometheus ...", async window =>
            {
                Installer.InstallSelf(); // keeps the Play link pointing at this launcher
                var exe = await Installer.EnsureClient(year, window.Report);
                DiscordPresence.Log("play: starting " + exe);
                window.Report("Connecting to Prometheus ...", null);
                // The server address is built into the launcher (Installer.BaseUrl), never taken from the link,
                // so a link can't point the client at someone else's server.
                var baseUrl = Installer.BaseUrl;
                return Process.Start(new ProcessStartInfo(exe)
                {
                    Arguments = $"-a \"{baseUrl}/login/negotiate.ashx\" -j \"{baseUrl}/game/PlaceLauncher.ashx?ticket={Uri.EscapeDataString(ticket)}\" -t \"{ticket}\"",
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                });
            });
            Application.Run(connecting);
            Installer.UpdateSelf(); // after the game started, so updating never delays playing
            // The window closes once the game is up; stay in the background for Discord until the game closes.
            if (connecting.Game is Process game && !game.HasExited) DiscordPresence.ShowUntilExit(game, ticket);
            else DiscordPresence.Log("no running game after the window closed");
            return 0;
        }

        private static System.Collections.Generic.Dictionary<string, string> ParseQuery(string link)
        {
            var result = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var q = link.IndexOf('?');
            if (q < 0) return result;
            foreach (var pair in link.Substring(q + 1).Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0) result[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
            }
            return result;
        }
    }

    /// <summary>The themed window shown while installing, updating or starting the game.</summary>
    internal sealed class ConnectingWindow : Form
    {
        private readonly Func<ConnectingWindow, Task<Process>> work;
        private readonly Timer timer = new Timer { Interval = 16 };
        private readonly Image logo;
        private Process game;
        public Process Game => game;
        private string status;
        private float? progress; // null = sliding block, else 0..1 filled bar
        private float slide;
        private DateTime started;

        public ConnectingWindow(string status, Func<ConnectingWindow, Task<Process>> work)
        {
            this.status = status;
            this.work = work;
            Text = "Prometheus";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            ClientSize = new Size(650, 400);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Program.Background;
            DoubleBuffered = true;
            using (var s = typeof(ConnectingWindow).Assembly.GetManifestResourceStream("flame.png"))
                logo = Image.FromStream(s);
            timer.Tick += (_, __) => Step();
        }

        /// <summary>Thread-safe status update; fraction null shows the sliding block.</summary>
        public void Report(string text, float? fraction)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Report(text, fraction))); return; }
            status = text;
            progress = fraction;
            Invalidate();
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Dark title bar to match the window (Windows 10 2004+ / 11; older versions ignore it).
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            timer.Start();
            try
            {
                game = await work(this);
                started = DateTime.UtcNow;
                if (game == null) Close();
            }
            catch (Exception ex)
            {
                timer.Stop();
                MessageBox.Show(this, ex.Message, "Prometheus", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void Step()
        {
            slide = (slide + 0.008f) % 1.3f;
            Invalidate();
            if (game == null) return;
            game.Refresh();
            if (game.HasExited)
            {
                timer.Stop();
                MessageBox.Show(this, "The game closed while starting. Press Play on the website again.", "Prometheus", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Close();
            }
            // The game window exists: our job is done.
            else if (game.MainWindowHandle != IntPtr.Zero || DateTime.UtcNow - started > TimeSpan.FromSeconds(60))
            {
                timer.Stop();
                Close();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var w = ClientSize.Width;

            g.DrawImage(logo, new Rectangle((w - 140) / 2, 62, 140, 140));

            using (var font = new Font("Segoe UI", 13f))
            using (var brush = new SolidBrush(Program.Text))
            {
                var size = g.MeasureString(status, font);
                g.DrawString(status, font, brush, (w - size.Width) / 2, 248);
            }

            var track = new Rectangle(38, 302, w - 76, 26);
            using (var b = new SolidBrush(Program.Track)) g.FillRectangle(b, track);
            Rectangle bar;
            if (progress is float p)
                bar = new Rectangle(track.Left + 3, track.Top + 3, (int)((track.Width - 6) * Math.Max(0, Math.Min(1, p))), track.Height - 6);
            else
            {
                // Red block sliding across, like Roblox's launcher bar.
                var block = (int)(track.Width * 0.3f);
                var x = track.Left + (int)((track.Width + block) * slide) - block;
                bar = Rectangle.Intersect(track, new Rectangle(x, track.Top + 3, block, track.Height - 6));
            }
            if (bar.Width > 0)
                using (var b = new SolidBrush(Program.Red)) g.FillRectangle(b, bar);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Dispose();
            logo.Dispose();
            base.OnFormClosed(e);
        }
    }
}
