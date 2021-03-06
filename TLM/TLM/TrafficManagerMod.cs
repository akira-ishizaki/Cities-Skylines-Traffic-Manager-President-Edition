using ICities;
using UnityEngine;

namespace TrafficManager {
	public class TrafficManagerMod : IUserMod {
		public string Name => "Traffic Manager: President Edition";

		public string Description => "Traffic Junction Manager [v1.4]";

		public void OnEnabled() {
			Log.Message("TrafficManagerMod Enabled");
		}

		public void OnDisabled() {
			Log.Message("TrafficManagerMod Disabled");
		}

		public void OnSettingsUI(UIHelperBase helper) {
			Options.makeSettings(helper);
		}
	}
}
