using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;
using YarnResearch.Common.Configs;
using YarnResearch.Common.Players;
using YarnResearch.Common.UI;

namespace YarnResearch.Common.Systems
{
	public class InfiniteBuffSystem : ModSystem
	{
		// What this system does with a given item, and which buffType that drives.
		private enum BuffItemKind
		{
			None,
			GardenGnome,
			Banner,
			Placed,
			Consumable,
		}

		// Toggle state lives on the local player (InfiniteBuffPlayer). BuffStates only gets an entry for a
		// buffType actually touched (player toggle, auto-default, tiering) - absence means "never decided",
		// not "off". It covers potions/food and every whitelisted placed buff item - NOT banners or Garden
		// Gnome, which have no buffType of their own and use bespoke per-tick proximity forcing instead.
		private static InfiniteBuffPlayer State => Main.LocalPlayer.GetModPlayer<InfiniteBuffPlayer>();

		// Every buffType whose process-global TimeLeftDoesNotDecrease/buffNoTimeDisplay flags are currently
		// switched on by this system.
		private static readonly HashSet<int> FlaggedBuffs = new();

		// Curated whitelists of non-potion buff items that grant a normal Player.buffType buff - itemType ->
		// buffType. Activated buffs come from using the item and can be right-click dismissed like a potion.
		private static readonly Dictionary<int, int> ActivatedBuffItems = new() {
			{ ItemID.AmmoBox, BuffID.AmmoBox },
			{ ItemID.BewitchingTable, BuffID.Bewitched },
			{ ItemID.CrystalBall, BuffID.Clairvoyance },
			{ ItemID.SliceOfCake, BuffID.SugarRush },
			{ ItemID.SharpeningStation, BuffID.Sharpened },
			{ ItemID.WarTable, BuffID.WarTable },
			{ ItemID.DeadCellsPotionStation, BuffID.DeadCellsPotionStation },
			{ ItemID.BottledHoney, BuffID.Honey },
			{ ItemID.HoneyBucket, BuffID.Honey },
			{ ItemID.BottomlessHoneyBucket, BuffID.Honey },
		};

		// Aura buffs come from tile proximity. They can't be right-click dismissed, and vanilla removes them
		// itself on leaving proximity.
		private static readonly Dictionary<int, int> AuraBuffItems = new() {
			{ ItemID.CatBast, BuffID.CatBast },
			{ ItemID.Campfire, BuffID.Campfire },
			{ ItemID.Fireplace, BuffID.Campfire },
			{ ItemID.HeartLantern, BuffID.HeartLamp },
			{ ItemID.StarinaBottle, BuffID.StarInBottle },
			{ ItemID.Sunflower, BuffID.Sunflower },
		};

		private static readonly int[] WellFedTierOrder = { BuffID.WellFed, BuffID.WellFed2, BuffID.WellFed3 };

		// Banners are a bespoke case: every banner shares one generic BuffID.MonsterBanner buff, while the
		// per-enemy damage bonus is a separate non-buff bool array (Main.SceneMetrics.NPCBannerBuff) that
		// vanilla recomputes from tile proximity every tick. BannerStates tracks individual banner *items*.
		// The shared BuffID.MonsterBanner buff itself goes through the normal
		// SetInfinite/TimeLeftDoesNotDecrease path, granted once when the first banner is toggled on;
		// letting vanilla re-grant it from the forced proximity flags instead made the buff flicker on and
		// off. ForceProximityFlags still forces the per-enemy damage-bonus flags every tick, which is read
		// live at hit time rather than in a specific tick phase.
		//
		// Garden Gnome's luck bonus is not a real Player.buffType/BuffID at all - no buff icon, implemented
		// purely via the Player.HasGardenGnomeNearby proximity bool - so it gets the same per-tick forcing
		// as banners, just a single bool instead of a set.
		private static bool AnyBannerOn(InfiniteBuffPlayer state) => state.BannerStates.ContainsValue(true);

		// Vanilla banner itemType -> banner ID. Vanilla only maps the other way (BannerSystem.BannerToItem).
		private static Dictionary<int, int> _vanillaBannerIds;

		public static ModKeybind ToggleInfiniteBuffKeybind { get; private set; }

