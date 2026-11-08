// Add-on: radio safe code for the two VW radios KW1281Test can read
// (VW Premium 4 by Clarion at address 56, VW Premium 5 by Delco at address 7C).
using System;
using System.Windows;
using System.Windows.Controls;

namespace KklDiag
{
    public partial class Shell
    {
        UIElement RadioPage()
        {
            var result = new ContentControl();
            Action<string, string, string> read = (address, command, label) => Run(new Job
            {
                Address = address, Command = command, Label = "Reading the code from the " + label + "…",
                OnDone = (text, o) =>
                {
                    var code = Data.ParseValue(text, "Safe code:");
                    result.Content = o == Data.Outcome.Ok && code != null
                        ? Banner("ok", "Radio code: " + code)
                        : Banner("danger", o == Data.Outcome.Ok ? "The radio didn't give a code. This only works on the two radios listed." : Data.Explain(o));
                }
            });
            return V(Card(Heading("Radio code"),
                Sub("Reads the anti-theft code from two VW factory radios. Turn the radio ON first."),
                Banner("warn", "Only VW Premium 4 (Clarion) and VW Premium 5 (Delco) are supported. Other radios (Škoda, Seat, Audi, aftermarket) won't answer."),
                M(H(Btn("VW Premium 4 (Clarion)", "Primary", () => read("56", "ClarionVWPremium4SafeCode", "Premium 4 radio"), ""),
                    Btn("VW Premium 5 (Delco)", "Secondary", () => read("7C", "DelcoVWPremium5SafeCode", "Premium 5 radio"), "")), 0, 4, 0, 16),
                result));
        }

        Addon RadioCodeAddon()
        {
            return new Addon
            {
                Id = "radio",
                Name = "VW radio code",
                Description = "Reads the anti-theft code from VW Premium 4 / Premium 5 radios. Off by default because only these two radios are supported.",
                Icon = "",
                DefaultOn = false,
                Page = RadioPage,
            };
        }
    }
}
