// Add-on: ABS & other KWP2000 modules.
// Some modules (ABS on early-2000s VAG cars) answer the wakeup with KWP2000 instead of KWP1281.
// This add-on reruns fault code / identify jobs for those modules with the extended KW1281Test
// in addons\kwp2000 (source in addons-src\kw1281test, Kwp2000\Kwp2000Diagnostics.cs).
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace KklDiag
{
    public partial class Shell
    {
        readonly HashSet<string> kwp2000Addresses = new HashSet<string>();

        static readonly Dictionary<string, string> Kwp2000Commands = new Dictionary<string, string>
        {
            { "ReadFaultCodes", "Kwp2000ReadFaultCodes" },
            { "ClearFaultCodes", "Kwp2000ClearFaultCodes" },
            { "ReadIdent", "Kwp2000ReadIdent" },
        };

        string Kwp2000Exe { get { return fake ? KwPath : Path.Combine(AppDir, "addons", "kwp2000", "kw1281test.exe"); } }

        bool UseKwp2000(Job j)
        {
            string cmd;
            if (j.Exe != null || !Kwp2000Commands.TryGetValue(j.Command, out cmd) || !File.Exists(Kwp2000Exe)) return false;
            j.Command = cmd;
            j.Exe = Kwp2000Exe;
            j.Label += "  (KWP2000)";
            return true;
        }

        UIElement Kwp2000Page()
        {
            var result = new ContentControl();
            var run = Btn("Run ABS connection test", "Primary", () => Run(new Job
            {
                Address = "03", Command = "Kwp2000Probe", Exe = Kwp2000Exe, Label = "Testing how the ABS wants to be spoken to…",
                OnDone = (text, o) =>
                {
                    var line = Data.ParseValue(text, "PROBE RESULT:");
                    result.Content = line != null
                        ? Banner(line.StartsWith("answered to", System.StringComparison.Ordinal) ? "ok" : "warn",
                                 line + "\n\nOpen the Log page, press Copy and attach it to a GitHub issue, so the add-on can learn this format.")
                        : Banner("danger", o == Data.Outcome.Ok ? "The test didn't finish. Copy the Log page and attach it to a GitHub issue." : Data.Explain(o));
                }
            }), "");
            return V(Card(Heading("ABS connection test"),
                Sub("Some ABS units wake up with KWP2000 but ignore the first message. This test tries the common message formats one after another (about 20 seconds) and records which one the ABS answers."),
                Bullet("", "Ignition ON, engine off", "Cable plugged in, no other diagnostic program open."),
                Bullet("", "Afterwards: Log › Copy", "Attach the log to a GitHub issue so support for this module can be added."),
                M(H(run), 0, 6, 0, 16),
                result));
        }

        Addon Kwp2000Addon()
        {
            return new Addon
            {
                Id = "kwp2000",
                Name = "ABS & KWP2000 modules",
                Description = "Reads, clears and identifies modules that use the newer KWP2000 protocol, such as the ABS (03) on many early-2000s cars. Experimental. Works automatically in Control Modules and Auto-Scan. Live data, adaptation and coding stay KWP1281-only.",
                Icon = "",
                DefaultOn = true,
                Page = Kwp2000Page,
                MissingPart = () => File.Exists(Kwp2000Exe) ? null : "Missing addons\\kwp2000\\kw1281test.exe",
                // Modules already known to speak KWP2000 go straight to the extended tool.
                Route = j => kwp2000Addresses.Contains(j.Address) && UseKwp2000(j),
                // Otherwise switch over the first time a module answers with KWP2000.
                Retry = (j, text, outcome) =>
                {
                    if (outcome != Data.Outcome.NotSupported || !UseKwp2000(j)) return false;
                    kwp2000Addresses.Add(j.Address);
                    j.BaudIndex = 0;
                    return true;
                },
            };
        }
    }
}
