using System.Linq;
using Waterfall;

namespace FNPlugin.Propulsion
{
    // This class exists solely as a shield for referencing Waterfall, so it can be referenced without spamming errors if it isn't installed.
    // This should be the only class referencing Waterfall, and you should ALWAYS verify that a ModuleWaterfallFX is present before calling
    // anything in this class -- a quick way to do this is if (part.Modules.Contains("ModuleWaterfallFX"))
    public class WaterfallIntegration
    {
        public static void RCSPower(Part part, string thrusterTransform, bool rcsPowered)
        {
            foreach (ModuleWaterfallFX module in part.FindModulesImplementing<ModuleWaterfallFX>())
            {
                // HeavyRCS has two effects that are independently powered, this distinguishes them by their transform
                WaterfallEffect effect = module.FX.FirstOrDefault();
                if (effect == null || effect.parentName != thrusterTransform)
                    continue;

                // "rcsPower" is a custom controller (linkedTo = custom), which Waterfall 0.8+ drives through SetControllerValue
                module.SetControllerValue("rcsPower", rcsPowered ? 1f : 0f);
            }
        }
    }
}