		private static LocalizedText _buffBarFullText;

		private static On_Player.hook_DelBuff _delBuffHook;
		private static On_ItemSlot.hook_DrawItemIcon _drawItemIconHook;
		private static On_Main.hook_UpdateSceneMetrics _updateSceneMetricsHook;

		public override void Load()
		{
			ToggleInfiniteBuffKeybind = KeybindLoader.RegisterKeybind(Mod, "ToggleInfiniteBuff", "Mouse3");
			_buffBarFullText = Mod.GetLocalization($"{nameof(InfiniteBuffSystem)}.BuffBarFull");

			_delBuffHook = (On_Player.orig_DelBuff orig, Player self, int b) =>
			{
				if (self.whoAmI == Main.myPlayer)
					HandleDismiss(self.buffType[b]);

				orig(self, b);
			};
			On_Player.DelBuff += _delBuffHook;

			// Marks items shown as infinite-toggled in the Journey Mode Duplication panel. Scoped to that
			// one slot context deliberately, rather than GlobalItem.PreDrawInInventory everywhere, to avoid
			// a distracting highlight in the normal inventory/hotbar/chests. Hooks DrawItemIcon rather than
			// the outer Draw because it hands us the icon's exact center point and size limit directly.
			_drawItemIconHook = (On_ItemSlot.orig_DrawItemIcon orig, Item item, int context, SpriteBatch spriteBatch, Vector2 screenPositionForItemCenter, float scale, float sizeLimit, Color environmentColor, float itemFade, bool flip) =>
			{
				if (context == ItemSlot.Context.CreativeInfinite && IsItemInfinite(item))
					SlotTint.Draw(spriteBatch, screenPositionForItemCenter, sizeLimit * scale, YarnColors.InfiniteBuffSlot);

				return orig(item, context, spriteBatch, screenPositionForItemCenter, scale, sizeLimit, environmentColor, itemFade, flip);
			};
			On_ItemSlot.DrawItemIcon += _drawItemIconHook;

			// The scene scan runs from Draw (through the lighting engine) rather than the update tick, and
			// resets the banner flags to real tile proximity. Anything drawn later that frame - the Monster
			// Banner buff tooltip's enemy list - would see them cleared until the next tick's forcing.
			_updateSceneMetricsHook = orig =>
			{
				orig();

				if (!Main.gameMenu)
					ForceProximityFlags(Main.LocalPlayer);
			};
			On_Main.UpdateSceneMetrics += _updateSceneMetricsHook;
		}

		public override void PostSetupContent()
		{
			_vanillaBannerIds = new Dictionary<int, int>();
			for (int bannerId = 1; bannerId < BannerSystem.MaxBannerTypes; bannerId++)
			{
				if (BannerSystem.BannerToNPC(bannerId) != NPCID.None)
					_vanillaBannerIds[BannerSystem.BannerToItem(bannerId)] = bannerId;
			}
		}

		public override void Unload()
		{
			ToggleInfiniteBuffKeybind = null;
			_vanillaBannerIds = null;

			if (_delBuffHook != null)
			{
				On_Player.DelBuff -= _delBuffHook;
				_delBuffHook = null;
			}

			if (_drawItemIconHook != null)
			{
				On_ItemSlot.DrawItemIcon -= _drawItemIconHook;
				_drawItemIconHook = null;
			}

			if (_updateSceneMetricsHook != null)
			{
				On_Main.UpdateSceneMetrics -= _updateSceneMetricsHook;
				_updateSceneMetricsHook = null;
			}
		}

		// Single source of truth for what an item counts as here, so the tooltip gate, the auto-default on
		// research, and the manual toggle can't disagree about a given item. BuffType is 0 for Garden Gnome,
		// which has no buff of its own.
		private static (BuffItemKind Kind, int BuffType) Classify(Item item)
		{
			if (item.type == ItemID.GardenGnome)
				return (BuffItemKind.GardenGnome, 0);

			if (IsBannerItem(item.type))
				return (BuffItemKind.Banner, BuffID.MonsterBanner);

			if (ActivatedBuffItems.TryGetValue(item.type, out int placedBuffType) ||
				AuraBuffItems.TryGetValue(item.type, out placedBuffType))
				return (BuffItemKind.Placed, placedBuffType);

			// item.consumable, not item.potion: item.potion only marks items that inflict Potion Sickness,
			// which excludes non-sickness buff potions (Builder, Flipper, Torch God's Flavor, etc.) - those
			// still need to toggle like any other consumed buff item.
			if (item.consumable && item.buffType > 0)
				return (BuffItemKind.Consumable, item.buffType);

			return (BuffItemKind.None, 0);
		}

