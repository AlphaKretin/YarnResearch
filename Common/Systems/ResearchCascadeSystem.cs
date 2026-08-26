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
		// as PendingHeldOrigins but for recipe-cascade unlocks. Needed because, unlike the other origins,
		// this one used to be inferred from "_activeCascadeQueue != null" (i.e. running inside a
		// DrainCascade re-entry) - which silently broke for ManualCascadeScan, which calls
		// ProcessCraftableOutputs directly in a loop outside that re-entrant context, so its unlocks never
		// set that flag and got no notification queue at all.
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
			ResearchCrateContentsKeybind = KeybindLoader.RegisterKeybind(Mod, "ResearchCrateContents", "OemPeriod");
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
			ShimmerOutputsByInput.Clear();

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
		}

		private static void TryResearchRecipeOutput(int recipeIndex)
		{
			Recipe recipe = Main.recipe[recipeIndex];
			int outputType = recipe.createItem.type;

			if (ResearchedTypes.Contains(outputType))
				return;

			if (!AllIngredientsResearched(recipe) || !StationResearched(recipe))
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

			if (!StationItemTypesByTile.TryGetValue(recipe.requiredTile, out List<int> itemTypes))
				return false;

			foreach (int itemType in itemTypes) {
				if (ResearchedTypes.Contains(itemType))
					return true;
			}

			return false;
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
