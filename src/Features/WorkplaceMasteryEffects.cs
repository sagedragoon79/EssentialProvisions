// Workplace Mastery — per-job effects for the 16 jobs the work-rate hooks never reach, plus
// a combat damage boost for hunters. Companion to WorkplaceMastery.cs, which owns tenure.
// Design and verified hooks: _handoffs/2026-10-15_mastery-for-every-job.md. Line numbers
// below refer to the June 10, 2026 decompile.
//
// SCALE. One tenure fraction per villager, t = min(years in current job, 25) / 25, so every
// effect grows +1/25 of its cap per year. Each effect is t × its cap:
//   Production  +25%  Builder, Miner, Fisherman (same as crafting and gathering)
//   Service     +10%  Healer, Teacher, Scholar, Priest, Publican, Librarian, Grocer,
//                     Guildmaster, Rat Catcher, Groomer
//   Combat      +10%  damage for Soldier, Guard, Hunter; 10% less damage taken for Soldier
//                     and Guard (WorkplaceMasteryCombat sub-toggle)
//   Trade        5%   Trader; steps aside while Sovereign Boons' Merchant's Gambit is on,
//                     because that boon prices both its legs through the same modifier
// Building-level effects (heal rate, research, entertainment, spirituality, mine output,
// tower damage, pet births) use the average t of that building's staff of the matching job.
//
// CACHE. t is computed once per villager per in-game day and on every job change, so the
// hot hooks (movement speed, every combat hit) cost one dictionary lookup.
//
// SAFETY. Every target is patched by hand in Initialize() with its own try/catch: a game
// update that renames one method disables only that job's effect. Every patch body is
// wrapped so nothing throws into game code, and each effect logs its first failure only.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace EssentialProvisions.Features
{
    internal static class WorkplaceMasteryEffects
    {
        internal const float ProductionCap = 0.25f;
        internal const float ServiceCap = 0.10f;
        internal const float CombatCap = 0.10f;
        internal const float TradeCap = 0.05f;

        // VillagerOccupation.Occupation values (June 10, 2026 enum).
        private const int OccHunter = 1, OccBuilder = 2, OccGuard = 9, OccMiner = 10, OccFisherman = 15,
                          OccPublican = 26, OccTrader = 30, OccHealer = 33, OccTeacher = 39,
                          OccSoldier = 45, OccGrocer = 46, OccRatCatcher = 48, OccLibrarian = 53,
                          OccPriest = 54, OccGuildmaster = 55, OccScholar = 57, OccGroomer = 58,
                          OccNone = 59;

        private readonly struct Entry
        {
            public readonly int Occ;
            public readonly float T;
            public Entry(int occ, float t) { Occ = occ; T = t; }
        }

        private static readonly Dictionary<int, Entry> _cache = new Dictionary<int, Entry>();
        private static int _cacheDay = int.MinValue;

        // Town-wide aggregates, rebuilt lazily after a new day or any job change.
        private static bool _aggregatesDirty = true;
        private static float _bestTraderT;
        private static float _avgGuildmasterT;

        // Per-villager fractional build intervals, and per-herd fractional births.
        private static readonly Dictionary<int, float> _buildCarry = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _birthCarry = new Dictionary<int, float>();
        private static int _constructionDepth;

        // Lessons already shortened (cleared when the lesson completes), and the vanilla
        // remaining time to put back once the shortened timer is running.
        private static readonly HashSet<int> _shortenedLesson = new HashSet<int>();
        private static readonly Dictionary<int, CETimeSpan> _restoreLesson = new Dictionary<int, CETimeSpan>();

        // Guard towers by CombatComponent instance id (null = not a tower).
        private static readonly Dictionary<int, GuardTower?> _towerByCombat = new Dictionary<int, GuardTower?>();

        private static MelonPreferences_Entry<bool>? _merchantsGambit;
        private static bool _merchantsGambitResolved;
        private static MethodInfo? _bornThisYearSetter;

        private static readonly HashSet<string> _attached = new HashSet<string>();
        private static readonly HashSet<string> _errorLogged = new HashSet<string>();

        private static bool Enabled => Config.EnableWorkplaceMastery.Value;
        private static bool CombatOn => Config.EnableWorkplaceMastery.Value && Config.WorkplaceMasteryCombat.Value;

        // ----- Lifecycle -----

        public static void Reset()
        {
            _cache.Clear();
            _cacheDay = int.MinValue;
            _aggregatesDirty = true;
            _buildCarry.Clear();
            _birthCarry.Clear();
            _constructionDepth = 0;
            _shortenedLesson.Clear();
            _restoreLesson.Clear();
            _towerByCombat.Clear();
            _merchantsGambitResolved = false;   // re-resolve per scene; cheap
        }

        /// <summary>Per-frame from WorkplaceMastery.OnUpdate: drop cached tenure when the
        /// in-game day changes.</summary>
        internal static void Tick(int absDay)
        {
            if (absDay == _cacheDay) return;
            _cacheDay = absDay;
            _cache.Clear();
            _aggregatesDirty = true;
        }

        /// <summary>A villager changed jobs: recompute their t on next use.</summary>
        internal static void Invalidate(int instanceID)
        {
            _cache.Remove(instanceID);
            _aggregatesDirty = true;
        }

        /// <summary>Tenure was reloaded or realigned wholesale.</summary>
        internal static void InvalidateAll()
        {
            _cache.Clear();
            _aggregatesDirty = true;
        }

        public static void Initialize()
        {
            var h = new HarmonyLib.Harmony("EssentialProvisions.WorkplaceMastery.Effects");

            TryPatch(h, "Builder", AccessTools.Method(typeof(WorkOnConstructionSubTask), "OnArrivedUpdate"),
                prefix: nameof(ConstructionEnter), finalizer: nameof(ConstructionExit));
            TryPatch(h, "Builder", AccessTools.Method(typeof(Villager), nameof(Villager.IsTimeToDetachItems)),
                postfix: nameof(BuilderIntervals));
            TryPatch(h, "Miner", AccessTools.Method(typeof(Mine), "UpdateOreProduction"),
                prefix: nameof(MineOre));
            TryPatch(h, "Fisherman", AccessTools.Method(typeof(FishFromShoreSubTask), "StartFishing"),
                postfix: nameof(FasterCatch));
            TryPatch(h, "Fisherman (boat)", AccessTools.Method(typeof(FishFromBoatSubTask), "StartFishing"),
                postfix: nameof(FasterCatch));
            TryPatch(h, "Combat", AccessTools.Method(typeof(TechTreeManager), nameof(TechTreeManager.GetCombatMultipler)),
                postfix: nameof(CombatMultiplier));
            TryPatch(h, "Guard tower", AccessTools.Method(typeof(CombatComponent), "GetRangedDamage", new[] { typeof(RangedWeaponData) }),
                postfix: nameof(TowerDamage));
            TryPatch(h, "Healer", AccessTools.PropertyGetter(typeof(HealersHouse), "healFactor"),
                postfix: nameof(HealRate));
            TryPatch(h, "Teacher", AccessTools.Method(typeof(School), nameof(School.StartLessonTeaching)),
                prefix: nameof(LessonStart), finalizer: nameof(LessonStartRestore));
            TryPatch(h, "Teacher", AccessTools.Method(typeof(School), "CompleteLessonTaught"),
                postfix: nameof(LessonDone));
            TryPatch(h, "Scholar", AccessTools.Method(typeof(Academy), "GetDailyWorkerKPUs"),
                postfix: nameof(Research));
            TryPatch(h, "Publican and Librarian", AccessTools.Method(typeof(HappinessManager), "UpdateEntertainmentBuildingPoints"),
                postfix: nameof(EntertainmentTotal));
            TryPatch(h, "Priest", AccessTools.Method(typeof(HappinessManager), "UpdateSpiritualityBuildingPoints"),
                postfix: nameof(SpiritualityTotal));
            TryPatch(h, "Grocer", AccessTools.PropertyGetter(typeof(Character), "movementSpeed"),
                postfix: nameof(GrocerWalk));
            TryPatch(h, "Trader", AccessTools.Method(typeof(TradeManager), nameof(TradeManager.GetItemPriceModifier)),
                postfix: nameof(TraderPrice));
            TryPatch(h, "Guildmaster", AccessTools.Method(typeof(GuildManager), nameof(GuildManager.GetMultiplierForNumberOfGuildmasters)),
                postfix: nameof(GuildBonus));
            TryPatch(h, "Rat Catcher", AccessTools.Method(typeof(CatchRatsSubTask), "OnArrivedUpdate"),
                prefix: nameof(RatCatching));
            TryPatch(h, "Groomer", AccessTools.Method(typeof(Herd), "CheckSubSeason"),
                prefix: nameof(BirthsBefore), postfix: nameof(BirthsAfter));

            _bornThisYearSetter = AccessTools.PropertySetter(typeof(Herd), "numAnimalsBornThisYear");
            Plugin.Log.Msg($"[WorkplaceMastery] Job effects attached ({_attached.Count}/16): {string.Join(", ", new List<string>(_attached).ToArray())}.");
        }

        private static void TryPatch(HarmonyLib.Harmony h, string effect, MethodBase? target,
            string? prefix = null, string? postfix = null, string? finalizer = null)
        {
            try
            {
                if (target == null)
                {
                    Plugin.Log.Warning($"[WorkplaceMastery] {effect}: game method not found; that job's mastery effect is off.");
                    return;
                }
                h.Patch(target,
                    prefix: prefix != null ? new HarmonyMethod(typeof(WorkplaceMasteryEffects), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(WorkplaceMasteryEffects), postfix) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(typeof(WorkplaceMasteryEffects), finalizer) : null);
                _attached.Add(effect);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning($"[WorkplaceMastery] {effect}: patch failed ({ex.Message}); that job's mastery effect is off.");
            }
        }

        private static void LogOnce(string effect, Exception ex)
        {
            if (_errorLogged.Add(effect))
                Plugin.Log.Warning($"[WorkplaceMastery] {effect} effect: {ex.Message} (further errors suppressed)");
        }

        // ----- Tenure lookups -----

        private static Entry Get(Villager v)
        {
            int id = v.GetInstanceID();
            if (_cache.TryGetValue(id, out var e)) return e;
            float t = WorkplaceMastery.CurrentTenureFraction(v, out int occ);
            e = new Entry(occ, t);
            _cache[id] = e;
            return e;
        }

        // t for this villager if they currently hold `occ`, else 0.
        private static float TFor(Villager? v, int occ)
        {
            if (v == null) return 0f;
            var e = Get(v);
            return e.Occ == occ ? e.T : 0f;
        }

        // Average t over a building's assigned workers who hold `occ`.
        private static float AvgWorkerT(Building? b, int occ)
        {
            var ws = b?.workersRO;
            if (ws == null || ws.Count == 0) return 0f;
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < ws.Count; i++)
            {
                if (!(ws[i] is Villager v)) continue;
                var e = Get(v);
                if (e.Occ != occ) continue;
                sum += e.T;
                n++;
            }
            return n > 0 ? sum / n : 0f;
        }

        // Average t over the villagers physically inside a building who hold `occ`.
        private static float AvgOccupantT(EnterableBuilding? b, int occ)
        {
            var os = b?.occupancyComponent?.occupantsRO;
            if (os == null || os.Count == 0) return 0f;
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < os.Count; i++)
            {
                if (!(os[i] is Villager v)) continue;
                var e = Get(v);
                if (e.Occ != occ) continue;
                sum += e.T;
                n++;
            }
            return n > 0 ? sum / n : 0f;
        }

        private static void RefreshAggregates()
        {
            if (!_aggregatesDirty) return;
            _aggregatesDirty = false;
            _bestTraderT = 0f;
            _avgGuildmasterT = 0f;
            var villagers = UnitySingleton<GameManager>.Instance?.resourceManager?.villagersRO;
            if (villagers == null) return;
            float gmSum = 0f;
            int gmCount = 0;
            for (int i = 0; i < villagers.Count; i++)
            {
                var v = villagers[i];
                if (v == null) continue;
                var e = Get(v);
                if (e.Occ == OccTrader && e.T > _bestTraderT) _bestTraderT = e.T;
                else if (e.Occ == OccGuildmaster) { gmSum += e.T; gmCount++; }
            }
            _avgGuildmasterT = gmCount > 0 ? gmSum / gmCount : 0f;
        }

        internal static bool MerchantsGambitOn()
        {
            if (!_merchantsGambitResolved)
            {
                _merchantsGambitResolved = true;
                try { _merchantsGambit = MelonPreferences.GetEntry<bool>("SovereignBoons", "EnableMerchantsGambit"); }
                catch { _merchantsGambit = null; }
            }
            return _merchantsGambit != null && _merchantsGambit.Value;
        }

        // ----- Builder: builds and repairs faster (+25%) -----
        // Construction and repairs share WorkOnConstructionSubTask.OnArrivedUpdate, which asks
        // the villager's work-interval timer how many intervals passed and detaches that many
        // work units with costs ignored, so the work-rate hook never sees it. The same timer
        // drives gathering, crafting, and pub drinking, so extra intervals are added only
        // inside the construction update, and only for Builders.

        private static void ConstructionEnter() => _constructionDepth++;

        private static Exception? ConstructionExit(Exception? __exception)
        {
            if (_constructionDepth > 0) _constructionDepth--;
            return __exception;
        }

        private static void BuilderIntervals(Villager __instance, bool __result, ref uint numIntervalsPassed)
        {
            if (!__result || _constructionDepth == 0 || numIntervalsPassed == 0) return;
            try
            {
                if (!Enabled) return;
                float t = TFor(__instance, OccBuilder);
                if (t <= 0f) return;
                int id = __instance.GetInstanceID();
                _buildCarry.TryGetValue(id, out float carry);
                carry += numIntervalsPassed * t * ProductionCap;
                uint extra = (uint)Mathf.FloorToInt(carry);
                _buildCarry[id] = carry - extra;
                numIntervalsPassed += extra;
            }
            catch (Exception ex) { LogOnce("Builder", ex); }
        }

        // ----- Miner: more ore (+25%) -----
        // Mine.UpdateOreProduction (L351824) adds orePerMinerPerMonth × occupants × elapsed
        // time, and elapsedSec feeds nothing else, so scaling it scales output. Covers every
        // Mine subclass; stacks with the guild and tech multipliers.

        private static void MineOre(Mine __instance, ref float elapsedSec)
        {
            try
            {
                if (!Enabled || elapsedSec <= 0f) return;
                float avg = AvgOccupantT(__instance, OccMiner);
                if (avg > 0f) elapsedSec *= 1f + avg * ProductionCap;
            }
            catch (Exception ex) { LogOnce("Miner", ex); }
        }

        // ----- Fisherman: catches sooner (+25%) -----
        // StartFishing rolls catchFishTime from timeBetweenFishMin/Max. Warden of the Wilds
        // writes that range for its fishing modes, so only the rolled time is scaled and the
        // range fields are never touched.

        private static void FasterCatch(Villager ___villager, ref float ___catchFishTime)
        {
            try
            {
                if (!Enabled || ___catchFishTime <= 0f) return;
                float t = TFor(___villager, OccFisherman);
                if (t > 0f) ___catchFishTime /= 1f + t * ProductionCap;
            }
            catch (Exception ex) { LogOnce("Fisherman", ex); }
        }

        // ----- Soldier, Guard, Hunter: hit harder; Soldier and Guard take less damage (+10%) -----
        // GetCombatMultipler (L215605) is additive: Attack feeds melee and ranged damage,
        // Defense reduces incoming damage, and vanilla returns 0 for Defense. Read on every hit,
        // so it uses the day cache. Tower shots pass no villager and are handled below.

        private static void CombatMultiplier(Villager villager, CombatModify.Type type, ref float __result)
        {
            if (villager == null) return;
            try
            {
                if (!CombatOn) return;
                var e = Get(villager);
                if (e.T <= 0f) return;
                bool attack = type == CombatModify.Type.Attack;
                if (e.Occ == OccSoldier || e.Occ == OccGuard || (attack && e.Occ == OccHunter))
                    __result += e.T * CombatCap;
            }
            catch (Exception ex) { LogOnce("Combat", ex); }
        }

        // ----- Guard towers: shots hit harder with experienced guards inside (+10%) -----

        private static void TowerDamage(CombatComponent __instance, ref float __result)
        {
            try
            {
                if (!CombatOn || __result <= 0f) return;
                int id = __instance.GetInstanceID();
                if (!_towerByCombat.TryGetValue(id, out var tower))
                {
                    tower = __instance.GetComponent<GuardTower>();
                    _towerByCombat[id] = tower;
                }
                if (tower == null) return;
                float avg = AvgOccupantT(tower, OccGuard);
                if (avg > 0f) __result *= 1f + avg * CombatCap;
            }
            catch (Exception ex) { LogOnce("Guard tower", ex); }
        }

        // ----- Healer: patients heal faster (+10%) -----

        private static void HealRate(HealersHouse __instance, ref float __result)
        {
            try
            {
                if (!Enabled) return;
                float avg = AvgWorkerT(__instance, OccHealer);
                if (avg > 0f) __result *= 1f + avg * ServiceCap;
            }
            catch (Exception ex) { LogOnce("Healer", ex); }
        }

        // ----- Teacher: lessons finish sooner (+10%) -----
        // StartLessonTeaching (L356211) starts a timer from the private remainingTeachingTime.
        // Shorten it for the timer only, then put vanilla's value back, so nothing compounds
        // from lesson to lesson. An interrupted lesson saves its shortened remainder through
        // vanilla StopLessonTeaching; the flag stops a resume from shortening it again.

        private static void LessonStart(School __instance, ref CETimeSpan ___remainingTeachingTime, GameObject ___teachingTeacher)
        {
            try
            {
                if (!Enabled) return;
                int id = __instance.GetInstanceID();
                if (_shortenedLesson.Contains(id)) return;
                float t = ___teachingTeacher != null ? TFor(___teachingTeacher.GetComponent<Villager>(), OccTeacher) : 0f;
                if (t <= 0f) t = AvgWorkerT(__instance, OccTeacher);
                if (t <= 0f) return;
                float days = ___remainingTeachingTime.totalDays;
                if (days <= 0f) return;
                _restoreLesson[id] = ___remainingTeachingTime;
                ___remainingTeachingTime = new CETimeSpan(0, 0, days / (1f + t * ServiceCap));
                _shortenedLesson.Add(id);
            }
            catch (Exception ex) { LogOnce("Teacher", ex); }
        }

        private static Exception? LessonStartRestore(School __instance, ref CETimeSpan ___remainingTeachingTime, Exception? __exception)
        {
            try
            {
                int id = __instance.GetInstanceID();
                if (_restoreLesson.TryGetValue(id, out var original))
                {
                    ___remainingTeachingTime = original;
                    _restoreLesson.Remove(id);
                }
            }
            catch (Exception ex) { LogOnce("Teacher", ex); }
            return __exception;
        }

        private static void LessonDone(School __instance)
        {
            try { _shortenedLesson.Remove(__instance.GetInstanceID()); }
            catch (Exception ex) { LogOnce("Teacher", ex); }
        }

        // ----- Scholar: more research points (+10%) -----
        // GetDailyWorkerKPUs also feeds the academy's monthly forecast, so the UI agrees.

        private static void Research(Academy __instance, ref float __result)
        {
            try
            {
                if (!Enabled || __result <= 0f) return;
                float avg = AvgWorkerT(__instance, OccScholar);
                if (avg > 0f) __result *= 1f + avg * ServiceCap;
            }
            catch (Exception ex) { LogOnce("Scholar", ex); }
        }

        // ----- Publican, Librarian, Priest: more entertainment and spirituality (+10%) -----
        // HappinessManager sums coverage × points as a float over every active building and
        // rounds once into a private int. The per-building getters return int, so a 10% bonus
        // there would round away on small values. Instead, redo vanilla's final sum with each
        // staffed pub, library, or temple's bonus, and only when some bonus applies. Every
        // input is state vanilla just wrote (the residence lists) or public getters.

        private static void EntertainmentTotal(HappinessManager __instance, ref int ___entertainmentBuildingPoints)
        {
            try
            {
                if (!Enabled) return;
                var buildings = UnitySingleton<GameManager>.Instance?.resourceManager?.allBuildingsRO;
                if (buildings == null) return;
                float sum = 0f;
                bool boosted = false;
                for (int i = 0; i < buildings.Count; i++)
                {
                    var b = buildings[i];
                    if (b == null || b.entertainmentPoints <= 0 || !(b.entertainmentRadius > 0f) || !b.isWorkEnabled || !b.entertainmentActive)
                        continue;
                    float coverage = 1f;
                    if (b.sheltersInRadiusForMaxPoints > 0)
                        coverage = Mathf.Clamp01((float)(b.residencesInRadiusForEntertainmentPoints?.Count ?? 0) / b.sheltersInRadiusForMaxPoints);
                    float bonus = b is Pub ? AvgWorkerT(b, OccPublican) * ServiceCap
                                : b is Library ? AvgWorkerT(b, OccLibrarian) * ServiceCap
                                : 0f;
                    if (bonus > 0f) boosted = true;
                    sum += coverage * b.entertainmentPoints * (1f + bonus);
                }
                if (boosted) ___entertainmentBuildingPoints = Mathf.RoundToInt(sum);
            }
            catch (Exception ex) { LogOnce("Publican and Librarian", ex); }
        }

        private static void SpiritualityTotal(HappinessManager __instance, ref int ___spiritualityBuildingPoints)
        {
            try
            {
                if (!Enabled) return;
                var buildings = UnitySingleton<GameManager>.Instance?.resourceManager?.allBuildingsRO;
                if (buildings == null) return;
                float sum = 0f;
                bool boosted = false;
                for (int i = 0; i < buildings.Count; i++)
                {
                    var b = buildings[i];
                    if (b == null || b.spiritualityPoints <= 0 || !(b.spiritualityRadius > 0f) || !b.isWorkEnabled || !b.spiritualityActive)
                        continue;
                    float coverage = 1f;
                    if (b.sheltersInRadiusForMaxSprtPoints > 0)
                        coverage = Mathf.Clamp01((float)(b.residencesInRadiusForSpiritualityPoints?.Count ?? 0) / b.sheltersInRadiusForMaxSprtPoints);
                    float bonus = b is Temple ? AvgWorkerT(b, OccPriest) * ServiceCap : 0f;
                    if (bonus > 0f) boosted = true;
                    sum += coverage * b.spiritualityPoints * (1f + bonus);
                }
                if (boosted) ___spiritualityBuildingPoints = Mathf.RoundToInt(sum);
            }
            catch (Exception ex) { LogOnce("Priest", ex); }
        }

        // ----- Grocer: walks faster (+10%) -----
        // A market trip already carries far more than one home needs, and each trip serves one
        // home, so walking time is the grocer's real limit. Character.movementSpeed is read
        // constantly for every villager; the cache keeps this to one lookup.

        private static void GrocerWalk(Character __instance, ref float __result)
        {
            try
            {
                if (!Enabled || !(__instance is Villager v)) return;
                float t = TFor(v, OccGrocer);
                if (t > 0f) __result *= 1f + t * ServiceCap;
            }
            catch (Exception ex) { LogOnce("Grocer", ex); }
        }

        // ----- Trader: better prices (5%) -----
        // isSelling: false carries the trader's markup (vanilla multiplies it by
        // 1 + sellPriceModifier), so it's the price the player pays. Uses the town's most
        // experienced trader.

        private static void TraderPrice(bool isSelling, ref float __result)
        {
            try
            {
                if (!Enabled || MerchantsGambitOn()) return;
                RefreshAggregates();
                if (_bestTraderT <= 0f) return;
                float b = _bestTraderT * TradeCap;
                __result *= isSelling ? 1f + b : 1f - b;
            }
            catch (Exception ex) { LogOnce("Trader", ex); }
        }

        // ----- Guildmaster: stronger guild bonus (+10%) -----
        // Feeds every guild multiplier through AddGuildmasterMult. One guild hall per town, so
        // the town's guildmaster average is that hall's average.

        private static void GuildBonus(ref float __result)
        {
            try
            {
                if (!Enabled || __result <= 0f) return;
                RefreshAggregates();
                if (_avgGuildmasterT > 0f) __result *= 1f + _avgGuildmasterT * ServiceCap;
            }
            catch (Exception ex) { LogOnce("Guildmaster", ex); }
        }

        // ----- Rat Catcher: clears rats faster (+10%) -----
        // OnArrivedUpdate adds the real elapsed time to timeAtTarget, then compares it with the
        // time to kill. Adding the bonus share of the same elapsed time first is the same as
        // dividing the kill time. The method only exits early on failure.

        private static void RatCatching(Villager ___villager, ref float ___timeAtTarget, float ___lastTimeAccumulationTime)
        {
            try
            {
                if (!Enabled || ___lastTimeAccumulationTime < 0f) return;
                float t = TFor(___villager, OccRatCatcher);
                if (t <= 0f) return;
                float dt = Time.time - ___lastTimeAccumulationTime;
                if (dt > 0f) ___timeAtTarget += dt * t * ServiceCap;
            }
            catch (Exception ex) { LogOnce("Rat Catcher", ex); }
        }

        // ----- Groomer: more pet births (+10%) -----
        // Kennel births round to whole animals, so a 10% bump inside the birth roll would
        // usually round to nothing. Instead, count the births each roll produces and carry the
        // fractional bonus per herd, spawning extras through the herd's own SpawnLivestock with
        // the same capacity rule vanilla uses.

        private static void BirthsBefore(Herd __instance, out uint __state)
        {
            __state = 0;
            try { __state = __instance.numAnimalsBornThisYear; }
            catch { }
        }

        private static void BirthsAfter(Herd __instance, uint __state)
        {
            try
            {
                if (!Enabled) return;
                if (!(__instance.homeBuilding is Kennel kennel)) return;
                uint now = __instance.numAnimalsBornThisYear;
                if (now <= __state) return;
                float avg = AvgWorkerT(kennel, OccGroomer);
                if (avg <= 0f) return;

                int id = __instance.GetHashCode();
                _birthCarry.TryGetValue(id, out float carry);
                carry += (now - __state) * avg * ServiceCap;
                int extra = Mathf.FloorToInt(carry);
                _birthCarry[id] = carry - extra;

                for (int i = 0; i < extra; i++)
                {
                    var herd = __instance.animalsInHerdRO;
                    bool breedsAtCapacity = __instance.herdSetupData != null && __instance.herdSetupData.breedingWhenAtCapacity;
                    if (!breedsAtCapacity && herd != null && herd.Count >= kennel.userDefinedMaxLivestock) break;
                    Vector3 at = herd != null && herd.Count > 0
                        ? herd[UnityEngine.Random.Range(0, herd.Count)].transform.position
                        : kennel.transform.position;
                    if (__instance.SpawnLivestock(1, at, spawnAsAdult: false) <= 0) break;
                    _bornThisYearSetter?.Invoke(__instance, new object[] { __instance.numAnimalsBornThisYear + 1 });
                    UnitySingletonPersistent<EventManager>.Instance?.Raise(new LivestockAnimalBornEvent(__instance));
                }
            }
            catch (Exception ex) { LogOnce("Groomer", ex); }
        }

        // ----- Keep Clarity readout (WorkInfoApi) -----

        /// <summary>The current job's mastery effect in percentage points on that job's own
        /// scale, plus short English text such as "Teaches 6% faster". 0 and "" when the job
        /// has no effect, the effect is switched off, or tenure hasn't earned a whole percent.
        /// Jobs covered by the work-rate hooks report their work-rate bonus.</summary>
        internal static float EffectFor(Villager v, out string text)
        {
            text = "";
            if (v == null || !Enabled) return 0f;
            var e = Get(v);
            if (e.T <= 0f) return 0f;

            float Pct(float cap) => Mathf.Floor(e.T * cap * 100f);
            float p;
            switch (e.Occ)
            {
                case OccBuilder: p = Pct(ProductionCap); text = $"Builds {p:0}% faster"; break;
                case OccMiner: p = Pct(ProductionCap); text = $"Mines {p:0}% more ore"; break;
                case OccFisherman: p = Pct(ProductionCap); text = $"Catches fish {p:0}% faster"; break;
                case OccSoldier:
                case OccGuard:
                    if (!Config.WorkplaceMasteryCombat.Value) return 0f;
                    p = Pct(CombatCap); text = $"Hits {p:0}% harder, takes {p:0}% less damage"; break;
                case OccHunter:
                {
                    WorkplaceMastery.TryGetCurrentMasteryBonus(v, out float work);
                    float w = Mathf.Floor(work);
                    p = Config.WorkplaceMasteryCombat.Value ? Pct(CombatCap) : 0f;
                    if (p >= 1f && w >= 1f) text = $"Butchers {w:0}% faster, hits {p:0}% harder";
                    else if (p >= 1f) text = $"Hits {p:0}% harder";
                    else if (w >= 1f) { text = $"Butchers {w:0}% faster"; p = w; }
                    break;
                }
                case OccHealer: p = Pct(ServiceCap); text = $"Heals patients {p:0}% faster"; break;
                case OccTeacher: p = Pct(ServiceCap); text = $"Teaches {p:0}% faster"; break;
                case OccScholar: p = Pct(ServiceCap); text = $"Researches {p:0}% faster"; break;
                case OccPriest: p = Pct(ServiceCap); text = $"Temple spirituality +{p:0}%"; break;
                case OccPublican: p = Pct(ServiceCap); text = $"Pub entertainment +{p:0}%"; break;
                case OccLibrarian: p = Pct(ServiceCap); text = $"Library entertainment +{p:0}%"; break;
                case OccGrocer: p = Pct(ServiceCap); text = $"Walks {p:0}% faster"; break;
                case OccGuildmaster: p = Pct(ServiceCap); text = $"Guild bonus +{p:0}%"; break;
                case OccRatCatcher: p = Pct(ServiceCap); text = $"Clears rats {p:0}% faster"; break;
                case OccGroomer: p = Pct(ServiceCap); text = $"Pet births +{p:0}%"; break;
                case OccTrader:
                    p = Pct(TradeCap);
                    text = MerchantsGambitOn() ? $"Prices {p:0}% better (paused: Merchant's Gambit is on)" : $"Prices {p:0}% better";
                    break;
                default:
                {
                    WorkplaceMastery.TryGetCurrentMasteryBonus(v, out float work);
                    p = Mathf.Floor(work);
                    text = $"Works {p:0}% faster";
                    break;
                }
            }
            if (p < 1f) { text = ""; return 0f; }
            return p;
        }
    }
}
