using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.GameContent.Creative;
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

		private static readonly Queue<int> PendingHeldNotifications = new();
		private static readonly Queue<int> PendingCraftableNotifications = new();
		private static readonly Queue<int> PendingShimmerNotifications = new();

		// Set while a cascade triggered by HandleResearched is draining, so a CreativeUI.ResearchItem
		// call made from within that drain - which synchronously re-enters HandleResearched via
		// GlobalItem.OnResearched - is recognized as our own cascade rather than an external research.
		private static Queue<int> _activeCascadeQueue;

		// Set for the duration of a caller-defined batch (e.g. one YarnResearchPlayer scan pass), so
		// several distinct top-level HandleResearched calls within it share one queue/flush instead of
		// each draining and flushing independently.
		private static Queue<int> _batchQueue;

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

		// Idempotent - safe to call every tick while the player is near Shimmer. Only the first call
		// (per world) does anything: it flips the persisted flag and runs a one-time catch-up pass over
		// already-researched items so any newly-reachable Shimmer outputs unlock immediately, batched
		// into one notification.
		public static void DiscoverShimmer()
		{
			if (_shimmerDiscovered)
				return;

			_shimmerDiscovered = true;

			int[] snapshot = ResearchedTypes.ToArray();
			var stopwatch = Stopwatch.StartNew();

			BeginBatch();
			try {
				foreach (int type in snapshot)
					ProcessShimmerOutputs(type);
			}
			finally {
				EndBatch();
			}

			stopwatch.Stop();
			ModContent.GetInstance<YarnResearch>().Logger.Info(
				$"ResearchCascadeSystem shimmer discovery: scanned {snapshot.Length} already-researched items, " +
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
			bool isReentrant = _activeCascadeQueue != null;

			Queue<int> notificationQueue = heldOrigin
				? PendingHeldNotifications
				: shimmerOrigin
					? PendingShimmerNotifications
					: isReentrant ? PendingCraftableNotifications : null;

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

				if (config.AutoResearchCraftable && RecipesConsumingItem.TryGetValue(type, out List<int> recipeIndices)) {
					foreach (int recipeIndex in recipeIndices) {
						Recipe recipe = Main.recipe[recipeIndex];
						int outputType = recipe.createItem.type;

						if (ResearchedTypes.Contains(outputType))
							continue;

						if (!AllIngredientsResearched(recipe) || !StationResearched(recipe))
							continue;

						// Synchronously re-enters HandleResearched above via GlobalItem.OnResearched.
						CreativeUI.ResearchItem(outputType);
					}
				}

				if (config.AutoResearchShimmerOutputs && _shimmerDiscovered)
					ProcessShimmerOutputs(type);

				if (queue.Count > maxQueueDepth)
					maxQueueDepth = queue.Count;
			}
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
			if (PendingHeldNotifications.Count == 0 && PendingCraftableNotifications.Count == 0 && PendingShimmerNotifications.Count == 0)
				return;

			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (config.ShowAutoResearchNotifications) {
				if (PendingHeldNotifications.Count > 0)
					Main.NewText($"Auto-researched: {BuildTagList(PendingHeldNotifications)}");

				if (PendingCraftableNotifications.Count > 0)
					Main.NewText($"Auto-crafted: {BuildTagList(PendingCraftableNotifications)}");

				if (PendingShimmerNotifications.Count > 0)
					Main.NewText($"Auto-discovered: {BuildTagList(PendingShimmerNotifications)}");

				SoundEngine.PlaySound(SoundID.ResearchComplete);
			}

			PendingHeldNotifications.Clear();
			PendingCraftableNotifications.Clear();
			PendingShimmerNotifications.Clear();
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
