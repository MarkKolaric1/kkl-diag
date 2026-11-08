// KKL Diag - a friendly front-end for KW1281Test (github.com/gmenounos/kw1281test).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace KklDiag
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application();
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(e.Exception.ToString(), "KKL Diag - unexpected error");
                e.Handled = true;
            };
            var ui = new Shell();
            app.Run(ui.W);
        }
    }

    public class Job
    {
        public string Address;
        public string Command;
        public string Args;
        public string Label;
        public bool Live;
        public Action<string> OnLive;
        public Action<string, Data.Outcome> OnDone;
        public int[] Bauds;
        public int BaudIndex;
        public bool Cancelled;
        public DateTime? QuitSentAt;
        public string Exe; // set by add-ons that use a different tool; null = standard KW1281Test
    }

    public partial class Shell
    {
        public Window W;
        readonly string AppDir, KwPath, LogsDir, SettingsPath;
        readonly bool fake; // developer testing: KKLDIAG_FAKE_KW points at a stand-in for kw1281test
        readonly Runner runner = new Runner();
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly DispatcherTimer portWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        readonly Dictionary<string, int> knownBaud = new Dictionary<string, int>();
        readonly StringBuilder logHistory = new StringBuilder();
        Job current;
        string page = "";
        bool navSync; // set while Show() highlights the sidebar, so that doesn't navigate again
        string lastPortSet = null;
        Action onDialogYes;

        // Named elements from Shell.xaml
        ComboBox PortBox, SpeedBox;
        ContentControl PageHost;
        ScrollViewer Scroller;
        TextBlock Crumb, PageTitle, BusyText, StatusTitle, StatusText, DlgTitle, DlgText;
        Border BusyBar;
        System.Windows.Shapes.Ellipse StatusDot;
        Grid Overlay;
        Button DlgYes, DlgNo, CancelBtn;
        RadioButton NavHome, NavModules, NavScan, NavKeys, NavLog, NavSettings;

        public Shell()
        {
            AppDir = AppDomain.CurrentDomain.BaseDirectory;
            KwPath = Path.Combine(AppDir, "kw1281test", "kw1281test.exe");
            LogsDir = Path.Combine(AppDir, "logs");
            SettingsPath = Path.Combine(AppDir, "settings.ini");
            var fakeKw = Environment.GetEnvironmentVariable("KKLDIAG_FAKE_KW");
            if (!string.IsNullOrEmpty(fakeKw)) { KwPath = fakeKw; fake = true; }

            using (var s = typeof(Shell).Assembly.GetManifestResourceStream("KklDiag.Shell.xaml"))
                W = (Window)XamlReader.Load(s);

            PortBox = Find<ComboBox>("PortBox"); SpeedBox = Find<ComboBox>("SpeedBox");
            PageHost = Find<ContentControl>("PageHost"); Scroller = Find<ScrollViewer>("Scroller");
            Crumb = Find<TextBlock>("Crumb"); PageTitle = Find<TextBlock>("PageTitle"); BusyText = Find<TextBlock>("BusyText");
            StatusTitle = Find<TextBlock>("StatusTitle"); StatusText = Find<TextBlock>("StatusText");
            DlgTitle = Find<TextBlock>("DlgTitle"); DlgText = Find<TextBlock>("DlgText");
            BusyBar = Find<Border>("BusyBar"); StatusDot = Find<System.Windows.Shapes.Ellipse>("StatusDot"); Overlay = Find<Grid>("Overlay");
            DlgYes = Find<Button>("DlgYes"); DlgNo = Find<Button>("DlgNo"); CancelBtn = Find<Button>("CancelBtn");
            NavHome = Find<RadioButton>("NavHome"); NavModules = Find<RadioButton>("NavModules"); NavScan = Find<RadioButton>("NavScan");
            NavKeys = Find<RadioButton>("NavKeys"); NavLog = Find<RadioButton>("NavLog"); NavSettings = Find<RadioButton>("NavSettings");

            NavHome.Checked += (s, e) => { if (!navSync) Show("home"); };
            NavModules.Checked += (s, e) => { if (!navSync) Show("modules"); };
            NavScan.Checked += (s, e) => { if (!navSync) Show("scan"); };
            NavKeys.Checked += (s, e) => { if (!navSync) Show("keys"); };
            NavLog.Checked += (s, e) => { if (!navSync) Show("log"); };
            NavSettings.Checked += (s, e) => { if (!navSync) Show("settings"); };

            AddonNav = Find<StackPanel>("AddonNav"); AddonsHeader = Find<TextBlock>("AddonsHeader");

            foreach (var sp in new[] { "Auto", "10400", "9600", "4800" }) SpeedBox.Items.Add(sp);
            LoadSettings();
            RegisterAddons();
            RebuildAddonNav();
            SpeedBox.SelectionChanged += (s, e) => SaveSettings();
            PortBox.SelectionChanged += (s, e) => { UpdateStatus(); SaveSettings(); };
            Find<Button>("RefreshPorts").Click += (s, e) => RefreshPorts(true);
            CancelBtn.Click += (s, e) => Cancel();
            DlgNo.Click += (s, e) => Overlay.Visibility = Visibility.Collapsed;
            DlgYes.Click += (s, e) =>
            {
                Overlay.Visibility = Visibility.Collapsed;
                var a = onDialogYes; onDialogYes = null;
                if (a != null) a();
            };

            tick.Tick += (s, e) => Tick();
            portWatch.Tick += (s, e) => RefreshPorts(false);
            portWatch.Start();
            RefreshPorts(true);

            W.Closing += (s, e) => { runner.Stop(); };
            NavHome.IsChecked = true;
        }

        T Find<T>(string name) where T : class { return (T)W.FindName(name); }

        // ===================================================================
        // UI building helpers
        // ===================================================================

        Brush Br(string key) { return key == null ? null : (Brush)W.Resources[key]; }
        Style St(string key) { return (Style)W.Resources[key]; }

        TextBlock T(string text, double size = 14, string brush = "Ink", bool bold = false)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            if (brush != null) t.Foreground = Br(brush);
            if (bold) t.FontWeight = FontWeights.SemiBold;
            return t;
        }

        TextBlock Ic(string glyph, double size = 16, string brush = null)
        {
            var t = new TextBlock { Text = glyph, FontSize = size, FontFamily = (FontFamily)W.Resources["Icons"], VerticalAlignment = VerticalAlignment.Center };
            if (brush != null) t.Foreground = Br(brush);
            return t;
        }

        static E M<E>(E el, double l, double t, double r, double b) where E : FrameworkElement
        { el.Margin = new Thickness(l, t, r, b); return el; }

        StackPanel V(params UIElement[] kids)
        {
            var p = new StackPanel();
            foreach (var k in kids) if (k != null) p.Children.Add(k);
            return p;
        }

        StackPanel H(params UIElement[] kids)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var k in kids) if (k != null) p.Children.Add(k);
            return p;
        }

        Border Card(params UIElement[] kids) { return new Border { Style = St("CardBorder"), Child = V(kids) }; }

        TextBlock Heading(string text) { return M(T(text, 16, "Ink", true), 0, 0, 0, 4); }
        TextBlock Sub(string text) { return M(T(text, 13.5, "Muted"), 0, 0, 0, 14); }

        Button Btn(string text, string style, Action click, string icon = null)
        {
            var b = new Button { Style = St(style) };
            if (icon == null) b.Content = text;
            else b.Content = H(M(Ic(icon, 13), 0, 1, 8, 0), new TextBlock { Text = text });
            b.Click += (s, e) => click();
            return b;
        }

        Border Banner(string kind, string text)
        {
            string bg, fg, icon;
            switch (kind)
            {
                case "ok": bg = "OkSoft"; fg = "Ok"; icon = ""; break;
                case "warn": bg = "WarnSoft"; fg = "Warn"; icon = ""; break;
                case "danger": bg = "DangerSoft"; fg = "Danger"; icon = ""; break;
                default: bg = "AccentSoft"; fg = "Accent"; icon = ""; break;
            }
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var ic = M(Ic(icon, 15, fg), 0, 2, 12, 0); ic.VerticalAlignment = VerticalAlignment.Top;
            var tx = T(text, 13.5, "Ink");
            Grid.SetColumn(tx, 1);
            g.Children.Add(ic); g.Children.Add(tx);
            return new Border { Background = Br(bg), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 0, 0, 12), Child = g };
        }

        Border Chip(string text, string fg, string bg)
        {
            return new Border
            {
                Background = Br(bg), CornerRadius = new CornerRadius(20), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = T(text, 12, fg, true)
            };
        }

        TextBox Field(string text, double width)
        {
            return new TextBox { Style = St("Field"), Text = text, Width = width, FontSize = 14 };
        }

        StackPanel Labeled(string label, UIElement field)
        {
            var fe = field as FrameworkElement;
            if (fe != null) fe.HorizontalAlignment = HorizontalAlignment.Left;
            return M(V(M(T(label, 12.5, "Muted", true), 0, 0, 0, 6), field), 0, 0, 20, 0);
        }

        // [-] [value] [+] number input
        StackPanel Stepper(TextBox box, int min, int max, Action<int> onStep)
        {
            Action<int> step = d =>
            {
                int v; if (!int.TryParse(box.Text, out v)) v = min;
                v = Math.Max(min, Math.Min(max, v + d));
                box.Text = v.ToString();
                if (onStep != null) onStep(d);
            };
            box.TextAlignment = TextAlignment.Center;
            var minus = Btn("", "Secondary", () => step(-1), null); minus.Content = Ic("", 12); minus.Padding = new Thickness(11, 9, 11, 9); minus.Margin = new Thickness(0, 0, 6, 0);
            var plus = Btn("", "Secondary", () => step(1), null); plus.Content = Ic("", 12); plus.Padding = new Thickness(11, 9, 11, 9); plus.Margin = new Thickness(6, 0, 0, 0);
            return H(minus, box, plus);
        }

        Border IconBubble(string glyph, string fg = "Accent", string bg = "AccentSoft", double size = 40)
        {
            return new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 4), Background = Br(bg),
                Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)W.Resources["Icons"], FontSize = size * 0.45, Foreground = Br(fg), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
        }

        Button Tile(string glyph, string title, string text, Action click)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var body = V(T(title, 15, "Ink", true), M(T(text, 13, "Muted"), 0, 3, 0, 0));
            Grid.SetColumn(body, 1);
            g.Children.Add(M(IconBubble(glyph), 0, 0, 14, 0));
            g.Children.Add(body);
            var b = new Button { Style = St("Tile"), Content = g };
            b.Click += (s, e) => click();
            return b;
        }

        void Confirm(string title, string text, string yes, bool danger, Action onYes)
        {
            DlgTitle.Text = title; DlgText.Text = text; DlgYes.Content = yes;
            DlgYes.Style = St(danger ? "DangerBtn" : "Primary");
            DlgNo.Visibility = Visibility.Visible;
            onDialogYes = onYes;
            Overlay.Visibility = Visibility.Visible;
        }

        void Alert(string title, string text)
        {
            Confirm(title, text, "OK", false, null);
            DlgNo.Visibility = Visibility.Collapsed;
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ===================================================================
        // Settings, ports, status
        // ===================================================================

        void LoadSettings()
        {
            string port = null, speed = "Auto";
            try
            {
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    var i = line.IndexOf('=');
                    if (i < 0) continue;
                    var k = line.Substring(0, i).Trim(); var v = line.Substring(i + 1).Trim();
                    if (k == "Port") port = v;
                    if (k == "Speed") speed = v;
                    ReadAddonSetting(k, v);
                }
            }
            catch { }
            SpeedBox.SelectedItem = SpeedBox.Items.Contains(speed) ? speed : "Auto";
            PortBox.Tag = port; // preferred port, applied by RefreshPorts
        }

        void SaveSettings()
        {
            try
            {
                var port = (fake ? null : SelectedPort()) ?? (PortBox.Tag as string) ?? "";
                File.WriteAllText(SettingsPath, "Port=" + port + "\r\nSpeed=" + (SpeedBox.SelectedItem ?? "Auto") + "\r\n" + AddonSettingsLines());
            }
            catch { }
        }

        string SelectedPort()
        {
            var item = PortBox.SelectedItem as ComboBoxItem;
            if (fake && (item == null || item.Tag == null)) return "COM99";
            return item == null ? null : item.Tag as string;
        }

        void RefreshPorts(bool force)
        {
            string[] names;
            try { names = SerialPort.GetPortNames().Distinct().OrderBy(n => n.Length).ThenBy(n => n).ToArray(); }
            catch { names = new string[0]; }
            var key = string.Join(",", names);
            if (!force && key == lastPortSet) return;
            lastPortSet = key;

            var friendly = new Dictionary<string, string>();
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                    foreach (ManagementObject o in q.Get())
                    {
                        var n = (o["Name"] ?? "").ToString();
                        var a = n.LastIndexOf("(COM", StringComparison.Ordinal);
                        if (a >= 0) friendly[n.Substring(a + 1).TrimEnd(')')] = n.Substring(0, a).Trim();
                    }
            }
            catch { }

            var want = SelectedPort() ?? (PortBox.Tag as string);
            PortBox.Items.Clear();
            foreach (var n in names)
            {
                string f;
                var label = friendly.TryGetValue(n, out f) ? n + "  —  " + f : n;
                PortBox.Items.Add(new ComboBoxItem { Content = label, Tag = n });
            }
            if (names.Length == 0)
                PortBox.Items.Add(new ComboBoxItem { Content = "No cable found", Tag = null, IsEnabled = false });

            ComboBoxItem pick = PortBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == want);
            if (pick == null) pick = PortBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Content as string ?? "").Contains("USB Serial"));
            if (pick == null) pick = PortBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag != null);
            if (pick == null) pick = (ComboBoxItem)PortBox.Items[0];
            PortBox.SelectedItem = pick;
            UpdateStatus();
            if (page == "home") Show("home");
        }

        void UpdateStatus()
        {
            var port = SelectedPort();
            if (port == null)
            {
                StatusDot.Fill = Br("Muted"); StatusTitle.Text = "No cable";
                StatusText.Text = "Plug the KKL cable into the laptop";
            }
            else
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                StatusTitle.Text = "Cable ready · " + port;
                StatusText.Text = "Speed: " + (SpeedBox.SelectedItem ?? "Auto");
            }
        }

        // ===================================================================
        // Running kw1281test
        // ===================================================================

        bool Busy { get { return current != null; } }

        bool Run(Job j)
        {
            if (Busy) { Alert("Please wait", "Another task is still running. Wait for it to finish or press Stop."); return false; }
            if (!File.Exists(KwPath)) { Alert("KW1281Test is missing", "Download kw1281test.exe from github.com/gmenounos/kw1281test/releases and put it here:\n" + KwPath); return false; }
            var port = SelectedPort();
            if (port == null) { j.OnDone("", Data.Outcome.NoCable); return false; }
            RouteJob(j);

            var speed = (SpeedBox.SelectedItem ?? "Auto").ToString();
            if (speed == "Auto")
            {
                int kb;
                j.Bauds = knownBaud.TryGetValue(j.Address, out kb)
                    ? new[] { kb }.Concat(new[] { 10400, 9600, 4800 }.Where(b => b != kb)).ToArray()
                    : new[] { 10400, 9600, 4800 };
            }
            else j.Bauds = new[] { int.Parse(speed) };
            j.BaudIndex = 0;
            current = j;
            StartAttempt();
            return true;
        }

        void StartAttempt()
        {
            var j = current;
            var args = SelectedPort() + " " + j.Bauds[j.BaudIndex] + " " + j.Address + " " + j.Command + (string.IsNullOrEmpty(j.Args) ? "" : " " + j.Args);
            logHistory.AppendLine().AppendLine("> kw1281test " + args);
            runner.Start(j.Exe ?? KwPath, LogsDir, args);
            BusyText.Text = j.Label + (j.BaudIndex > 0 ? "  (retrying at " + j.Bauds[j.BaudIndex] + " baud)" : "");
            CancelBtn.Content = j.Live ? "Stop" : "Cancel";
            BusyBar.Visibility = Visibility.Visible;
            tick.Start();
        }

        void Cancel()
        {
            var j = current;
            if (j == null) return;
            j.Cancelled = true;
            if (scanning) scanStop = true;
            if (j.Live && runner.IsRunning)
            {
                runner.SendKey(0x51, 'q'); // let kw1281test end the session politely
                j.QuitSentAt = DateTime.Now;
            }
            else runner.Stop();
        }

        public void SendKey(ushort vk, char ch) { if (current != null && current.Live) runner.SendKey(vk, ch); }

        void Tick()
        {
            var j = current;
            if (j == null) { tick.Stop(); return; }
            var text = runner.ReadScreen() ?? "";
            if (j.Live && j.OnLive != null) j.OnLive(text);
            if (page == "log") UpdateLogBox(text);
            if (j.QuitSentAt.HasValue && (DateTime.Now - j.QuitSentAt.Value).TotalSeconds > 4) runner.Stop();

            if (!runner.IsDone && runner.IsRunning) return;

            runner.Stop();
            var outcome = Data.Classify(text);
            logHistory.Append(text.Replace(Runner.DoneMarker, "").TrimEnd()).AppendLine();
            if (outcome == Data.Outcome.WrongSpeed && !j.Cancelled && j.BaudIndex + 1 < j.Bauds.Length)
            {
                j.BaudIndex++;
                StartAttempt();
                return;
            }
            if (!j.Cancelled && RetryWithAddon(j, text, outcome))
            {
                StartAttempt();
                return;
            }
            if (outcome == Data.Outcome.Ok) knownBaud[j.Address] = j.Bauds[j.BaudIndex];
            if (j.Cancelled && !j.Live) outcome = Data.Outcome.Cancelled;
            current = null;
            tick.Stop();
            BusyBar.Visibility = Visibility.Collapsed;
            if (page == "log") UpdateLogBox("");
            NoteIdent(j.Address, text);
            if (j.OnDone != null) j.OnDone(text, outcome);
        }

        // ===================================================================
        // Navigation
        // ===================================================================

        void Show(string p)
        {
            page = p;
            RadioButton nav = null;
            switch (p)
            {
                case "home": nav = NavHome; SetTitle("Start", "Home"); PageHost.Content = HomePage(); break;
                case "modules": nav = NavModules; SetTitle("Diagnose", "Control Modules"); PageHost.Content = ModulesPage(); break;
                case "module": nav = NavModules; SetTitle("Control Modules  ›  " + mod.Address, mod.Name); PageHost.Content = ModulePage(); break;
                case "scan": nav = NavScan; SetTitle("Diagnose", "Auto-Scan"); PageHost.Content = ScanPage(); break;
                case "keys": nav = NavKeys; SetTitle("Guided", "Keys & Immobilizer"); PageHost.Content = KeysPage(); break;
                case "log": nav = NavLog; SetTitle("System", "Log"); PageHost.Content = LogPage(); break;
                case "settings": nav = NavSettings; SetTitle("System", "Settings"); PageHost.Content = SettingsPage(); break;
                default:
                    if (p.StartsWith("addon:", StringComparison.Ordinal)) nav = ShowAddon(p.Substring(6));
                    break;
            }
            if (nav != null && nav.IsChecked != true) { navSync = true; nav.IsChecked = true; navSync = false; }
            Scroller.ScrollToTop();
        }

        void SetTitle(string crumb, string title) { Crumb.Text = crumb; PageTitle.Text = title; }

        // ===================================================================
        // Home
        // ===================================================================

        UIElement HomePage()
        {
            var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -12, 4) };
            tiles.Children.Add(Tile("", "Scan all modules", "Read fault codes from every module in one go.", () => Show("scan")));
            tiles.Children.Add(Tile("", "Open a control module", "Codes, live data, adaptation and more for one module.", () => Show("modules")));
            tiles.Children.Add(Tile("", "Program keys", "Step by step: teach the car a new key.", () => Show("keys")));

            var port = SelectedPort();
            int stepNo = 0;
            Func<bool, string, string, UIElement> check = (ok, title, text) =>
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                stepNo++;
                var b = ok ? IconBubble("\uE73E", "Ok", "OkSoft", 28)
                           : new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = Br("AccentSoft"),
                                          Child = new TextBlock { Text = stepNo.ToString(), FontWeight = FontWeights.SemiBold, Foreground = Br("Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                var body = V(T(title, 14, "Ink", true), T(text, 13, "Muted"));
                Grid.SetColumn(body, 1);
                g.Children.Add(M(b, 0, 0, 12, 0)); g.Children.Add(body);
                return g;
            };

            var result = new ContentControl();
            var test = Btn("Test connection", "Primary", () =>
            {
                result.Content = null;
                Run(new Job
                {
                    Address = "01", Command = "ReadIdent", Label = "Testing connection to 01 · Engine…",
                    OnDone = (text, o) =>
                    {
                        var ecu = Data.ParseValue(text, "ECU:");
                        result.Content = o == Data.Outcome.Ok
                            ? Banner("ok", "Connected! Engine module: " + (ecu ?? "answered"))
                            : Banner("danger", Data.Explain(o));
                    }
                });
            }, "");

            return V(
                M(T("What would you like to do?", 26, "Ink", true), 0, 0, 0, 4),
                M(T("For VW, Audi, Škoda and Seat from about 1996–2005 with a KKL (K-line) cable.", 14, "Muted"), 0, 0, 0, 20),
                tiles,
                Card(
                    Heading("Before you start"),
                    Sub("Three things need to be right for the car to answer."),
                    check(port != null, port != null ? "Cable found on " + port : "Plug the cable into the laptop", port != null ? "Your KKL cable is ready." : "The port appears here automatically once Windows sees the cable."),
                    check(false, "Plug the cable into the car", "The diagnostic socket is under the dashboard, near the driver's knees."),
                    check(false, "Ignition ON, engine off", "Turn the key to position II — dashboard lights on, engine not running."),
                    M(H(test), 0, 4, 0, 12),
                    result));
        }

        // ===================================================================
        // Control modules list
        // ===================================================================

        Module mod; // module currently open

        UIElement ModulesPage()
        {
            var root = V(M(T("Pick the part of the car you want to talk to. The number is the module's address (the same numbers VCDS uses).", 14, "Muted"), 0, 0, 0, 18));
            foreach (var group in Data.Modules.GroupBy(m => m.Group))
            {
                root.Children.Add(M(T(group.Key.ToUpperInvariant(), 12, "Muted", true), 0, 4, 0, 10));
                var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -12, 10) };
                foreach (var m in group)
                {
                    var mm = m;
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    var body = V(T(m.Name, 14.5, "Ink", true),
                                 H(M(new Border { Background = Br("Bg"), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1), Child = new TextBlock { Text = m.Address, FontFamily = (FontFamily)W.Resources["Mono"], FontSize = 12, Foreground = Br("Muted") } }, 0, 4, 6, 0),
                                   m.Common ? null : M(T("less common", 12, "Muted"), 0, 4, 0, 0)));
                    Grid.SetColumn(body, 1);
                    g.Children.Add(M(IconBubble(m.Icon, "Accent", "AccentSoft", 36), 0, 0, 12, 0));
                    g.Children.Add(body);
                    var b = new Button { Style = St("Tile"), Content = g };
                    b.Click += (s, e) => OpenModule(mm);
                    grid.Children.Add(b);
                }
                root.Children.Add(grid);
            }

            var addr = Field("", 90);
            Action open = () =>
            {
                var a = addr.Text.Trim().ToUpperInvariant();
                int v;
                if (!int.TryParse(a, System.Globalization.NumberStyles.HexNumber, null, out v) || v < 1 || v > 0x7F)
                { Alert("Address not valid", "Enter a module address in hex, from 01 to 7F (for example 17 or 46)."); return; }
                var known = Data.FindModule(a);
                OpenModule(known ?? new Module(v.ToString("X2"), "Module " + v.ToString("X2"), "Other", "", false));
            };
            addr.KeyDown += (s, e) => { if (e.Key == Key.Enter) open(); };
            root.Children.Add(Card(Heading("Other address"), Sub("Know the address of a module that isn't listed? Enter it here (hex, like VCDS)."),
                H(addr, M(Btn("Open", "Primary", open), 10, 0, 0, 0))));
            return root;
        }

        // ===================================================================
        // One module: header + function tabs
        // ===================================================================

        string modTab = "faults";
        readonly Dictionary<string, string> identByAddress = new Dictionary<string, string>();

        void OpenModule(Module m) { mod = m; modTab = "faults"; Show("module"); }

        void NoteIdent(string address, string text)
        {
            var ecu = Data.ParseValue(text, "ECU:");
            if (ecu != null) identByAddress[address] = ecu;
            if (ecu != null && page == "module" && mod != null && mod.Address == address && modIdent != null && !modIdent.Text.Contains("\n"))
                modIdent.Text = ecu;
        }

        TextBlock modIdent; // header line of the open module page

        UIElement ModulePage()
        {
            var identText = T("", 13, "Muted");
            modIdent = identText;
            Action refreshIdent = () =>
            {
                string id;
                identText.Text = identByAddress.TryGetValue(mod.Address, out id) ? id : "Not identified yet — it fills in automatically after the first successful action.";
            };
            refreshIdent();

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = M(V(T(mod.Title, 18, "Ink", true), M(identText, 0, 3, 0, 0)), 0, 0, 16, 0);
            Grid.SetColumn(info, 1);
            var actions = H(
                Btn("Identify", "Secondary", () => Run(new Job { Address = mod.Address, Command = "ReadIdent", Label = "Identifying " + mod.Title + "…",
                    OnDone = (text, o) =>
                    {
                        if (o != Data.Outcome.Ok) { Alert("Couldn't identify", Data.Explain(o)); return; }
                        var lines = Data.ParseLines(text, "Ident:");
                        string ecu; identByAddress.TryGetValue(mod.Address, out ecu);
                        identText.Text = string.Join("\n", new[] { ecu }.Concat(lines).Where(x => !string.IsNullOrEmpty(x)));
                    } }), ""),
                Btn("All modules", "Ghost", () => Show("modules"), ""));
            actions.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(actions, 2);
            header.Children.Add(M(IconBubble(mod.Icon, "Accent", "AccentSoft", 48), 0, 0, 16, 0));
            header.Children.Add(info);
            header.Children.Add(actions);

            var tabs = new[]
            {
                new[] { "faults", "Fault Codes", "" },
                new[] { "live", "Live Data", "" },
                new[] { "adapt", "Adaptation", "" },
                new[] { "basic", "Basic Settings", "" },
                new[] { "output", "Output Tests", "" },
                new[] { "coding", "Coding", "" },
            };
            var host = new ContentControl();
            var pills = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
            foreach (var t in tabs)
            {
                var key = t[0];
                var rb = new RadioButton { Style = St("Pill"), Content = t[1], Tag = t[2], GroupName = "modtab", IsChecked = key == modTab };
                rb.Checked += (s, e) => { modTab = key; host.Content = ModuleTab(key); };
                pills.Children.Add(rb);
            }
            host.Content = ModuleTab(modTab);

            return V(new Border { Style = St("CardBorder"), Child = header }, pills, host);
        }

        UIElement ModuleTab(string key)
        {
            switch (key)
            {
                case "faults": return FaultsTab();
                case "live": return LiveTab(false);
                case "adapt": return AdaptTab();
                case "basic": return LiveTab(true);
                case "output": return OutputTab();
                default: return CodingTab();
            }
        }

        // ----- Fault codes -----

        UIElement FaultList(List<FaultCode> codes)
        {
            var list = V();
            foreach (var f in codes)
            {
                var ff = f;
                var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var codeCol = V(new TextBlock { Text = f.Code, FontFamily = (FontFamily)W.Resources["Mono"], FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Br("Danger") },
                                f.PCode == null ? null : new TextBlock { Text = f.PCode, FontFamily = (FontFamily)W.Resources["Mono"], FontSize = 12.5, Foreground = Br("Muted") });
                var mid = V(T(f.Name, 14.5, "Ink", true),
                            M(H(f.Intermittent ? Chip("Intermittent", "Warn", "WarnSoft") : Chip("Present", "Danger", "DangerSoft"),
                                Chip("Type " + f.Type.ToString("D2"), "Muted", "Bg")), 0, 6, 0, 0));
                Grid.SetColumn(mid, 1);
                var look = Btn("Look up", "Ghost", () => OpenUrl(Data.WikiUrl(ff)), "");
                look.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(look, 2);
                g.Children.Add(codeCol); g.Children.Add(mid); g.Children.Add(look);
                list.Children.Add(new Border { Background = Brushes.White, BorderBrush = Br("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 12, 12), Margin = new Thickness(0, 0, 0, 10), Child = g });
            }
            return list;
        }

        UIElement FaultsTab()
        {
            var result = new ContentControl();
            Action read = () => Run(new Job
            {
                Address = mod.Address, Command = "ReadFaultCodes", Label = "Reading fault codes from " + mod.Title + "…",
                OnDone = (text, o) =>
                {
                    if (o != Data.Outcome.Ok || !Data.HasFaultList(text)) { result.Content = Banner("danger", o == Data.Outcome.Ok ? "The module didn't send a fault list." : Data.Explain(o)); return; }
                    var codes = Data.ParseFaults(text);
                    result.Content = codes.Count == 0
                        ? (UIElement)Banner("ok", "No fault codes stored in " + mod.Title + ".")
                        : V(M(T(codes.Count + (codes.Count == 1 ? " fault code found" : " fault codes found"), 14, "Ink", true), 0, 0, 0, 10), FaultList(codes));
                }
            });
            Action clear = () => Confirm("Clear fault codes?",
                "This deletes all stored fault codes in " + mod.Title + ".\n\nClearing doesn't fix anything: if the problem is still there, the code comes back. Write the codes down first if you need them.",
                "Clear codes", true, () => Run(new Job
                {
                    Address = mod.Address, Command = "ClearFaultCodes", Label = "Clearing fault codes in " + mod.Title + "…",
                    OnDone = (text, o) =>
                    {
                        if (o != Data.Outcome.Ok) { result.Content = Banner("danger", Data.Explain(o)); return; }
                        var left = Data.ParseFaults(text);
                        result.Content = text.Contains("Fault codes cleared.") || (Data.HasFaultList(text) && left.Count == 0)
                            ? (UIElement)Banner("ok", "Fault codes cleared.")
                            : V(Banner("warn", "Some codes came straight back — the fault is still present:"), FaultList(left));
                    }
                }));

            return Card(Heading("Fault codes"), Sub("Read the fault memory of this module, and clear it once the problem is fixed."),
                M(H(Btn("Read fault codes", "Primary", read, ""), Btn("Clear fault codes", "Secondary", clear, "")), 0, 0, 0, 16),
                result);
        }

        // ----- Live data / Basic settings (same screen, different command) -----

        UIElement LiveTab(bool basic)
        {
            var groupBox = Field("1", 64);
            var status = T("Not running", 13, "Muted");
            var values = new TextBlock[4];
            var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 16, -12, 0) };
            for (int i = 0; i < 4; i++)
            {
                values[i] = new TextBlock { Text = "—", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Br("Ink"), TextWrapping = TextWrapping.Wrap };
                grid.Children.Add(new Border
                {
                    Background = Br("Bg"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 0, 12, 0),
                    Child = V(M(T("Field " + (i + 1), 12, "Muted", true), 0, 0, 0, 6), values[i])
                });
            }
            Job job = null;
            Button start = null, stop = null;
            Action<bool> running = on => { start.IsEnabled = !on; stop.IsEnabled = on; };

            start = Btn("Start", "Primary", () =>
            {
                int g;
                if (!int.TryParse(groupBox.Text, out g) || g < 1 || g > 255) { Alert("Group not valid", "Enter a group number from 1 to 255."); return; }
                job = new Job
                {
                    Address = mod.Address, Command = basic ? "BasicSetting" : "GroupRead", Args = g.ToString(), Live = true,
                    Label = (basic ? "Basic settings" : "Live data") + " · " + mod.Title,
                    OnLive = text =>
                    {
                        int grp;
                        var v = Data.ParseGroup(text, out grp);
                        if (grp < 0) { status.Text = "Connecting…"; return; }
                        groupBox.Text = grp.ToString();
                        if (v.Length == 0) { status.Text = "Group " + grp.ToString("D3") + " isn't available on this module."; for (int i = 0; i < 4; i++) values[i].Text = "—"; return; }
                        status.Text = "●  Live · group " + grp.ToString("D3");
                        for (int i = 0; i < 4; i++) values[i].Text = i < v.Length ? v[i] : "—";
                    },
                    OnDone = (text, o) => { running(false); status.Text = o == Data.Outcome.Ok ? "Stopped" : Data.Explain(o); }
                };
                if (Run(job)) running(true);
            }, "");
            stop = Btn("Stop", "Secondary", () => { if (current == job) Cancel(); }, "");
            stop.IsEnabled = false;

            // While running, +/- switch group live (kw1281test listens for arrow keys).
            var stepper = Stepper(groupBox, 1, 255, d => { if (job != null && current == job) runner.SendKey(d > 0 ? (ushort)0x26 : (ushort)0x28, '\0'); });

            return Card(
                Heading(basic ? "Basic settings" : "Live data (measuring blocks)"),
                Sub(basic
                    ? "Shows a measuring group in basic-setting mode. Some modules start a calibration in this mode (for example throttle body adaptation)."
                    : "Live sensor values, four per group. Use − / + to move between groups while it's running."),
                basic ? Banner("warn", "Only use basic settings when a repair manual tells you which group to use — it can start calibration routines.") : null,
                H(Labeled("Group", stepper), M(H(start, stop), 0, 22, 0, 0)),
                M(status, 0, 14, 0, 0),
                grid);
        }

        // ----- Adaptation -----

        UIElement AdaptTab()
        {
            var channel = Field("1", 64);
            var login = Field("", 110);
            var newValue = Field("", 110);
            var result = new ContentControl();
            Func<string> loginArg = () =>
            {
                var l = login.Text.Trim();
                int v;
                return l.Length == 0 ? "" : (int.TryParse(l, out v) && v >= 0 && v <= 65535 ? " " + v : null);
            };
            Func<int> chan = () => { int c; return int.TryParse(channel.Text, out c) && c >= 0 && c <= 99 ? c : -1; };

            Action read = () =>
            {
                var c = chan(); var l = loginArg();
                if (c < 0 || l == null) { Alert("Check the inputs", "Channel must be 0–99 and login (optional) 0–65535."); return; }
                Run(new Job
                {
                    Address = mod.Address, Command = "AdaptationRead", Args = c + l, Label = "Reading channel " + c + "…",
                    OnDone = (text, o) =>
                    {
                        var v = Data.ParseValue(text, "Adaptation value:");
                        if (o == Data.Outcome.Ok && v != null) { result.Content = Banner("info", "Channel " + c + " current value: " + v); if (newValue.Text.Length == 0) newValue.Text = v; }
                        else result.Content = Banner("danger", o == Data.Outcome.Ok ? "The module didn't return a value for this channel." : Data.Explain(o));
                    }
                });
            };
            Action<bool> write = save =>
            {
                var c = chan(); var l = loginArg(); int nv;
                if (c < 0 || l == null || !int.TryParse(newValue.Text, out nv) || nv < 0 || nv > 65535) { Alert("Check the inputs", "Channel 0–99, new value 0–65535, login (optional) 0–65535."); return; }
                Action go = () => Run(new Job
                {
                    Address = mod.Address, Command = save ? "AdaptationSave" : "AdaptationTest", Args = c + " " + nv + l,
                    Label = (save ? "Saving" : "Testing") + " channel " + c + " = " + nv + "…",
                    OnDone = (text, o) =>
                    {
                        var v = Data.ParseValue(text, "Adaptation value:");
                        result.Content = o == Data.Outcome.Ok && v != null
                            ? Banner("ok", (save ? "Saved. " : "Test value accepted (not stored). ") + "Channel " + c + " is now " + v + ".")
                            : Banner("danger", o == Data.Outcome.Ok ? "The module didn't confirm the new value." : Data.Explain(o));
                    }
                });
                if (save) Confirm("Save new value?", "Channel " + c + " in " + mod.Title + " will be set to " + nv + " permanently.\n\nMake sure you noted the old value.", "Save", true, go);
                else go();
            };

            return Card(Heading("Adaptation"), Sub("Read or change a module's stored settings by channel number. Your repair manual says which channel does what."),
                Banner("warn", "Wrong adaptation values can make parts stop working. Read and write down the current value before changing anything."),
                H(Labeled("Channel", channel), Labeled("Login / PIN (if needed)", login)),
                M(H(Btn("Read value", "Primary", read, "")), 0, 14, 0, 18),
                H(Labeled("New value", newValue), M(H(Btn("Test", "Secondary", () => write(false)), Btn("Save", "DangerBtn", () => write(true))), 0, 22, 0, 0)),
                M(result, 0, 16, 0, 0));
        }

        // ----- Output tests -----

        UIElement OutputTab()
        {
            var now = T("—", 20, "Ink", true);
            var status = T("Not running", 13, "Muted");
            Job job = null;
            Button start = null, next = null, stop = null;
            Action<bool> running = on => { start.IsEnabled = !on; next.IsEnabled = on; stop.IsEnabled = on; };
            start = Btn("Start tests", "Primary", () =>
            {
                job = new Job
                {
                    Address = mod.Address, Command = "ActuatorTest", Live = true, Label = "Output tests · " + mod.Title,
                    OnLive = text => { var a = Data.ParseActuator(text); if (a != null) { now.Text = a; status.Text = "Running — press Next for the next part."; } else status.Text = "Connecting…"; },
                    OnDone = (text, o) => { running(false); var a = Data.ParseActuator(text); if (a != null) now.Text = a; status.Text = o == Data.Outcome.Ok ? "Finished" : Data.Explain(o); }
                };
                if (Run(job)) running(true);
            }, "");
            next = Btn("Next", "Secondary", () => { if (current == job) runner.SendKey(0x4E, 'n'); }, "");
            stop = Btn("Stop", "Secondary", () => { if (current == job) Cancel(); }, "");
            next.IsEnabled = stop.IsEnabled = false;
            return Card(Heading("Output tests"), Sub("The module switches its parts on one at a time (gauges, relays, motors) so you can check they work."),
                Banner("warn", "Engine OFF. Parts will click, move or light up. Keep hands away from moving parts."),
                M(H(start, next, stop), 0, 4, 0, 18),
                T("Currently testing", 12.5, "Muted", true), M(now, 0, 4, 0, 6), status);
        }

        // ----- Coding -----

        UIElement CodingTab()
        {
            var coding = Field("", 120);
            var wsc = Field("0", 110);
            var result = new ContentControl();
            string id; identByAddress.TryGetValue(mod.Address, out id);
            Action write = () =>
            {
                int c, w;
                if (!int.TryParse(coding.Text, out c) || c < 0 || c > 32767 || !int.TryParse(wsc.Text, out w) || w < 0 || w > 99999)
                { Alert("Check the inputs", "Coding must be 0–32767 and workshop code 0–99999."); return; }
                Confirm("Write new coding?", "The software coding of " + mod.Title + " will be changed to " + c.ToString("D5") + ".\n\nA wrong coding can disable features or set fault codes. Note the current coding first (Identify shows it).",
                    "Write coding", true, () => Run(new Job
                    {
                        Address = mod.Address, Command = "SetSoftwareCoding", Args = c + " " + w, Label = "Writing coding…",
                        OnDone = (text, o) => result.Content = o == Data.Outcome.Ok ? Banner("ok", "Coding written. Use Identify to check it.") : Banner("danger", Data.Explain(o))
                    }));
            };
            return Card(Heading("Coding"), Sub("Advanced: the module's software coding tells it which equipment the car has."),
                Banner("danger", "Only change coding if you know the correct value for your car. Write down the current coding first."),
                Banner("info", "Current identification: " + (id ?? "press Identify at the top of this page.")),
                H(Labeled("New coding", coding), Labeled("Workshop code", wsc), M(Btn("Write coding", "DangerBtn", write), 0, 22, 0, 0)),
                M(result, 0, 16, 0, 0));
        }

        // ===================================================================
        // Auto-Scan
        // ===================================================================

        class ScanRow { public Module M; public ContentControl State; public ContentControl Faults; public string StateText; public List<FaultCode> Codes = new List<FaultCode>(); }
        UIElement scanPage;
        List<ScanRow> scanRows = new List<ScanRow>();
        StackPanel scanList;
        ContentControl scanSummary;
        CheckBox scanAll;
        Button scanStart, scanClear;
        bool scanning, scanStop;
        int scanIndex;

        UIElement ScanPage()
        {
            if (scanPage != null) return scanPage;
            scanList = V();
            scanSummary = new ContentControl();
            scanAll = new CheckBox { Content = "Include less common modules (slower)", Foreground = Br("Ink"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            scanStart = Btn("Start scan", "Primary", StartScan, "");
            scanClear = Btn("Clear all fault codes", "Secondary", ClearAllScanned, "");
            scanClear.Visibility = Visibility.Collapsed;
            scanPage = V(
                Card(Heading("Scan the whole car"), Sub("Asks every module for its fault codes, one after another. Modules that aren't fitted simply won't answer — that's normal. A scan takes 1–3 minutes."),
                    H(scanStart, scanClear, scanAll)),
                scanSummary, scanList);
            return scanPage;
        }

        void SetRowState(ScanRow r, string text, string fg, string bg) { r.StateText = text; r.State.Content = Chip(text, fg, bg); }

        void StartScan()
        {
            if (Busy) { Alert("Please wait", "Another task is still running."); return; }
            scanRows.Clear(); scanList.Children.Clear(); scanSummary.Content = null; scanClear.Visibility = Visibility.Collapsed;
            foreach (var m in Data.Modules.Where(x => x.Common || scanAll.IsChecked == true))
            {
                var r = new ScanRow { M = m, State = new ContentControl(), Faults = new ContentControl() };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = T(m.Title, 14.5, "Ink", true); name.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(name, 1); Grid.SetColumn(r.State, 2);
                g.Children.Add(M(IconBubble(m.Icon, "Accent", "AccentSoft", 32), 0, 0, 12, 0)); g.Children.Add(name); g.Children.Add(r.State);
                var open = new Button { Style = St("Tile"), Margin = new Thickness(0, 0, 0, 8), Content = V(g, M(r.Faults, 44, 0, 0, 0)) };
                var mm = m; open.Click += (s, e) => OpenModule(mm);
                open.ToolTip = "Open " + m.Title;
                scanList.Children.Add(open);
                SetRowState(r, "Waiting", "Muted", "Bg");
                scanRows.Add(r);
            }
            scanning = true; scanStop = false; scanIndex = 0; scanStart.IsEnabled = false;
            NextScan();
        }

        void NextScan()
        {
            if (scanStop || scanIndex >= scanRows.Count) { FinishScan(); return; }
            var r = scanRows[scanIndex];
            SetRowState(r, "Scanning…", "Accent", "AccentSoft");
            Run(new Job
            {
                Address = r.M.Address, Command = "ReadFaultCodes",
                Label = "Auto-Scan: " + r.M.Title + "  (" + (scanIndex + 1) + " of " + scanRows.Count + ")",
                OnDone = (text, o) =>
                {
                    if (o == Data.Outcome.NoCable || o == Data.Outcome.PortBusy) { scanStop = true; scanSummary.Content = Banner("danger", Data.Explain(o)); SetRowState(r, "Skipped", "Muted", "Bg"); FinishScan(); return; }
                    if (scanStop) { SetRowState(r, "Stopped", "Muted", "Bg"); FinishScan(); return; }
                    ApplyScanResult(r, text, o);
                    scanIndex++;
                    NextScan();
                }
            });
        }

        void ApplyScanResult(ScanRow r, string text, Data.Outcome o)
        {
            if (o == Data.Outcome.NoAnswer) { SetRowState(r, "No answer", "Muted", "Bg"); r.Faults.Content = null; return; }
            if (o != Data.Outcome.Ok || !Data.HasFaultList(text)) { SetRowState(r, "Couldn't read", "Warn", "WarnSoft"); return; }
            r.Codes = Data.ParseFaults(text);
            if (r.Codes.Count == 0) { SetRowState(r, "No faults", "Ok", "OkSoft"); r.Faults.Content = null; }
            else
            {
                SetRowState(r, r.Codes.Count + (r.Codes.Count == 1 ? " fault" : " faults"), "Danger", "DangerSoft");
                r.Faults.Content = M(V(r.Codes.Select(f => (UIElement)M(T(f.Code + (f.PCode != null ? " (" + f.PCode + ")" : "") + "  —  " + f.Name + (f.Intermittent ? "  · intermittent" : ""), 13, "Muted"), 0, 3, 0, 0)).ToArray()), 0, 8, 0, 0);
            }
        }

        void FinishScan()
        {
            scanning = false; scanStart.IsEnabled = true;
            foreach (var r in scanRows) if (r.StateText == "Waiting" || r.StateText == "Scanning…") SetRowState(r, "Not scanned", "Muted", "Bg");
            if (scanSummary.Content != null) return;
            var answered = scanRows.Count(r => r.StateText != "No answer" && r.StateText != "Not scanned" && r.StateText != "Stopped" && r.StateText != "Skipped");
            var withFaults = scanRows.Where(r => r.Codes.Count > 0).ToList();
            scanSummary.Content = withFaults.Count == 0
                ? Banner(answered > 0 ? "ok" : "warn", answered > 0 ? "Scan finished: " + answered + " modules answered, no fault codes found." : "No module answered. Check the ignition is ON and the cable is plugged into the car.")
                : Banner("warn", "Scan finished: " + answered + " modules answered, " + withFaults.Count + (withFaults.Count == 1 ? " has" : " have") + " fault codes. Click a module for details.");
            scanClear.Visibility = withFaults.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void ClearAllScanned()
        {
            var targets = scanRows.Where(r => r.Codes.Count > 0).ToList();
            if (targets.Count == 0) return;
            Confirm("Clear all fault codes?", "Fault codes will be cleared in: " + string.Join(", ", targets.Select(t => t.M.Address)) + ".\n\nCodes for problems that are still present will come back.", "Clear all", true, () =>
            {
                int i = 0;
                Action next = null;
                next = () =>
                {
                    if (i >= targets.Count) { scanSummary.Content = Banner("ok", "Done. Codes that came back are still shown above."); scanClear.Visibility = Visibility.Collapsed; return; }
                    var r = targets[i++];
                    SetRowState(r, "Clearing…", "Accent", "AccentSoft");
                    Run(new Job
                    {
                        Address = r.M.Address, Command = "ClearFaultCodes", Label = "Clearing " + r.M.Title + "…",
                        OnDone = (text, o) =>
                        {
                            if (o == Data.Outcome.Ok && text.Contains("Fault codes cleared.")) { r.Codes.Clear(); SetRowState(r, "Cleared", "Ok", "OkSoft"); r.Faults.Content = null; }
                            else ApplyScanResult(r, text, o);
                            next();
                        }
                    });
                };
                next();
            });
        }

        // ===================================================================
        // Keys & immobilizer wizard
        // ===================================================================

        int keyStep = 1;
        string keyAddr = "17";
        string keyPin = "";
        int keyCount = 2;
        DispatcherTimer countdown;
        int countdownLeft;
        TextBlock cdBig, cdSub;
        Border cdRing;

        void PaintCountdown()
        {
            if (cdBig == null) return;
            cdBig.Text = countdownLeft.ToString();
            if (countdownLeft <= 10) { cdBig.Foreground = Br("Danger"); cdRing.BorderBrush = Br("DangerSoft"); }
            if (countdownLeft == 0) cdSub.Text = "Time is up. If a key wasn't taught in time, go back and program again with all keys ready.";
        }

        UIElement KeysPage()
        {
            var steps = new[] { "Prepare", "PIN", "Program", "Teach keys", "Done" };
            var stepper = new UniformGrid { Columns = steps.Length, Margin = new Thickness(0, 0, 0, 18) };
            for (int i = 0; i < steps.Length; i++)
            {
                var n = i + 1;
                var done = n < keyStep; var now = n == keyStep;
                var bubble = new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                    Background = done ? Br("Ok") : now ? Br("Accent") : Br("Line"),
                    Child = done ? (UIElement)new TextBlock { Text = "", FontFamily = (FontFamily)W.Resources["Icons"], FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                                 : new TextBlock { Text = n.ToString(), FontWeight = FontWeights.SemiBold, Foreground = now ? Brushes.White : Br("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                stepper.Children.Add(H(bubble, M(T(steps[i], 14, now ? "Ink" : "Muted", now), 10, 0, 0, 0)));
                ((StackPanel)stepper.Children[i]).VerticalAlignment = VerticalAlignment.Center;
            }

            UIElement body;
            switch (keyStep)
            {
                case 1: body = KeyPrepare(); break;
                case 2: body = KeyPin(); break;
                case 3: body = KeyProgram(); break;
                case 4: body = KeyTeach(); break;
                default: body = KeyDone(); break;
            }
            return V(new Border { Style = St("CardBorder"), Padding = new Thickness(20, 16, 20, 16), Child = stepper }, body);
        }

        void KeyGo(int step)
        {
            if (countdown != null && step != 4) { countdown.Stop(); countdown = null; }
            keyStep = step;
            Show("keys");
        }

        UIElement Bullet(string glyph, string title, string text)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var body = V(T(title, 14, "Ink", true), text == null ? null : T(text, 13, "Muted"));
            Grid.SetColumn(body, 1);
            g.Children.Add(M(IconBubble(glyph, "Accent", "AccentSoft", 30), 0, 0, 12, 0)); g.Children.Add(body);
            return g;
        }

        UIElement KeyPrepare()
        {
            Func<string, string, string, UIElement> choice = (addr, title, text) =>
            {
                var rb = new RadioButton { GroupName = "immo", IsChecked = keyAddr == addr, Margin = new Thickness(0, 0, 0, 10), Cursor = Cursors.Hand,
                    Content = V(T(title, 14, "Ink", true), T(text, 13, "Muted")), VerticalContentAlignment = VerticalAlignment.Top };
                rb.Checked += (s, e) => keyAddr = addr;
                return rb;
            };
            return Card(Heading("Get ready"), Sub("This teaches the car's immobilizer a new key. You need:"),
                Bullet("", "The new key, with its blade already cut", "It must have a blank (unused) ID48 transponder chip."),
                Bullet("", "ALL keys you want to keep", "Every key has to be taught again now. A key you don't present stops starting the car."),
                Bullet("", "A healthy battery", "Connect a charger if the battery is weak."),
                Bullet("", "Original key in the ignition, ignition ON", "Doors closed, dashboard lights on, engine NOT running. Cable plugged in."),
                M(T("Where is the immobilizer?", 14, "Ink", true), 0, 8, 0, 8),
                choice("17", "17 · Instruments  (most cars, about 1998 onwards)", "The immobilizer is built into the instrument cluster. Start with this."),
                choice("25", "25 · Separate immobilizer box  (older cars)", "Use this if 17 doesn't answer."),
                M(H(Btn("Continue", "Primary", () => KeyGo(2), "")), 0, 10, 0, 0));
        }

        UIElement KeyPin()
        {
            var pin = Field(keyPin, 130);
            pin.FontSize = 18; pin.FontFamily = (FontFamily)W.Resources["Mono"];
            var result = new ContentControl();
            Action readPin = () => Run(new Job
            {
                Address = keyAddr, Command = "GetSKC", Label = "Reading the PIN from " + keyAddr + "…",
                OnDone = (text, o) =>
                {
                    var skc = Data.ParseValue(text, "SKC:");
                    if (o == Data.Outcome.Ok && skc != null) { pin.Text = skc; result.Content = Banner("ok", "PIN found: " + skc + ". Write it down and keep it with the car's papers."); }
                    else result.Content = Banner("danger", o == Data.Outcome.Ok
                        ? "This dashboard type isn't supported for reading the PIN. Use the PIN you got another way (Commander, dealer, locksmith)."
                        : Data.Explain(o));
                }
            });
            Action next = () =>
            {
                int v;
                var p = pin.Text.Trim();
                if (p.Length == 0 || p.Length > 5 || !int.TryParse(p, out v) || v > 65535) { Alert("PIN not valid", "Enter the immobilizer PIN: 4 digits (or 5 digits starting with 0)."); return; }
                keyPin = p; KeyGo(3);
            };
            return Card(Heading("Immobilizer PIN"), Sub("Also called login, SKC or secret key code. 4 digits; 5-digit codes start with 0 (01234 = 1234)."),
                Labeled("PIN", pin),
                M(T("Don't have it?", 14, "Ink", true), 0, 18, 0, 4),
                M(T("The app can try to read it from the dashboard. This works on many VDO and Magneti Marelli clusters, and takes up to a minute.", 13, "Muted"), 0, 0, 0, 10),
                H(Btn("Read PIN from the car", "Secondary", readPin, "")),
                M(result, 0, 14, 0, 0),
                M(H(Btn("Back", "Secondary", () => KeyGo(1)), Btn("Continue", "Primary", next, "")), 0, 10, 0, 0));
        }

        UIElement KeyProgram()
        {
            var count = Field(keyCount.ToString(), 56);
            count.FontSize = 18;
            var result = new ContentControl();
            Action program = () =>
            {
                int n;
                if (!int.TryParse(count.Text, out n) || n < 1 || n > 8) { Alert("Number of keys", "Enter 1 to 8 keys."); return; }
                keyCount = n;
                Confirm("Program " + n + (n == 1 ? " key?" : " keys?"),
                    "The car will forget all keys and accept " + n + " key" + (n == 1 ? "" : "s") + ". The key in the ignition is taught straight away; you then have 60 seconds for the others.\n\nIs the original key in the ignition and switched ON?",
                    "Program", false, () => Run(new Job
                    {
                        Address = keyAddr, Command = "AdaptationSave", Args = "21 " + n + " " + int.Parse(keyPin),
                        Label = "Logging in and setting the number of keys to " + n + "…",
                        OnDone = (text, o) =>
                        {
                            var v = Data.ParseValue(text, "Adaptation value:");
                            if (o == Data.Outcome.Ok && v != null) { KeyGo(4); return; }
                            if (o == Data.Outcome.Rejected || (o == Data.Outcome.Ok && v == null) || o == Data.Outcome.Error)
                                result.Content = Banner("danger", "The car refused. The PIN is most likely wrong.\n\nDon't try again straight away: after a wrong PIN the immobilizer locks out. Leave the ignition ON for about 30 minutes, double-check the PIN, then retry.");
                            else result.Content = Banner("danger", Data.Explain(o));
                        }
                    }));
            };
            return Card(Heading("Number of keys"), Sub("How many keys should start the car in total — including the one in the ignition right now?"),
                Stepper(count, 1, 8, null),
                M(Banner("info", "Module " + keyAddr + "  ·  PIN " + keyPin + "  ·  channel 21"), 0, 18, 0, 12),
                result,
                M(H(Btn("Back", "Secondary", () => KeyGo(2)), Btn("Program keys", "Primary", program, "")), 0, 6, 0, 0));
        }

        UIElement KeyTeach()
        {
            var big = new TextBlock { FontSize = 54, FontWeight = FontWeights.Bold, Foreground = Br("Accent"), HorizontalAlignment = HorizontalAlignment.Center };
            var sub = T("seconds left", 13, "Muted"); sub.HorizontalAlignment = HorizontalAlignment.Center;
            var ring = new Border { Width = 170, Height = 170, CornerRadius = new CornerRadius(85), BorderThickness = new Thickness(8), BorderBrush = Br("AccentSoft"), Child = V(M(big, 0, 30, 0, 0), sub) };
            if (countdown == null)
            {
                countdownLeft = 60;
                countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                countdown.Tick += (s, e) => { if (countdownLeft > 0) countdownLeft--; PaintCountdown(); if (countdownLeft == 0) countdown.Stop(); };
                countdown.Start();
            }
            cdBig = big; cdRing = ring;

            var steps = V(
                Bullet("", "Key 1 is done", "The key in the ignition was taught when you pressed Program."),
                Bullet("", "Ignition OFF, take the key out", null),
                Bullet("", "Next key in, ignition ON", "Leave it ON until the immobilizer light on the dash goes out (about 5 seconds)."),
                keyCount > 2 ? Bullet("", "Repeat for each remaining key", "Keys left after this one: " + (keyCount - 2)) : null);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(steps, 1);
            g.Children.Add(M(ring, 0, 0, 30, 0)); g.Children.Add(steps);
            cdSub = Sub("You have 60 seconds from programming to present every other key.");
            PaintCountdown();
            return Card(Heading("Teach the other keys now"), cdSub,
                g,
                M(H(Btn("Back to programming", "Secondary", () => KeyGo(3)), Btn("All keys done", "Primary", () => KeyGo(5), "")), 0, 16, 0, 0));
        }

        UIElement KeyDone()
        {
            return Card(Heading("Test every key"), Sub("Start the engine with each key."),
                Banner("ok", "Each key should start the engine, and the immobilizer light should go out after a second or two."),
                Bullet("", "A key doesn't start the car?", "Its chip may be used or not the ID48 type, or time ran out. Go back to Program and try again with all keys ready."),
                Bullet("", "Immobilizer light flashing?", "That key isn't accepted. Run Auto-Scan to see the immobilizer fault codes."),
                M(H(Btn("Program again", "Secondary", () => KeyGo(3)), Btn("Start over", "Ghost", () => { keyPin = ""; KeyGo(1); })), 0, 6, 0, 0));
        }

        // ===================================================================
        // Log & settings
        // ===================================================================

        TextBox logBox;

        UIElement LogPage()
        {
            logBox = new TextBox
            {
                IsReadOnly = true, FontFamily = (FontFamily)W.Resources["Mono"], FontSize = 12.5, Height = 470,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Br("Side"), Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)), BorderThickness = new Thickness(0), Padding = new Thickness(14)
            };
            UpdateLogBox(runner.LastScreen ?? "");
            return Card(Heading("Log"), Sub("Everything KW1281Test printed during this session. Useful if something doesn't work."),
                M(H(Btn("Copy", "Secondary", () => { try { Clipboard.SetText(logBox.Text); } catch { } }, ""),
                    Btn("Clear", "Secondary", () => { logHistory.Clear(); UpdateLogBox(""); }, ""),
                    Btn("Open log folder", "Ghost", () => { Directory.CreateDirectory(LogsDir); OpenUrl(LogsDir); }, "")), 0, 0, 0, 12),
                new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = logBox });
        }

        void UpdateLogBox(string live)
        {
            if (logBox == null) return;
            logBox.Text = logHistory.ToString() + (Busy ? live.Replace(Runner.DoneMarker, "") : "");
            logBox.ScrollToEnd();
        }

        UIElement SettingsPage()
        {
            return V(
                AddonsCard(),
                Card(Heading("Cable"), Sub("KKL cables with an FTDI chip work best. Windows shows them as \"USB Serial Port (COMx)\"."),
                    Bullet("", "Pick the port in the top bar", "It's chosen automatically when one cable is plugged in, and remembered for next time."),
                    Bullet("", "Latency timer = 1 ms", "Device Manager › Ports › USB Serial Port › Properties › Port Settings › Advanced. Helps with connection problems."),
                    Bullet("", "One program at a time", "Close VCDS-Lite, Commander or WBH-Diag before using KKL Diag — only one program can use the cable."),
                    H(Btn("Open Device Manager", "Secondary", () => OpenUrl("devmgmt.msc"), ""))),
                Card(Heading("Speed (baud)"), Sub("Auto tries 10400, then 9600, then 4800, and remembers what each module used. Only change it if a module keeps failing."),
                    Banner("info", "Current setting: " + (SpeedBox.SelectedItem ?? "Auto") + " — change it in the top bar.")),
                Card(Heading("About"), Sub("KKL Diag is a front-end for KW1281Test, the free open-source K-line tool by gmenounos. All communication with the car is done by KW1281Test."),
                    T("KW1281Test: " + KwPath, 12.5, "Muted"),
                    M(T("Fault code names come from a small built-in list; \"Look up\" opens the Ross-Tech wiki for the rest.", 12.5, "Muted"), 0, 4, 0, 14),
                    H(Btn("KW1281Test on GitHub", "Secondary", () => OpenUrl("https://github.com/gmenounos/kw1281test"), ""),
                      Btn("Open app folder", "Ghost", () => OpenUrl(AppDir), ""))));
        }
    }
}