		// The gate every entry point shares: the feature is on, and the item is researched.
		private static bool BuffTogglesAllowed(Item item) =>
			ModContent.GetInstance<YarnResearchConfig>().InfiniteResearchedBuffs &&
			ResearchCascadeSystem.IsResearched(item.type);

		// Whether hoverItem is something TryToggle would act on - used to gate the tooltip hint,
		// independent of its current on/off state.
		public static bool IsToggleable(Item item) =>
			BuffTogglesAllowed(item) && Classify(item).Kind != BuffItemKind.None;

		public static bool IsItemInfinite(Item item)
		{
			if (item == null || item.IsAir)
				return false;

			(BuffItemKind kind, int buffType) = Classify(item);

			return kind switch
			{
				BuffItemKind.None => false,
				BuffItemKind.GardenGnome => State.GardenGnome == true,
				BuffItemKind.Banner => State.BannerStates.GetValueOrDefault(item.type),
				_ => IsInfinite(buffType),
			};
		}

		public override void UpdateUI(GameTime gameTime)
		{
			if (Main.gameMenu || !ToggleInfiniteBuffKeybind.JustPressed)
				return;

			if (!DuplicationHoverSystem.IsHoveringSlot)
				return;

			Item hoverItem = Main.HoverItem;
			if (!hoverItem.IsAir)
				TryToggle(hoverItem);
		}

		// ItemID.Sets.BannerStrength[type].Enabled is NOT an "is this item a banner" flag - it's true for
		// every item, gating the per-item damage-scaling curve override - so the actual banner check goes
		// through the item -> banner ID mappings. NPCLoader.BannerItemToNPC only knows modded banners (whose
		// banner ID is their NPC type), so vanilla ones come from _vanillaBannerIds.
		// Returns -1 for anything that isn't a banner item.
		private static int BannerIdForItem(int itemType) =>
			_vanillaBannerIds != null && _vanillaBannerIds.TryGetValue(itemType, out int bannerId)
				? bannerId
				: NPCLoader.BannerItemToNPC(itemType);

		private static bool IsBannerItem(int itemType) => BannerIdForItem(itemType) >= 0;

		// Re-forces the bespoke proximity flags for banners/Garden Gnome so vanilla's own buff-granting, luck
		// calculation and banner tooltip see them as active. Vanilla keeps resetting them to their real
		// proximity values, so this is called both every tick from YarnResearchPlayer.PreModifyLuck and
		// straight after each scene scan, not just once at toggle time.
		public static void ForceProximityFlags(Player player)
		{
			InfiniteBuffPlayer state = player.GetModPlayer<InfiniteBuffPlayer>();

			if (state.GardenGnome == true)
				player.HasGardenGnomeNearby = true;

			if (!AnyBannerOn(state))
				return;

			Main.SceneMetrics.hasBanner = true;
			foreach (var (itemType, on) in state.BannerStates)
			{
				if (!on)
					continue;

				int bannerId = BannerIdForItem(itemType);
				if (bannerId >= 0 && bannerId < Main.SceneMetrics.NPCBannerBuff.Length)
					Main.SceneMetrics.NPCBannerBuff[bannerId] = true;
			}
		}

