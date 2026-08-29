using System;
using System.Collections.Generic;
using Terraria;
using Terraria.GameContent.Creative;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using YarnResearch.Common.Configs;
using YarnResearch.Common.Systems;

namespace YarnResearch.Common.Players
{
	public class YarnResearchPlayer : ModPlayer
	{
		// Player.inventory layout: 0-9 hotbar, 10-49 main storage grid, 50+ coins/ammo/trash. The bulk
		// buttons only touch the main storage grid - sweeping the hotbar or the coin/ammo/trash slots is
		// never what the player means by "clear my inventory".
		private const int MainInventoryStart = 10;
		private const int MainInventoryEnd = 50;

		// Last-seen type/stack per inventory slot, so a scan only checks slots that actually changed.
		private int[] _snapshotTypes = Array.Empty<int>();
		private int[] _snapshotStacks = Array.Empty<int>();

		// Persistent default prefix per PrefixGroup for the Journey duplication prefix picker - e.g. choosing
		// "Warding" while looking at an accessory applies it to every future accessory duplication.
		public Dictionary<PrefixGroup, int> DefaultPrefixByGroup { get; } = new();

		// Not saved - the prefix PrefixPickerSystem.DuplicateWithPrefix is about to force onto the item it is
		// building, handed to YarnResearchGlobalItem.OnCreated and consumed there in the same call.
		private (int ItemType, int PrefixId)? _pendingOneShotPrefix;

		// Choosing "no modifier" clears the group's default rather than storing it: an unprefixed duplicate is
		// what a group with no entry already produces, so the two would be the same state stored two ways.
		public void SetDefaultPrefix(PrefixGroup group, int prefixId)
		{
			if (prefixId == PrefixTrial.NoPrefixId)
				DefaultPrefixByGroup.Remove(group);
			else
				DefaultPrefixByGroup[group] = prefixId;
		}

		// A group's default still has to survive being applied to this particular item - the group is a set of
		// items, not one item, so a prefix chosen on one member can be a no-op on another, and Item.Prefix
		// answers a no-op by rolling something random instead (see PrefixTrial).
		public bool TryGetDefaultPrefix(Item item, out int prefixId) =>
			DefaultPrefixByGroup.TryGetValue(PrefixGroup.Of(item), out prefixId) &&
			item.CanRollPrefix(prefixId) &&
			PrefixTrial.Of(item.type, prefixId).Applied;

		public void ArmOneShotPrefix(int itemType, int prefixId) => _pendingOneShotPrefix = (itemType, prefixId);

		public bool TryConsumeOneShotPrefix(int itemType, out int prefixId)
		{
			if (_pendingOneShotPrefix is { } pending && pending.ItemType == itemType) {
				prefixId = pending.PrefixId;
				_pendingOneShotPrefix = null;
				return true;
			}

			prefixId = 0;
			return false;
		}

		// Free-crafting toggle (see FreeCraftingSystem) - a property of the character, not the world, so a
		// player who wants it on keeps it on everywhere.
		public bool FreeCraftingEnabled { get; set; }

		public override void SaveData(TagCompound tag)
		{
			if (FreeCraftingEnabled)
				tag["FreeCrafting"] = true;

			if (DefaultPrefixByGroup.Count == 0)
				return;

			var entries = new List<TagCompound>();
			foreach (var (group, prefixId) in DefaultPrefixByGroup) {
				var entry = new TagCompound { ["Prefix"] = prefixId };
				group.Save(entry);
				entries.Add(entry);
			}

			tag["DefaultPrefixGroups"] = entries;
		}

		public override void LoadData(TagCompound tag)
		{
			FreeCraftingEnabled = tag.ContainsKey("FreeCrafting");

			// Defaults saved before groups existed were keyed by a single PrefixCategory, which no longer
			// identifies anything - that tag is left unread so those entries are simply dropped.
			if (!tag.TryGet("DefaultPrefixGroups", out List<TagCompound> entries))
				return;

			foreach (TagCompound entry in entries)
				DefaultPrefixByGroup[PrefixGroup.Load(entry)] = entry.GetInt("Prefix");
		}

