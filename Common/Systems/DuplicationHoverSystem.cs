using Microsoft.Xna.Framework;
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
		private static long _lastGridDrawTick = -10;

		// Whether the duplication grid itself is on screen. Reported from the same ItemSlot.DrawItemIcon hook
		// as the hover flag below, on the CreativeInfinite slot context that only the duplication grid draws
		// with - so switching power category stops the reports and this goes false, with the same tick
		// tolerance as IsHoveringSlot. Main.CreativeMenu can't answer this: one CreativeUI instance backs
		// both the Research/sacrifice and Duplication panels, with no public distinction between them.
		public static bool IsGridVisible => _tick - _lastGridDrawTick <= 1;

		public static void MarkGridDrawn() => _lastGridDrawTick = _tick;

		// Whether the cursor is over an actual duplication-grid slot. Reported from PrefixPickerSystem's
		// ItemSlot.DrawItemIcon hook, which runs during Draw - so a reader in Draw (a tooltip) sees a
		// tick delta of 0, and a reader in the next UpdateUI (a hotkey) sees 1. Accepting both keeps this
		// independent of ModSystem update order, at the cost of the flag outliving the hover by one tick.
		public static bool IsHoveringSlot => _tick - _lastHoverTick <= 1;

		public static void MarkHoveringSlot() => _lastHoverTick = _tick;

		public override void UpdateUI(GameTime gameTime) => _tick++;
	}
}
