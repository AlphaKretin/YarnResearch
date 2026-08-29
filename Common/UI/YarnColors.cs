using Microsoft.Xna.Framework;

namespace YarnResearch.Common.UI
{
	// Every colour the mod picks for itself, in one place so they can be compared and adjusted together.
	// Colours taken from vanilla (a copied button's own override colours, Color.White for an untinted icon)
	// stay where they are used - this is only for the mod's own choices.
	public static class YarnColors
	{
		// Slot tints in the Journey duplication grid, drawn by SlotTint. Deliberately far apart in hue,
		// since a slot only ever shows one of them and the tint is the only thing distinguishing the states.
		public static readonly Color InfiniteBuffSlot = Color.LightGreen;

		// The slot the prefix picker popup is currently open for.
		public static readonly Color PrefixPickerTargetSlot = Color.LightGreen; // possibility for confusion exists but is low

		// The mod's own tooltip lines (hotkey hints, popup controls), so they read as this mod's rather than
		// as part of the item's real tooltip.
		public static readonly Color TooltipHint = Color.Pink;

		// A destructive powers-strip button whose confirm window is armed - see ConfirmGuard.
		public static readonly Color ArmedActionIcon = Color.Red;

		// Behind a powers-strip icon that doesn't have a drop shadow baked into its own sprite.
		public static readonly Color IconDropShadow = new(0, 0, 0, 150);
	}
}
