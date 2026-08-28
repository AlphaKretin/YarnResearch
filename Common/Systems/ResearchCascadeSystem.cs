using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameContent.Creative;
using Terraria.GameContent.ItemDropRules;
using Terraria.GameContent.UI.Chat;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using YarnResearch.Common.Configs;

namespace YarnResearch.Common.Systems
{
	public class ResearchCascadeSystem : ModSystem
	{
		// Which mechanism unlocked an item, so the right chat notification is batched for it. Declared in
		// the order FlushNotifications prints them.
		private enum ResearchOrigin
		{
			Held,
			Craftable,
			BiomeTorch,
			Extractinator,
			Shimmer,
			Crate,
			Sacrifice,
			Shop,
		}

		// Chat label per origin, keyed by enum member name so the two can't drift out of order.
		private static readonly LocalizedText[] NotificationLabels =
			Enum.GetValues<ResearchOrigin>().Select(origin => Localization($"Origins.{origin}")).ToArray();

		private static readonly LocalizedText NotificationText = Localization("Notification");

		private static LocalizedText Localization(string key) =>
			ModContent.GetInstance<YarnResearch>().GetLocalization($"{nameof(ResearchCascadeSystem)}.{key}");

		// Which mechanisms a drain should run despite their config toggle being off - see DrainCascade.
		[Flags]
		private enum Mechanism
		{
			None = 0,
			Craftable = 1,
			Shimmer = 2,
			Crate = 4,
		}

		private static readonly HashSet<int> ResearchedTypes = new();

		// Types currently mid-ResearchItem for a given mechanism, so the synchronous re-entry into
		// HandleResearched can tell which mechanism unlocked them. Indexed by ResearchOrigin.
		private static readonly HashSet<int>[] PendingOrigins =
			Enum.GetValues<ResearchOrigin>().Select(_ => new HashSet<int>()).ToArray();

		// Unlocks awaiting their batched chat notification, indexed the same way.
		private static readonly Queue<int>[] PendingNotifications =
			Enum.GetValues<ResearchOrigin>().Select(_ => new Queue<int>()).ToArray();

		// Proxy signal for any gating Condition (on a recipe or an NPC shop entry) that the player can
		// trivially force or recreate on demand via some researchable item, standing in for the live
		// Condition.IsMet() check - see ConditionsSatisfiable/ShopConditionsSatisfiable. The first five
		// entries are the only recipe-gating proximity Conditions vanilla has; the rest only ever gate shop
		// entries. IMPORTANT: an entry here must never apply to a Pylon shop entry, which is deliberately
		// gated on physically visiting the correct biome rather than on a proxy - see
		// IsPylonItem/ShopConditionsSatisfiable.
		//
		// Permanent world-seed and world-state flags deliberately get no entry, since the IsMet() fallback
		// alone handles them correctly: ZenithWorld, Hardmode/PreHardmode, downed-boss flags, CrimsonWorld,
		// CorruptWorld, NotRemixWorld, and BiomeSpreadingItems (whose predicate is a world-seed check,
		// !Main.remixWorld || (Main.tenthAnniversaryWorld && !Main.getGoodWorld), despite the name).
		private static readonly Dictionary<Condition, HashSet<int>> ConditionProxyItemTypes = new() {
			[Condition.InGraveyard] = new HashSet<int> {
				ItemID.Tombstone, ItemID.GraveMarker, ItemID.CrossGraveMarker,
				ItemID.Headstone, ItemID.Gravestone, ItemID.Obelisk,
				ItemID.RichGravestone1, ItemID.RichGravestone2, ItemID.RichGravestone3,
				ItemID.RichGravestone4, ItemID.RichGravestone5,
			},
			[Condition.InSnow] = new HashSet<int> { ItemID.SnowBlock, ItemID.IceBlock },
			[Condition.NearWater] = new HashSet<int> { ItemID.WaterBucket, ItemID.BottomlessBucket },
			[Condition.NearLava] = new HashSet<int> { ItemID.LavaBucket, ItemID.BottomlessLavaBucket },
			[Condition.NearHoney] = new HashSet<int> { ItemID.HoneyBucket, ItemID.BottomlessHoneyBucket },
			[Condition.InDesert] = new HashSet<int> { ItemID.SandBlock },
			[Condition.InUnderworld] = new HashSet<int> { ItemID.AshBlock, ItemID.Hellstone },
			[Condition.InJungle] = new HashSet<int> { ItemID.JungleGrassSeeds },
			[Condition.InHallow] = new HashSet<int> { ItemID.PearlstoneBlock, ItemID.PinkIceBlock },
			[Condition.InGlowshroom] = new HashSet<int> { ItemID.MushroomGrassSeeds },
			// Sundial/Moondial force-advance to the next day/night, cycling through every time-of-day and
			// moon-phase gate eventually. Moondial is a rarer, later-game alternative, but either one
			// satisfies the same set of conditions.
			[Condition.TimeDay] = new HashSet<int> { ItemID.Sundial, ItemID.Moondial },
			[Condition.TimeNight] = new HashSet<int> { ItemID.Sundial, ItemID.Moondial },
			[Condition.MoonPhasesQuarter0] = new HashSet<int> { ItemID.Sundial, ItemID.Moondial },
			[Condition.MoonPhasesQuarter1] = new HashSet<int> { ItemID.Sundial, ItemID.Moondial },
			[Condition.MoonPhasesQuarter2] = new HashSet<int> { ItemID.Sundial, ItemID.Moondial },
			[Condition.MoonPhasesQuarter3] = new HashSet<int> { ItemID.Sundial, ItemID.Moondial },
			// Bloody Tear summons a Blood Moon on demand.
			[Condition.BloodMoon] = new HashSet<int> { ItemID.BloodMoonStarter },
			[Condition.BloodMoonOrHardmode] = new HashSet<int> { ItemID.BloodMoonStarter },
			// Solar Tablet summons a Solar Eclipse on demand; EclipseOrBloodMoon is satisfied by either.
			[Condition.Eclipse] = new HashSet<int> { ItemID.SolarTablet },
			[Condition.EclipseOrBloodMoon] = new HashSet<int> { ItemID.SolarTablet, ItemID.BloodMoonStarter },
			[Condition.NightOrEclipse] = new HashSet<int> { ItemID.SolarTablet },
			// Contrived, but deliberate: reaching wave 15 of the Pumpkin/Frost Moon events (summonable
			// anytime post-Hardmode via these items) actually flips Main.halloween/Main.xMas true until the
			// real season starts again, so these items are a genuine (if late-game) way to satisfy an
			// otherwise-uncontrollable real-calendar-date gate.
			[Condition.Halloween] = new HashSet<int> { ItemID.PumpkinMoonMedallion },
			[Condition.Christmas] = new HashSet<int> { ItemID.NaughtyPresent },
		};

		// Reverse of ConditionProxyItemTypes: proxy item type -> Conditions it satisfies. Built once since
		// ConditionProxyItemTypes is static data.
		private static readonly Dictionary<int, List<Condition>> ConditionsByProxyItemType = BuildConditionsByProxyItemType();

		// Demon and Crimson Altars share the single TileID.DemonAltar, differing only by tile frame. A real
		// altar is a world tile whose item form (ItemID.DemonAltar/CrimsonAltar) only drops from the Eye of
		// Cthulhu in a world that has none; those items, and any mod equivalent, already register as
		// stations for the tile through their own createTile.
		//
		// The Replica Altars are decorative in vanilla - they place their own tile and craft nothing - but
		// they are craftable, so a researched Replica is deliberately counted as owning the real station
		// here. That is more generous than vanilla, and intentionally so: without it an altar-gated recipe
		// can never cascade in a world where no altar item ever drops.
		private static readonly int[] AltarProxyItemTypes = { ItemID.DemonAltarReplica, ItemID.CrimsonAltarReplica };

