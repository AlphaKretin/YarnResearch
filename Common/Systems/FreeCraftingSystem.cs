using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ModLoader;
using YarnResearch.Common.Players;

namespace YarnResearch.Common.Systems
{
	public enum FreeCraftingMode
	{
		Off,
		// Researched stations and proximity Conditions count; ingredients are sourced as vanilla.
		Inventory,
		// As Inventory, and researched items also count as ingredients.
		Research,
	}

	// While the free-crafting toggle is on, every crafting interface behaves as if the player were standing
	// next to every crafting station they've researched, and in every biome/liquid whose Condition proxy
	// they've researched. In Inventory mode ingredient sourcing is untouched - inventory plus nearby chests,
	// exactly as vanilla. In Research mode every researched item is additionally an ingredient the player
	// always has and never spends.
	//
	// Recipe.FindRecipes decides availability from exactly two things (Recipe.UpdateRecipeList, per the
	// public patches repo): recipe.PlayerMeetsEnvironmentConditions(player), which reads player.adjTile, and
	// RecipeLoader.RecipeAvailable(recipe) -> recipe.Conditions.All(c => c.IsMet()), which reads live
	// player/world state. Faking that state for the duration of one FindRecipes call therefore covers
	// stations and Conditions together, and nothing outside that call - other UI, other mods, or this mod's
	// own CheckLiveConditionEdges - ever observes the fake values.
	public class FreeCraftingSystem : ModSystem
	{
		// One faked player flag: how to read its real value and how to write it, so ApplyFreeCrafting can
		// restore exactly what it overwrote without a separate hand-maintained save/restore list.
		private readonly record struct ProximityFlag(Func<Player, bool> Read, Action<Player, bool> Write);

		// TML's AddRecipe rewrite converts exactly six legacy needXxx fields into recipe Conditions
		// (Recipe.cs.patch's ReplaceCondition calls). Five of them stand for somewhere the player could
		// physically stand and are faked here; the sixth, ZenithWorld (from needMechdusa), is a world-seed
		// property rather than a place, so it keeps blocking its recipes. Every other Condition with a proxy
		// registered in ResearchCascadeSystem exists for NPC shop entries and never gates a vanilla recipe -
		// LogUnfakeableProxiedConditions reports any that turn out to.
		private static readonly Dictionary<Condition, ProximityFlag> ProximityConditionFakes = new()
		{
			[Condition.NearWater] = new(p => p.adjWaterSource, (p, on) => p.adjWaterSource = on),
			[Condition.NearLava] = new(p => p.adjLava, (p, on) => p.adjLava = on),
			[Condition.NearHoney] = new(p => p.adjHoney, (p, on) => p.adjHoney = on),
			[Condition.InSnow] = new(p => p.ZoneSnow, (p, on) => p.ZoneSnow = on),
			[Condition.InGraveyard] = new(p => p.ZoneGraveyard, (p, on) => p.ZoneGraveyard = on),
		};

		// What this call actually overwrote, so the restore only touches those rather than stomping the
		// tiles/biomes the player is genuinely in. Reused across calls (FindRecipes is single-threaded
		// UI-path work) to keep allocations off a method that runs on every inventory change.
		private static readonly List<int> FlippedTiles = [];
		private static readonly List<(ProximityFlag Flag, bool Original)> FlippedFlags = [];

		public override void Load()
		{
			On_Recipe.UpdateRecipeList += ApplyFreeCrafting;
		}

		public override void Unload()
		{
			On_Recipe.UpdateRecipeList -= ApplyFreeCrafting;
		}

		// Recipes carry their consumption rules individually, with no hook that covers all of them at once.
		public override void PostAddRecipes()
		{
			for (int i = 0; i < Recipe.numRecipes; i++)
				Main.recipe[i].AddConsumeIngredientCallback(KeepResearchedIngredients);
		}

		public override void PostSetupRecipes()
		{
			LogUnfakeableProxiedConditions();
		}

		// Large enough to cover any recipe's requirement, small enough that adding it to a carried stack in
		// the game's owned-item totals can't overflow.
		private const int ResearchedMaterialStack = 9999;

		private static readonly List<Item> ResearchedMaterials = [];
		private static int _researchedMaterialsVersion = -1;

		// One stand-in stack per researched item, offered to the recipe list as extra crafting materials
		// while the mode is Research. Null otherwise, which the caller treats as "nothing to add".
		public static IEnumerable<Item> GetResearchedMaterials(out ModPlayer.ItemConsumedCallback itemConsumedCallback)
		{
			itemConsumedCallback = null;

			if (Mode != FreeCraftingMode.Research)
				return null;

			if (_researchedMaterialsVersion != ResearchCascadeSystem.ResearchedTypesVersion)
			{
				ResearchedMaterials.Clear();
				foreach (int type in ResearchCascadeSystem.ResearchedItemTypes)
					ResearchedMaterials.Add(new Item(type, ResearchedMaterialStack));

				_researchedMaterialsVersion = ResearchCascadeSystem.ResearchedTypesVersion;
			}

			// KeepResearchedIngredients normally stops a craft before it reaches these stand-ins. Another
			// mod's callback running after it can put the amount back, and the craft then drains a stand-in
			// in place, so the list is rebuilt rather than left short.
			itemConsumedCallback = (_, _) => _researchedMaterialsVersion = -1;
			return ResearchedMaterials;
		}

