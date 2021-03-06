using System;
using System.Collections.Generic;
using System.Linq;
using ColossalFramework;
using TrafficManager.TrafficLight;
using TrafficManager.Custom.AI;
using UnityEngine;
using TrafficManager.State;

namespace TrafficManager.Traffic {
	class TrafficPriority {
		private static uint[] segmentsCheckLoadBalanceMod = new uint[] { 127, 255, 511, 1023, 2047 };
		private static uint checkMod = 0;

		public static bool LeftHandDrive;

		/// <summary>
		/// Dictionary of segments that are connected to roads with timed traffic lights or priority signs. Index: segment id
		/// </summary>
		public static TrafficSegment[] PrioritySegments = null;

		/// <summary>
		/// Known vehicles and their current known positions. Index: vehicle id
		/// </summary>
		public static VehiclePosition[] Vehicles = null;

		/// <summary>
		/// Markers that defined in which segment(s) a vehicle will be in the near future. Index: vehicle id
		/// </summary>
		public static HashSet<ushort>[] markedVehicles = null;

		/// <summary>
		/// Nodes that have timed traffic lights or priority signs
		/// </summary>
		private static HashSet<ushort> priorityNodes = new HashSet<ushort>();

		static TrafficPriority() {
			PrioritySegments = new TrafficSegment[Singleton<NetManager>.instance.m_segments.m_size];
			Vehicles = new VehiclePosition[Singleton<VehicleManager>.instance.m_vehicles.m_size];
			markedVehicles = new HashSet<ushort>[Singleton<VehicleManager>.instance.m_vehicles.m_size];
			for (int i = 0; i < markedVehicles.Length; ++i) {
				markedVehicles[i] = new HashSet<ushort>();
			}
		}

		public static void AddPrioritySegment(ushort nodeId, ushort segmentId, PrioritySegment.PriorityType type) {
			if (nodeId <= 0 || segmentId <= 0)
				return;

			Log.Message("adding PrioritySegment @ node " + nodeId + ", seg. " + segmentId + ", type " + type);

			var prioritySegment = PrioritySegments[segmentId];
			if (prioritySegment != null) { // do not replace with IsPrioritySegment!
				prioritySegment.Segment = segmentId;

				Log.Message("Priority segment already exists. Node1=" + prioritySegment.Node1 + " Node2=" + prioritySegment.Node2);

				if (prioritySegment.Node1 == nodeId || prioritySegment.Node1 == 0) {
					Log.Message("Updating Node1");
					prioritySegment.Node1 = nodeId;
					PrioritySegments[segmentId].Instance1 = new PrioritySegment(nodeId, segmentId, type);
					return;
				}

				if (prioritySegment.Node2 != 0) {
					// overwrite Node2
					Log.Warning("Overwriting priority segment for node " + nodeId + ", seg. " + segmentId + ", type " + type);
					prioritySegment.Node2 = nodeId;
					prioritySegment.Instance2 = new PrioritySegment(nodeId, segmentId, type);
					rebuildPriorityNodes();
				} else {
					// add Node2
					Log.Message("Adding as Node2");
					prioritySegment.Node2 = nodeId;
					prioritySegment.Instance2 = new PrioritySegment(nodeId, segmentId, type);
				}
			} else {
				// add Node1
				Log.Message("Adding as Node1");
				prioritySegment = new TrafficSegment();
				prioritySegment.Segment = segmentId;
				prioritySegment.Node1 = nodeId;
				prioritySegment.Instance1 = new PrioritySegment(nodeId, segmentId, type);
				PrioritySegments[segmentId] = prioritySegment;
			}
			priorityNodes.Add(nodeId);
		}

		internal static void UnmarkVehicleInSegment(ushort vehicleId, ushort segmentId) {
			markedVehicles[vehicleId].Remove(segmentId);
		}

		internal static void MarkVehicleInSegment(ushort vehicleId, ushort segmentId) {
			markedVehicles[vehicleId].Add(segmentId);
		}

		public static void RemovePrioritySegments(ushort nodeId) { // priorityNodes: OK
			if (nodeId <= 0)
				return;

			var node = Singleton<NetManager>.instance.m_nodes.m_buffer[nodeId];
			for (var s = 0; s < 8; s++) {
				var segmentId = node.GetSegment(s);
				if (segmentId <= 0)
					continue;

				if (IsPrioritySegment(nodeId, segmentId)) {
					//Log.Message("Housekeeping: node " + nodeId + " contains prio seg. " + segmentId);
					var prioritySegment = PrioritySegments[segmentId];
					if (prioritySegment.Node1 == nodeId) {
						prioritySegment.Node1 = 0;
						prioritySegment.Instance1.RemoveAllCars();
						prioritySegment.Instance1 = null;
					} else {
						prioritySegment.Node2 = 0;
						prioritySegment.Instance2.RemoveAllCars();
						prioritySegment.Instance2 = null;
					}

					if (prioritySegment.Node1 == 0 && prioritySegment.Node2 == 0) {
						PrioritySegments[segmentId] = null;
					}
				}
			}
			priorityNodes.Remove(nodeId);
		}

		public static List<ushort> GetPrioritySegmentIds(ushort nodeId) {
			List<ushort> ret = new List<ushort>();
			if (nodeId <= 0)
				return ret;

			var node = Singleton<NetManager>.instance.m_nodes.m_buffer[nodeId];
			for (var s = 0; s < 8; s++) {
				var segmentId = node.GetSegment(s);
				if (segmentId <= 0)
					continue;

				if (IsPrioritySegment(nodeId, segmentId)) {
					ret.Add(segmentId);
				}
			}

			return ret;
		}

		internal static void HandleAllVehicles() {
			VehicleManager vehicleManager = Singleton<VehicleManager>.instance;
			for (ushort i = 0; i < vehicleManager.m_vehicles.m_size; ++i) {
				if (vehicleManager.m_vehicles.m_buffer[i].m_flags != Vehicle.Flags.None) {
					try {
						CustomCarAI.HandleVehicle(i, ref Singleton<VehicleManager>.instance.m_vehicles.m_buffer[i], true);
					} catch (Exception e) {
						Log.Error("TrafficPriority HandleAllVehicles Error: " + e.ToString());
					}
				}
			}
		}