		// Auto-default on research: a newly-researched buff item turns itself on, unless the player has
		// already made an explicit decision about that buffType.
		public static void HandleItemResearched(Item item)
		{
			if (!BuffTogglesAllowed(item))
				return;

			(BuffItemKind kind, int buffType) = Classify(item);
			InfiniteBuffPlayer state = State;

			switch (kind)
			{
				case BuffItemKind.GardenGnome:
					state.GardenGnome ??= true;
					break;

				case BuffItemKind.Banner:
					if (!state.BannerStates.ContainsKey(item.type) &&
						(AnyBannerOn(state) || SetInfinite(Main.LocalPlayer, BuffID.MonsterBanner, on: true)))
						state.BannerStates[item.type] = true;
					break;

				case BuffItemKind.Placed:
					if (!state.BuffStates.ContainsKey(buffType))
						SetInfinite(Main.LocalPlayer, buffType, on: true);
					break;

				case BuffItemKind.Consumable:
					if (ItemID.Sets.IsFood[item.type])
						HandleFoodResearched(buffType);
					else if (!state.BuffStates.ContainsKey(buffType))
						SetInfinite(Main.LocalPlayer, buffType, on: DefaultsOn(buffType));
					break;
			}
		}

		// Tipsy is vanilla's one mixed-effect buff registered as a debuff (Main.debuff[BuffID.Tipsy] = true),
		// so right-click dismissal never works on it the way it does for other buffs - it defaults OFF
		// instead of the usual default-ON so it can't get stuck permanently active. Ale and Sake are in
		// ItemID.Sets.IsFood, so this has to be honoured on the food path too, not just the potion one.
		private static bool DefaultsOn(int buffType) => buffType != BuffID.Tipsy;

		// Well Fed comes in three escalating tiers that share no buffType - only the best researched tier
		// should be held, so a better one turns the others off. A tier the player already decided on is left
		// alone, like any other buffType.
		private static void HandleFoodResearched(int buffType)
		{
			Dictionary<int, bool> states = State.BuffStates;
			if (states.ContainsKey(buffType))
				return;

			int tierRank = Array.IndexOf(WellFedTierOrder, buffType);
			if (tierRank < 0)
			{
				// Non-Well-Fed food: same default rule as potions.
				SetInfinite(Main.LocalPlayer, buffType, on: DefaultsOn(buffType));
				return;
			}

			int currentBestRank = -1;
			for (int i = 0; i < WellFedTierOrder.Length; i++)
			{
				if (states.GetValueOrDefault(WellFedTierOrder[i]))
					currentBestRank = i;
			}

			if (tierRank <= currentBestRank)
				return;

			SetInfinite(Main.LocalPlayer, buffType, on: true);
			for (int i = 0; i < WellFedTierOrder.Length; i++)
			{
				if (i != tierRank && states.GetValueOrDefault(WellFedTierOrder[i]))
					SetInfinite(Main.LocalPlayer, WellFedTierOrder[i], on: false);
			}
		}

		// Manual hotkey entry point - unlike HandleItemResearched's auto-default, a refused toggle-ON here
		// gives chat feedback (see SetInfinite) since it's a direct player action.
		public static void TryToggle(Item hoverItem)
		{
			if (!BuffTogglesAllowed(hoverItem))
				return;

			(BuffItemKind kind, int buffType) = Classify(hoverItem);
			InfiniteBuffPlayer state = State;

			switch (kind)
			{
				case BuffItemKind.GardenGnome:
					state.GardenGnome = state.GardenGnome != true;
					break;

				case BuffItemKind.Banner:
					// The shared buff is granted with the first toggled banner and dropped with the last.
					if (state.BannerStates.GetValueOrDefault(hoverItem.type))
					{
						state.BannerStates[hoverItem.type] = false;
						if (!AnyBannerOn(state))
							SetInfinite(Main.LocalPlayer, BuffID.MonsterBanner, on: false);
					}
					else if (AnyBannerOn(state) ||
						SetInfinite(Main.LocalPlayer, BuffID.MonsterBanner, on: true, isManualToggle: true))
					{
						state.BannerStates[hoverItem.type] = true;
					}
					break;

				case BuffItemKind.Placed:
				case BuffItemKind.Consumable:
					SetInfinite(Main.LocalPlayer, buffType, on: !state.BuffStates.GetValueOrDefault(buffType),
						isManualToggle: true);
					break;
			}
		}