		// The same callback runs for Shimmer decrafting, where the amount is what the player gets back.
		private static void KeepResearchedIngredients(Recipe recipe, int type, ref int amount, bool isDecrafting)
		{
			if (isDecrafting || amount <= 0 || Mode != FreeCraftingMode.Research)
				return;

			if (ResearchCascadeSystem.IsResearched(type) || ResearchedGroupMember(recipe, type))
				amount = 0;
		}

		// An "any iron bar" ingredient is listed under one member's type but is met by any of them.
		private static bool ResearchedGroupMember(Recipe recipe, int type)
		{
			foreach (int groupId in recipe.acceptedGroups)
			{
				if (!RecipeGroup.recipeGroups.TryGetValue(groupId, out RecipeGroup group) || !group.ValidItems.Contains(type))
					continue;

				foreach (int member in group.ValidItems)
				{
					if (ResearchCascadeSystem.IsResearched(member))
						return true;
				}
			}

			return false;
		}

		public static FreeCraftingMode Mode
		{
			get
			{
				if (Main.gameMenu || Main.LocalPlayer == null || !Main.LocalPlayer.active)
					return FreeCraftingMode.Off;

				return Main.LocalPlayer.GetModPlayer<YarnResearchPlayer>().FreeCraftingMode;
			}
		}

		public static bool Enabled => Mode != FreeCraftingMode.Off;

		public static void Cycle()
		{
			var modPlayer = Main.LocalPlayer.GetModPlayer<YarnResearchPlayer>();
			modPlayer.FreeCraftingMode = modPlayer.FreeCraftingMode switch
			{
				FreeCraftingMode.Off => FreeCraftingMode.Inventory,
				FreeCraftingMode.Inventory => FreeCraftingMode.Research,
				_ => FreeCraftingMode.Off,
			};

			// Rebuild the available-recipe list immediately rather than relying on whatever makes vanilla
			// refresh it next, so the crafting menu reflects the new mode on the same click.
			Recipe.UpdateRecipeList();
		}

		private static void ApplyFreeCrafting(On_Recipe.orig_UpdateRecipeList orig)
		{
			if (!Enabled)
			{
				orig();
				return;
			}

			Player player = Main.LocalPlayer;
			FlippedTiles.Clear();
			FlippedFlags.Clear();

			try
			{
				foreach (int tile in ResearchCascadeSystem.ResearchedStations)
					FakeAdjacentTile(player, tile);

				foreach ((Condition condition, ProximityFlag flag) in ProximityConditionFakes)
				{
					if (!ResearchCascadeSystem.ConditionProxyResearched(condition))
						continue;

					FlippedFlags.Add((flag, flag.Read(player)));
					flag.Write(player, true);
				}

				orig();
			}
			finally
			{
				foreach (int tile in FlippedTiles)
					player.adjTile[tile] = false;

				foreach ((ProximityFlag flag, bool original) in FlippedFlags)
					flag.Write(player, original);

				FlippedTiles.Clear();
				FlippedFlags.Clear();
			}
		}

		private static void FakeAdjacentTile(Player player, int tile)
		{
			if (tile < 0 || tile >= player.adjTile.Length)
				return;

			if (!player.adjTile[tile])
			{
				player.adjTile[tile] = true;
				FlippedTiles.Add(tile);
			}
		}

		// A Condition with a registered proxy is one the research cascade already treats as satisfiable, so
		// one that also gates a real recipe but has no faking mechanism here is a genuine gap: the cascade
		// will unlock the recipe's output while free crafting still refuses to craft it. Vanilla has none;
		// this exists so a modded recipe (or a vanilla change) that introduces one shows up in the log
		// instead of silently behaving inconsistently.
		private static void LogUnfakeableProxiedConditions()
		{
			List<string> gaps = [.. ResearchCascadeSystem.RecipeGatingConditions
				.Where(condition => ResearchCascadeSystem.IsConditionProxied(condition) &&
					!ProximityConditionFakes.ContainsKey(condition))
				.Select(condition => condition.Description.Value)
				.Distinct()];

			if (gaps.Count > 0)
			{
				ModContent.GetInstance<YarnResearch>().Logger.Info(
					$"FreeCraftingSystem: {gaps.Count} recipe-gating Condition(s) have a research proxy but no " +
					$"free-crafting equivalent, so their recipes stay blocked while free crafting is on: {string.Join(", ", gaps)}");
			}
		}
	}
}
