using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria.UI;

namespace YarnResearch.Common.UI.ManualTriggersUI
{
	// The visible face of a GroupOptionButton in the Journey Mode powers menu: an icon centered, and
	// scaled down if needed, inside the element's bounds. Appended as a child of the button rather than
	// using GroupOptionButton's own icon rendering, which real vanilla buttons don't use either. The
	// owning button handles hover detection and tooltips, so this is always mouse-transparent.
	public class ItemIconButton : UIElement
	{
		private static readonly Vector2 DropShadowOffset = new(2f, 2f);
		private static readonly Color DropShadowColor = new(0, 0, 0, 150);

		private readonly Asset<Texture2D> _icon;
		private readonly Rectangle? _sourceRect;
		private readonly bool _drawDropShadow;

		// Mutable so a caller can retint at runtime, e.g. to show an "are you sure" state on a
		// destructive action.
		public Color IconTint { get; set; } = Color.White;

		// sourceRect, if given, selects one frame out of a multi-icon spritesheet (e.g. Infinite_Powers)
		// instead of drawing the whole texture.
		//
		// drawDropShadow draws a second, offset dark copy of the icon underneath - vanilla's own power
		// icons bake a drop shadow into the image itself, so this is only needed for an icon (like an
		// item sprite) that doesn't already have one.
		public ItemIconButton(Asset<Texture2D> icon, Rectangle? sourceRect = null, bool drawDropShadow = false)
		{
			_icon = icon;
			_sourceRect = sourceRect;
			_drawDropShadow = drawDropShadow;
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			Texture2D texture = _icon.Value;
			CalculatedStyle dimensions = GetDimensions();
			Rectangle sourceRect = _sourceRect ?? texture.Bounds;

			float scale = MathHelper.Min(dimensions.Width / sourceRect.Width, dimensions.Height / sourceRect.Height);
			scale = MathHelper.Min(scale, 1f);

			var origin = new Vector2(sourceRect.Width, sourceRect.Height) / 2f;
			// Round to a whole pixel - drawing pixel art at a fractional position makes the point-clamp
			// sampler blend unevenly between texels, which reads as jagged edges.
			var center = new Vector2(
				(int)(dimensions.X + dimensions.Width / 2f),
				(int)(dimensions.Y + dimensions.Height / 2f));

			if (_drawDropShadow)
				spriteBatch.Draw(texture, center + DropShadowOffset, sourceRect, DropShadowColor, 0f, origin, scale, SpriteEffects.None, 0f);

			spriteBatch.Draw(texture, center, sourceRect, IconTint, 0f, origin, scale, SpriteEffects.None, 0f);
		}
	}
}
