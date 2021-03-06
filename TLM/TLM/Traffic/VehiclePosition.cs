using ColossalFramework;

namespace TrafficManager.Traffic {
	public class VehiclePosition {
		private CarState carState;

		public CarState CarState {
			get { return carState; }
			set {
				if (value != carState)
					LastCarStateUpdate = Singleton<SimulationManager>.instance.m_currentFrameIndex;
				carState = value;
			}
		}

		public uint LastCarStateUpdate = 0;

		public int WaitTime = 0;

		public ushort ToNode;
		public ushort FromSegment;
		public ushort ToSegment;
		//public uint ToLaneId;
		public uint ToLaneIndex;
		//public uint FromLaneId;
		public uint FromLaneIndex;
		public float ReduceSpeedByValueToYield;
		public bool Stopped = false;

		public uint LastFrame;

		public VehiclePosition() {
			ResetCar();
		}

		public void ResetCar() {
			ToNode = 0;
			FromSegment = 0;
			ToSegment = 0;
			//ToLaneId = 0;
			ToLaneIndex = 0;
			//FromLaneId = 0;
			FromLaneIndex = 0;
			Stopped = false;

			WaitTime = 0;
			CarState = CarState.None;
		}
	}
}