		public static void segmentHousekeeping(ushort segmentId) {
			if (PrioritySegments[segmentId] == null)
				return;
			var prioritySegment = PrioritySegments[segmentId];

			// segment is valid, check for invalid cars
			if (prioritySegment.Node1 != 0) {
				List<ushort> vehicleIdsToDelete = new List<ushort>();
				foreach (KeyValuePair<ushort, VehiclePosition> e in prioritySegment.Instance1.getCars()) {
					var vehicleId = e.Key;
					if ((Singleton<VehicleManager>.instance.m_vehicles.m_buffer[vehicleId].m_flags & Vehicle.Flags.Created) == Vehicle.Flags.None) {
						vehicleIdsToDelete.Add(vehicleId);
					}
				}

				foreach (var vehicleId in vehicleIdsToDelete) {
					//Log.Warning("Housekeeping: Deleting vehicle " + vehicleId);
					prioritySegment.Instance1.RemoveCar(vehicleId);
					Vehicles[vehicleId] = null;
				}
			}

			if (prioritySegment.Node2 != 0) {
				List<ushort> vehicleIdsToDelete = new List<ushort>();

				foreach (KeyValuePair<ushort, VehiclePosition> e in prioritySegment.Instance2.getCars()) {
					var vehicleId = e.Key;
					if ((Singleton<VehicleManager>.instance.m_vehicles.m_buffer[vehicleId].m_flags & Vehicle.Flags.Created) == Vehicle.Flags.None) {
						vehicleIdsToDelete.Add(vehicleId);
					}
				}

				foreach (var vehicleId in vehicleIdsToDelete) {
					//Log.Warning("Housekeeping: Deleting vehicle " + vehicleId);
					prioritySegment.Instance2.RemoveCar(vehicleId);
					Vehicles[vehicleId] = null;
				}
			}
		}

