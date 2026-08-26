using System.Collections.Generic;
using Terraria;
using Terraria.GameContent.Creative;
using Terraria.ModLoader;
using YarnResearch.Common.Configs;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.Players
{
	public class YarnResearchPlayer : ModPlayer
	{
		// Player.inventory layout: 0-9 hotbar, 10-49 main storage grid, 50+ coins/ammo/trash - the bulk
		// buttons only touch the main storage grid, matching the backlog's "excluding the hotbar" scope
		// (coins/ammo/trash aren't meant to be swept either, so they're left out too).
		private const int MainInventoryStart = 10;
		private const int MainInventoryEnd = 50;

		private readonly Dictionary<Item[], (int[] Types, int[] Stacks)> _snapshots = new();

		public override void PostUpdate()
		{
			if (Player.whoAmI != Main.myPlayer)
				return;

			var config = ModContent.GetInstance<YarnResearchConfig>();

			if (Player.ZoneShimmer && ResearchCascadeSystem.MarkShimmerDiscovered() && config.AutoResearchShimmerOutputs)
				ResearchCascadeSystem.RunShimmerCatchupScan();

			if (config.AutoResearchHeldItems)
				RunScan(config);
		}

		// Callable independent of AutoResearchHeldItems - used by the manual trigger button.
		public static void ManualScan()
		{
			var config = ModContent.GetInstance<YarnResearchConfig>();
			Main.LocalPlayer.GetModPlayer<YarnResearchPlayer>().RunScan(config);
		}

		private void RunScan(YarnResearchConfig config)
		{
			ResearchCascadeSystem.BeginBatch();
			try {
				ScanAndDiff(Player.inventory);

				if (config.IncludeBankAndSafeInScan) {
					ScanAndDiff(Player.bank.item);
					ScanAndDiff(Player.bank2.item);
					ScanAndDiff(Player.bank3.item);
				}
			}
			finally {
				ResearchCascadeSystem.EndBatch();
			}
		}

		// Bulk inventory-declutter trigger: sacrifices every unresearched, non-favorited item in the main
		// storage grid toward research, via the same Main.CreativeMenu.SacrificeItem call vanilla's own
		// research-slot drag-and-drop uses - unlike ManualScan/CheckItem above, this actually consumes the
		// stack (partial sacrifices are allowed; a stack larger than the remaining threshold keeps its
		// leftover). Destructive and irreversible - the caller (the manual-trigger button) is responsible
		// for confirming with the player first.
		// Returns whether anything was actually sacrificed - the caller uses this to skip the "action
		// taken" SFX on a no-op click (e.g. nothing left in the main grid to sacrifice).
		public static bool BulkSacrificeUnresearched()
		{
			Item[] inventory = Main.LocalPlayer.inventory;
			bool sacrificedAnything = false;

			ResearchCascadeSystem.BeginBatch();
			try {
				for (int i = MainInventoryStart; i < MainInventoryEnd; i++) {
					Item item = inventory[i];
					if (item.IsAir || item.favorited || item.ResearchUnlockCount <= 0)
						continue;

					if (ResearchCascadeSystem.IsResearched(item.type))
						continue;

					ResearchCascadeSystem.RegisterSacrificeOrigin(item.type);
					Main.CreativeMenu.SacrificeItem(ref item, out int amountSacrificed, spawnExcessItem: false, onlySacrificeIfItWouldFinishResearch: false);
					ResearchCascadeSystem.ClearSacrificeOrigin(item.type);

					inventory[i] = item;
					sacrificedAnything |= amountSacrificed > 0;
				}
			}
			finally {
				ResearchCascadeSystem.EndBatch();
			}

			return sacrificedAnything;
		}

		// Bulk inventory-declutter trigger: destroys every fully-researched, non-favorited item in the
		// main storage grid, since a researched item can always be recrafted/pulled from the Research
		// menu instead. Destructive and irreversible - the caller (the manual-trigger button) is
		// responsible for confirming with the player first.
		// Returns whether anything was actually cleared - same purpose as BulkSacrificeUnresearched's
		// return value.
		public static bool BulkClearResearched()
		{
			Item[] inventory = Main.LocalPlayer.inventory;
			bool clearedAnything = false;

			for (int i = MainInventoryStart; i < MainInventoryEnd; i++) {
				Item item = inventory[i];
				if (item.IsAir || item.favorited)
					continue;

				if (!ResearchCascadeSystem.IsResearched(item.type))
					continue;

				inventory[i] = new Item();
				clearedAnything = true;
			}

			return clearedAnything;
		}

		private void ScanAndDiff(Item[] items)
		{
			if (!_snapshots.TryGetValue(items, out var snapshot) || snapshot.Types.Length != items.Length) {
				snapshot = (new int[items.Length], new int[items.Length]);
				_snapshots[items] = snapshot;
			}

			for (int i = 0; i < items.Length; i++) {
				Item item = items[i];
				if (snapshot.Types[i] == item.type && snapshot.Stacks[i] == item.stack)
					continue;

				snapshot.Types[i] = item.type;
				snapshot.Stacks[i] = item.stack;

				CheckItem(item);
			}
		}

		private static void CheckItem(Item item)
		{
			if (item.IsAir || item.ResearchUnlockCount <= 0)
				return;

			if (ResearchCascadeSystem.IsResearched(item.type))
				return;

			int? remaining = CreativeUI.GetSacrificesRemaining(item.type);
			if (remaining.HasValue && item.stack >= remaining.Value) {
				ResearchCascadeSystem.RegisterHeldOrigin(item.type);
				CreativeUI.ResearchItem(item.type);
				ResearchCascadeSystem.ClearHeldOrigin(item.type);
			}
		}
	}
}