		// Reverse of recipe.Conditions, covering every Condition attached to any recipe (proxied or not):
		// Condition -> recipe indices gated by it. Needed for the same reason as RecipesRequiringTile below:
		// a recipe whose ingredients and station were already satisfied must get rechecked the instant its
		// Condition becomes satisfiable, not only on the next full manual rescan. Two independent things can
		// make a Condition newly satisfiable, and both read this map - a registered proxy item getting
		// researched (ProcessCraftableOutputs, via ConditionsByProxyItemType), or the Condition's live
		// IsMet() turning true (CheckLiveConditionEdges). The latter applies even to a proxied Condition,
		// since a player standing in the real thing should work exactly as it does in vanilla, with no proxy
		// research required first.
		private static readonly Dictionary<Condition, List<int>> RecipesRequiringCondition = new();

		// Last-observed IsMet() per Condition in RecipesRequiringCondition, so CheckLiveConditionEdges can
		// detect a false->true transition rather than re-triggering every tick the condition happens to be
		// true.
		private static readonly Dictionary<Condition, bool> LiveConditionWasMet = new();

		// Recipe indices gated by Recipe.needTorchGodsFavor - tracked separately from
		// RecipesRequiringCondition since this gate isn't a Condition at all (see TorchGodsFavorSatisfied).
		// Watched the same way: a false->true transition on Player.unlockedBiomeTorches rechecks every
		// recipe here.
		private static readonly List<int> RecipesRequiringTorchGodsFavor = new();
		private static bool _torchGodsFavorWasUnlocked;

		// Recipe.needTorchGodsFavor is a legacy internal bool that tModLoader never converts into a
		// Condition the way it does needWater/needGraveyardBiome/etc. (PostAddRecipes' ReplaceCondition
		// calls list every field that does get converted, and this isn't one), so recipe.Conditions has no
		// representation of it at all. Reflection is the only way to read our own runtime's copy of this
		// vanilla flag, same justification as TryGetPlayerCarriesItemType.
		private static readonly FieldInfo NeedTorchGodsFavorField =
			typeof(Recipe).GetField("needTorchGodsFavor", BindingFlags.Instance | BindingFlags.NonPublic);

		// Every biome whose presence Torch God's Favor can convert a torch or campfire for. Held as
		// getter/setter pairs because the enumeration in GetBiomeTorchVariants has to save, override one
		// at a time, and restore the player's real zone state.
		private static readonly (Func<Player, bool> Get, Action<Player, bool> Set)[] BiomeTorchZones = {
			(p => p.ZoneJungle, (p, v) => p.ZoneJungle = v),
			(p => p.ZoneLihzhardTemple, (p, v) => p.ZoneLihzhardTemple = v),
			(p => p.ZoneSnow, (p, v) => p.ZoneSnow = v),
			(p => p.ZoneDesert, (p, v) => p.ZoneDesert = v),
			(p => p.ZoneUndergroundDesert, (p, v) => p.ZoneUndergroundDesert = v),
			(p => p.ZoneGlowshroom, (p, v) => p.ZoneGlowshroom = v),
			(p => p.ZoneCorrupt, (p, v) => p.ZoneCorrupt = v),
			(p => p.ZoneCrimson, (p, v) => p.ZoneCrimson = v),
			(p => p.ZoneHallow, (p, v) => p.ZoneHallow = v),
			(p => p.ZoneDungeon, (p, v) => p.ZoneDungeon = v),
			(p => p.ZoneShimmer, (p, v) => p.ZoneShimmer = v),
			(p => p.ZoneUnderworldHeight, (p, v) => p.ZoneUnderworldHeight = v),
		};

		// Convertible torch/campfire type -> every biome variant it can become. Cached because the answer
		// is fixed game data, while working it out costs one vanilla call per biome.
		private static readonly Dictionary<int, int[]> BiomeTorchVariants = new();

		// Every tile that functions as an Extractinator, in the form RollExtractinatorDrop takes as its
		// extractinatorBlockType. The two roll different tables, so each is enumerated separately.
		private static readonly int[] ExtractinatorTiles = { TileID.Extractinator, TileID.ChlorophyteExtractinator };

		// (extract mode, extractinator tile) -> every item that pairing can yield. Cached for the same
		// reason as BiomeTorchVariants, but far more so: working an entry out costs ExtractinatorRollSamples
		// rolls.
		private static readonly Dictionary<(int Mode, int BlockType), int[]> ExtractinatorOutputs = new();

		// How many times each pairing is rolled to recover its drop table. The rarest researchable vanilla
		// result is the Amber Mosquito at 1/10,000, which this leaves a ~1e-11 chance of missing.
		private const int ExtractinatorRollSamples = 250_000;

		// Fixed, so a pairing enumerates identically in every session - a sampled table that quietly varied
		// run to run would make a missed drop impossible to reproduce.
		private const int ExtractinatorSampleSeed = 20260828;

		private static readonly Dictionary<int, List<int>> RecipesConsumingItem = new();
		private static readonly Dictionary<int, List<int>> StationItemTypesByTile = new();

		// Every tile placed by an already-researched item, maintained incrementally as items are researched
		// rather than derived from ResearchedTypes on demand - FreeCraftingSystem reads it from inside
		// Recipe.FindRecipes, which runs on every inventory change.
		private static readonly HashSet<int> ResearchedStationTiles = new();

		// Reverse of the above two: recipe.requiredTile -> recipe indices needing that tile as a station.
		// Needed because a recipe otherwise only gets rechecked when one of its *ingredients* is newly
		// researched (via RecipesConsumingItem): a station becoming available on its own triggered nothing,
		// so a recipe whose ingredients were already satisfied while its station was still unknown stayed
		// unchecked for the rest of that cascade, and only got picked up by a later full rescan.
		private static readonly Dictionary<int, List<int>> RecipesRequiringTile = new();

		// Direct Shimmer transmute table (ItemID.Sets.ShimmerTransformToItem), input type -> output type.
		// Built once since this is static game data. Decraft outputs are looked up dynamically instead (see
		// ProcessShimmerOutputs), since RecipeLoader.DecraftAvailable depends on live Conditions (biome,
		// world type, etc.) that can change without a recipe-data reload.
		private static readonly Dictionary<int, int> ShimmerOutputsByInput = new();

		// Persisted per-world: once true, Shimmer cascade edges stay open for the rest of the world's life.
		private static bool _shimmerDiscovered;

		public static ModKeybind ResearchCrateContentsKeybind { get; private set; }

		// Set while a cascade triggered by HandleResearched is draining, so a CreativeUI.ResearchItem call
		// made from within that drain - which synchronously re-enters HandleResearched via
		// GlobalItem.OnResearched - is recognized as our own cascade rather than an external research.
		private static Queue<int> _activeCascadeQueue;

		// Set for the duration of a caller-defined batch (e.g. one YarnResearchPlayer scan pass), so several
		// distinct top-level HandleResearched calls within it share one queue and flush instead of each
		// draining and flushing independently.
		private static Queue<int> _batchQueue;

		// Mechanisms whose config toggle a currently-running drain should ignore - set by a manual trigger
		// for the duration of its own batch. See DrainCascade.
		private static Mechanism _forcedMechanisms;

		// Breaks one cascade down by phase so a felt hitch can be attributed to a specific piece of the
		// machinery rather than to the cascade as a whole. Accumulation starts at BeginBatch (or at the
		// first HandleResearched of a standalone cascade) and is reported by DrainAndLog, so it covers the
		// pre-drain collection pass a batched trigger does as well as the drain itself. Timers nest: an
		// indented phase in the report is a subset of the one above it, and ResearchItem is a subset of
		// every phase that unlocks anything.
		private static class CascadeProfile
		{
			internal enum Phase
			{
				Craftable,
				RecipeCheck,
				BiomeTorch,
				TorchVariantBuild,
				Extractinator,
				ExtractinatorSample,
				Shimmer,
				DecraftLookup,
				Crate,
				CrateDropRules,
				ResearchItem,
				Notifications,
				TagList,
				NewText,
			}

			// Parallel to Phase; leading spaces mark a phase nested inside the one above it.
			private static readonly string[] PhaseLabels = {
				"craftable outputs",
				"  recipe checks",
				"biome torches",
				"  variant table build",
				"extractinator outputs",
				"  output table sample",
				"shimmer outputs",
				"  decraft lookup",
				"crate contents",
				"  drop-rule report",
				"CreativeUI.ResearchItem",
				"chat notifications",
				"  tag list build",
				"  Main.NewText",
			};