		public static bool IsPrioritySegment(ushort nodeId, ushort segmentId) {
			if (nodeId <= 0 || segmentId <= 0)
				return false;

			if (PrioritySegments[segmentId] != null) {
				var prioritySegment = PrioritySegments[segmentId];

				NetManager netManager = Singleton<NetManager>.instance;
				if ((netManager.m_segments.m_buffer[segmentId].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) {
					RemovePrioritySegment(nodeId, segmentId);
					TrafficLightsManual.RemoveSegmentLight(segmentId);
					return false;
				}

				if (prioritySegment.Node1 == nodeId || prioritySegment.Node2 == nodeId) {
					return true;
				}
			}

			return false;
		}

		public static bool IsPriorityNode(ushort nodeId) {
			return priorityNodes.Contains(nodeId);
		}

		public static HashSet<ushort> getPriorityNodes() {
			return priorityNodes;
		}

		public static PrioritySegment GetPrioritySegment(ushort nodeId, ushort segmentId) {
			if (!IsPrioritySegment(nodeId, segmentId)) return null;

			var prioritySegment = PrioritySegments[segmentId];

			if (prioritySegment.Node1 == nodeId) {
				return prioritySegment.Instance1;
			}

			return prioritySegment.Node2 == nodeId ?
				prioritySegment.Instance2 : null;
		}

		internal static void RemovePrioritySegment(ushort nodeId, ushort segmentId) { // priorityNodes: OK
			if (nodeId <= 0 || segmentId <= 0 || PrioritySegments[segmentId] == null)
				return;
			var prioritySegment = PrioritySegments[segmentId];

			if (prioritySegment.Node1 == nodeId) {
				prioritySegment.Node1 = 0;
				prioritySegment.Instance1.RemoveAllCars();
				prioritySegment.Instance1 = null;
			}
			if (prioritySegment.Node2 == nodeId) {
				prioritySegment.Node2 = 0;
				prioritySegment.Instance2.RemoveAllCars();
				prioritySegment.Instance2 = null;
			}

			if (prioritySegment.Node1 == 0 && prioritySegment.Node2 == 0)
				PrioritySegments[segmentId] = null;
			rebuildPriorityNodes();
		}

		internal static void ClearTraffic() {
			try {
				lock (Singleton<VehicleManager>.instance) {
					for (ushort i = 0; i < Singleton<VehicleManager>.instance.m_vehicles.m_size; ++i) {
						if (
							(Singleton<VehicleManager>.instance.m_vehicles.m_buffer[i].m_flags & Vehicle.Flags.Created) != Vehicle.Flags.None &&
							Singleton<VehicleManager>.instance.m_vehicles.m_buffer[i].Info.m_vehicleType == VehicleInfo.VehicleType.Car)
							Singleton<VehicleManager>.instance.ReleaseVehicle(i);
					}
				}
			} catch (Exception ex) {
				Log.Error($"Error occured when trying to clear traffic: {ex.ToString()}");
            }
		}

		/// <summary>
		/// Adds/Sets a node as a priority node
		/// </summary>
		/// <param name="nodeId"></param>
		/// <returns>number of priority segments added</returns>
		internal static byte AddPriorityNode(ushort nodeId) {
			if (nodeId <= 0)
				return 0;

			byte ret = 0;
			for (var i = 0; i < 8; i++) {
				var segmentId = Singleton<NetManager>.instance.m_nodes.m_buffer[nodeId].GetSegment(i);

				if (segmentId == 0)
					continue;
				if (TrafficPriority.IsPrioritySegment(nodeId, segmentId))
					continue;
				if (TrafficLightsManual.SegmentIsOutgoingOneWay(segmentId, nodeId))
					continue;

				TrafficPriority.AddPrioritySegment(nodeId, segmentId, PrioritySegment.PriorityType.None);
				++ret;
			}
			return ret;
		}

		public static bool HasIncomingVehicles(ushort targetVehicleId, ushort nodeId) {
			try {
#if DEBUG
				//bool debug = nodeId == 8621;
				bool debug = false;
#else
				bool debug = false;
#endif

				VehiclePosition targetVehiclePos = Vehicles[targetVehicleId];
				if (targetVehiclePos == null) {
					Log.Warning($"HasIncomingVehicles: {targetVehicleId} @ {nodeId}, fromSegment: {targetVehiclePos.FromSegment}, toSegment: {targetVehiclePos.ToSegment}. Car does not exist!");
					return false;
				}

				if (targetVehiclePos.ToNode != nodeId) {
					Log.Warning($"HasIncomingVehicles: The vehicle {targetVehicleId} is not driving on a segment adjacent to node {nodeId}.");
					return false;
				}/* else if (targetVehiclePos.FromSegment == 22980u && nodeId == 13630u) {
					Log.Error("vehicleId " + targetVehicleId + ". ToNode: " + targetVehiclePos.ToNode + ". FromSegment: " + targetVehiclePos.FromSegment + ". ToSegment: " + targetVehiclePos.ToSegment);
				}*/

#if DEBUG
				if (debug) {
					Log.Message($"HasIncomingVehicles: {targetVehicleId} @ {nodeId}, fromSegment: {targetVehiclePos.FromSegment}, toSegment: {targetVehiclePos.ToSegment}");
				}
#endif

				var currentFrameIndex = Singleton<SimulationManager>.instance.m_currentFrameIndex;
				var frame = currentFrameIndex >> 4;
				var node = TrafficLightTool.GetNetNode(nodeId);

				var targetFromPrioritySegment = GetPrioritySegment(nodeId, targetVehiclePos.FromSegment);
				if (targetFromPrioritySegment == null) {
#if DEBUG
					if (debug) {
						Log.Message($"source priority segment not found.");
					}
#endif
					return false;
				}

				Direction targetToDir = CustomRoadAI.segmentGeometries[targetVehiclePos.FromSegment].GetDirection(targetVehiclePos.ToSegment, nodeId);

				var numCars = 0;

				// get all cars
				for (var s = 0; s < 8; s++) {
					var incomingSegmentId = node.GetSegment(s);

					if (incomingSegmentId == 0 || incomingSegmentId == targetVehiclePos.FromSegment) continue;
					if (!IsPrioritySegment(nodeId, incomingSegmentId)) {
#if DEBUG
						if (debug) {
							Log.Message($"Segment {incomingSegmentId} @ {nodeId} is not a priority segment (1).");
						}
#endif
						continue;
					}

					var incomingFromPrioritySegment = GetPrioritySegment(nodeId, incomingSegmentId);
					if (incomingFromPrioritySegment == null) {
#if DEBUG
						if (debug) {
							Log.Message($"Segment {incomingSegmentId} @ {nodeId} is not a priority segment (2).");
						}
#endif
						continue; // should not happen
					}

					if ((node.m_flags & NetNode.Flags.TrafficLights) == NetNode.Flags.None) {
						if (targetFromPrioritySegment.Type == PrioritySegment.PriorityType.Main) {
#if DEBUG
							if (debug)
								Log.Message($"HasIncomingVehicles: Target {targetVehicleId} is on a main road @ {nodeId}.");
#endif
							// target is on a main segment
							if (incomingFromPrioritySegment.Type != PrioritySegment.PriorityType.Main) {
								continue; // ignore cars coming from low priority segments (yield/stop)
										  // count incoming cars from other main segment
							}

							numCars += incomingFromPrioritySegment.getNumApproachingVehicles();
#if DEBUG
							if (debug)
								Log.Message($"HasIncomingVehicles: Target {targetVehicleId} has {numCars} possibly conflicting vehicles coming from segment {incomingSegmentId} @ {nodeId}.");
#endif

							foreach (KeyValuePair<ushort, VehiclePosition> e in incomingFromPrioritySegment.getApproachingVehicles()) {
								var incomingCar = e.Key;
								/*if (!Vehicles.ContainsKey(incomingCar)) {
									--numCars;
									continue;
								}*/

								if (Singleton<VehicleManager>.instance.m_vehicles.m_buffer[incomingCar].GetLastFrameVelocity().magnitude > 0.25f) {
									if (HasVehiclePriority(debug, targetVehicleId, true, incomingCar, true, nodeId)) {
										--numCars;
#if DEBUG
										if (debug)
											Log.Message($"HasIncomingVehicles: Incoming {incomingCar} is not conflicting.");
#endif
									}  else {
										/*if (debug) {
											Log.Message($"Vehicle {targetCar} on segment {Vehicles[targetCar].FromSegment} has to wait for vehicle {car} on segment {Vehicles[car].FromSegment}");
										}*/
#if DEBUG
										if (debug)
											Log.Message($"HasIncomingVehicles: Incoming {incomingCar} IS conflicting.");
#endif
										return true;
									}
								} else {
									numCars--;
								}
							}
						} else {
							// target car is on a low-priority segment
#if DEBUG
							if (debug)
								Log.Message($"HasIncomingVehicles: Target {targetVehicleId} is on a low priority road @ {nodeId}.");
#endif

							// Main - Yield/Stop
							numCars += incomingFromPrioritySegment.getNumApproachingVehicles();
#if DEBUG
							if (debug)
								Log.Message($"HasIncomingVehicles: Target {targetVehicleId} has {numCars} possibly conflicting vehicles coming from segment {incomingSegmentId} @ {nodeId}.");
#endif

							foreach (KeyValuePair<ushort, VehiclePosition> e in incomingFromPrioritySegment.getApproachingVehicles()) {
								var incomingCar = e.Key;
								VehiclePosition otherVehiclePos = Vehicles[incomingCar];
								if (otherVehiclePos == null) {
									--numCars;
									continue;
								}

								if (incomingFromPrioritySegment.Type == PrioritySegment.PriorityType.Main) {
									if (!otherVehiclePos.Stopped && Singleton<VehicleManager>.instance.m_vehicles.m_buffer[incomingCar].GetLastFrameVelocity().magnitude > 0.25f) {
										if (HasVehiclePriority(debug, targetVehicleId, false, incomingCar, true, nodeId)) {
#if DEBUG
											if (debug)
												Log.Message($"HasIncomingVehicles: Incoming {incomingCar} (main) is not conflicting.");
#endif
											--numCars;
										} else {
#if DEBUG
											if (debug)
												Log.Message($"HasIncomingVehicles: Incoming {incomingCar} (main) IS conflicting.");
#endif
											return true;
										}
									} else {
#if DEBUG
										if (debug)
											Log.Message($"HasIncomingVehicles: Incoming {incomingCar} (main) is not conflicting due to low speed.");
#endif
										numCars--;
									}
								} else {
									if (Singleton<VehicleManager>.instance.m_vehicles.m_buffer[incomingCar].GetLastFrameVelocity().magnitude > 0.25f) {
										if (HasVehiclePriority(debug, targetVehicleId, false, incomingCar, false, nodeId)) {
#if DEBUG
											if (debug)
												Log.Message($"HasIncomingVehicles: Incoming {incomingCar} (low) is not conflicting.");
#endif
											--numCars;
										} else {
#if DEBUG
											if (debug)
												Log.Message($"HasIncomingVehicles: Incoming {incomingCar} (low) IS conflicting.");
#endif
											return true;
										}
									} else {
#if DEBUG
										if (debug)
											Log.Message($"HasIncomingVehicles: Incoming {incomingCar} (low) is not conflicting due to low speed.");
#endif
										numCars--;
									}
								}
							}
						}
					} else {
#if DEBUG
						if (debug)
							Log.Message($"Node {nodeId} is a traffic light.");
#endif

						// Traffic lights
						if (!TrafficLightsManual.IsSegmentLight(nodeId, incomingSegmentId)) {
#if DEBUG
							if (debug) {
								Log.Message($"Segment {incomingSegmentId} @ {nodeId} does not have live traffic lights.");
							}
#endif
							continue;
						}

						var segmentLight = TrafficLightsManual.GetSegmentLight(nodeId, incomingSegmentId);

						if (segmentLight.GetLightMain() != RoadBaseAI.TrafficLightState.Green)
							continue;
#if DEBUG
						if (debug)
							Log.Message($"Segment {incomingSegmentId} @ {nodeId} is a GREEN traffic light.");
#endif

						numCars += incomingFromPrioritySegment.getNumApproachingVehicles();

						foreach (KeyValuePair<ushort, VehiclePosition> e in incomingFromPrioritySegment.getApproachingVehicles()) {
							var incomingCar = e.Key;
							/*if (!Vehicles.ContainsKey(otherCar)) {
								--numCars;
								continue;
							}*/

							if (Singleton<VehicleManager>.instance.m_vehicles.m_buffer[incomingCar].GetLastFrameVelocity().magnitude > 0.25f) {
								if (HasVehiclePriority(debug, targetVehicleId, true, incomingCar, true, nodeId)) {
#if DEBUG
									if (debug)
										Log.Message($"HasIncomingVehicles: Incoming {incomingCar} (light) is not conflicting.");
#endif
									--numCars;
								} else {
#if DEBUG
									if (debug)
										Log.Message($"HasIncomingVehicles: Incoming {incomingCar} (light) IS conflicting.");
#endif
									return true;
								}
							} else {
#if DEBUG
								if (debug)
									Log.Message($"HasIncomingVehicles: Incoming {incomingCar} (light) is not conflicting due to low speed.");
#endif
								numCars--;
							}
						}
					}

					if (numCars > 0)
						return true;
				}

				return numCars > 0;
			} catch (Exception e) {
				Log.Error($"Error occurred: {e.ToString()}");
            }
			return false;
		}

		internal static void AddVehicle(ushort vehicleId, VehiclePosition vehiclePos) {
			Vehicles[vehicleId] = vehiclePos;
		}

		internal static void RemoveVehicleFromSegments(ushort vehicleId) {
			HashSet<ushort> segmentIds = new HashSet<ushort>(markedVehicles[vehicleId]);
			foreach (ushort segmentId in segmentIds) {
				TrafficSegment trafficSeg = PrioritySegments[segmentId];
				if (trafficSeg == null) {
					markedVehicles[vehicleId].Remove(segmentId);
					Log.Warning($"RemoveVehicleFromSegments: Inconsistency detected in markedVehicles: PrioritySegment for {segmentId} does not exist. Tried to remove vehicle {vehicleId}");
					continue;
				}

				if (trafficSeg.Instance1 != null)
					trafficSeg.Instance1.RemoveCar(vehicleId); // modifies markedVehicles
				if (trafficSeg.Instance2 != null)
					trafficSeg.Instance2.RemoveCar(vehicleId); // modifies markedVehicles
			}
		}

		protected static bool HasVehiclePriority(bool debug, ushort targetCarId, bool targetIsOnMainRoad, ushort incomingCarId, bool incomingIsOnMainRoad, ushort nodeId) {
			try {
#if DEBUG
				//debug = nodeId == 8621;
				debug = false;
				if (debug) {
					Log.Message($"HasVehiclePriority: Checking if {targetCarId} (main road = {targetIsOnMainRoad}) has priority over {incomingCarId} (main road = {incomingIsOnMainRoad}).");
                }
#endif

				// delete invalid incoming car
				if ((Singleton<VehicleManager>.instance.m_vehicles.m_buffer[incomingCarId].m_flags & Vehicle.Flags.Created) == Vehicle.Flags.None) {
					RemoveVehicleFromSegments(incomingCarId);
					Vehicles[incomingCarId] = null;
					return true;
				}

				var targetCar = Vehicles[targetCarId];
				var incomingCar = Vehicles[incomingCarId];

				if (targetCar == null || incomingCar == null) {
					Log.Warning($"HasVehiclePriority: incoming car {incomingCarId} or targetCar {targetCarId} is null.");
					return true;
				}
				if (incomingCar.ToNode != targetCar.ToNode) {
					Log.Warning($"HasVehiclePriority: incoming car {incomingCarId} goes to node {incomingCar.ToNode} where target car {targetCarId} goes to {targetCar.ToNode}. Ignoring.");
                    return true;
				}

				//         TOP
				//          |
				//          |
				// LEFT --- + --- RIGHT
				//          |
				//          |
				//        BOTTOM

				// We assume the target car is coming from BOTTOM.

				Direction targetToDir = CustomRoadAI.segmentGeometries[targetCar.FromSegment].GetDirection(targetCar.ToSegment, nodeId);
				Direction incomingRelDir = CustomRoadAI.segmentGeometries[targetCar.FromSegment].GetDirection(incomingCar.FromSegment, nodeId);
				Direction incomingToDir = CustomRoadAI.segmentGeometries[incomingCar.FromSegment].GetDirection(incomingCar.ToSegment, nodeId);
#if DEBUG
				if (debug) {
					Log.Message($"HasVehiclePriority: targetToDir: {targetToDir.ToString()}, incomingRelDir: {incomingRelDir.ToString()}, incomingToDir: {incomingToDir.ToString()}");
                }
#endif

				if (LeftHandDrive) {
					// mirror situation for left-hand traffic systems
					targetToDir = InvertLeftRight(targetToDir);
					incomingRelDir = InvertLeftRight(incomingRelDir);
					incomingToDir = InvertLeftRight(incomingToDir);
#if DEBUG
					if (debug) {
						Log.Message($"HasVehiclePriority: LHD! targetToDir: {targetToDir.ToString()}, incomingRelDir: {incomingRelDir.ToString()}, incomingToDir: {incomingToDir.ToString()}");
					}
#endif
				}

				bool sameTargets = false;
				bool laneOrderCorrect = false;
				if (targetCar.ToSegment == incomingCar.ToSegment) {
					// target and incoming are both going to same segment
					sameTargets = true;
					if (targetCar.ToLaneIndex == incomingCar.ToLaneIndex && targetCar.FromSegment != incomingCar.FromSegment)
						laneOrderCorrect = false;
					else {
						switch (targetToDir) {
							case Direction.Left:
								laneOrderCorrect = IsLaneOrderConflictFree(targetCar.ToSegment, targetCar.ToLaneIndex, incomingCar.ToLaneIndex); // stay left
								break;
							case Direction.Forward:
							default:
								switch (incomingRelDir) {
									case Direction.Left:
									case Direction.Forward:
										laneOrderCorrect = IsLaneOrderConflictFree(targetCar.ToSegment, incomingCar.ToLaneIndex, targetCar.ToLaneIndex); // stay right
										break;
									case Direction.Right:
										laneOrderCorrect = IsLaneOrderConflictFree(targetCar.ToSegment, targetCar.ToLaneIndex, incomingCar.ToLaneIndex); // stay left
										break;
									case Direction.Turn:
									default:
										laneOrderCorrect = true;
										break;
								}
								break;
							case Direction.Right:
								laneOrderCorrect = IsLaneOrderConflictFree(targetCar.ToSegment, incomingCar.ToLaneIndex, targetCar.ToLaneIndex); // stay right
								break;
						}
						laneOrderCorrect = IsLaneOrderConflictFree(targetCar.ToSegment, targetCar.ToLaneIndex, incomingCar.ToLaneIndex);
#if DEBUG
						if (debug) {
							Log.Message($"HasVehiclePriority: target {targetCarId} (going to lane {targetCar.ToLaneIndex}) and incoming {incomingCarId} (going to lane {incomingCar.ToLaneIndex}) are going to the same segment. Lane order correct? {laneOrderCorrect}");
						}
#endif
					}
				}

				if (sameTargets && laneOrderCorrect) {
#if DEBUG
					if (debug) {
						Log.Message($"Lane order between car {targetCarId} and {incomingCarId} is correct.");
					}
#endif
					return true;
				}

				bool incomingCrossingStreet = incomingToDir == Direction.Forward || incomingToDir == Direction.Left;

				switch (targetToDir) {
					case Direction.Right:
						// target: BOTTOM->RIGHT
#if DEBUG
						if (debug) {
							Log.Message($"Car {targetCarId} (vs. {incomingCar.FromSegment}->{incomingCar.ToSegment}) is going right without conflict!");
                        }
#endif
						if (!targetIsOnMainRoad && incomingIsOnMainRoad && !laneOrderCorrect) {
#if DEBUG
							if (debug) {
								Log.Message($"Car {targetCarId} (vs. {incomingCar.FromSegment}->{incomingCar.ToSegment}) is on low-priority road turning right. the other vehicle is on a priority road.");
							}
#endif
							return false; // vehicle must wait for incoming vehicle on priority road
						}

						return true;
					case Direction.Forward:
					default:
#if DEBUG
						if (debug) {
							Log.Message($"Car {targetCarId} (vs. {incomingCar.FromSegment}->{incomingCar.ToSegment}) is going forward: {incomingRelDir}, {targetIsOnMainRoad}, {incomingCrossingStreet}!");
						}
#endif
						// target: BOTTOM->TOP
						switch (incomingRelDir) {
							case Direction.Right:
							case Direction.Left:
								return targetIsOnMainRoad || !incomingCrossingStreet;
							case Direction.Forward:
							default:
								return true;
						}
					case Direction.Left:
#if DEBUG
						if (debug) {
							Log.Message($"Car {targetCarId} (vs. {incomingCar.FromSegment}->{incomingCar.ToSegment}) is going left: {incomingRelDir}, {targetIsOnMainRoad}, {incomingIsOnMainRoad}, {incomingCrossingStreet}, {incomingToDir}!");
						}
#endif
						// target: BOTTOM->LEFT
						switch (incomingRelDir) {
							case Direction.Right:
								return !incomingCrossingStreet;
							case Direction.Left:
								if (targetIsOnMainRoad && incomingIsOnMainRoad) // bent priority road
									return true;
								return !incomingCrossingStreet;
							case Direction.Forward:
							default:
								return incomingToDir == Direction.Left || incomingToDir == Direction.Turn;
						}
				}
			} catch (Exception e) {
				Log.Error("Error occured: " + e.ToString());
			}

			return false;
		}

		private static Direction InvertLeftRight(Direction dir) {
			if (dir == Direction.Left)
				dir = Direction.Right;
			else if (dir == Direction.Right)
				dir = Direction.Left;
			return dir;
		}

		internal static void OnLevelLoading() {
			try {
				TrafficPriority.fixJunctions(); // TODO maybe remove this
			} catch (Exception e) {
				Log.Error($"OnLevelLoading: {e.ToString()}");
            }
		}

		internal static void OnLevelUnloading() {
			TrafficLightSimulation.LightSimulationByNodeId.Clear();
			priorityNodes.Clear();
			for (int i = 0; i < PrioritySegments.Length; ++i)
				PrioritySegments[i] = null;
			for (int i = 0; i < Vehicles.Length; ++i)
				Vehicles[i] = null;
			for (int i = 0; i < markedVehicles.Length; ++i)
				markedVehicles[i].Clear();
		}

		public static bool IsLaneOrderConflictFree(ushort segmentId, uint leftLaneIndex, uint rightLaneIndex) {
			try {
				NetInfo segmentInfo = Singleton<NetManager>.instance.m_segments.m_buffer[segmentId].Info;
				NetInfo.Direction normDirection = TrafficPriority.LeftHandDrive ? NetInfo.Direction.Forward : NetInfo.Direction.Backward; // direction to normalize indices to
				NetInfo.Lane leftLane = segmentInfo.m_lanes[leftLaneIndex];
				NetInfo.Lane rightLane = segmentInfo.m_lanes[rightLaneIndex];

				// forward (right-hand traffic system): left < right
				// backward (right-hand traffic system): left > right
				if ((byte)(leftLane.m_direction & normDirection) != 0) {
					return leftLane.m_position < rightLane.m_position; 
				} else {
					return rightLane.m_position < leftLane.m_position;
				}
			} catch (Exception e) {
				Log.Error($"IsLaneOrderConflictFree({segmentId}, {leftLaneIndex}, {rightLaneIndex}): Error: {e.ToString()}");
            }
			return true;
		}

		/*public static bool LaneOrderCorrect(int segmentid, uint leftLane, uint rightLane) {
			if (leftLane == rightLane)
				return false;

			var instance = Singleton<NetManager>.instance;

			var segment = instance.m_segments.m_buffer[segmentid];
			var info = segment.Info;

			var curLaneId = segment.m_lanes;
			var laneIndex = 0;

			var oneWaySegment = true;
			while (laneIndex < info.m_lanes.Length && curLaneId != 0u) {
				if (info.m_lanes[laneIndex].m_laneType != NetInfo.LaneType.Pedestrian &&
					(info.m_lanes[laneIndex].m_direction == NetInfo.Direction.Backward)) {
					oneWaySegment = false;
					break;
				}

				curLaneId = instance.m_lanes.m_buffer[(int)((UIntPtr)curLaneId)].m_nextLane;
				laneIndex++;
			}

			laneIndex = 0;
			var leftLanePosition = 0f;
			var rightLanePosition = 0f;

			while (laneIndex < info.m_lanes.Length && curLaneId != 0u) {
				if (curLaneId == leftLane) {
					leftLanePosition = info.m_lanes[laneIndex].m_position;
				}

				if (curLaneId == rightLane) {
					rightLanePosition = info.m_lanes[laneIndex].m_position;
				}

				curLaneId = instance.m_lanes.m_buffer[(int)((UIntPtr)curLaneId)].m_nextLane;
				laneIndex++;
			}

			if (oneWaySegment) {
				if (leftLanePosition < rightLanePosition) {
					return true;
				}
			} else {
				if (leftLanePosition > rightLanePosition) {
					return true;
				}
			}

			return false;
		}*/

		/// <summary>
		/// Determines the direction vehicles are turning when changing from segment `fromSegment` to segment `toSegment` at node `nodeId`.
		/// </summary>
		/// <param name="fromSegment"></param>
		/// <param name="toSegment"></param>
		/// <param name="nodeId"></param>
		/// <returns></returns>
		public static Direction GetDirection(ushort fromSegment, ushort toSegment, ushort nodeId) {
			return CustomRoadAI.segmentGeometries[fromSegment].GetDirection(toSegment, nodeId);
		}

		public static bool IsRightSegment(ushort fromSegment, ushort toSegment, ushort nodeid) {
			if (fromSegment <= 0 || toSegment <= 0)
				return false;

			return IsLeftSegment(toSegment, fromSegment, nodeid);
		}

		public static bool IsLeftSegment(ushort fromSegment, ushort toSegment, ushort nodeid) {
			if (fromSegment <= 0 || toSegment <= 0)
				return false;

			var fromDir = GetSegmentDir(fromSegment, nodeid);
			fromDir.y = 0;
			fromDir.Normalize();
			var toDir = GetSegmentDir(toSegment, nodeid);
			toDir.y = 0;
			toDir.Normalize();
			return Vector3.Cross(fromDir, toDir).y >= 0.5;
		}

		public static bool HasLeftSegment(ushort segmentId, ushort nodeId) {
			return CustomRoadAI.segmentGeometries[segmentId].HasLeftSegment(nodeId);
		}

		public static bool HasRightSegment(int segmentId, ushort nodeId, bool debug = false) {
			return CustomRoadAI.segmentGeometries[segmentId].HasRightSegment(nodeId);
		}

		public static bool HasStraightSegment(int segmentId, ushort nodeId, bool debug = false) {
			return CustomRoadAI.segmentGeometries[segmentId].HasStraightSegment(nodeId);
		}


		// not very important:
		/*
		public static bool HasLeftLane(ushort nodeId, int segmentId) {
			var instance = Singleton<NetManager>.instance;
			var segment = Singleton<NetManager>.instance.m_segments.m_buffer[segmentId];
			var dir = NetInfo.Direction.Forward;
			if (segment.m_startNode == nodeId)
				dir = NetInfo.Direction.Backward;
			var dir2 = ((segment.m_flags & NetSegment.Flags.Invert) == NetSegment.Flags.None) ? dir : NetInfo.InvertDirection(dir);
			var dir3 = LeftHandDrive ? NetInfo.InvertDirection(dir2) : dir2;

			var info = segment.Info;

			var num2 = segment.m_lanes;
			var num3 = 0;

			while (num3 < info.m_lanes.Length && num2 != 0u) {
				var flags = (NetLane.Flags)Singleton<NetManager>.instance.m_lanes.m_buffer[num2].m_flags;

				if (info.m_lanes[num3].m_direction == dir3 && (flags & NetLane.Flags.Left) != NetLane.Flags.None) {
					return true;
				}

				num2 = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_nextLane;
				num3++;
			}

			return false;
		}

		public static bool HasForwardLane(ushort nodeId, int segmentId) {
			var instance = Singleton<NetManager>.instance;
			var segment = Singleton<NetManager>.instance.m_segments.m_buffer[segmentId];
			var dir = NetInfo.Direction.Forward;
			if (segment.m_startNode == nodeId)
				dir = NetInfo.Direction.Backward;
			var dir2 = ((segment.m_flags & NetSegment.Flags.Invert) == NetSegment.Flags.None) ? dir : NetInfo.InvertDirection(dir);
			var dir3 = LeftHandDrive ? NetInfo.InvertDirection(dir2) : dir2;

			var info = segment.Info;

			var num2 = segment.m_lanes;
			var num3 = 0;

			while (num3 < info.m_lanes.Length && num2 != 0u) {
				var flags = (NetLane.Flags)Singleton<NetManager>.instance.m_lanes.m_buffer[num2].m_flags;

				if (info.m_lanes[num3].m_direction == dir3 && (flags & NetLane.Flags.Left) != NetLane.Flags.Forward) {
					return true;
				}

				num2 = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_nextLane;
				num3++;
			}

			return false;
		}

		public static bool HasRightLane(ushort nodeId, int segmentId) {
			var instance = Singleton<NetManager>.instance;
			var segment = Singleton<NetManager>.instance.m_segments.m_buffer[segmentId];
			var dir = NetInfo.Direction.Forward;
			if (segment.m_startNode == nodeId)
				dir = NetInfo.Direction.Backward;
			var dir2 = ((segment.m_flags & NetSegment.Flags.Invert) == NetSegment.Flags.None) ? dir : NetInfo.InvertDirection(dir);
			var dir3 = LeftHandDrive ? NetInfo.InvertDirection(dir2) : dir2;

			var info = segment.Info;

			var num2 = segment.m_lanes;
			var num3 = 0;

			while (num3 < info.m_lanes.Length && num2 != 0u) {
				var flags = (NetLane.Flags)Singleton<NetManager>.instance.m_lanes.m_buffer[num2].m_flags;

				if (info.m_lanes[num3].m_direction == dir3 && (flags & NetLane.Flags.Left) != NetLane.Flags.Right) {
					return true;
				}

				num2 = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_nextLane;
				num3++;
			}

			return false;
		}*/

		public static Vector3 GetSegmentDir(int segment, ushort nodeid) {
			var instance = Singleton<NetManager>.instance;

			Vector3 dir;

			dir = instance.m_segments.m_buffer[segment].m_startNode == nodeid ?
				instance.m_segments.m_buffer[segment].m_startDirection :
				instance.m_segments.m_buffer[segment].m_endDirection;

			return dir;
		}

		/// <summary>
		/// rebuilds the implicitly defined set of priority nodes (traffic light nodes & nodes with priority signs)
		/// </summary>
		private static void rebuildPriorityNodes() {
			priorityNodes.Clear();

			for (ushort i = 0; i < PrioritySegments.Length; ++i) {
				var trafficSeg = PrioritySegments[i];
				if (trafficSeg == null)
					continue;
				if (trafficSeg.Node1 != 0)
					priorityNodes.Add(trafficSeg.Node1);
				if (trafficSeg.Node2 != 0)
					priorityNodes.Add(trafficSeg.Node2);
			}
		}

		internal static void fixJunctions() {
			for (ushort i = 0; i < Singleton<NetManager>.instance.m_nodes.m_size; ++i) {
				NetNode node = Singleton<NetManager>.instance.m_nodes.m_buffer[i];
				if ((node.m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
					continue;
				if (node.CountSegments() > 2)
					Singleton<NetManager>.instance.m_nodes.m_buffer[i].m_flags |= NetNode.Flags.Junction;
			}
		}

		public static void nodeHousekeeping(ushort nodeId) {
			try {
				uint frame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
				checkMod = (checkMod + 1u) & segmentsCheckLoadBalanceMod[Options.simAccuracy];

				NetManager netManager = Singleton<NetManager>.instance;
				VehicleManager vehicleManager = Singleton<VehicleManager>.instance;

				Flags.applyNodeTrafficLightFlag(nodeId);

				// update lane arrows
				var node = netManager.m_nodes.m_buffer[nodeId];
				for (var s = 0; s < 8; s++) {
					var segmentId = node.GetSegment(s);
					if (segmentId <= 0)
						continue;
					NetSegment segment = netManager.m_segments.m_buffer[segmentId];

					uint laneId = segment.m_lanes;
					while (laneId != 0) {
						if (!Flags.applyLaneArrowFlags(laneId)) {
							Flags.removeLaneArrowFlags(laneId);
						}
						laneId = netManager.m_lanes.m_buffer[laneId].m_nextLane;
					}
				}

				if (IsPriorityNode(nodeId)) {
					NodeValidityState nodeState = NodeValidityState.Valid;
					if (!isValidPriorityNode(nodeId, out nodeState)) {
						if (nodeState != NodeValidityState.SimWithoutLight) {
							Log.Warning("Housekeeping: Deleting node " + nodeId);
							RemovePrioritySegments(nodeId);
						}

						switch (nodeState) {
							case NodeValidityState.SimWithoutLight:
								Log.Warning("Housekeeping: Re-adding traffic light at node " + nodeId);
								Flags.setNodeTrafficLight(nodeId, true);
								break;
							case NodeValidityState.Unused:
							case NodeValidityState.IllegalSim:
								// delete traffic light simulation
								Log.Warning("Housekeeping: RemoveNodeFromSimulation " + nodeId);
								TrafficLightSimulation.RemoveNodeFromSimulation(nodeId, false);
								Flags.setNodeTrafficLight(nodeId, false);
								break;
							default:
								break;
						}
					}
				}

				// add newly created segments to timed traffic lights
				if (TrafficLightsTimed.TimedScripts.ContainsKey(nodeId)) {
					TrafficLightsTimed.TimedScripts[nodeId].handleNewSegments();
				}
			} catch (Exception e) {
				Log.Warning($"Housekeeping failed: {e.Message}");
			}
		}

		private enum NodeValidityState {
			Valid,
			/// <summary>
			/// the node is currently not used (no traffic junction exists for the node id)
			/// </summary>
			Unused,
			/// <summary>
			/// a traffic light simulation is running for this node but the node does not have a traffic light
			/// </summary>
			SimWithoutLight,
			/// <summary>
			/// a traffic light simulation is running at a node that does not allow traffic lights
			/// </summary>
			IllegalSim,
			/// <summary>
			/// none of the node's possible priority signs is set
			/// </summary>
			NoValidSegments,
			/// <summary>
			/// Invalid node id given
			/// </summary>
			Invalid
		}

		private static bool isValidPriorityNode(ushort nodeId, out NodeValidityState nodeState) {
			nodeState = NodeValidityState.Valid;

			if (nodeId <= 0) {
				nodeState = NodeValidityState.Invalid;
				Log.Warning($"Housekeeping: Node {nodeId} is invalid!");
				return false;
			}

			NetManager netManager = Singleton<NetManager>.instance;

			Flags.applyNodeTrafficLightFlag(nodeId);
			var node = netManager.m_nodes.m_buffer[nodeId];
			if ((node.m_flags & NetNode.Flags.Created) == NetNode.Flags.None) {
				nodeState = NodeValidityState.Unused;
				Log.Warning($"Housekeeping: Node {nodeId} is unused!");
				return false; // node is unused
			}

			bool hasTrafficLight = (node.m_flags & NetNode.Flags.TrafficLights) != NetNode.Flags.None;
			var nodeSim = TrafficLightSimulation.GetNodeSimulation(nodeId);
			if (nodeSim != null) {
				if (! Flags.mayHaveTrafficLight(nodeId)) {
					nodeState = NodeValidityState.IllegalSim;
					Log.Warning($"Housekeeping: Node {nodeId} has traffic light simulation but must not have a traffic light!");
					return false;
				}

				if (!hasTrafficLight) {
					// traffic light simulation is active but node does not have a traffic light
					nodeState = NodeValidityState.SimWithoutLight;
					Log.Warning($"Housekeeping: Node {nodeId} has traffic light simulation but no traffic light!");
					return false;
				} else {
					// check if all timed step segments are valid
					if (nodeSim.TimedTrafficLights && nodeSim.TimedTrafficLightsActive) {
						TrafficLightsTimed timedLight = TrafficLightsTimed.GetTimedLight(nodeId);
						if (timedLight == null || timedLight.Steps.Count <= 0) {
							Log.Warning("Housekeeping: Timed light is null or no steps for node {nodeId}!");
							TrafficLightSimulation.RemoveNodeFromSimulation(nodeId, false);
							return false;
						}

						/*foreach (var segmentId in timedLight.Steps[0].segmentIds) {
							if (! IsPrioritySegment(nodeId, segmentId)) {
								Log.Warning("Housekeeping: Timed light - Priority segment has gone away!");
								RemoveNodeFromSimulation(nodeId);
								return false;
							}
						}*/
					}
					return true;
				}
			} else {
				byte numSegmentsWithSigns = 0;
				for (var s = 0; s < 8; s++) {
					var segmentId = node.GetSegment(s);
					if (segmentId <= 0)
						continue;
					NetSegment segment = netManager.m_segments.m_buffer[segmentId];
					if (segment.m_startNode != nodeId && segment.m_endNode != nodeId)
						continue;

					PrioritySegment prioritySegment = GetPrioritySegment(nodeId, segmentId);
					if (prioritySegment == null) {
						continue;
					}

					// if node is a traffic light, it must not have priority signs
					if (hasTrafficLight && prioritySegment.Type != PrioritySegment.PriorityType.None) {
						Log.Warning($"Housekeeping: Node {nodeId}, Segment {segmentId} is a priority sign but node has a traffic light!");
						prioritySegment.Type = PrioritySegment.PriorityType.None;
					}

					// if a priority sign is set, everything is ok
					if (prioritySegment.Type != PrioritySegment.PriorityType.None) {
						++numSegmentsWithSigns;
					}
				}

				if (numSegmentsWithSigns > 0) {
					// add priority segments for newly created segments
					numSegmentsWithSigns += AddPriorityNode(nodeId);
				}

				bool ok = numSegmentsWithSigns >= 2;
				if (!ok) {
					Log.Warning($"Housekeeping: Node {nodeId} does not have valid priority segments!");
					nodeState = NodeValidityState.NoValidSegments;
				}
				return ok;
			}
		}
	}
}
