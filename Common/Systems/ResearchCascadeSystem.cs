using System.Collections.Generic;
using Terraria;
using Terraria.GameContent.Creative;
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
		private static readonly Queue<string> PendingNotifications = new();

		public static bool IsResearched(int type)
		{
			if (ResearchedTypes.Contains(type))
				return true;

			CreativeUI.GetSacrificeCount(type, out bool fullyResearched);
			if (fullyResearched)
				ResearchedTypes.Add(type);

			return fullyResearched;
		}

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
				if (fullyResearched)
					MarkResearched(type, queue);
			}

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
					MarkResearched(outputType, queue);
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

		private static void MarkResearched(int type, Queue<int> queue)
		{
			if (!ResearchedTypes.Add(type))
				return;

			queue.Enqueue(type);
			PendingNotifications.Enqueue(Lang.GetItemNameValue(type));
		}

		private static void FlushNotifications()
		{
			if (PendingNotifications.Count == 0)
				return;

			var config = ModContent.GetInstance<YarnResearchConfig>();
			if (config.ShowAutoResearchNotifications) {
				var names = new List<string>(PendingNotifications);
				string message = names.Count == 1
					? $"Auto-researched: {names[0]}"
					: $"Auto-researched: {string.Join(", ", names)}";

				Main.NewText(message);
			}

			PendingNotifications.Clear();
		}
	}
}
