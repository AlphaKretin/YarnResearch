using Microsoft.Xna.Framework;
using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;
using Terraria.UI;

namespace YarnResearch.Common.UI.ManualTriggersUI
{
	[Autoload(Side = ModSide.Client)]
	public class ManualTriggersUISystem : ModSystem
	{
		private UserInterface _userInterface;
		private ManualTriggersUIState _state;

		public override void PostSetupContent()
		{
			_userInterface = new UserInterface();
			_state = new ManualTriggersUIState();
			_state.Activate();
			_userInterface.SetState(_state);
		}

		public override void UpdateUI(GameTime gameTime)
		{
			if (_userInterface?.CurrentState != null)
				_userInterface.Update(gameTime);
		}

		public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
		{
			int mouseTextIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Mouse Text"));
			if (mouseTextIndex == -1)
				return;

			layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
				"YarnResearch: Manual Triggers",
				delegate {
					if (_userInterface?.CurrentState != null)
						_userInterface.Draw(Main.spriteBatch, new GameTime());

					return true;
				},
				InterfaceScaleType.UI));
		}

		public override void Unload()
		{
			_userInterface = null;
			_state = null;
		}
	}
}