			// Only a drain slow enough to be felt gets a breakdown - ordinary one- or two-item cascades run
			// constantly during play and would bury the interesting runs in log noise.
			private const double ReportThresholdMs = 25.0;

			private static readonly long[] Ticks = new long[PhaseLabels.Length];
			private static readonly int[] Calls = new int[PhaseLabels.Length];

			private static long _startTimestamp;
			private static long _startAllocatedBytes;
			private static int _startGen0, _startGen1, _startGen2;
			private static int _slowestStepType;
			private static long _slowestStepTicks;
			private static int _notifiedCount;

			internal readonly ref struct Scope
			{
				private readonly int _phase;
				private readonly long _start;

				internal Scope(Phase phase)
				{
					_phase = (int)phase;
					_start = Stopwatch.GetTimestamp();
				}

				public void Dispose()
				{
					Ticks[_phase] += Stopwatch.GetTimestamp() - _start;
					Calls[_phase]++;
				}
			}

			internal static Scope Time(Phase phase) => new(phase);

			internal static void Reset()
			{
				Array.Clear(Ticks);
				Array.Clear(Calls);
				_slowestStepType = 0;
				_slowestStepTicks = 0;
				_notifiedCount = 0;

				// Read last so the counters' own cost lands outside the measured window.
				_startGen0 = GC.CollectionCount(0);
				_startGen1 = GC.CollectionCount(1);
				_startGen2 = GC.CollectionCount(2);
				_startAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
				_startTimestamp = Stopwatch.GetTimestamp();
			}

			// One drained type's whole slice of the drain, so a cascade dominated by a single high-fanout
			// item (a common ingredient, a station placed by dozens of items) is visible as such.
			internal static void NoteStep(int type, long ticks)
			{
				if (ticks <= _slowestStepTicks)
					return;

				_slowestStepType = type;
				_slowestStepTicks = ticks;
			}

			internal static void NoteNotified(int count) => _notifiedCount += count;

			// Called once a top-level cascade has fully finished - drain and chat notifications both - since
			// the notification flush is outside the drain and was invisible to the drain's own timing.
			internal static void Report()
			{
				if (BuildReport() is string report)
					ModContent.GetInstance<YarnResearch>().Logger.Info(report);
			}

			// Null when the run was too fast to be worth reporting.
			private static string BuildReport()
			{
				long elapsedTicks = Stopwatch.GetTimestamp() - _startTimestamp;
				long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _startAllocatedBytes;

				if (ToMs(elapsedTicks) < ReportThresholdMs)
					return null;

				var report = new System.Text.StringBuilder();
				report.Append($"ResearchCascadeSystem cascade profile: {ToMs(elapsedTicks):F2}ms from batch start, " +
					$"{_notifiedCount} items announced in chat");

				for (int phase = 0; phase < PhaseLabels.Length; phase++) {
					if (Calls[phase] > 0)
						report.Append($"\n  {PhaseLabels[phase]}: {ToMs(Ticks[phase]):F2}ms over {Calls[phase]} calls");
				}

				report.Append($"\n  allocated {allocatedBytes / 1024.0 / 1024.0:F1}MB, collections gen0 " +
					$"{GC.CollectionCount(0) - _startGen0} / gen1 {GC.CollectionCount(1) - _startGen1} / " +
					$"gen2 {GC.CollectionCount(2) - _startGen2}");

				if (_slowestStepTicks > 0) {
					string name = ContentSamples.ItemsByType.TryGetValue(_slowestStepType, out Item item)
						? item.Name : _slowestStepType.ToString();
					report.Append($"\n  slowest single drained type: {name} at {ToMs(_slowestStepTicks):F2}ms");
				}

				return report.ToString();
			}

			private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
		}

		private static Dictionary<int, List<Condition>> BuildConditionsByProxyItemType()
		{
			var result = new Dictionary<int, List<Condition>>();

			foreach ((Condition condition, HashSet<int> itemTypes) in ConditionProxyItemTypes) {
				foreach (int itemType in itemTypes)
					AddToIndex(result, itemType, condition);
			}

			return result;
		}

		private static void AddToIndex<TKey, TValue>(Dictionary<TKey, List<TValue>> index, TKey key, TValue value)
		{
			if (!index.TryGetValue(key, out List<TValue> values)) {
				values = new List<TValue>();
				index[key] = values;
			}

			values.Add(value);
		}

		public override void Load()
		{
			ResearchCrateContentsKeybind = KeybindLoader.RegisterKeybind(Mod, "ResearchCrateContents", "Mouse3");
		}

		public override void Unload()
		{
			ResearchCrateContentsKeybind = null;
		}

		// UpdateUI (not ModPlayer.ProcessTriggers) so the hotkey still works during the partial updates
		// Main runs while autoPause is active and a menu (e.g. inventory) is open - ProcessTriggers is
		// tied to Player.Update, which those partial updates skip.
		public override void UpdateUI(GameTime gameTime)
		{
			if (Main.gameMenu)
				return;

			ReleaseDeferredNotifications();

			if (!ResearchCrateContentsKeybind.JustPressed)
				return;

			Item hoverItem = Main.HoverItem;
			if (!hoverItem.IsAir)
				TryUnpackCrate(hoverItem.type);
		}

		// Watches every recipe-gating Condition (proxied or not), plus the altar tile-station proxy and
		// Torch God's Favor, for a false->true transition, so a recipe blocked only by an unmet gate gets
		// rechecked the instant it actually becomes true (e.g. the player walks near lava, or finds a Demon
		// Altar). Includes proxied Conditions, so meeting the real requirement works even before the proxy
		// item is ever researched, exactly like a real crafting attempt would.
		// TryResearchRecipeOutput/ConditionsSatisfiable/StationResearched still re-verify everything, so
		// this is only a trigger. The Torch God's Favor edge additionally drives the one-off biome-torch
		// catch-up, since that unlock retroactively widens what an already-researched torch is worth. Called from YarnResearchPlayer.PostUpdate rather than UpdateUI: this is a
		// live world/biome state check, not input handling that has to survive autopause.
		public static void CheckLiveConditionEdges()
		{
			var config = ModContent.GetInstance<YarnResearchConfig>();

			bool torchGodsFavorJustUnlocked = !_torchGodsFavorWasUnlocked && Main.LocalPlayer.unlockedBiomeTorches;
			if (torchGodsFavorJustUnlocked)
				_torchGodsFavorWasUnlocked = true;

			List<int> recipesToRecheck = null;

			if (config.AutoResearchCraftable &&
				(RecipesRequiringCondition.Count > 0 || RecipesRequiringTorchGodsFavor.Count > 0)) {
				// Player.adjTile/adjWaterSource/adjLava/adjHoney (what NearWater/NearLava/NearHoney read) are
				// normally only recomputed by the crafting UI's own per-frame update, not by ordinary
				// Player.Update - without this, NearWater stays false while standing in water and only flips
				// true the instant the inventory is opened. AdjTiles() is the public vanilla method the crafting
				// UI itself calls to do that computation; forcing it here keeps those flags fresh regardless of
				// whether any menu is open.
				Main.LocalPlayer.AdjTiles();

				foreach ((Condition condition, List<int> recipeIndices) in RecipesRequiringCondition) {
					bool isMet = condition.IsMet();
					bool wasMet = LiveConditionWasMet.TryGetValue(condition, out bool previous) && previous;
					LiveConditionWasMet[condition] = isMet;

					if (isMet && !wasMet)
						(recipesToRecheck ??= new List<int>()).AddRange(recipeIndices);
				}

				if (torchGodsFavorJustUnlocked)
					(recipesToRecheck ??= new List<int>()).AddRange(RecipesRequiringTorchGodsFavor);
			}

			// Torches researched before the Favor was unlocked were never convertible at the time, so the
			// unlock edge is the one moment their variants have to be swept for retroactively.
			bool catchUpBiomeTorches = torchGodsFavorJustUnlocked && config.AutoResearchMiscCascades;

			if (recipesToRecheck == null && !catchUpBiomeTorches)
				return;

			BeginBatch();
			try {
				if (recipesToRecheck != null) {
					foreach (int recipeIndex in recipesToRecheck)
						TryResearchRecipeOutput(recipeIndex);
				}

				if (catchUpBiomeTorches) {
					foreach (int type in ResearchedTypes.ToArray())
						ProcessBiomeTorchVariants(type);
				}
			}
			finally {
				EndBatch();
			}
		}

