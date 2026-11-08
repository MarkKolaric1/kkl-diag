// Add-on framework: optional features that can be switched on/off in Settings.
// Each add-on registers itself in RegisterAddons(); its code lives in an Addon.*.cs file.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace KklDiag
{
    public class Addon
    {
        public string Id;
        public string Name;
        public string Description;
        public string Icon;
        public bool DefaultOn;
        public bool Enabled;
        public Func<UIElement> Page;      // own sidebar page, or null if it only plugs into existing screens
        public Func<string> MissingPart;  // returns why it can't run (e.g. a file is missing), or null
        public Func<Job, bool> Route;     // may rewrite a job before it starts; true if it did
        public Func<Job, string, Data.Outcome, bool> Retry; // may rewrite a finished job to run again
    }

    public partial class Shell
    {
        readonly List<Addon> addons = new List<Addon>();
        readonly Dictionary<string, bool> savedAddonState = new Dictionary<string, bool>();
        StackPanel AddonNav;
        TextBlock AddonsHeader;

        void RegisterAddons()
        {
            addons.Add(Kwp2000Addon());
            addons.Add(TdiAddon());
            addons.Add(ServiceResetAddon());
            addons.Add(RadioCodeAddon());
            foreach (var a in addons)
            {
                bool on;
                a.Enabled = savedAddonState.TryGetValue(a.Id, out on) ? on : a.DefaultOn;
            }
        }

        bool AddonOn(string id) { return addons.Any(a => a.Id == id && a.Enabled && (a.MissingPart == null || a.MissingPart() == null)); }

        void RouteJob(Job j)
        {
            foreach (var a in addons.Where(x => x.Enabled && x.Route != null))
                if (a.Route(j)) return;
        }

        bool RetryWithAddon(Job j, string text, Data.Outcome outcome)
        {
            foreach (var a in addons.Where(x => x.Enabled && x.Retry != null))
                if (a.Retry(j, text, outcome)) return true;
            return false;
        }

        void RebuildAddonNav()
        {
            AddonNav.Children.Clear();
            foreach (var a in addons.Where(x => x.Enabled && x.Page != null))
            {
                var id = a.Id;
                var rb = new RadioButton { Style = St("Nav"), Tag = a.Icon, Content = a.Name, GroupName = "nav" };
                rb.Checked += (s, e) => { if (!navSync) Show("addon:" + id); };
                AddonNav.Children.Add(rb);
            }
            AddonsHeader.Visibility = AddonNav.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        RadioButton ShowAddon(string id)
        {
            var a = addons.FirstOrDefault(x => x.Id == id);
            if (a == null || a.Page == null) { Show("settings"); return null; }
            SetTitle("Add-ons", a.Name);
            PageHost.Content = a.Page();
            return AddonNav.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Content == a.Name);
        }

        UIElement AddonsCard()
        {
            var list = V();
            foreach (var a in addons)
            {
                var aa = a;
                var g = new Grid { Margin = new Thickness(0, 0, 0, 16) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var missing = a.MissingPart == null ? null : a.MissingPart();
                var body = V(T(a.Name, 14.5, "Ink", true), M(T(a.Description, 13, "Muted"), 0, 2, 0, 0),
                             missing == null ? null : M(T("⚠ " + missing, 12.5, "Warn"), 0, 4, 0, 0));
                body.Margin = new Thickness(0, 0, 16, 0);
                Grid.SetColumn(body, 1);
                var toggle = new CheckBox { Style = St("Toggle"), IsChecked = a.Enabled, VerticalAlignment = VerticalAlignment.Center };
                toggle.Checked += (s, e) => SetAddon(aa, true);
                toggle.Unchecked += (s, e) => SetAddon(aa, false);
                Grid.SetColumn(toggle, 2);
                g.Children.Add(M(IconBubble(a.Icon, "Accent", "AccentSoft", 36), 0, 0, 14, 0));
                g.Children.Add(body);
                g.Children.Add(toggle);
                list.Children.Add(g);
            }
            return Card(Heading("Add-ons"), Sub("Extra features you can switch on or off. Switched-on add-ons with their own page appear in the sidebar under ADD-ONS."), list);
        }

        void SetAddon(Addon a, bool on)
        {
            a.Enabled = on;
            SaveSettings();
            RebuildAddonNav();
        }

        string AddonSettingsLines()
        {
            return string.Concat(addons.Select(a => "Addon." + a.Id + "=" + (a.Enabled ? "1" : "0") + "\r\n"));
        }

        void ReadAddonSetting(string key, string value)
        {
            if (key.StartsWith("Addon.", StringComparison.Ordinal))
                savedAddonState[key.Substring(6)] = value == "1";
        }
    }
}
