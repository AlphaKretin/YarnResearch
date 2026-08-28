using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace YarnResearch.Common.Systems
{
	// Shared "where is the cursor in the Journey Mode power menu" state, so every feature that scopes a
	// hotkey or tooltip hint to the duplication grid agrees on the answer.
	public class DuplicationHoverSystem : ModSystem
	{
		// UpdateUI rather than a real frame counter: it ticks during Journey autopause, which is exactly
		// when these hover-gated hotkeys have to keep working.
		private static long _tick;
		private static long _lastHoverTick = -10;

		// Main.CreativeMenu (type CreativeUI) backs both the Research/sacrifice and Duplication panels of
		// the Journey Mode power-icon menu as one instance - vanilla's own Main.cs gates input on exactly
		// this combination (Main.cs.patch: "bool flag9 = CreativeMenu.Enabled && !CreativeMenu.Blocked;").
		// There's no further public distinction between its Research/Duplication tabs, so this only means
		// "the power menu is open at all" - the player's ordinary inventory is visible alongside it, so
		// anything that should apply to duplication-grid items specifically wants IsHoveringSlot instead.
		public static bool IsMenuOpen => Main.CreativeMenu.Enabled && !Main.CreativeMenu.Blocked;

		// Whether the cursor is over an actual duplication-grid slot. Reported from PrefixPickerSystem's
		// ItemSlot.DrawItemIcon hook, which runs during Draw - so a reader in Draw (a tooltip) sees a
		// tick delta of 0, and a reader in the next UpdateUI (a hotkey) sees 1. Accepting both keeps this
		// independent of ModSystem update order, at the cost of the flag outliving the hover by one tick.
		public static bool IsHoveringSlot => _tick - _lastHoverTick <= 1;

		public static void MarkHoveringSlot() => _lastHoverTick = _tick;

		public override void UpdateUI(GameTime gameTime) => _tick++;
	}
}