		public static bool IsResearched(int type)
		{
			if (ResearchedTypes.Contains(type))
				return true;

			CreativeUI.GetSacrificeCount(type, out bool fullyResearched);
			if (fullyResearched) {
				ResearchedTypes.Add(type);
				NoteStationTile(type);
			}

			return fullyResearched;
		}

		private static void NoteStationTile(int type)
		{
			if (ContentSamples.ItemsByType.TryGetValue(type, out Item item) && item.createTile != -1)
				ResearchedStationTiles.Add(item.createTile);

			// A Replica places its own decorative tile, so the altar it stands in for has to be recorded
			// explicitly - see AltarProxyItemTypes.
			if (AltarProxyItemTypes.Contains(type))
				ResearchedStationTiles.Add(TileID.DemonAltar);
		}

		public static IReadOnlyCollection<int> ResearchedStations => ResearchedStationTiles;

		// Every Condition attached to a real recipe (modded recipes included) - the authoritative set of
		// craft-gating Conditions at runtime, as opposed to the shop-only ones ConditionProxyItemTypes also
		// covers.
		public static IEnumerable<Condition> RecipeGatingConditions => RecipesRequiringCondition.Keys;

		public static bool IsConditionProxied(Condition condition) => ConditionProxyItemTypes.ContainsKey(condition);

		public static bool ConditionProxyResearched(Condition condition) =>
			ConditionProxyItemTypes.TryGetValue(condition, out HashSet<int> proxyItemTypes) &&
			proxyItemTypes.Any(ResearchedTypes.Contains);

		public static bool ShimmerDiscovered => _shimmerDiscovered;

		// Researches type, tagging it with the mechanism responsible for the duration of the call.
		// CreativeUI.ResearchItem synchronously re-enters HandleResearched via GlobalItem.OnResearched,
		// which reads the tag back off to pick the right notification queue.
		private static void ResearchWithOrigin(int type, ResearchOrigin origin)
		{
			PendingOrigins[(int)origin].Add(type);

			using (CascadeProfile.Time(CascadeProfile.Phase.ResearchItem))
				CreativeUI.ResearchItem(type);

			PendingOrigins[(int)origin].Remove(type);
		}

		// The held-item threshold path, driven by YarnResearchPlayer's inventory scan.
		public static void ResearchAsHeldItem(int type) => ResearchWithOrigin(type, ResearchOrigin.Held);

		// The bulk-sacrifice trigger can't go through ResearchWithOrigin - it researches by consuming the
		// stack via Main.CreativeMenu.SacrificeItem rather than by calling CreativeUI.ResearchItem - so it
		// brackets that call with these instead.
		public static void RegisterSacrificeOrigin(int type) => PendingOrigins[(int)ResearchOrigin.Sacrifice].Add(type);

		public static void ClearSacrificeOrigin(int type) => PendingOrigins[(int)ResearchOrigin.Sacrifice].Remove(type);

		// Idempotent - safe to call every tick while the player is near Shimmer. Sets the persisted
		// per-world "has seen Shimmer" flag unconditionally (independent of the AutoResearchShimmerOutputs
		// toggle - visiting Shimmer is world knowledge, not itself a research action). Returns true only
		// on the call that actually flips the flag, so callers can gate a one-time catch-up scan on it.
		public static bool MarkShimmerDiscovered()
		{
			if (_shimmerDiscovered)
				return false;

			_shimmerDiscovered = true;
			return true;
		}

		// Requires MarkShimmerDiscovered to have been called at least once (per world) - a no-op otherwise.
		// Callable both by the automatic path (immediately after first discovery, gated by the config toggle
		// there) and directly by the manual trigger button, which bypasses the toggle.
		public static void RunShimmerCatchupScan()
		{
			if (!_shimmerDiscovered)
				return;

			RunCatchupScan(Mechanism.Shimmer, ProcessShimmerOutputs, "shimmer catch-up");
		}

		// Catch-up pass over already-researched items, ignoring the mechanism's config toggle - callable by
		// a manual trigger button so a player who keeps the toggle off can still fire it on demand.
		public static void ManualCascadeScan() =>
			RunCatchupScan(Mechanism.Craftable, ProcessCraftableOutputs, "manual cascade scan");

		private static void RunCatchupScan(Mechanism mechanism, Action<int> processType, string logLabel)
		{
			int[] snapshot = ResearchedTypes.ToArray();
			var stopwatch = Stopwatch.StartNew();

			BeginBatch();
			_forcedMechanisms |= mechanism;
			try {
				foreach (int type in snapshot)
					processType(type);
			}
			finally {
				EndBatch();
				_forcedMechanisms &= ~mechanism;
			}

			stopwatch.Stop();
			ModContent.GetInstance<YarnResearch>().Logger.Info(
				$"ResearchCascadeSystem {logLabel}: scanned {snapshot.Length} already-researched items, " +
				$"took {stopwatch.Elapsed.TotalMilliseconds:F2}ms total (includes the cascade drain logged separately above)");
		}

		// Direct transmute takes priority (matching vanilla: a set ShimmerTransformToItem entry means the
		// item does not attempt to decraft). Otherwise falls back to decraft: finds the recipe Shimmer would
		// currently reverse via ShimmerTransforms.GetDecraftingRecipeIndex (which calls
		// RecipeLoader.DecraftAvailable per candidate recipe, so this is always evaluated live rather than
		// cached) and unlocks its ingredients - or its customShimmerResults, if the recipe overrides what
		// decrafting returns.
		private static void ProcessShimmerOutputs(int type)
		{
			using var _ = CascadeProfile.Time(CascadeProfile.Phase.Shimmer);

			if (ShimmerOutputsByInput.TryGetValue(type, out int transformOutput)) {
				AttemptShimmerResearch(transformOutput);
				return;
			}

			int decraftRecipeIndex;
			using (CascadeProfile.Time(CascadeProfile.Phase.DecraftLookup))
				decraftRecipeIndex = ShimmerTransforms.GetDecraftingRecipeIndex(type);

			if (decraftRecipeIndex < 0)
				return;

			Recipe recipe = Main.recipe[decraftRecipeIndex];
			List<Item> decraftOutputs = recipe.customShimmerResults ?? recipe.requiredItem;

			foreach (Item output in decraftOutputs)
				AttemptShimmerResearch(output.type);
		}

		// Torch God's Favor converts a torch or campfire into the variant matching the biome it is used in,
		// and a placed one keeps that variant when broken - so owning any convertible torch is really
		// owning every biome variant of it.
		private static void ProcessBiomeTorchVariants(int type)
		{
			using var _ = CascadeProfile.Time(CascadeProfile.Phase.BiomeTorch);

			if (!Main.LocalPlayer.unlockedBiomeTorches || !(ItemID.Sets.Torches[type] || ItemID.Sets.Campfires[type]))
				return;

			foreach (int variant in GetBiomeTorchVariants(type)) {
				if (IsUnresearchedAndResearchable(variant))
					ResearchWithOrigin(variant, ResearchOrigin.BiomeTorch);
			}
		}

