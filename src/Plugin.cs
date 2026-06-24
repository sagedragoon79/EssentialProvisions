using System;
using System.Collections.Generic;
using MelonLoader;

[assembly: MelonInfo(typeof(EssentialProvisions.Plugin), "Essential Provisions", "1.3.1", "sagedragoon79")]
[assembly: MelonGame("Crate Entertainment", "Farthest Frontier")]

namespace EssentialProvisions
{
    /// <summary>
    /// Essential Provisions — curated, opt-in QoL bundle for Farthest Frontier.
    ///
    /// Design rules (recap):
    ///   - Every feature is OFF by default. Players opt in to what they want.
    ///   - One file per feature in src/Features/<Name>.cs, self-contained.
    ///   - Per-feature kill switch at top of each Postfix/Prefix:
    ///         if (!Config.X.Value) return;
    ///   - Foreign-mod detection: if a player has the original standalone mod
    ///     loaded, the matching feature stays off even if its toggle is on.
    ///   - All prefs registered with KC SettingsAPI for rich panel UX. Soft-dep,
    ///     so EP works whether or not Keep Clarity is installed.
    /// </summary>
    public class Plugin : MelonMod
    {
        public static Plugin Instance { get; private set; } = null!;
        public static MelonLogger.Instance Log => Instance.LoggerInstance;

        // Names of foreign mod assemblies that, if loaded, cause us to defer
        // the matching feature even if its toggle is on. Each fold-in adds its
        // peer-mod assembly name(s) to the watched list below as it lands.
        public static readonly HashSet<string> LoadedForeignMods =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public override void OnInitializeMelon()
        {
            Instance = this;

            Config.Initialize();
            DetectForeignMods();

            // Features with one-time post-Config setup hooks.
            Features.ProjectPrep.Initialize();
            Features.WorkplaceMastery.Initialize();

            // Soft-dep registration with Keep Clarity's settings panel. No-ops
            // cleanly when KC isn't installed.
            KeepClarityIntegration.TryRegisterAll();

            // MelonLoader's HarmonyInit() ran before this method and already
            // called HarmonyInstance.PatchAll(); calling again would double-patch.
            LoggerInstance.Msg($"{Info.Name} {Info.Version} initialized");
        }

        private void DetectForeignMods()
        {
            // Watched assembly names — populated as fold-ins are added. Each
            // new feature appends the original mod's assembly name here so
            // that feature's kill switch can defer when the original is loaded.
            string[] watchedAssemblies = {
                "FFQoL",              // Labor Shortage / Long Travels / Idle Hands / Blight Watch / Soil Wisdom / Service Bounds
                "FFAutomation",       // Penny Pincher / Clearcutting / Efficient Labor (IdleFarmers subset) / Project Prep / Consumable Control / Surplus Selling
                "NoMoreSlackingOff",  // Efficient Labor (primary mechanism source)
                "CowardlyVillagers",  // Self Preservation
                "Cowardly Villagers", // (variant — assembly may include the space)
                "FastFrontier",       // Fast Forward
                "Fast Frontier Speed Mod", // (variant — Fast Frontier's actual MelonInfo name)
            };
            if (watchedAssemblies.Length == 0) return;

            var watched = new HashSet<string>(watchedAssemblies, StringComparer.OrdinalIgnoreCase);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (watched.Contains(name))
                {
                    LoadedForeignMods.Add(name);
                    LoggerInstance.Msg($"Detected peer mod assembly '{name}' — corresponding feature(s) will be skipped.");
                }
            }
        }

        public static bool IsForeignModLoaded(params string[] names)
        {
            foreach (var n in names)
                if (LoadedForeignMods.Contains(n)) return true;
            return false;
        }

        /// <summary>
        /// Forwarded to features that hold static state which needs to clear
        /// on scene transitions (e.g. cached UI references that don't survive
        /// a Map → main menu → Map round trip). Each feature with such state
        /// adds a Reset() call here as it lands.
        /// </summary>
        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            Features.LaborShortage.Reset();
            Features.LongTravels.Reset();
            Features.IdleHands.Reset();
            Features.BlightWatch.Reset();
            Features.SoilWisdom.Reset();
            Features.ServiceBounds.Reset();
            Features.PennyPincher.Reset();
            Features.Clearcutting.Reset();
            Features.EfficientLabor.Reset();
            Features.ProjectPrep.Reset();
            Features.ConsumableControl.Reset();
            Features.SurplusSelling.Reset();
            Features.SelfPreservation.Reset();
            Features.FastForward.Reset();
            Features.ShortWalks.Reset();
            Features.BroadShelves.Reset();
            Features.PlantingAlmanac.Reset();
            Features.LearnedHands.Reset();
            Features.WorkplaceMastery.Reset();
        }

        /// <summary>
        /// Per-frame tick. Each feature with per-frame work registers itself
        /// here. Features with their own kill-switch checks short-circuit
        /// internally; we don't gate at this level.
        /// </summary>
        public override void OnUpdate()
        {
            Features.LongTravels.OnUpdate();
            Features.IdleHands.OnUpdate();
            Features.BlightWatch.OnUpdate();
            Features.SoilWisdom.OnUpdate();
            Features.PennyPincher.OnUpdate();
            Features.Clearcutting.OnUpdate();
            Features.EfficientLabor.OnUpdate();
            Features.ConsumableControl.OnUpdate();
            Features.SurplusSelling.OnUpdate();
            Features.FastForward.OnUpdate();
            Features.ShortWalks.OnUpdate();
            Features.WorkplaceMastery.OnUpdate();
        }
    }
}
