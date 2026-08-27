using Microsoft.Xna.Framework;
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
			Shimmer,
			Crate,
			Sacrifice,
			Shop,
		}

		// Chat label per origin, parallel to the enum above.
		private static readonly string[] NotificationLabels = {
			"Auto-researched",
			"Auto-crafted",
			"Auto-converted",
			"Auto-discovered",
			"Auto-unpacked",
			"Sacrificed",
			"Auto-researched from shop",
		};

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
			if (Main.gameMenu || !ResearchCrateContentsKeybind.JustPressed)
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
			bool catchUpBiomeTorches = torchGodsFavorJustUnlocked && config.AutoResearchBiomeTorches;

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
			if (ShimmerOutputsByInput.TryGetValue(type, out int transformOutput)) {
				AttemptShimmerResearch(transformOutput);
				return;
			}

			int decraftRecipeIndex = ShimmerTransforms.GetDecraftingRecipeIndex(type);
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
			var drops = new List<DropRateInfo>();
			var chainFeed = new DropRateInfoChainFeed(1f);

			foreach (IItemDropRule rule in Main.ItemDropsDB.GetRulesForItemID(crateType))
				rule.ReportDroprates(drops, chainFeed);

			return drops.Select(d => d.itemId).Distinct();
		}

		private static void ProcessCrateContents(int type)
		{
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

		public static void BeginBatch() => _batchQueue = new Queue<int>();

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
			if (stepsProcessed > 0) {
				ModContent.GetInstance<YarnResearch>().Logger.Info(
					$"ResearchCascadeSystem cascade: {stepsProcessed} steps processed, {researchedCount} items researched, " +
					$"max queue depth {maxQueueDepth}, took {stopwatch.Elapsed.TotalMilliseconds:F2}ms");
			}
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

				// _forcedMechanisms lets a manual trigger's drain fully resolve that mechanism's transitive
				// chain in one call even with its toggle off. Without it, only the first layer unlocked by
				// the trigger's initial pass would bypass the toggle; anything further downstream (found
				// only once this drain re-queues newly-unlocked items) would fall back to the toggle-gated
				// automatic behavior and be skipped, so the player would have to click several times before
				// the cascade converged.
				if (config.AutoResearchCraftable || _forcedMechanisms.HasFlag(Mechanism.Craftable))
					ProcessCraftableOutputs(type);

				if (config.AutoResearchBiomeTorches)
					ProcessBiomeTorchVariants(type);

				if ((config.AutoResearchShimmerOutputs || _forcedMechanisms.HasFlag(Mechanism.Shimmer)) && _shimmerDiscovered)
					ProcessShimmerOutputs(type);

				if (config.AutoResearchCrateContents || _forcedMechanisms.HasFlag(Mechanism.Crate))
					ProcessCrateContents(type);

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

			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (config.ShowAutoResearchNotifications) {
				for (int origin = 0; origin < PendingNotifications.Length; origin++) {
					if (PendingNotifications[origin].Count > 0)
						Main.NewText($"{NotificationLabels[origin]}: {BuildTagList(PendingNotifications[origin])}");
				}

				SoundEngine.PlaySound(SoundID.ResearchComplete);
			}

			foreach (Queue<int> queue in PendingNotifications)
				queue.Clear();
		}

		private static string BuildTagList(Queue<int> types)
		{
			var tags = new List<string>();

			foreach (int type in types) {
				if (ContentSamples.ItemsByType.TryGetValue(type, out Item item))
					tags.Add(ItemTagHandler.GenerateTag(item));
			}

			return string.Join("", tags);
		}
	}
}