		// Vanilla only exposes "convert this for the biome I am in right now" (Player.BiomeTorchHoldStyle),
		// so the full mapping is read back out of it by standing the player in each convertible biome in
		// turn. Faking the zone flags rather than hardcoding the variant list keeps this correct for
		// whatever vanilla's conversion rules actually are, modded torches included.
		private static int[] GetBiomeTorchVariants(int type)
		{
			if (BiomeTorchVariants.TryGetValue(type, out int[] cached))
				return cached;

			using var _ = CascadeProfile.Time(CascadeProfile.Phase.TorchVariantBuild);

			Player player = Main.LocalPlayer;
			bool[] realZones = BiomeTorchZones.Select(zone => zone.Get(player)).ToArray();
			bool realUsingBiomeTorches = player.UsingBiomeTorches;
			var variants = new HashSet<int>();

			try {
				player.UsingBiomeTorches = true;

				for (int biome = 0; biome < BiomeTorchZones.Length; biome++) {
					for (int other = 0; other < BiomeTorchZones.Length; other++)
						BiomeTorchZones[other].Set(player, other == biome);

					int converted = player.BiomeTorchHoldStyle(type);
					if (converted > 0 && converted != type)
						variants.Add(converted);
				}
			}
			finally {
				for (int biome = 0; biome < BiomeTorchZones.Length; biome++)
					BiomeTorchZones[biome].Set(player, realZones[biome]);

				player.UsingBiomeTorches = realUsingBiomeTorches;
			}

			int[] result = variants.ToArray();
			BiomeTorchVariants[type] = result;

			ModContent.GetInstance<YarnResearch>().Logger.Info(
				$"ResearchCascadeSystem biome torch variants for {ContentSamples.ItemsByType[type].Name}: " +
				(result.Length == 0 ? "none" : string.Join(", ", result.Select(v => ContentSamples.ItemsByType[v].Name))));

			return result;
		}

		// An Extractinator turns a material into things the player can otherwise only get by mining, so
		// owning both the machine and the material is effectively owning everything it can produce.
		private static void ProcessExtractinatorOutputs(int type)
		{
			using var _ = CascadeProfile.Time(CascadeProfile.Phase.Extractinator);

			// The machine arriving is the moment every already-researched material becomes extractable, so
			// it sweeps them all rather than yielding anything itself.
			if (ContentSamples.ItemsByType.TryGetValue(type, out Item item) &&
				Array.IndexOf(ExtractinatorTiles, item.createTile) >= 0) {
				foreach (int researched in ResearchedTypes.ToArray()) {
					ProcessExtractedMaterial(researched);
					ProcessChlorophyteTrade(researched);
				}

				return;
			}

			ProcessExtractedMaterial(type);
			ProcessChlorophyteTrade(type);
		}

		// The Chlorophyte Extractinator also swaps items one for one - a mechanism entirely separate from the
		// RNG extraction table, and the one place ItemID.Sets.ExtractinatorMode explicitly does not cover.
		// Its trades live in ItemTrader.ChlorophyteExtractinator, which mods register their own swaps into,
		// so reading them back covers modded trades for free. A cyclic trade loop (copper -> tin -> copper)
		// resolves itself: each newly-researched result re-enters the drain and looks up its own trade.
		private static void ProcessChlorophyteTrade(int type)
		{
			if (!ResearchedStationTiles.Contains(TileID.ChlorophyteExtractinator) ||
				!ContentSamples.ItemsByType.TryGetValue(type, out Item sample))
				return;

			// A trade can want several of an item (ExampleMod's own asks for five bars) and the lookup weighs
			// the offered stack against that, so offering ContentSamples' stack of one would silently miss
			// every such trade.
			Item offer = sample.Clone();
			offer.stack = offer.maxStack;

			if (!ItemTrader.ChlorophyteExtractinator.TryGetTradeOption(offer, out ItemTrader.TradeOption option))
				return;

			if (IsUnresearchedAndResearchable(option.GivingItemType))
				ResearchWithOrigin(option.GivingItemType, ResearchOrigin.Extractinator);
		}

		private static void ProcessExtractedMaterial(int type)
		{
			if (type < 0 || type >= ItemID.Sets.ExtractinatorMode.Length)
				return;

			int extractMode = ItemID.Sets.ExtractinatorMode[type];
			if (extractMode < 0)
				return;

			foreach (int blockType in ExtractinatorTiles) {
				if (!ResearchedStationTiles.Contains(blockType))
					continue;

				foreach (int outputType in GetExtractinatorOutputs(extractMode, blockType)) {
					if (IsUnresearchedAndResearchable(outputType))
						ResearchWithOrigin(outputType, ResearchOrigin.Extractinator);
				}
			}
		}

		// Vanilla exposes only "roll one result" (ExtractinatorHelper.RollExtractinatorDrop), never the table
		// behind it, so the table is recovered by rolling until every outcome has almost certainly appeared.
		// Main.rand is swapped for a fixed-seed instance for the duration - same push-fake-state/restore
		// shape as GetBiomeTorchVariants - so a quarter-million rolls neither consume nor perturb the world's
		// own RNG stream.
		private static int[] GetExtractinatorOutputs(int extractMode, int blockType)
		{
			if (ExtractinatorOutputs.TryGetValue((extractMode, blockType), out int[] cached))
				return cached;

			using var _ = CascadeProfile.Time(CascadeProfile.Phase.ExtractinatorSample);

			var outputs = new HashSet<int>();
			UnifiedRandom realRand = Main.rand;

			try {
				Main.rand = new UnifiedRandom(ExtractinatorSampleSeed);

				for (int roll = 0; roll < ExtractinatorRollSamples; roll++) {
					ExtractinatorHelper.RollExtractinatorDrop(extractMode, blockType, out int itemType, out int stack);

					// Same order Player.ExtractinatorUse itself uses: mods get to replace or add to the
					// vanilla roll before it becomes a real drop, so sampling without this would miss every
					// modded extraction result.
					ItemLoader.ExtractinatorUse(ref itemType, ref stack, extractMode, blockType);

					if (itemType > 0)
						outputs.Add(itemType);
				}
			}
			finally {
				Main.rand = realRand;
			}

			int[] result = outputs.ToArray();
			ExtractinatorOutputs[(extractMode, blockType)] = result;

			ModContent.GetInstance<YarnResearch>().Logger.Info(
				$"ResearchCascadeSystem extractinator outputs for mode {extractMode} on tile {blockType}: " +
				(result.Length == 0 ? "none" : string.Join(", ", result.Select(v => ContentSamples.ItemsByType[v].Name))));

			return result;
		}

		private static void AttemptShimmerResearch(int outputType)
		{
			if (!ResearchedTypes.Contains(outputType))
				ResearchWithOrigin(outputType, ResearchOrigin.Shimmer);
		}

		// Unpacks a researched crate's possible contents on demand, regardless of the
		// AutoResearchCrateContents toggle - this is the manual hotkey path's deliberate override.
		public static void TryUnpackCrate(int type)
		{
			if (!ItemID.Sets.OpenableBag[type] || !IsResearched(type))
				return;

			BeginBatch();
			_forcedMechanisms |= Mechanism.Crate;
			try {
				ProcessCrateContents(type);
			}
			finally {
				EndBatch();
				_forcedMechanisms &= ~Mechanism.Crate;
			}
		}

		// True if a crate's possible contents include at least one item that is both researchable and
		// not yet researched - used to gate the "press X to research contents" tooltip hint so it stops
		// appearing once there's nothing left for the hotkey to do.
		public static bool HasUnresearchedCrateContents(int crateType)
		{
			foreach (int contentType in GetPossibleCrateContents(crateType)) {
				if (IsUnresearchedAndResearchable(contentType))
					return true;
			}

			return false;
		}

		// Enumerates everything a crate/bag could possibly contain, using the same ReportDroprates
		// mechanism the vanilla Bestiary "possible drops" panel uses for NPCs - no RNG rolled, nothing
		// actually dropped. Coins and other unresearchable items are filtered out by the caller.
		private static IEnumerable<int> GetPossibleCrateContents(int crateType)
		{
			using var _ = CascadeProfile.Time(CascadeProfile.Phase.CrateDropRules);

			var drops = new List<DropRateInfo>();
			var chainFeed = new DropRateInfoChainFeed(1f);

			foreach (IItemDropRule rule in Main.ItemDropsDB.GetRulesForItemID(crateType))
				rule.ReportDroprates(drops, chainFeed);

			return drops.Select(d => d.itemId).Distinct();
		}

		private static void ProcessCrateContents(int type)
		{
			using var _ = CascadeProfile.Time(CascadeProfile.Phase.Crate);

			foreach (int contentType in GetPossibleCrateContents(type))
				AttemptCrateResearch(contentType);
		}