		public override void PostUpdate()
		{
			if (Player.whoAmI != Main.myPlayer)
				return;

			var config = ModContent.GetInstance<YarnResearchConfig>();

			if (Player.ZoneShimmer && ResearchCascadeSystem.MarkShimmerDiscovered() && config.AutoResearchShimmerOutputs)
				ResearchCascadeSystem.RunShimmerCatchupScan();

			ResearchCascadeSystem.CheckLiveConditionEdges();
		}

		// Called from ResearchCascadeSystem.UpdateUI, not PostUpdate: the world-update path is skipped while
		// Journey autopause holds a menu open, which is exactly when items arrive from crafting or a chest.
		public static void AutoScan()
		{
			if (ModContent.GetInstance<YarnResearchConfig>().AutoResearchHeldItems)
				Main.LocalPlayer.GetModPlayer<YarnResearchPlayer>().RunScan();
		}

		// Re-forces the toggled banner/Garden Gnome proximity flags every tick - see
		// InfiniteBuffSystem.ForceProximityFlags for why this can't just be set once at toggle time.
		// PreModifyLuck is the right hook: vanilla resets those flags to their real proximity values before
		// it runs, and it runs before any other per-tick point that reads them, so one call here covers
		// both the buff-granting and luck paths. ExampleMod's own Garden Gnome workaround uses this hook too.
		public override bool PreModifyLuck(ref float luck)
		{
			if (Player.whoAmI == Main.myPlayer)
				InfiniteBuffSystem.ForceProximityFlags(Player);

			return true;
		}

		public override void OnRespawn()
		{
			if (Player.whoAmI == Main.myPlayer)
				InfiniteBuffSystem.RegrantToggledBuffs(Player);
		}

		// Entering the world needs the same re-grant as a respawn: LoadWorldData restores the toggle
		// bookkeeping but never calls AddBuff, so without this a buff toggled on in a previous session
		// isn't actually present until the player dies once.
		public override void OnEnterWorld()
		{
			if (Player.whoAmI == Main.myPlayer)
				InfiniteBuffSystem.RegrantToggledBuffs(Player);
		}

		// Callable independent of AutoResearchHeldItems - used by the manual trigger button.
		public static void ManualScan() => Main.LocalPlayer.GetModPlayer<YarnResearchPlayer>().RunScan();

		private void RunScan()
		{
			ResearchCascadeSystem.BeginBatch();
			try {
				ScanAndDiff(Player.inventory);
			}
			finally {
				ResearchCascadeSystem.EndBatch();
			}
		}

		// Bulk inventory-declutter trigger: sacrifices every unresearched, non-favorited item in the main
		// storage grid toward research, via the same Main.CreativeMenu.SacrificeItem call vanilla's own
		// research-slot drag-and-drop uses. Unlike ManualScan/CheckItem above, this actually consumes the
		// stack; partial sacrifices are allowed, and a stack larger than the remaining threshold keeps its
		// leftover. Destructive and irreversible - the caller (the manual-trigger button) is responsible for
		// confirming with the player first.
		// Returns whether anything was actually sacrificed, so the caller can skip the "action taken" SFX
		// on a no-op click.
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

					Main.CreativeMenu.SacrificeItem(ref item, out int amountSacrificed, spawnExcessItem: false, onlySacrificeIfItWouldFinishResearch: false);
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
		// menu instead. Destructive and irreversible - the caller (the manual-trigger button) is responsible
		// for confirming with the player first. Returns whether anything was actually cleared, same purpose
		// as BulkSacrificeUnresearched's return value.
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
			if (_snapshotTypes.Length != items.Length) {
				_snapshotTypes = new int[items.Length];
				_snapshotStacks = new int[items.Length];
			}

			for (int i = 0; i < items.Length; i++) {
				Item item = items[i];
				if (_snapshotTypes[i] == item.type && _snapshotStacks[i] == item.stack)
					continue;

				_snapshotTypes[i] = item.type;
				_snapshotStacks[i] = item.stack;

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
			if (remaining.HasValue && item.stack >= remaining.Value)
				ResearchCascadeSystem.ResearchAsHeldItem(item.type);
		}
	}
}
