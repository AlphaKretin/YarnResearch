using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using YarnResearch.Common.Players;

namespace YarnResearch.Common.Systems
{
	// While the free-crafting toggle is on, every crafting interface behaves as if the player were standing
	// next to every crafting station they've researched, and in every biome/liquid whose Condition proxy
	// they've researched. Ingredient sourcing is untouched - inventory plus nearby chests, exactly as vanilla.
	//
	// Recipe.FindRecipes decides availability from exactly two things (Recipe.UpdateRecipeList, per the
	// public patches repo): recipe.PlayerMeetsEnvironmentConditions(player), which reads player.adjTile, and
	// RecipeLoader.RecipeAvailable(recipe) -> recipe.Conditions.All(c => c.IsMet()), which reads live
	// player/world state. Faking that state for the duration of one FindRecipes call therefore covers
	// stations and Conditions together, and nothing outside that call - other UI, other mods, or this mod's
	// own CheckLiveConditionEdges - ever observes the fake values.
	public class FreeCraftingSystem : ModSystem
	{
		// TML's AddRecipe rewrite converts exactly six legacy needXxx fields into recipe Conditions
		// (Recipe.cs.patch's ReplaceCondition calls). Five of them stand for somewhere the player could
		// physically stand and are faked here; the sixth, ZenithWorld (from needMechdusa), is a world-seed
		// property rather than a place, so it keeps blocking its recipes. Every other Condition with a proxy
		// registered in ResearchCascadeSystem exists for NPC shop entries and never gates a vanilla recipe -
		// LogUnfakeableProxiedConditions reports any that turn out to.
		private static readonly Dictionary<Condition, Action<Player>> ProximityConditionFakes = new() {
			[Condition.NearWater] = player => player.adjWaterSource = true,
			[Condition.NearLava] = player => player.adjLava = true,
			[Condition.NearHoney] = player => player.adjHoney = true,
			[Condition.InSnow] = player => player.ZoneSnow = true,
			[Condition.InGraveyard] = player => player.ZoneGraveyard = true,
		};

		// Which adjTile entries this call actually flipped, so the restore only clears those rather than
		// stomping the tiles the player is genuinely standing next to. Reused across calls (FindRecipes is
		// single-threaded UI-path work) to keep an allocation off a method that runs on every inventory change.
		private static readonly List<int> FlippedTiles = new();

		public override void Load()
		{
			On_Recipe.UpdateRecipeList += ApplyFreeCrafting;
		}

		public override void Unload()
		{
			On_Recipe.UpdateRecipeList -= ApplyFreeCrafting;
		}

		public override void PostSetupRecipes()
		{
			LogUnfakeableProxiedConditions();
		}

		public static bool Enabled {
			get {
				if (Main.gameMenu || Main.LocalPlayer == null || !Main.LocalPlayer.active)
					return false;

				return Main.LocalPlayer.GetModPlayer<YarnResearchPlayer>().FreeCraftingEnabled;
			}
		}

		public static void Toggle()
		{
			var modPlayer = Main.LocalPlayer.GetModPlayer<YarnResearchPlayer>();
			modPlayer.FreeCraftingEnabled = !modPlayer.FreeCraftingEnabled;

			// Rebuild the available-recipe list immediately rather than relying on whatever makes vanilla
			// refresh it next (Recipe.FindRecipes, the old explicit trigger, was removed in 1.4.5), so the
			// crafting menu reflects the toggle on the same click.
			Recipe.UpdateRecipeList();
		}

		private static void ApplyFreeCrafting(On_Recipe.orig_UpdateRecipeList orig)
		{
			if (!Enabled) {
				orig();
				return;
			}

			Player player = Main.LocalPlayer;
			bool adjWaterSource = player.adjWaterSource;
			bool adjLava = player.adjLava;
			bool adjHoney = player.adjHoney;
			bool zoneSnow = player.ZoneSnow;
			bool zoneGraveyard = player.ZoneGraveyard;
			FlippedTiles.Clear();

			try {
				foreach (int tile in ResearchCascadeSystem.ResearchedStations)
					FakeAdjacentTile(player, tile);

				// The altar proxy has no researchable station item behind it (altars aren't obtainable
				// normally) - it's the same "has ever been near one" flag the cascade's StationResearched
				// falls back to.
				if (ResearchCascadeSystem.EverNearAltar)
					FakeAdjacentTile(player, TileID.DemonAltar);

				foreach ((Condition condition, Action<Player> fake) in ProximityConditionFakes) {
					if (ResearchCascadeSystem.ConditionProxyResearched(condition))
						fake(player);
				}

				orig();
			}
			finally {
				foreach (int tile in FlippedTiles)
					player.adjTile[tile] = false;

				FlippedTiles.Clear();
				player.adjWaterSource = adjWaterSource;
				player.adjLava = adjLava;
				player.adjHoney = adjHoney;
				player.ZoneSnow = zoneSnow;
				player.ZoneGraveyard = zoneGraveyard;
			}
		}

		private static void FakeAdjacentTile(Player player, int tile)
		{
			if (tile < 0 || tile >= player.adjTile.Length)
				return;

			if (!player.adjTile[tile]) {
				player.adjTile[tile] = true;
				FlippedTiles.Add(tile);
			}

			// A researched station only satisfies the recipes of the tiles it counts as (a Table counting as
			// a Work Bench, etc.) if those aliases are set too - vanilla's own AdjTiles applies this same
			// public mapping when it fills adjTile from real proximity.
			List<int> countsAs = Recipe.TileCountsAs[tile];
			if (countsAs == null)
				return;

			foreach (int alias in countsAs) {
				if (alias >= 0 && alias < player.adjTile.Length && !player.adjTile[alias]) {
					player.adjTile[alias] = true;
					FlippedTiles.Add(alias);
				}
			}
		}

		// A Condition with a registered proxy is one the research cascade already treats as satisfiable, so
		// one that also gates a real recipe but has no faking mechanism here is a genuine gap: the cascade
		// will unlock the recipe's output while free crafting still refuses to craft it. Vanilla has none;
		// this exists so a modded recipe (or a vanilla change) that introduces one shows up in the log
		// instead of silently behaving inconsistently.
		private static void LogUnfakeableProxiedConditions()
		{
			List<string> gaps = ResearchCascadeSystem.RecipeGatingConditions
				.Where(condition => ResearchCascadeSystem.IsConditionProxied(condition) &&
					!ProximityConditionFakes.ContainsKey(condition))
				.Select(condition => condition.Description.Value)
				.Distinct()
				.ToList();

			if (gaps.Count > 0) {
				ModContent.GetInstance<YarnResearch>().Logger.Info(
					$"FreeCraftingSystem: {gaps.Count} recipe-gating Condition(s) have a research proxy but no " +
					$"free-crafting equivalent, so their recipes stay blocked while free crafting is on: {string.Join(", ", gaps)}");
			}
		}
	}
}