		private static void AttemptCrateResearch(int outputType)
		{
			if (IsUnresearchedAndResearchable(outputType))
				ResearchWithOrigin(outputType, ResearchOrigin.Crate);
		}

		private static bool IsUnresearchedAndResearchable(int type)
		{
			if (ResearchedTypes.Contains(type))
				return false;

			return ContentSamples.ItemsByType.TryGetValue(type, out Item item) && item.ResearchUnlockCount > 0;
		}

		// Called from YarnResearchGlobalNPC.ModifyActiveShop, which fires exactly when a shop UI opens for
		// one NPC - scoped to that shop only, not a sweep of every shop in the game. Enumerates the shop's
		// full entry list (not just ActiveEntries) so conditional/currently-hidden stock is considered too,
		// e.g. a biome-exclusive item counts once its gating Condition is researched-satisfiable even while
		// the shop isn't showing it right now. Only NPCShop (not other AbstractNPCShop implementers) exposes
		// the full entry list needed for this. TravelingMerchantShop is a special case: its actual per-visit
		// randomized stock lives in the raw-item-id Main.travelShop array, not in any AbstractNPCShop.Entry
		// list (its own Entries/InfoEntries are unrelated fixed/info-only listings), so it's handled
		// separately below to research only what he's currently stocking, not his full pool.
		public static void ProcessShopEntries(AbstractNPCShop shop)
		{
			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (!config.AutoResearchShopStock)
				return;

			BeginBatch();
			try {
				if (shop is TravelingMerchantShop) {
					foreach (int itemType in Main.travelShop) {
						if (itemType != 0)
							AttemptShopResearch(itemType);
					}
				}
				else if (shop is NPCShop npcShop) {
					foreach (NPCShop.Entry entry in npcShop.Entries)
						AttemptShopResearch(entry);
				}
			}
			finally {
				EndBatch();
			}
		}

		private static void AttemptShopResearch(NPCShop.Entry entry)
		{
			Item item = entry.Item;
			if (!IsUnresearchedAndResearchable(item.type) || !IsShopEntryAffordable(item) ||
				!ShopConditionsSatisfiable(entry))
				return;

			ResearchWithOrigin(item.type, ResearchOrigin.Shop);
		}

		// Travelling Merchant's already-rolled stock has no Entry/Condition wrapper to check - by the time
		// an item type lands in Main.travelShop, the RNG has already committed to selling it this visit.
		private static void AttemptShopResearch(int itemType)
		{
			if (!IsUnresearchedAndResearchable(itemType) ||
				!ContentSamples.ItemsByType.TryGetValue(itemType, out Item item) || !IsShopEntryAffordable(item))
				return;

			ResearchWithOrigin(itemType, ResearchOrigin.Shop);
		}

		// Mirrors the coin math Player.CanAfford(long, int) already does (the same public method vanilla's
		// shop-buy click handler uses), but skips the check entirely once any coin denomination is
		// researched - at that point the player can duplicate coins for free via Journey Mode, so price is
		// no longer a real constraint. Only applies to ordinary coin-priced entries; a custom-currency entry
		// (Item.shopSpecialCurrency != -1, e.g. Defender Medals) always goes through the real check.
		private static bool IsShopEntryAffordable(Item item)
		{
			if (item.shopSpecialCurrency == -1 && AnyCoinResearched())
				return true;

			return Main.LocalPlayer.CanAfford(item.value, item.shopSpecialCurrency);
		}

		private static bool AnyCoinResearched() =>
			ResearchedTypes.Contains(ItemID.CopperCoin) || ResearchedTypes.Contains(ItemID.SilverCoin) ||
			ResearchedTypes.Contains(ItemID.GoldCoin) || ResearchedTypes.Contains(ItemID.PlatinumCoin);

		// Lazily built from vanilla's own NPCShopDatabase.GetPylonEntries() - the authoritative list of sold
		// Pylons - rather than pattern-matching each entry's Conditions, so this stays correct even if
		// vanilla changes which Conditions gate a Pylon. Built on first use, not at PostAddRecipes time,
		// since shop registration timing relative to recipe setup isn't guaranteed.
		private static HashSet<int> _pylonItemTypes;

		private static bool IsPylonItem(int type)
		{
			_pylonItemTypes ??= NPCShopDatabase.GetPylonEntries().Select(e => e.Item.type).ToHashSet();
			return _pylonItemTypes.Contains(type);
		}

		// Same shape as ConditionsSatisfiable (live IsMet() first, proxy fallback), but for a shop entry's
		// Conditions rather than a recipe's. Separate method since shop entries have no recipe index to key
		// a live-edge recheck off of - shop Conditions are only evaluated at shop-open time, not watched
		// continuously the way CheckLiveConditionEdges watches recipe Conditions. Pylons never get the proxy
		// fallback: they're deliberately gated on physically visiting the correct biome, so a Pylon entry's
		// Conditions must all be live-IsMet() to pass, same as buying one for real.
		private static bool ShopConditionsSatisfiable(NPCShop.Entry entry)
		{
			bool allowProxy = !IsPylonItem(entry.Item.type);

			foreach (Condition condition in entry.Conditions) {
				if (condition.IsMet())
					continue;

				if (!allowProxy)
					return false;

				if (ConditionProxyResearched(condition))
					continue;

				if (TryGetPlayerCarriesItemType(condition) is int carriedItemType &&
					ResearchedTypes.Contains(carriedItemType))
					continue;

				return false;
			}

			return true;
		}

