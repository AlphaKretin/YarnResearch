using System.Collections.Generic;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.Creative;
using Terraria.GameContent.UI.Chat;
using Terraria.ID;
using Terraria.ModLoader;
using YarnResearch.Common.Configs;

namespace YarnResearch.Common.Systems
{
	public class ResearchCascadeSystem : ModSystem
	{
		private static readonly HashSet<int> ResearchedTypes = new();
		private static int _lastKnownEditId = -1;

		private static readonly Dictionary<int, List<int>> RecipesConsumingItem = new();
		private static readonly Dictionary<int, List<int>> StationItemTypesByTile = new();

		// Populated by YarnResearchPlayer just before it calls CreativeUI.ResearchItem, so the
		// detection loop below can tell "held-threshold" apart from a manual vanilla-UI research.
		private static readonly HashSet<int> PendingHeldOrigins = new();

		private static readonly Queue<int> PendingHeldNotifications = new();
		private static readonly Queue<int> PendingCraftableNotifications = new();

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

		public override void PostAddRecipes()
		{
			RecipesConsumingItem.Clear();
			StationItemTypesByTile.Clear();

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

		public override void OnWorldLoad()
		{
			ResearchedTypes.Clear();
			PendingHeldOrigins.Clear();
			_lastKnownEditId = -1;

			for (int type = 0; type < ItemLoader.ItemCount; type++) {
				if (!ContentSamples.ItemsByType.TryGetValue(type, out Item item) || item.ResearchUnlockCount <= 0)
					continue;

				CreativeUI.GetSacrificeCount(type, out bool fullyResearched);
				if (fullyResearched)
					ResearchedTypes.Add(type);
			}
		}

		public override void PostUpdateEverything()
		{
			int currentEditId = Main.LocalPlayerCreativeTracker.ItemSacrifices.LastEditId;
			if (currentEditId == _lastKnownEditId)
				return;

			_lastKnownEditId = currentEditId;

			var queue = new Queue<int>();

			for (int type = 0; type < ItemLoader.ItemCount; type++) {
				if (ResearchedTypes.Contains(type))
					continue;

				if (!ContentSamples.ItemsByType.TryGetValue(type, out Item item) || item.ResearchUnlockCount <= 0)
					continue;

				CreativeUI.GetSacrificeCount(type, out bool fullyResearched);
				if (!fullyResearched)
					continue;

				// Anything not registered by YarnResearchPlayer this tick was researched by some
				// other means (typically the player using the vanilla Research UI directly) - still
				// feed the cascade so downstream recipes unlock, but don't announce it as "ours".
				bool heldOrigin = PendingHeldOrigins.Remove(type);
				MarkResearched(type, queue, heldOrigin ? PendingHeldNotifications : null);
			}

			PendingHeldOrigins.Clear();

			DrainCascade(queue);
			FlushNotifications();
		}

		private void DrainCascade(Queue<int> queue)
		{
			var config = ModContent.GetInstance<YarnResearchConfig>();

			while (queue.Count > 0) {
				int type = queue.Dequeue();

				if (!config.AutoResearchCraftable || !RecipesConsumingItem.TryGetValue(type, out List<int> recipeIndices))
					continue;

				foreach (int recipeIndex in recipeIndices) {
					Recipe recipe = Main.recipe[recipeIndex];
					int outputType = recipe.createItem.type;

					if (ResearchedTypes.Contains(outputType))
						continue;

					if (!AllIngredientsResearched(recipe) || !StationResearched(recipe))
						continue;

					CreativeUI.ResearchItem(outputType);
					MarkResearched(outputType, queue, PendingCraftableNotifications);
				}
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
			if (PendingHeldNotifications.Count == 0 && PendingCraftableNotifications.Count == 0)
				return;

			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (config.ShowAutoResearchNotifications) {
				if (PendingHeldNotifications.Count > 0)
					Main.NewText($"Auto-researched: {BuildTagList(PendingHeldNotifications)}");

				if (PendingCraftableNotifications.Count > 0)
					Main.NewText($"Auto-crafted: {BuildTagList(PendingCraftableNotifications)}");

				SoundEngine.PlaySound(SoundID.ResearchComplete);
			}

			PendingHeldNotifications.Clear();
			PendingCraftableNotifications.Clear();
		}

		private static string BuildTagList(Queue<int> types)
		{
			var tags = new List<string>();

			foreach (int type in types) {
				if (ContentSamples.ItemsByType.TryGetValue(type, out Item item))
					tags.Add(ItemTagHandler.GenerateTag(item));
			}

			return string.Join(", ", tags);
		}
	}
}
