using System;
using System.Collections.Generic;
using Terraria.GameContent.UI.Elements;
using Terraria.UI;

namespace YarnResearch.Common.UI.PrefixPickerUI
{
	public class PrefixPickerUIState : UIState
	{
		private readonly Action _onClose;
		private UIList _list;

		public PrefixPickerUIState(Action onClose)
		{
			_onClose = onClose;
		}

		public override void OnInitialize()
		{
			var panel = new UIPanel();
			panel.Width.Set(240f, 0f);
			panel.Height.Set(300f, 0f);
			// Biased left/down from dead center so it sits closer to the duplication menu itself.
			panel.HAlign = 0.42f;
			panel.VAlign = 0.6f;
			Append(panel);

			_list = new UIList();
			_list.Width.Set(-24f, 1f);
			_list.Height.Set(0f, 1f);
			_list.ListPadding = 4f;
			// UIList.Add re-sorts the whole list on every call by default - an empty ManualSortMethod
			// preserves insertion order instead, which is what actually reflects our own value-based sort.
			_list.ManualSortMethod = _ => { };
			panel.Append(_list);

			var scrollbar = new UIScrollbar();
			scrollbar.Height.Set(0f, 1f);
			scrollbar.HAlign = 1f;
			panel.Append(scrollbar);
			_list.SetScrollbar(scrollbar);
		}

		public void Populate(int itemType, List<PrefixCandidate> candidates)
		{
			_list.Clear();
			foreach (PrefixCandidate candidate in candidates)
				_list.Add(new PrefixEntryElement(candidate, itemType, _onClose));
		}
	}
}