		// Condition.PlayerCarriesItem(itemId) has no public property exposing itemId - it's a factory method
		// whose Condition just captures itemId in the predicate closure. Reading the compiler-generated
		// closure's field is the only way to recover it - this reads our own runtime's shop-condition data,
		// not third-party code, so it isn't the kind of decompiling this project avoids elsewhere. Returns
		// null for any Condition not shaped like a PlayerCarriesItem closure.
		private static int? TryGetPlayerCarriesItemType(Condition condition)
		{
			object closure = condition.Predicate.Target;
			if (closure == null)
				return null;

			FieldInfo field = closure.GetType()
				.GetField("itemId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

			return field?.GetValue(closure) as int?;
		}

		public static void BeginBatch()
		{
			CascadeProfile.Reset();
			_batchQueue = new Queue<int>();
		}

		public static void EndBatch()
		{
			Queue<int> queue = _batchQueue;
			_batchQueue = null;

			if (queue == null)
				return;

			_activeCascadeQueue = queue;
			try {
				DrainAndLog(queue);
			}
			finally {
				_activeCascadeQueue = null;
			}

			FlushNotifications();
			CascadeProfile.Report();
		}

		// Wraps DrainCascade with timing/counting, logged via Mod.Logger so it's cheap enough to leave
		// on permanently rather than needing to be stripped out after a one-off perf investigation.
		private static void DrainAndLog(Queue<int> queue)
		{
			int startingCount = ResearchedTypes.Count;
			int stepsProcessed = 0;
			int maxQueueDepth = queue.Count;
			var stopwatch = Stopwatch.StartNew();

			DrainCascade(queue, ref stepsProcessed, ref maxQueueDepth);

			stopwatch.Stop();

			int researchedCount = ResearchedTypes.Count - startingCount;
			if (stepsProcessed == 0)
				return;

			ModContent.GetInstance<YarnResearch>().Logger.Info(
				$"ResearchCascadeSystem cascade: {stepsProcessed} steps processed, {researchedCount} items researched, " +
				$"max queue depth {maxQueueDepth}, took {stopwatch.Elapsed.TotalMilliseconds:F2}ms");
		}

		public static void HandleResearched(int type)
		{
			if (ResearchedTypes.Contains(type))
				return;

			Queue<int> notificationQueue = null;
			for (int origin = 0; origin < PendingOrigins.Length; origin++) {
				if (PendingOrigins[origin].Remove(type))
					notificationQueue ??= PendingNotifications[origin];
			}

			if (_activeCascadeQueue != null) {
				// The active DrainCascade loop owns processing this type further.
				MarkResearched(type, _activeCascadeQueue, notificationQueue);
				return;
			}

			if (_batchQueue != null) {
				// Collected, not drained yet - EndBatch drains and flushes everything together.
				MarkResearched(type, _batchQueue, notificationQueue);
				return;
			}

			CascadeProfile.Reset();

			var queue = new Queue<int>();
			MarkResearched(type, queue, notificationQueue);

			_activeCascadeQueue = queue;
			try {
				DrainAndLog(queue);
			}
			finally {
				_activeCascadeQueue = null;
			}

			FlushNotifications();
			CascadeProfile.Report();
		}

		public override void PostAddRecipes()
		{
			RecipesConsumingItem.Clear();
			StationItemTypesByTile.Clear();
			RecipesRequiringTile.Clear();
			RecipesRequiringCondition.Clear();
			RecipesRequiringTorchGodsFavor.Clear();
			ShimmerOutputsByInput.Clear();
			BiomeTorchVariants.Clear();

			int[] shimmerTransforms = ItemID.Sets.ShimmerTransformToItem;
			for (int type = 0; type < shimmerTransforms.Length; type++) {
				if (shimmerTransforms[type] > 0)
					ShimmerOutputsByInput[type] = shimmerTransforms[type];
			}

			for (int i = 0; i < Recipe.numRecipes; i++) {
				Recipe recipe = Main.recipe[i];

				foreach (Item ingredient in recipe.requiredItem)
					AddToIndex(RecipesConsumingItem, ingredient.type, i);

				if (recipe.requiredTile >= 0)
					AddToIndex(RecipesRequiringTile, recipe.requiredTile, i);

				foreach (Condition condition in recipe.Conditions)
					AddToIndex(RecipesRequiringCondition, condition, i);

				if (NeedTorchGodsFavorField != null && (bool)NeedTorchGodsFavorField.GetValue(recipe))
					RecipesRequiringTorchGodsFavor.Add(i);
			}

			for (int type = 0; type < ItemLoader.ItemCount; type++) {
				if (!ContentSamples.ItemsByType.TryGetValue(type, out Item item) || item.createTile == -1)
					continue;

				AddToIndex(StationItemTypesByTile, item.createTile, type);
			}

			foreach (int type in AltarProxyItemTypes)
				AddToIndex(StationItemTypesByTile, TileID.DemonAltar, type);
		}

		public override void SaveWorldData(TagCompound tag)
		{
			if (_shimmerDiscovered)
				tag["shimmerDiscovered"] = true;
		}

		public override void LoadWorldData(TagCompound tag)
		{
			_shimmerDiscovered = tag.ContainsKey("shimmerDiscovered");
		}

		public override void ClearWorld()
		{
			_shimmerDiscovered = false;
		}

		public override void OnWorldLoad()
		{
			ResearchedTypes.Clear();
			ResearchedStationTiles.Clear();
			ClearDeferredNotifications();

			foreach (HashSet<int> origins in PendingOrigins)
				origins.Clear();

			// Both track a false->true edge against the previous tick, so they belong to the world/character
			// being played rather than to the mod load - carrying them over would swallow the first real
			// transition in the world being entered.
			LiveConditionWasMet.Clear();
			_torchGodsFavorWasUnlocked = false;

			for (int type = 0; type < ItemLoader.ItemCount; type++) {
				if (!ContentSamples.ItemsByType.TryGetValue(type, out Item item) || item.ResearchUnlockCount <= 0)
					continue;

				CreativeUI.GetSacrificeCount(type, out bool fullyResearched);
				if (fullyResearched) {
					ResearchedTypes.Add(type);
					NoteStationTile(type);
				}
			}
		}

		private static void DrainCascade(Queue<int> queue, ref int stepsProcessed, ref int maxQueueDepth)
		{
			var config = ModContent.GetInstance<YarnResearchConfig>();

			while (queue.Count > 0) {
				int type = queue.Dequeue();
				stepsProcessed++;
				long stepStart = Stopwatch.GetTimestamp();

				// _forcedMechanisms lets a manual trigger's drain fully resolve that mechanism's transitive
				// chain in one call even with its toggle off. Without it, only the first layer unlocked by
				// the trigger's initial pass would bypass the toggle; anything further downstream (found
				// only once this drain re-queues newly-unlocked items) would fall back to the toggle-gated
				// automatic behavior and be skipped, so the player would have to click several times before
				// the cascade converged.
				if (config.AutoResearchCraftable || _forcedMechanisms.HasFlag(Mechanism.Craftable))
					ProcessCraftableOutputs(type);

				if (config.AutoResearchMiscCascades) {
					ProcessBiomeTorchVariants(type);
					ProcessExtractinatorOutputs(type);
				}

				if ((config.AutoResearchShimmerOutputs || _forcedMechanisms.HasFlag(Mechanism.Shimmer)) && _shimmerDiscovered)
					ProcessShimmerOutputs(type);

				if (config.AutoResearchCrateContents || _forcedMechanisms.HasFlag(Mechanism.Crate))
					ProcessCrateContents(type);

				CascadeProfile.NoteStep(type, Stopwatch.GetTimestamp() - stepStart);

				if (queue.Count > maxQueueDepth)
					maxQueueDepth = queue.Count;
			}
		}

		// type may complete a recipe three different ways: as a newly-researched ingredient, as a
		// newly-researched station item (keyed by the tile it places), or as a newly-researched proxy item
		// for a gating Condition. Each needs its own index - a recipe whose other requirements were already
		// satisfied would otherwise never be rechecked when the last one arrives.
		private static void ProcessCraftableOutputs(int type)
		{
			using var _ = CascadeProfile.Time(CascadeProfile.Phase.Craftable);

			if (RecipesConsumingItem.TryGetValue(type, out List<int> ingredientRecipes)) {
				foreach (int recipeIndex in ingredientRecipes)
					TryResearchRecipeOutput(recipeIndex);
			}

			if (ContentSamples.ItemsByType.TryGetValue(type, out Item stationItem) && stationItem.createTile != -1 &&
				RecipesRequiringTile.TryGetValue(stationItem.createTile, out List<int> stationRecipes)) {
				foreach (int recipeIndex in stationRecipes)
					TryResearchRecipeOutput(recipeIndex);
			}

			if (ConditionsByProxyItemType.TryGetValue(type, out List<Condition> proxiedConditions)) {
				foreach (Condition condition in proxiedConditions) {
					if (!RecipesRequiringCondition.TryGetValue(condition, out List<int> conditionRecipes))
						continue;

					foreach (int recipeIndex in conditionRecipes)
						TryResearchRecipeOutput(recipeIndex);
				}
			}
		}

		private static void TryResearchRecipeOutput(int recipeIndex)
		{
			using var _ = CascadeProfile.Time(CascadeProfile.Phase.RecipeCheck);

			Recipe recipe = Main.recipe[recipeIndex];
			int outputType = recipe.createItem.type;

			if (ResearchedTypes.Contains(outputType))
				return;

			if (!AllIngredientsResearched(recipe) || !StationResearched(recipe) || !ConditionsSatisfiable(recipe) ||
				!TorchGodsFavorSatisfied(recipe))
				return;

			ResearchWithOrigin(outputType, ResearchOrigin.Craftable);
		}

		private static bool AllIngredientsResearched(Recipe recipe)
		{
			foreach (Item ingredient in recipe.requiredItem) {
				if (!ResearchedTypes.Contains(ingredient.type))
					return false;
			}

			return true;
		}

		private static bool StationResearched(Recipe recipe)
		{
			if (recipe.requiredTile < 0)
				return true;

			return StationItemTypesByTile.TryGetValue(recipe.requiredTile, out List<int> itemTypes) &&
				itemTypes.Any(ResearchedTypes.Contains);
		}

		private static bool TorchGodsFavorSatisfied(Recipe recipe)
		{
			if (Main.LocalPlayer.unlockedBiomeTorches)
				return true;

			return NeedTorchGodsFavorField == null || !(bool)NeedTorchGodsFavorField.GetValue(recipe);
		}

		// A live-true Condition always satisfies itself first, same as a real crafting attempt - a proxy is
		// only consulted as a fallback when the Condition isn't actually met right now. A Condition with no
		// registered proxy and no live match blocks the cascade, so an unrecognized Condition (e.g. from
		// another mod's recipe) behaves the same way a real crafting attempt would rather than being
		// silently bypassed.
		private static bool ConditionsSatisfiable(Recipe recipe)
		{
			foreach (Condition condition in recipe.Conditions) {
				if (!condition.IsMet() && !ConditionProxyResearched(condition))
					return false;
			}

			return true;
		}

		private static void MarkResearched(int type, Queue<int> queue, Queue<int> notificationQueue)
		{
			if (!ResearchedTypes.Add(type))
				return;

			NoteStationTile(type);

			queue.Enqueue(type);
			notificationQueue?.Enqueue(type);
		}

		private static void FlushNotifications()
		{
			if (PendingNotifications.All(queue => queue.Count == 0))
				return;

			using var _ = CascadeProfile.Time(CascadeProfile.Phase.Notifications);

			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (config.ShowAutoResearchNotifications) {
				for (int origin = 0; origin < PendingNotifications.Length; origin++) {
					if (PendingNotifications[origin].Count == 0)
						continue;

					CascadeProfile.NoteNotified(PendingNotifications[origin].Count);

					string tagList;
					using (CascadeProfile.Time(CascadeProfile.Phase.TagList))
						tagList = BuildTagList(PendingNotifications[origin], TaggedTypes);

					using (CascadeProfile.Time(CascadeProfile.Phase.NewText))
						DeferNotification(NotificationText.Format(NotificationLabels[origin].Value, tagList));
				}

				RequestTagTextures();
			}

			foreach (Queue<int> queue in PendingNotifications)
				queue.Clear();
		}

		// Posting a chat message containing item tags forces every tag's texture to be resident on the main
		// thread: ItemTagHandler.ItemSnippet.UniqueDraw calls Main.instance.LoadItem before its
		// justCheckingString guard, so it fires during word-wrap measurement, not only when drawing. A
		// cold texture costs roughly 1.5ms there, which is the whole of the large-cascade hitch.
		//
		// Two independent levers, and both are needed. AsyncLoad decides where the decode happens and is
		// worth keeping - the same 423 textures took 654ms that way against roughly 3555ms loaded one at a
		// time synchronously. But it does not by itself keep the game responsive: requesting all 423 at
		// once handed the engine an unbounded queue, which it drained inside a single frame. Rate-limiting
		// how many requests are in flight bounds how much completion work can land on any one frame while
		// still giving the decode enough parallelism to be fast. Only vanilla item textures are ever cold -
		// per Main.LoadItem's own documentation, modded item textures all load during mod loading.
		private static readonly List<string> DeferredMessages = new();
		private static readonly List<int> DeferredTextureTypes = new();

		// Types tagged by the current flush, reused across origins to keep this off the allocation path.
		private static readonly List<int> TaggedTypes = new();

		private static int _deferredTicksWaited;
		private static int _deferredColdCount;
		private static int _deferredLoadIndex;
		private static int _deferredInFlight;
		private static long _deferStartTimestamp;
		private static long _preloadTicksSpent;
		private static uint _deferStartUpdateCount;

		// How many texture requests may be outstanding at once. Wide enough to keep the loader's worker
		// threads busy, narrow enough that the completion work for a single frame stays small.
		private const int MaxTexturesInFlight = 16;

		// Safety valve: if warming somehow never completes, post anyway rather than swallowing the
		// notification. Worst case that costs the old synchronous stall.
		private const int MaxDeferredWaitTicks = 900;

		private static void DeferNotification(string message) => DeferredMessages.Add(message);

		private static void RequestTagTextures()
		{
			foreach (int type in TaggedTypes) {
				if (type < 0 || type >= TextureAssets.Item.Length)
					continue;

				if (TextureAssets.Item[type].State == AssetState.NotLoaded)
					DeferredTextureTypes.Add(type);
			}

			_deferredColdCount = DeferredTextureTypes.Count;
			_deferStartTimestamp = Stopwatch.GetTimestamp();
			_deferStartUpdateCount = Main.GameUpdateCount;
			TaggedTypes.Clear();
		}

		// Tops the in-flight window back up to MaxTexturesInFlight each tick, so the engine never holds more
		// outstanding requests than one frame can absorb. Deliberately does not wait on anything: the whole
		// point is that the requests are still settling while the game carries on rendering.
		private static void PumpTexturePreload()
		{
			long start = Stopwatch.GetTimestamp();
			int inFlight = 0;

			for (int i = 0; i < _deferredLoadIndex; i++) {
				if (TextureAssets.Item[DeferredTextureTypes[i]].State != AssetState.Loaded)
					inFlight++;
			}

			while (inFlight < MaxTexturesInFlight && _deferredLoadIndex < DeferredTextureTypes.Count) {
				Asset<Texture2D> texture = TextureAssets.Item[DeferredTextureTypes[_deferredLoadIndex++]];

				if (texture.State == AssetState.Loaded)
					continue;

				Main.Assets.Request<Texture2D>(texture.Name, AssetRequestMode.AsyncLoad);
				inFlight++;
			}

			_deferredInFlight = inFlight;
			_preloadTicksSpent += Stopwatch.GetTimestamp() - start;
		}

		// Driven from UpdateUI rather than a world-update hook so a cascade triggered with the inventory
		// open still releases its notification while autopause is holding the world still.
		private static void ReleaseDeferredNotifications()
		{
			if (DeferredMessages.Count == 0)
				return;

			PumpTexturePreload();

			bool valveFired = ++_deferredTicksWaited >= MaxDeferredWaitTicks;
			if (!valveFired && (_deferredLoadIndex < DeferredTextureTypes.Count || _deferredInFlight > 0))
				return;

			double waitedMs = (Stopwatch.GetTimestamp() - _deferStartTimestamp) * 1000.0 / Stopwatch.Frequency;
			var postStopwatch = Stopwatch.StartNew();

			foreach (string message in DeferredMessages)
				Main.NewText(message);

			postStopwatch.Stop();
			SoundEngine.PlaySound(SoundID.ResearchComplete);

			// Average frame time is the smoothness metric, and the only one that survives the work moving
			// off this call: at 60fps a healthy run sits near 16.7ms, while the failed all-at-once AsyncLoad
			// attempt showed 654ms across a single frame. Total elapsed and posting cost both looked fine
			// there, so neither is evidence on its own. Pump ms is our own bookkeeping, expected to stay
			// near zero - if it is not, the in-flight scan is the problem rather than the loading.
			if (_deferredColdCount > 0) {
				uint framesElapsed = Main.GameUpdateCount - _deferStartUpdateCount;
				ModContent.GetInstance<YarnResearch>().Logger.Info(
					$"ResearchCascadeSystem notification: {_deferredColdCount} cold textures warmed over " +
					$"{framesElapsed} frames / {_deferredTicksWaited} UpdateUI ticks, {waitedMs:F2}ms wall clock " +
					$"({(framesElapsed == 0 ? waitedMs : waitedMs / framesElapsed):F2}ms average frame time)" +
					$"{(valveFired ? " (SAFETY VALVE FIRED - warming did not finish)" : "")}, " +
					$"pump cost {_preloadTicksSpent * 1000.0 / Stopwatch.Frequency:F2}ms, " +
					$"posting {DeferredMessages.Count} message(s) then took {postStopwatch.Elapsed.TotalMilliseconds:F2}ms");
			}

			ClearDeferredNotifications();
		}

		private static void ClearDeferredNotifications()
		{
			DeferredMessages.Clear();
			DeferredTextureTypes.Clear();
			TaggedTypes.Clear();
			_deferredTicksWaited = 0;
			_deferredColdCount = 0;
			_deferredLoadIndex = 0;
			_deferredInFlight = 0;
			_preloadTicksSpent = 0;
		}

		// taggedTypes collects the types that made it into the list, so the caller can pre-load exactly
		// those textures.
		private static string BuildTagList(Queue<int> types, List<int> taggedTypes)
		{
			var tags = new List<string>();

			foreach (int type in types) {
				if (!ContentSamples.ItemsByType.TryGetValue(type, out Item item))
					continue;

				tags.Add(ItemTagHandler.GenerateTag(item));
				taggedTypes.Add(type);
			}

			return string.Join("", tags);
		}
	}
}