		// Returns whether the toggle was actually applied - false means a toggle-ON was refused by the
		// buff-cap safeguard, which banner toggling needs to know before adding to ToggledBanners.
		public static bool SetInfinite(Player player, int buffType, bool on, bool isManualToggle = false)
		{
			Dictionary<int, bool> states = player.GetModPlayer<InfiniteBuffPlayer>().BuffStates;

			if (on && IsBuffBarFull(player) && player.FindBuffIndex(buffType) < 0)
			{
				if (isManualToggle)
					Main.NewText(_buffBarFullText.Value);
				else
					states.Remove(buffType);

				return false;
			}

			states[buffType] = on;
			SetGlobalFlags(buffType, on);

			if (on)
			{
				if (player.FindBuffIndex(buffType) < 0)
					player.AddBuff(buffType, 60 * 60 * 60);
			}
			else
			{
				int index = player.FindBuffIndex(buffType);
				if (index >= 0)
					player.DelBuff(index);
			}

			return true;
		}

		private static bool IsBuffBarFull(Player player)
		{
			for (int i = 0; i < Player.MaxBuffs; i++)
			{
				if (player.buffType[i] <= 0)
					return false;
			}

			return true;
		}

		// A removed buff untoggles itself, except for aura buffs: those can legitimately expire via a real
		// DelBuff call on leaving proximity, which must not be read as a dismiss. Vanilla never wires them to
		// the right-click-dismiss UI either (right-clicking a toggled Cozy Fire never reaches Player.DelBuff),
		// so the mod's own toggle hotkey is the only way to turn those off.
		//
		// BuffID.MonsterBanner is held via TimeLeftDoesNotDecrease rather than being proximity-dependent once
		// any banner is toggled on, so a DelBuff on it can only be a deliberate right-click dismiss, and turns
		// every toggled banner off to match.
		public static void HandleDismiss(int buffType)
		{
			InfiniteBuffPlayer state = State;
			if (!state.BuffStates.GetValueOrDefault(buffType))
				return;

			if (buffType == BuffID.MonsterBanner)
			{
				foreach (int itemType in state.BannerStates.Keys.ToArray())
					state.BannerStates[itemType] = false;
			}
			else if (AuraBuffItems.ContainsValue(buffType))
			{
				return;
			}

			state.BuffStates[buffType] = false;
			SetGlobalFlags(buffType, on: false);
		}

		// Re-applies every toggled-on buffType: the process-global TimeLeftDoesNotDecrease/buffNoTimeDisplay
		// flags (reset by ClearWorld) and the buff itself if the player doesn't have it. Banners/Garden Gnome
		// need no handling here - ForceProximityFlags runs every tick regardless.
		public static void RegrantToggledBuffs(Player player)
		{
			foreach (var (buffType, on) in player.GetModPlayer<InfiniteBuffPlayer>().BuffStates.ToArray())
			{
				if (on)
					SetInfinite(player, buffType, on: true);
			}
		}

		// Gives every researched buff item the player hasn't decided on the same default a fresh research
		// would, so a character new to a world (or to this feature) starts with them on.
		public static void ApplyDefaultsForResearched()
		{
			for (int type = 1; type < ItemLoader.ItemCount; type++)
			{
				if (ContentSamples.ItemsByType.TryGetValue(type, out Item item) && Classify(item).Kind != BuffItemKind.None)
					HandleItemResearched(item);
			}
		}

		public static bool IsInfinite(int buffType) => State.BuffStates.GetValueOrDefault(buffType);

		private static void SetGlobalFlags(int buffType, bool on)
		{
			BuffID.Sets.TimeLeftDoesNotDecrease[buffType] = on;
			Main.buffNoTimeDisplay[buffType] = on;

			if (on)
				FlaggedBuffs.Add(buffType);
			else
				FlaggedBuffs.Remove(buffType);
		}

		// The toggle state belongs to the player and survives; only the process-global flags it set are
		// reset, so they don't leak onto the next character or world. This also runs during mod unload,
		// when Main.LocalPlayer may have no ModPlayers, so it must not read player state.
		public override void ClearWorld()
		{
			foreach (int buffType in FlaggedBuffs)
			{
				BuffID.Sets.TimeLeftDoesNotDecrease[buffType] = false;
				Main.buffNoTimeDisplay[buffType] = false;
			}

			FlaggedBuffs.Clear();
		}
	}
}
