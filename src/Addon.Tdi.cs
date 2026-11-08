// Add-on: 1.9 TDI dashboard. Labelled live data from the engine module (01) for the
// measuring groups that matter most on EDC15 1.9 TDI engines.
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KklDiag
{
    public partial class Shell
    {
        class TdiGroup
        {
            public int Group; public string Name; public string Icon; public string[] Labels; public string Tip;
            public TdiGroup(int g, string n, string i, string[] l, string t) { Group = g; Name = n; Icon = i; Labels = l; Tip = t; }
        }

        static readonly TdiGroup[] TdiGroups =
        {
            new TdiGroup(11, "Boost", "", new[] { "Engine speed", "Boost specified", "Boost actual", "N75 valve duty" },
                "Drive in 3rd gear and floor it from about 1800 to 3500 rpm (let a passenger watch). Actual boost should follow specified within about 200 mbar. Actual clearly ABOVE specified is overboost (fault 17965): check the vacuum hoses, the N75 valve and the turbo vanes. Clearly BELOW points to a boost leak."),
            new TdiGroup(3, "EGR & air mass", "", new[] { "Engine speed", "Air mass specified", "Air mass actual", "EGR valve duty" },
                "At warm idle, actual air mass should be close to specified. Much lower than specified at higher rpm often means a dirty or failing air-mass sensor (MAF) or a stuck EGR valve."),
            new TdiGroup(1, "Engine basics", "", new[] { "Engine speed", "Injected quantity", "Fuel pump piston", "Coolant temp" },
                "Warm idle: injected quantity typically around 3–7 mg/stroke. A high idle quantity can mean internal friction, a dragging alternator or AC, or injector problems."),
            new TdiGroup(4, "Injection timing", "", new[] { "Engine speed", "Timing specified", "Timing actual", "Timing valve duty" },
                "Warm engine: actual start of injection should be close to specified. A large gap points to pump timing that needs adjusting."),
            new TdiGroup(8, "Fuel limits", "", new[] { "Engine speed", "Quantity requested", "Limit by rpm", "Limit by air mass" },
                "Shows what limits fuel delivery under load. If the air-mass limit is the lowest value at full throttle, the engine is short of air (MAF, boost leak)."),
            new TdiGroup(13, "Smooth running", "", new[] { "Cylinder 1", "Cylinder 2", "Cylinder 3", "Cylinder 4" },
                "Warm idle: values close to 0 are good. One cylinder far from the others points to an injector or compression problem on that cylinder."),
        };

        static double? Number(string value)
        {
            var m = Regex.Match(value ?? "", @"-?\d+(?:[.,]\d+)?");
            double d;
            return m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : (double?)null;
        }

        int tdiGroup = 11;

        UIElement TdiPage()
        {
            var labels = new TextBlock[4];
            var values = new TextBlock[4];
            var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 16, -12, 0) };
            for (int i = 0; i < 4; i++)
            {
                labels[i] = T("", 12.5, "Muted", true);
                values[i] = new TextBlock { Text = "—", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Br("Ink"), TextWrapping = TextWrapping.Wrap };
                grid.Children.Add(new Border
                {
                    Background = Br("Bg"), CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 0, 12, 0),
                    Child = V(M(labels[i], 0, 0, 0, 6), values[i])
                });
            }
            var status = T("Not running", 13, "Muted");
            var verdict = new ContentControl { Margin = new Thickness(0, 14, 0, 0) };
            var tip = new ContentControl();
            Job job = null;
            bool restart = false;
            Button start = null, stop = null;
            Action<bool> running = on => { start.IsEnabled = !on; stop.IsEnabled = on; };

            Action paint = () =>
            {
                var g = TdiGroups.First(x => x.Group == tdiGroup);
                for (int i = 0; i < 4; i++) { labels[i].Text = g.Labels[i]; values[i].Text = "—"; }
                tip.Content = Banner("info", g.Tip);
                verdict.Content = null;
            };

            Action go = null;
            go = () =>
            {
                job = new Job
                {
                    Address = "01", Command = "GroupRead", Args = tdiGroup.ToString(), Live = true,
                    Label = "1.9 TDI dashboard · group " + tdiGroup.ToString("D3"),
                    OnLive = text =>
                    {
                        int grp;
                        var v = Data.ParseGroup(text, out grp);
                        if (grp < 0) { status.Text = "Connecting to the engine…"; return; }
                        if (v.Length == 0) { status.Text = "This engine doesn't have group " + grp.ToString("D3") + "."; return; }
                        status.Text = "●  Live · group " + grp.ToString("D3");
                        for (int i = 0; i < 4; i++) values[i].Text = i < v.Length ? v[i] : "—";
                        if (tdiGroup == 11 && v.Length >= 3)
                        {
                            var spec = Number(v[1]); var act = Number(v[2]); var rpm = Number(v[0]);
                            if (spec.HasValue && act.HasValue && rpm.HasValue && rpm > 1500 && spec > 1200)
                            {
                                var d = act.Value - spec.Value;
                                verdict.Content = Math.Abs(d) <= 200
                                    ? Banner("ok", "Boost follows the target (" + (d >= 0 ? "+" : "") + d.ToString("0") + " mbar).")
                                    : Banner(d > 0 ? "danger" : "warn", (d > 0 ? "Overboost: " : "Underboost: ") + (d >= 0 ? "+" : "") + d.ToString("0") + " mbar against the target.");
                            }
                        }
                    },
                    OnDone = (text, o) =>
                    {
                        running(false);
                        if (restart) { restart = false; go(); return; }
                        status.Text = o == Data.Outcome.Ok || o == Data.Outcome.Cancelled ? "Stopped" : Data.Explain(o);
                    }
                };
                if (Run(job)) running(true);
            };
            start = Btn("Start", "Primary", () => go(), "");
            stop = Btn("Stop", "Secondary", () => { if (current == job) Cancel(); }, "");
            stop.IsEnabled = false;

            var pills = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            foreach (var g in TdiGroups)
            {
                var gg = g;
                var rb = new RadioButton { Style = St("Pill"), Content = g.Name, Tag = g.Icon, GroupName = "tdi", IsChecked = g.Group == tdiGroup };
                rb.Checked += (s, e) =>
                {
                    tdiGroup = gg.Group;
                    paint();
                    if (job != null && current == job) { restart = true; Cancel(); }
                };
                pills.Children.Add(rb);
            }
            paint();

            return V(
                Card(Heading("1.9 TDI dashboard"),
                     Sub("Live engine data with plain names. Pick a topic, press Start, and the app reads the right measuring group from the engine (01)."),
                     pills, tip, M(H(start, stop), 0, 4, 0, 0), M(status, 0, 14, 0, 0), grid, verdict),
                Banner("info", "Labels are the usual ones for 1.9 TDI engines with Bosch EDC15 (Golf/Bora/Octavia/Leon/A3 and similar, about 1998–2005). The raw values from the engine are always shown as they come."));
        }

        Addon TdiAddon()
        {
            return new Addon
            {
                Id = "tdi",
                Name = "VAG 1.9 TDI dashboard",
                Description = "Labelled live data from 1.9 TDI engines (Bosch EDC15): boost, EGR / air mass, injection timing and smooth running, with an automatic boost check for the overboost code.",
                Icon = "",
                DefaultOn = false,
                Page = TdiPage,
            };
        }
    }
}
