using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
		private static readonly HashSet<int> ResearchedTypes = new();

		// Proxy signal for recipe Conditions that gate on a biome/field-effect proximity check with no
		// requiredItem representation the cascade can otherwise see (see ConditionsSatisfiable). Each entry:
		// researching any one of the listed items means the player can trivially recreate that proximity
		// anywhere via Journey Mode's infinite items (a bucket, a block, gravestones), standing in for the
		// live Condition.IsMet() check. These five are the only recipe-gating proximity Conditions vanilla
		// has, found by surveying every legacy needXxx -> Condition rewrite in Recipe.cs.patch (2026-08-27).
		// Condition.ZenithWorld (world-seed flag, gates Ocram's Razor) needs no entry here - it isn't a
		// proximity check, so ConditionsSatisfiable's IsMet() fallback for unlisted Conditions already
		// handles it correctly (permanently true or false for a given world's whole life).
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
		};

		// Reverse of ConditionProxyItemTypes: proxy item type -> Conditions it satisfies. Built once since
		// ConditionProxyItemTypes is static data.
		private static readonly Dictionary<int, List<Condition>> ConditionsByProxyItemType = BuildConditionsByProxyItemType();

		// Demon and Crimson Altars share a single TileID (26, TileID.DemonAltar - confirmed against the
		// wiki, which lists both under id 26) - they differ only by tile frame/style, not by id. Altars are
		// normally found in the world only - unlike the Condition proxies above, there's no obtainable item
		// form at all in a typical world, so StationResearched (which otherwise requires a researched item
		// whose createTile matches recipe.requiredTile) can never be satisfied for an altar-gated recipe.

		// True if this world has a genuine placeable altar item available, in which case _everNearAltar
		// below is disabled and the player is expected to actually go obtain and research the real item
		// instead of relying on the proxy. Two independent sources, both checked live in
		// AltarProxyDisabled: vanilla Skyblock worlds, where the Eye of Cthulhu drops a placeable
		// Demon/Crimson Altar item (ItemID 5532/5533) if no altars exist in the world - not a recipe, so
		// detected via Main.skyblockWorld instead (the item id always exists in ContentSamples regardless
		// of world type, so presence-checking the id itself would be a false positive in a normal world);
		// and a mod recipe whose createItem.createTile is TileID.DemonAltar (e.g. the "Craftable Altars"
		// Workshop mod), computed below in PostAddRecipes.
		private static bool _altarItemExceptionAvailable;

		// Persisted per-world proxy for StationResearched's altar case: once true, a recipe requiring
		// TileID.DemonAltar as its station is treated as satisfied from then on, standing in for a placed
		// altar the player found in the world - same shape as _shimmerDiscovered's persisted flag. See
		// _altarItemExceptionAvailable for when this proxy is disabled instead.
		private static bool _everNearAltar;

		// Reverse of recipe.Conditions, covering every Condition attached to any recipe (proxied or not):
		// Condition -> recipe indices gated by it. Needed for the same reason as RecipesRequiringTile below -
		// a recipe whose ingredients/station were already satisfied earlier must get rechecked the instant
		// its Condition becomes satisfiable, not only on the next full manual rescan. Two independent things
		// can make a Condition here newly satisfiable, both read this same map: a registered proxy item
		// getting researched (ProcessCraftableOutputs, via ConditionsByProxyItemType), or the Condition's
		// live IsMet() itself turning true (CheckLiveConditionEdges) - the latter applies even to a proxied
		// Condition, since a player standing in the real thing right now should work exactly like it does in
		// vanilla, with no proxy research required first.
		private static readonly Dictionary<Condition, List<int>> RecipesRequiringCondition = new();

		// Last-observed IsMet() per Condition in RecipesRequiringCondition, so CheckLiveConditionEdges can
		// detect a false->true transition rather than re-triggering every tick the condition happens to be
		// true.
		private static readonly Dictionary<Condition, bool> LiveConditionWasMet = new();

		private static Dictionary<int, List<Condition>> BuildConditionsByProxyItemType()
		{
			var result = new Dictionary<int, List<Condition>>();

			foreach ((Condition condition, HashSet<int> itemTypes) in ConditionProxyItemTypes) {
				foreach (int itemType in itemTypes) {
					if (!result.TryGetValue(itemType, out List<Condition> conditions)) {
						conditions = new List<Condition>();
						result[itemType] = conditions;
					}

					conditions.Add(condition);
				}
			}

			return result;
		}

		private static readonly Dictionary<int, List<int>> RecipesConsumingItem = new();
		private static readonly Dictionary<int, List<int>> StationItemTypesByTile = new();

		// Reverse of the above two: recipe.requiredTile -> recipe indices needing that tile as a station.
		// Needed because a recipe only ever gets (re-)checked when one of its *ingredients* is newly
		// researched (via RecipesConsumingItem) - a station becoming newly available on its own never
		// triggered a recheck, so a recipe whose ingredients were already satisfied earlier (station not
		// yet known at that moment) could stay permanently unchecked for the rest of that cascade, only
		// getting picked up later by a full re-scan (e.g. clicking the manual cascade button again).
		// Confirmed live: a single shimmer-triggered cascade unlocked both Iron and Lead ore/bars but
		// missed a bar-and-anvil recipe; a manual "Research Craftable Recipes" click found it immediately
		// after, since it re-checks every already-researched item from scratch rather than only deltas.
		private static readonly Dictionary<int, List<int>> RecipesRequiringTile = new();

		// Direct Shimmer transmute table (ItemID.Sets.ShimmerTransformToItem), input type -> output type.
		// Built once since this is static game data. Decraft outputs are looked up dynamically instead
		// (see ProcessShimmerOutputs) since RecipeLoader.DecraftAvailable depends on live Conditions
		// (biome, world type, etc.) that can change without a recipe-data reload.
		private static readonly Dictionary<int, int> ShimmerOutputsByInput = new();

		// Persisted per-world: once true, Shimmer cascade edges stay open for the rest of the world's life.
		private static bool _shimmerDiscovered;

		// Populated by YarnResearchPlayer just before it calls CreativeUI.ResearchItem, so
		// HandleResearched can tell "held-threshold" apart from a manual vanilla-UI research.
		private static readonly HashSet<int> PendingHeldOrigins = new();

		// Populated by AttemptShimmerResearch just before it calls CreativeUI.ResearchItem, same purpose
		// as PendingHeldOrigins but for Shimmer-sourced unlocks.
		private static readonly HashSet<int> PendingShimmerOrigins = new();

		// Populated by AttemptCrateResearch just before it calls CreativeUI.ResearchItem, same purpose
		// as PendingHeldOrigins but for crate-content unlocks.
		private static readonly HashSet<int> PendingCrateOrigins = new();

		// Populated by YarnResearchPlayer.BulkSacrificeUnresearched just before a stack finishes
		// researching via Main.CreativeMenu.SacrificeItem, same purpose as PendingHeldOrigins but for the
		// bulk-sacrifice manual trigger (which, unlike held-item research, actually consumes the stack).
		private static readonly HashSet<int> PendingSacrificeOrigins = new();

		// Populated by ProcessCraftableOutputs just before it calls CreativeUI.ResearchItem, same purpose
		// as PendingHeldOrigins but for recipe-cascade unlocks. Can't be inferred from
		// "_activeCascadeQueue != null" (i.e. running inside a DrainCascade re-entry), since
		// ManualCascadeScan calls ProcessCraftableOutputs directly in a loop outside that re-entrant
		// context.
		private static readonly HashSet<int> PendingCraftableOrigins = new();

		private static readonly Queue<int> PendingHeldNotifications = new();
		private static readonly Queue<int> PendingCraftableNotifications = new();
		private static readonly Queue<int> PendingShimmerNotifications = new();
		private static readonly Queue<int> PendingCrateNotifications = new();
		private static readonly Queue<int> PendingSacrificeNotifications = new();

		public static ModKeybind ResearchCrateContentsKeybind { get; private set; }

		// Set while a cascade triggered by HandleResearched is draining, so a CreativeUI.ResearchItem
		// call made from within that drain - which synchronously re-enters HandleResearched via
		// GlobalItem.OnResearched - is recognized as our own cascade rather than an external research.
		private static Queue<int> _activeCascadeQueue;

		// Set for the duration of a caller-defined batch (e.g. one YarnResearchPlayer scan pass), so
		// several distinct top-level HandleResearched calls within it share one queue/flush instead of
		// each draining and flushing independently.
		private static Queue<int> _batchQueue;

		// Set for the duration of a manual trigger's own BeginBatch/EndBatch, so DrainCascade's recursive
		// continuation of THAT SAME mechanism ignores its config toggle too, not just the manual trigger's
		// own initial pass - see DrainCascade for why this matters.
		private static bool _forceCraftableDrain;
		private static bool _forceShimmerDrain;
		private static bool _forceCrateDrain;

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

		// Watches every recipe-gating Condition (proxied or not), plus the altar tile-station proxy below,
		// for a false->true transition, so a recipe blocked only by an unmet Condition or an unvisited
		// altar gets rechecked the instant it actually becomes true (e.g. the player walks near lava, or
		// finds a Demon Altar) - including a proxied Condition, so meeting the real requirement naturally
		// works even before the proxy item is ever researched, exactly like a real crafting attempt would.
		// TryResearchRecipeOutput/ConditionsSatisfiable/StationResearched still re-verify everything, so this
		// is just a trigger. Called from YarnResearchPlayer.PostUpdate, same tick as the Shimmer-discovery
		// check - this isn't input-driven like the crate keybind above, it's a live world/biome state check,
		// so it belongs on the same per-tick path as Shimmer discovery rather than UpdateUI (which only
		// needs to survive autopause for keybind/UI purposes, not for this).
		public static void CheckLiveConditionEdges()
		{
			if (RecipesRequiringCondition.Count == 0 && RecipesRequiringTile.Count == 0)
				return;

			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (!config.AutoResearchCraftable)
				return;

			// Player.adjTile/adjWaterSource/adjLava/adjHoney (what NearWater/NearLava/NearHoney read) are
			// normally only recomputed by the crafting UI's own per-frame update, not by ordinary Player.Update
			// - confirmed live 2026-08-27 (diagnostic log): NearWater stayed false the whole time standing in
			// water and only flipped true the instant the inventory was opened. AdjTiles() is the public
			// vanilla method the crafting UI itself calls to do that computation (confirmed via
			// tModLoader.xml's doc comment on Player.adjTile, which cross-references it) - forcing it here
			// keeps those flags fresh so the live Conditions we check reflect the player's actual position
			// regardless of whether any menu is open. The altar loop below reads the same freshly-computed
			// adjTile array.
			Main.LocalPlayer.AdjTiles();

			List<int> recipesToRecheck = null;

			foreach ((Condition condition, List<int> recipeIndices) in RecipesRequiringCondition) {
				bool isMet = condition.IsMet();
				bool wasMet = LiveConditionWasMet.TryGetValue(condition, out bool previous) && previous;
				LiveConditionWasMet[condition] = isMet;

				if (isMet && !wasMet)
					(recipesToRecheck ??= new List<int>()).AddRange(recipeIndices);
			}

			if (!_everNearAltar && !AltarProxyDisabled() &&
				RecipesRequiringTile.TryGetValue(TileID.DemonAltar, out List<int> altarRecipeIndices) &&
				Main.LocalPlayer.adjTile[TileID.DemonAltar]) {
				_everNearAltar = true;
				(recipesToRecheck ??= new List<int>()).AddRange(altarRecipeIndices);
			}

			if (recipesToRecheck == null)
				return;

			BeginBatch();
			try {
				foreach (int recipeIndex in recipesToRecheck)
					TryResearchRecipeOutput(recipeIndex);
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
			if (fullyResearched)
				ResearchedTypes.Add(type);

			return fullyResearched;
		}

		public static void RegisterHeldOrigin(int type) => PendingHeldOrigins.Add(type);

		public static void ClearHeldOrigin(int type) => PendingHeldOrigins.Remove(type);

		public static void RegisterSacrificeOrigin(int type) => PendingSacrificeOrigins.Add(type);

		public static void ClearSacrificeOrigin(int type) => PendingSacrificeOrigins.Remove(type);

		public static bool ShimmerDiscovered => _shimmerDiscovered;

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

		// Requires MarkShimmerDiscovered to have been called at least once (per world) - a no-op
		// otherwise. Runs a catch-up pass over already-researched items so any reachable Shimmer outputs
		// unlock, batched into one notification. Callable both by the automatic path (immediately after
		// first discovery, gated by the config toggle there) and directly by the manual trigger button
		// (bypassing the toggle, same as the recipe-cascade manual trigger).
		public static void RunShimmerCatchupScan()
		{
			if (!_shimmerDiscovered)
				return;

			int[] snapshot = ResearchedTypes.ToArray();
			var stopwatch = Stopwatch.StartNew();

			BeginBatch();
			_forceShimmerDrain = true;
			try {
				foreach (int type in snapshot)
					ProcessShimmerOutputs(type);
			}
			finally {
				EndBatch();
				_forceShimmerDrain = false;
			}

			stopwatch.Stop();
			ModContent.GetInstance<YarnResearch>().Logger.Info(
				$"ResearchCascadeSystem shimmer catch-up: scanned {snapshot.Length} already-researched items, " +
				$"took {stopwatch.Elapsed.TotalMilliseconds:F2}ms total (includes the cascade drain logged separately above)");
		}

		// Direct transmute takes priority (matches vanilla: a set ShimmerTransformToItem entry means the
		// item does not attempt to decraft). Otherwise, falls back to decraft: finds the recipe Shimmer
		// would currently reverse via ShimmerTransforms.GetDecraftingRecipeIndex (which itself calls
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

		private static void AttemptShimmerResearch(int outputType)
		{
			if (ResearchedTypes.Contains(outputType))
				return;

			PendingShimmerOrigins.Add(outputType);
			// Synchronously re-enters HandleResearched below via GlobalItem.OnResearched.
			CreativeUI.ResearchItem(outputType);
			PendingShimmerOrigins.Remove(outputType);
		}

		// Unpacks a researched crate's possible contents on demand, regardless of the
		// AutoResearchCrateContents toggle - this is the manual hotkey path's deliberate override.
		public static void TryUnpackCrate(int type)
		{
			if (!ItemID.Sets.OpenableBag[type] || !IsResearched(type))
				return;

			BeginBatch();
			_forceCrateDrain = true;
			try {
				ProcessCrateContents(type);
			}
			finally {
				EndBatch();
				_forceCrateDrain = false;
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
			if (!IsUnresearchedAndResearchable(outputType))
				return;

			PendingCrateOrigins.Add(outputType);
			// Synchronously re-enters HandleResearched below via GlobalItem.OnResearched.
			CreativeUI.ResearchItem(outputType);
			PendingCrateOrigins.Remove(outputType);
		}

		private static bool IsUnresearchedAndResearchable(int type)
		{
			if (ResearchedTypes.Contains(type))
				return false;

			return ContentSamples.ItemsByType.TryGetValue(type, out Item item) && item.ResearchUnlockCount > 0;
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

			bool heldOrigin = PendingHeldOrigins.Remove(type);
			bool shimmerOrigin = PendingShimmerOrigins.Remove(type);
			bool crateOrigin = PendingCrateOrigins.Remove(type);
			bool sacrificeOrigin = PendingSacrificeOrigins.Remove(type);
			bool craftableOrigin = PendingCraftableOrigins.Remove(type);
			bool isReentrant = _activeCascadeQueue != null;

			Queue<int> notificationQueue = heldOrigin
				? PendingHeldNotifications
				: shimmerOrigin
					? PendingShimmerNotifications
					: crateOrigin
						? PendingCrateNotifications
						: sacrificeOrigin
							? PendingSacrificeNotifications
							: craftableOrigin
								? PendingCraftableNotifications
								: null;

			if (isReentrant) {
				// The active DrainCascade loop below owns processing this type further.
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
			LiveConditionWasMet.Clear();
			ShimmerOutputsByInput.Clear();
			_altarItemExceptionAvailable = false;

			int[] shimmerTransforms = ItemID.Sets.ShimmerTransformToItem;
			for (int type = 0; type < shimmerTransforms.Length; type++) {
				if (shimmerTransforms[type] > 0)
					ShimmerOutputsByInput[type] = shimmerTransforms[type];
			}

			for (int i = 0; i < Recipe.numRecipes; i++) {
				Recipe recipe = Main.recipe[i];

				foreach (Item ingredient in recipe.requiredItem) {
					if (!RecipesConsumingItem.TryGetValue(ingredient.type, out List<int> recipeIndices)) {
						recipeIndices = new List<int>();
						RecipesConsumingItem[ingredient.type] = recipeIndices;
					}

					recipeIndices.Add(i);
				}

				if (recipe.requiredTile >= 0) {
					if (!RecipesRequiringTile.TryGetValue(recipe.requiredTile, out List<int> tileRecipeIndices)) {
						tileRecipeIndices = new List<int>();
						RecipesRequiringTile[recipe.requiredTile] = tileRecipeIndices;
					}

					tileRecipeIndices.Add(i);
				}

				foreach (Condition condition in recipe.Conditions) {
					if (!RecipesRequiringCondition.TryGetValue(condition, out List<int> conditionRecipes)) {
						conditionRecipes = new List<int>();
						RecipesRequiringCondition[condition] = conditionRecipes;
					}

					conditionRecipes.Add(i);
				}

				if (recipe.createItem.createTile == TileID.DemonAltar)
					_altarItemExceptionAvailable = true;
			}

			for (int type = 0; type < ItemLoader.ItemCount; type++) {
				if (!ContentSamples.ItemsByType.TryGetValue(type, out Item item))
					continue;

				if (item.createTile == -1)
					continue;

				if (!StationItemTypesByTile.TryGetValue(item.createTile, out List<int> itemTypes)) {
					itemTypes = new List<int>();
					StationItemTypesByTile[item.createTile] = itemTypes;
				}

				itemTypes.Add(type);
			}
		}

		public override void SaveWorldData(TagCompound tag)
		{
			if (_shimmerDiscovered)
				tag["shimmerDiscovered"] = true;

			if (_everNearAltar)
				tag["everNearAltar"] = true;
		}

		public override void LoadWorldData(TagCompound tag)
		{
			_shimmerDiscovered = tag.ContainsKey("shimmerDiscovered");
			_everNearAltar = tag.ContainsKey("everNearAltar");
		}

		public override void ClearWorld()
		{
			_shimmerDiscovered = false;
			_everNearAltar = false;
		}

		public override void OnWorldLoad()
		{
			ResearchedTypes.Clear();
			PendingHeldOrigins.Clear();
			PendingShimmerOrigins.Clear();
			PendingCrateOrigins.Clear();
			PendingSacrificeOrigins.Clear();
			PendingCraftableOrigins.Clear();

			for (int type = 0; type < ItemLoader.ItemCount; type++) {
				if (!ContentSamples.ItemsByType.TryGetValue(type, out Item item) || item.ResearchUnlockCount <= 0)
					continue;

				CreativeUI.GetSacrificeCount(type, out bool fullyResearched);
				if (fullyResearched)
					ResearchedTypes.Add(type);
			}
		}

		private static void DrainCascade(Queue<int> queue, ref int stepsProcessed, ref int maxQueueDepth)
		{
			var config = ModContent.GetInstance<YarnResearchConfig>();

			while (queue.Count > 0) {
				int type = queue.Dequeue();
				stepsProcessed++;

				// The || _force* flags let a manual trigger's own drain fully resolve THAT mechanism's
				// transitive chain in one call, even with its toggle off - without them, only the first
				// layer unlocked by the manual trigger's initial pass would use the bypass; anything
				// further downstream (found only once this drain re-queues newly-unlocked items) would
				// silently fall back to the toggle-gated automatic behavior and get skipped, requiring
				// several repeated manual clicks to fully converge (confirmed live: repeatedly clicking
				// the Shimmer button kept finding new results for ~7 clicks before settling).
				if (config.AutoResearchCraftable || _forceCraftableDrain)
					ProcessCraftableOutputs(type);

				if ((config.AutoResearchShimmerOutputs || _forceShimmerDrain) && _shimmerDiscovered)
					ProcessShimmerOutputs(type);

				if (config.AutoResearchCrateContents || _forceCrateDrain)
					ProcessCrateContents(type);

				if (queue.Count > maxQueueDepth)
					maxQueueDepth = queue.Count;
			}
		}

		// type may complete a recipe two different ways: as a newly-researched ingredient (checked via
		// RecipesConsumingItem) or as a newly-researched station item (checked via RecipesRequiringTile,
		// keyed by the tile it places) - a recipe needs both its check paths covered, since a recipe whose
		// ingredients were already satisfied earlier (station not yet known at that point) would otherwise
		// never get re-checked once the station itself shows up later.
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

			if (!AllIngredientsResearched(recipe) || !StationResearched(recipe) || !ConditionsSatisfiable(recipe))
				return;

			PendingCraftableOrigins.Add(outputType);
			// Synchronously re-enters HandleResearched above via GlobalItem.OnResearched.
			CreativeUI.ResearchItem(outputType);
			PendingCraftableOrigins.Remove(outputType);
		}

		// Catch-up pass, ignoring the AutoResearchCraftable toggle - callable directly by the manual
		// trigger button so a player who keeps the toggle off can still fire the cascade on demand.
		public static void ManualCascadeScan()
		{
			int[] snapshot = ResearchedTypes.ToArray();
			var stopwatch = Stopwatch.StartNew();

			BeginBatch();
			_forceCraftableDrain = true;
			try {
				foreach (int type in snapshot)
					ProcessCraftableOutputs(type);
			}
			finally {
				EndBatch();
				_forceCraftableDrain = false;
			}

			stopwatch.Stop();
			ModContent.GetInstance<YarnResearch>().Logger.Info(
				$"ResearchCascadeSystem manual cascade scan: scanned {snapshot.Length} already-researched items, " +
				$"took {stopwatch.Elapsed.TotalMilliseconds:F2}ms total (includes the cascade drain logged separately above)");
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

			if (StationItemTypesByTile.TryGetValue(recipe.requiredTile, out List<int> itemTypes)) {
				foreach (int itemType in itemTypes) {
					if (ResearchedTypes.Contains(itemType))
						return true;
				}
			}

			return recipe.requiredTile == TileID.DemonAltar && _everNearAltar;
		}

		private static bool AltarProxyDisabled() => Main.skyblockWorld || _altarItemExceptionAvailable;

		// A live-true Condition always satisfies itself first, same as a real crafting attempt - a proxy is
		// only consulted as a fallback when the Condition isn't actually met right now. A Condition with no
		// registered proxy and no live match blocks the cascade, so an unrecognized Condition (e.g. from
		// another mod's recipe) behaves the same way a real crafting attempt would, rather than being
		// silently bypassed - which was the original Gravedigger's Shovel bug.
		private static bool ConditionsSatisfiable(Recipe recipe)
		{
			foreach (Condition condition in recipe.Conditions) {
				if (condition.IsMet())
					continue;

				if (!ConditionProxyItemTypes.TryGetValue(condition, out HashSet<int> proxyItemTypes) ||
					!proxyItemTypes.Any(ResearchedTypes.Contains))
					return false;
			}

			return true;
		}

		private static void MarkResearched(int type, Queue<int> queue, Queue<int> notificationQueue)
		{
			if (!ResearchedTypes.Add(type))
				return;

			queue.Enqueue(type);
			notificationQueue?.Enqueue(type);
		}

		private static void FlushNotifications()
		{
			if (PendingHeldNotifications.Count == 0 && PendingCraftableNotifications.Count == 0 &&
				PendingShimmerNotifications.Count == 0 && PendingCrateNotifications.Count == 0 &&
				PendingSacrificeNotifications.Count == 0)
				return;

			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (config.ShowAutoResearchNotifications) {
				if (PendingHeldNotifications.Count > 0)
					Main.NewText($"Auto-researched: {BuildTagList(PendingHeldNotifications)}");

				if (PendingCraftableNotifications.Count > 0)
					Main.NewText($"Auto-crafted: {BuildTagList(PendingCraftableNotifications)}");

				if (PendingShimmerNotifications.Count > 0)
					Main.NewText($"Auto-discovered: {BuildTagList(PendingShimmerNotifications)}");

				if (PendingCrateNotifications.Count > 0)
					Main.NewText($"Auto-unpacked: {BuildTagList(PendingCrateNotifications)}");

				if (PendingSacrificeNotifications.Count > 0)
					Main.NewText($"Sacrificed: {BuildTagList(PendingSacrificeNotifications)}");

				SoundEngine.PlaySound(SoundID.ResearchComplete);
			}

			PendingHeldNotifications.Clear();
			PendingCraftableNotifications.Clear();
			PendingShimmerNotifications.Clear();
			PendingCrateNotifications.Clear();
			PendingSacrificeNotifications.Clear();
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
