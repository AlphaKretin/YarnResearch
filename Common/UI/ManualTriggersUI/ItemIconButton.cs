using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria.GameContent;
using Terraria.GameContent.UI.Elements;
using Terraria.ModLoader.UI;
using Terraria.UI;

namespace YarnResearch.Common.UI.ManualTriggersUI
{
	// A UIPanel-backed item-icon button: an icon centered (and scaled down if needed) inside the
	// element's bounds, with a hover tooltip and a hover border (mimicking the vanilla Journey Mode
	// powers menu's own hover highlight). drawBackground controls whether the UIPanel's own bordered
	// box is drawn - off for a bare icon-only button (e.g. a menu toggle), on for an icon meant to
	// look like it sits inside a slot/panel; the hover border applies either way.
	//
	// hoverBorder, if given, is drawn instead of the default rectangular placeholder border - use this
	// for a shape that hugs the icon's actual silhouette (like vanilla's own icon border) rather than
	// a plain bounding-box rectangle.
	public class ItemIconButton : UIPanel
	{
		private const int HoverBorderThickness = 2;
		private static readonly Color HoverBorderColor = Color.Yellow;

		private readonly Asset<Texture2D> _icon;
		private readonly Rectangle? _sourceRect;
		private readonly bool _drawBackground;
		private readonly Asset<Texture2D> _hoverBorder;
		private readonly bool _drawDropShadow;

		// Mutable (not constructor-only) so a caller can change the tooltip/tint at runtime, e.g. to show
		// an "are you sure" confirmation state on a destructive action.
		public string HoverText { get; set; }
		public Color IconTint { get; set; } = Color.White;

		private static readonly Vector2 DropShadowOffset = new(2f, 2f);
		private static readonly Color DropShadowColor = new(0, 0, 0, 150);

		// sourceRect, if given, selects one frame out of a multi-icon spritesheet (e.g. Infinite_Powers.png)
		// instead of drawing the whole texture - use this for icons that aren't their own standalone asset.
		//
		// drawDropShadow draws a second, offset dark copy of the icon underneath - vanilla's own power
		// icons bake a drop shadow into the image itself, so this is only needed for an icon (like
		// Unlucky Yarn's item sprite) that doesn't already have one baked in.
		public ItemIconButton(Asset<Texture2D> icon, string hoverText, bool drawBackground = true, Asset<Texture2D> hoverBorder = null, Rectangle? sourceRect = null, bool drawDropShadow = false)
		{
			_icon = icon;
			HoverText = hoverText;
			_drawBackground = drawBackground;
			_hoverBorder = hoverBorder;
			_sourceRect = sourceRect;
			_drawDropShadow = drawDropShadow;
			SetPadding(0);
		}

		protected override void DrawSelf(SpriteBatch spriteBatch)
		{
			if (_drawBackground)
				base.DrawSelf(spriteBatch);

			Texture2D texture = _icon.Value;
			CalculatedStyle dimensions = GetDimensions();

			Rectangle sourceRect = _sourceRect ?? texture.Bounds;

			// The 8px inset reserves room for a slot border, so only applies when a background is
			// actually drawn - a bare icon (no background) should fill its full bounds instead.
			float inset = _drawBackground ? 8f : 0f;
			float scale = MathHelper.Min((dimensions.Width - inset) / sourceRect.Width, (dimensions.Height - inset) / sourceRect.Height);
			scale = MathHelper.Min(scale, 1f);

			var origin = new Vector2(sourceRect.Width, sourceRect.Height) / 2f;
			// Round to a whole pixel - drawing pixel art at a fractional position makes the point-clamp
			// sampler blend unevenly between texels, which reads as jagged/uneven edges.
			var center = new Vector2(
				(int)(dimensions.X + dimensions.Width / 2f),
				(int)(dimensions.Y + dimensions.Height / 2f));

			if (_drawDropShadow)
				spriteBatch.Draw(texture, center + DropShadowOffset, sourceRect, DropShadowColor, 0f, origin, scale, SpriteEffects.None, 0f);

			spriteBatch.Draw(texture, center, sourceRect, IconTint, 0f, origin, scale, SpriteEffects.None, 0f);

			if (IsMouseHovering) {
				if (_hoverBorder != null)
					DrawShapedHoverBorder(spriteBatch, dimensions);
				else
					DrawPlaceholderHoverBorder(spriteBatch, dimensions);

				UICommon.TooltipMouseText(HoverText);
			}
		}

		private void DrawShapedHoverBorder(SpriteBatch spriteBatch, CalculatedStyle dimensions)
		{
			Texture2D texture = _hoverBorder.Value;
			var center = new Vector2(
				(int)(dimensions.X + dimensions.Width / 2f),
				(int)(dimensions.Y + dimensions.Height / 2f));
			var origin = new Vector2(texture.Width, texture.Height) / 2f;
			spriteBatch.Draw(texture, center, null, Color.White, 0f, origin, 1f, SpriteEffects.None, 0f);
		}

		// Placeholder until a shaped border asset (matching the icon's silhouette, like vanilla's own
		// icon border) replaces it - a plain rectangle around the full bounding box.
		private static void DrawPlaceholderHoverBorder(SpriteBatch spriteBatch, CalculatedStyle dimensions)
		{
			Texture2D pixel = TextureAssets.MagicPixel.Value;
			var rect = new Rectangle((int)dimensions.X, (int)dimensions.Y, (int)dimensions.Width, (int)dimensions.Height);

			spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, HoverBorderThickness), HoverBorderColor);
			spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Bottom - HoverBorderThickness, rect.Width, HoverBorderThickness), HoverBorderColor);
			spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Y, HoverBorderThickness, rect.Height), HoverBorderColor);
			spriteBatch.Draw(pixel, new Rectangle(rect.Right - HoverBorderThickness, rect.Y, HoverBorderThickness, rect.Height), HoverBorderColor);
		}
	}
}
