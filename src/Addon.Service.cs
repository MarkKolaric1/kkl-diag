// Add-on: service reminder reset for VDO instrument clusters of this generation
// (Golf 4 / Octavia 1U / Passat B5 era): adaptation channel 02 in the cluster (17) set to 0.
using System.Windows;
using System.Windows.Controls;

namespace KklDiag
{
    public partial class Shell
    {
        UIElement ServicePage()
        {
            var result = new ContentControl();
            var check = Btn("Check current value", "Secondary", () => Run(new Job
            {
                Address = "17", Command = "AdaptationRead", Args = "2", Label = "Reading the service reminder…",
                OnDone = (text, o) =>
                {
                    var v = Data.ParseValue(text, "Adaptation value:");
                    result.Content = o == Data.Outcome.Ok && v != null
                        ? Banner("info", "Service reminder value (channel 02): " + v + ". 0 means it's already reset.")
                        : Banner("danger", o == Data.Outcome.Ok ? "The cluster didn't return a value." : Data.Explain(o));
                }
            }), "");
            var reset = Btn("Reset service reminder", "Primary", () => Confirm("Reset the service reminder?",
                "The instrument cluster's service counter will be set back to 0, so the next service is due at the normal interval from now.\n\nOnly do this after the service has actually been done.",
                "Reset", false, () => Run(new Job
                {
                    Address = "17", Command = "AdaptationSave", Args = "2 0", Label = "Resetting the service reminder…",
                    OnDone = (text, o) =>
                    {
                        var v = Data.ParseValue(text, "Adaptation value:");
                        result.Content = o == Data.Outcome.Ok && v != null
                            ? Banner("ok", "Done. The service reminder is reset — switch the ignition off and on to see it.")
                            : Banner("danger", o == Data.Outcome.Ok
                                ? "The cluster didn't confirm. Some clusters with LongLife service use other channels (40–42); open a GitHub issue with your cluster's ident so they can be added."
                                : Data.Explain(o));
                    }
                })), "");

            return V(Card(Heading("Service reminder reset"),
                Sub("Turns off the \"service\" / \"INSP\" reminder in the instrument cluster after an oil change or inspection."),
                Bullet("", "Ignition ON, engine off", "Cable plugged in."),
                Bullet("", "Uses the instrument cluster (17)", "Same procedure as VCDS on VW and Škoda clusters of this generation: adaptation channel 02 = 0."),
                M(H(reset, check), 0, 6, 0, 16),
                result));
        }

        Addon ServiceResetAddon()
        {
            return new Addon
            {
                Id = "service",
                Name = "Service reminder reset",
                Description = "One button to reset the oil / inspection reminder in the instrument cluster.",
                Icon = "",
                DefaultOn = true,
                Page = ServicePage,
            };
        }
    }
}
