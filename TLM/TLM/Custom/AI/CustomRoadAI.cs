using System;
using System.Collections.Generic;
using ColossalFramework;
using TrafficManager.TrafficLight;
using TrafficManager.Traffic;
using UnityEngine;
using ColossalFramework.Math;
using TrafficManager.Custom.Manager;
using TrafficManager.Custom.Misc;

namespace TrafficManager.Custom.AI {
	class CustomRoadAI : RoadBaseAI {
		public static uint lastSimulationStep = 0;
		public static uint lastUpdateFrame = 0;

		public static SegmentGeometry[] segmentGeometries;
		public static ushort[] currentLaneTrafficBuffer;
		public static byte[] laneTrafficDensity;
		public static float[] laneMeanTrafficDensity;
		public static bool initDone = false;

		private static uint segmentCheckMod = 0;
		private static uint nodeCheckMod = 0;

		private static uint[] segmentCheckLoadBalanceMod = new uint[] { 63, 127, 255, 511, 1023 };
		private static uint[] nodeCheckLoadBalanceMod = new uint[] { 63, 127, 255, 511, 1023 };

		public void Awake() {
		}

		public static void init() {
			segmentGeometries = new SegmentGeometry[Singleton<NetManager>.instance.m_segments.m_size];
			Log.Message($"Building {segmentGeometries.Length} segment geometries...");
			for (ushort i = 0; i < segmentGeometries.Length; ++i) {
				segmentGeometries[i] = new SegmentGeometry(i);
			}
			Log.Message($"Calculated segment geometries.");

			currentLaneTrafficBuffer = new ushort[Singleton<NetManager>.instance.m_lanes.m_size];
			laneTrafficDensity = new byte[Singleton<NetManager>.instance.m_lanes.m_size];
			laneMeanTrafficDensity = new float[Singleton<NetManager>.instance.m_lanes.m_size];
			for (uint i = 0; i < laneMeanTrafficDensity.Length; ++i) {
				laneMeanTrafficDensity[i] = 0.5f;
			}
			initDone = true;
		}

		// this implements the Update method of MonoBehaviour
		public void Update() {
			var currentFrameIndex = Singleton<SimulationManager>.instance.m_currentFrameIndex;

			uint curFrame = (currentFrameIndex >> 2);
			if ((lastUpdateFrame >> 2) < curFrame) {
				try {
					foreach (KeyValuePair<ushort, TrafficLightSimulation> e in TrafficLightSimulation.LightSimulationByNodeId) {
						if ((e.Key & 7) != (curFrame & 7)) // 111
							continue;

						try {
							var otherNodeSim = e.Value;
							otherNodeSim.SimulationStep();
						} catch (Exception ex) {
							Log.Warning($"Error occured while simulating traffic light @ node {e.Key}: {ex.ToString()}");
						}
					}
				} catch (Exception ex) {
					// TODO the dictionary was modified (probably a segment connected to a traffic light was changed/removed). rework this
					Log.Warning($"Error occured while iterating overs traffic light simulations: {ex.ToString()}");
				}
				lastUpdateFrame = currentFrameIndex;
			}
		}

		public void CustomNodeSimulationStep(ushort nodeId, ref NetNode data) {
			try {
				nodeCheckMod = (nodeCheckMod + 1u) & nodeCheckLoadBalanceMod[Options.simAccuracy];
				if ((nodeCheckMod & nodeId) == nodeCheckMod && TrafficLightTool.getToolMode() != ToolMode.AddPrioritySigns)
					TrafficPriority.nodeHousekeeping(nodeId);

				var nodeSim = TrafficLightSimulation.GetNodeSimulation(nodeId);
				if (nodeSim == null || (nodeSim.TimedTrafficLights && !nodeSim.TimedTrafficLightsActive)) {
					OriginalSimulationStep(nodeId, ref data);
				}
			} catch (Exception e) {
				Log.Warning($"CustomNodeSimulationStep: An error occurred: {e.ToString()}");
			}
		}

		public void CustomSegmentSimulationStep(ushort segmentID, ref NetSegment data) {
			if (initDone) {
				segmentCheckMod = (segmentCheckMod + 1u) & segmentCheckLoadBalanceMod[Options.simAccuracy];
				if ((segmentCheckMod & segmentID) == segmentCheckMod) {
					//Log.Message($"Segment simulation step: {segmentID}");

					try {
						TrafficPriority.segmentHousekeeping(segmentID);
					} catch (Exception e) {
						Log.Error("Error occured while housekeeping segment: " + e.ToString());
					}

					try {
						segmentGeometries[segmentID].recalculate();
					} catch (Exception e) {
						Log.Error("Error occured while recalculating segment geometry: " + e.ToString());
					}
				}

				if (!Options.isStockLaneChangerUsed()) {
					try {
						// calculate traffic density
						uint curLaneId = data.m_lanes;
						int nextNumLanes = data.Info.m_lanes.Length;
						int laneIndex = 0;
						while (laneIndex < nextNumLanes && curLaneId != 0u) {
							float laneLength = Singleton<NetManager>.instance.m_lanes.m_buffer[curLaneId].m_length;
							int buf = Convert.ToInt32(currentLaneTrafficBuffer[curLaneId]);
							int currentDensity = Math.Min(100, Mathf.RoundToInt((float)buf / laneLength * 100f));
							int prevDensity = Convert.ToInt32(laneTrafficDensity[curLaneId]);
							int diff = currentDensity - prevDensity;
							if (diff > 0)
								laneTrafficDensity[curLaneId] = Convert.ToByte(Mathf.Clamp(prevDensity + 5, 0, 100));
							else if (diff < 0)
								laneTrafficDensity[curLaneId] = Convert.ToByte(Mathf.Clamp(prevDensity - 5, 0, 100));

							laneMeanTrafficDensity[curLaneId] = 0.95f * laneMeanTrafficDensity[curLaneId] + 0.05f * (float)laneTrafficDensity[curLaneId] * 0.01f;
							currentLaneTrafficBuffer[curLaneId] = 0;

							laneIndex++;
							curLaneId = Singleton<NetManager>.instance.m_lanes.m_buffer[curLaneId].m_nextLane;
						}
					} catch (Exception e) {
						Log.Error("Error occured while calculating lane traffic density: " + e.ToString());
					}
				}
			}

			OriginalSimulationStep(segmentID, ref data);
		}

		public void OriginalSimulationStep(ushort nodeId, ref NetNode data) {
			var instance = Singleton<NetManager>.instance;
			if ((data.m_flags & NetNode.Flags.TrafficLights) != NetNode.Flags.None) {
				var currentFrameIndex = Singleton<SimulationManager>.instance.m_currentFrameIndex;
				var num = data.m_maxWaitTime & 3;
				var num2 = data.m_maxWaitTime >> 2 & 7;
				var num3 = data.m_maxWaitTime >> 5;
				var num4 = -1;
				var num5 = -1;
				var num6 = -1;
				var num7 = -1;
				var num8 = -1;
				var num9 = -1;
				var num10 = 0;
				var num11 = 0;
				var num12 = 0;
				var num13 = 0;
				var num14 = 0;
				for (var i = 0; i < 8; i++) {
					var segment = data.GetSegment(i);
					if (segment != 0) {
						var num15 = 0;
						var num16 = 0;
						instance.m_segments.m_buffer[segment].CountLanes(segment, NetInfo.LaneType.Vehicle, VehicleInfo.VehicleType.Car, ref num15, ref num16);
						var flag = instance.m_segments.m_buffer[segment].m_startNode == nodeId;
						var flag2 = (!flag) ? (num15 != 0) : (num16 != 0);
						var flag3 = (!flag) ? (num16 != 0) : (num15 != 0);
						if (flag2) {
							num10 |= 1 << i;
						}
						if (flag3) {
							num11 |= 1 << i;
							num13++;
						}
						TrafficLightState trafficLightState;
						TrafficLightState trafficLightState2;
						bool flag4;
						bool flag5;
						GetTrafficLightState(nodeId, ref instance.m_segments.m_buffer[segment], currentFrameIndex - 256u, out trafficLightState, out trafficLightState2, out flag4, out flag5);
						if ((trafficLightState2 & TrafficLightState.Red) != TrafficLightState.Green && flag5) {
							if (num7 == -1) {
								num7 = i;
							}
							if (num9 == -1 && num14 >= num3) {
								num9 = i;
							}
						}
						num14++;
						if (flag2 || flag4) {
							if ((trafficLightState & TrafficLightState.Red) == TrafficLightState.Green) {
								num5 = i;
								if (flag4) {
									num4 = i;
								}
							} else if (flag4) {
								if (num6 == -1) {
									num6 = i;
								}
								if (num8 == -1 && num12 >= num2) {
									num8 = i;
								}
							}
							num12++;
						}
					}
				}
				if (num8 == -1) {
					num8 = num6;
				}
				if (num9 == -1) {
					num9 = num7;
				}
				if (num5 != -1 && num4 != -1 && num <= 1) {
					num8 = -1;
					num9 = -1;
					num++;
				}
				if (num9 != -1 && num8 != -1 && Singleton<SimulationManager>.instance.m_randomizer.Int32(3u) != 0) {
					num9 = -1;
				}
				if (num8 != -1) {
					num5 = num8;
				}
				if (num9 == num5) {
					num5 = -1;
				}
				var vector = Vector3.zero;
				if (num9 != -1) {
					ushort segment2 = data.GetSegment(num9);
					vector = instance.m_segments.m_buffer[segment2].GetDirection(nodeId);
					if (num5 != -1) {
						segment2 = data.GetSegment(num5);
						var direction = instance.m_segments.m_buffer[segment2].GetDirection(nodeId);
						if (direction.x * vector.x + direction.z * vector.z < -0.5f) {
							num5 = -1;
						}
					}
					if (num5 == -1) {
						for (int j = 0; j < 8; j++) {
							if (j != num9 && (num10 & 1 << j) != 0) {
								segment2 = data.GetSegment(j);
								if (segment2 != 0) {
									var direction2 = instance.m_segments.m_buffer[segment2].GetDirection(nodeId);
									if (direction2.x * vector.x + direction2.z * vector.z >= -0.5f) {
										num5 = j;
										break;
									}
								}
							}
						}
					}
				}
				var num17 = -1;
				var vector2 = Vector3.zero;
				var vector3 = Vector3.zero;
				if (num5 != -1) {
					var segment3 = data.GetSegment(num5);
					vector2 = instance.m_segments.m_buffer[segment3].GetDirection(nodeId);
					if ((num10 & num11 & 1 << num5) != 0) {
						for (var k = 0; k < 8; k++) {
							if (k != num5 && k != num9 && (num10 & num11 & 1 << k) != 0) {
								segment3 = data.GetSegment(k);
								if (segment3 != 0) {
									vector3 = instance.m_segments.m_buffer[segment3].GetDirection(nodeId);
									if (num9 == -1 || vector3.x * vector.x + vector3.z * vector.z >= -0.5f) {
										if (num13 == 2) {
											num17 = k;
											break;
										}
										if (vector3.x * vector2.x + vector3.z * vector2.z < -0.9396926f) {
											num17 = k;
											break;
										}
									}
								}
							}
						}
					}
				}
				for (int l = 0; l < 8; l++) {
					ushort segment4 = data.GetSegment(l);
					if (segment4 != 0) {
						TrafficLightState trafficLightState3;
						TrafficLightState trafficLightState4;
						GetTrafficLightState(nodeId, ref instance.m_segments.m_buffer[segment4], currentFrameIndex - 256u, out trafficLightState3, out trafficLightState4);
						trafficLightState3 &= ~TrafficLightState.RedToGreen;
						trafficLightState4 &= ~TrafficLightState.RedToGreen;
						if (num5 == l || num17 == l) {
							if ((trafficLightState3 & TrafficLightState.Red) != TrafficLightState.Green) {
								trafficLightState3 = TrafficLightState.RedToGreen;
								num = 0;
								if (++num2 >= num12) {
									num2 = 0;
								}
							}
							if ((trafficLightState4 & TrafficLightState.Red) == TrafficLightState.Green) {
								trafficLightState4 = TrafficLightState.GreenToRed;
							}
						} else {
							if ((trafficLightState3 & TrafficLightState.Red) == TrafficLightState.Green) {
								trafficLightState3 = TrafficLightState.GreenToRed;
							}
							var direction3 = instance.m_segments.m_buffer[segment4].GetDirection(nodeId);
							if ((num11 & 1 << l) != 0 && num9 != l && ((num5 != -1 && direction3.x * vector2.x + direction3.z * vector2.z < -0.5f) || (num17 != -1 && direction3.x * vector3.x + direction3.z * vector3.z < -0.5f))) {
								if ((trafficLightState4 & TrafficLightState.Red) == TrafficLightState.Green) {
									trafficLightState4 = TrafficLightState.GreenToRed;
								}
							} else if ((trafficLightState4 & TrafficLightState.Red) != TrafficLightState.Green) {
								trafficLightState4 = TrafficLightState.RedToGreen;
								if (++num3 >= num14) {
									num3 = 0;
								}
							}
						}
						SetTrafficLightState(nodeId, ref instance.m_segments.m_buffer[segment4], currentFrameIndex, trafficLightState3, trafficLightState4, false, false);
					}
				}
				data.m_maxWaitTime = (byte)(num3 << 5 | num2 << 2 | num);
			}
			int num18 = 0;
			if (m_noiseAccumulation != 0) {
				var num19 = 0;
				for (var m = 0; m < 8; m++) {
					var segment5 = data.GetSegment(m);
					if (segment5 != 0) {
						num18 += instance.m_segments.m_buffer[segment5].m_trafficDensity;
						num19++;
					}
				}
				if (num19 != 0) {
					num18 /= num19;
				}
			}
			int num20 = 100 - (num18 - 100) * (num18 - 100) / 100;
			int num21 = m_noiseAccumulation * num20 / 100;
			if (num21 != 0) {
				Singleton<ImmaterialResourceManager>.instance.AddResource(ImmaterialResourceManager.Resource.NoisePollution, num21, data.m_position, m_noiseRadius);
			}
			if ((data.m_problems & Notification.Problem.RoadNotConnected) != Notification.Problem.None && (data.m_flags & NetNode.Flags.Original) != NetNode.Flags.None) {
				var properties = Singleton<GuideManager>.instance.m_properties;
				if (properties != null) {
					instance.m_outsideNodeNotConnected.Activate(properties.m_outsideNotConnected, nodeId, Notification.Problem.RoadNotConnected);
				}
			}
		}

		public void OriginalSimulationStep(ushort segmentID, ref NetSegment data) {
			//base.SimulationStep(segmentID, ref data);

			NetManager instance = Singleton<NetManager>.instance;
			Vector3 position = instance.m_nodes.m_buffer[(int)data.m_startNode].m_position;
			Vector3 position2 = instance.m_nodes.m_buffer[(int)data.m_endNode].m_position;

			if ((data.m_flags & NetSegment.Flags.Original) == NetSegment.Flags.None) {
				int num_a = this.GetMaintenanceCost(position, position2);
				bool flag = (ulong)(Singleton<SimulationManager>.instance.m_currentFrameIndex >> 8 & 15u) == (ulong)((long)(segmentID & 15));
				if (num_a != 0) {
					if (flag) {
						num_a = num_a * 16 / 100 - num_a / 100 * 15;
					} else {
						num_a /= 100;
					}
					Singleton<EconomyManager>.instance.FetchResource(EconomyManager.Resource.Maintenance, num_a, this.m_info.m_class);
				}
				if (flag) {
					float num_b = (float)instance.m_nodes.m_buffer[(int)data.m_startNode].m_elevation;
					float num_c = (float)instance.m_nodes.m_buffer[(int)data.m_endNode].m_elevation;
					if (this.IsUnderground()) {
						num_b = -num_b;
						num_c = -num_c;
					}
					int constructionCost = this.GetConstructionCost(position, position2, num_b, num_c);
					if (constructionCost != 0) {
						StatisticBase statisticBase = Singleton<StatisticsManager>.instance.Acquire<StatisticInt64>(StatisticType.CityValue);
						if (statisticBase != null) {
							statisticBase.Add(constructionCost);
						}
					}
				}
			}


			float num = 0f;
			uint num2 = data.m_lanes;
			int num3 = 0;
			while (num3 < this.m_info.m_lanes.Length && num2 != 0u) {
				NetInfo.Lane lane = this.m_info.m_lanes[num3];
				if ((byte)(lane.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0 && lane.m_vehicleType != VehicleInfo.VehicleType.Bicycle) {
					num += instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_length;
				}
				num2 = instance.m_lanes.m_buffer[(int)((UIntPtr)num2)].m_nextLane;
				num3++;
			}
			int num4 = 0;
			if (data.m_trafficBuffer == 65535) {
				if ((data.m_flags & NetSegment.Flags.Blocked) == NetSegment.Flags.None) {
					data.m_flags |= NetSegment.Flags.Blocked;
					data.m_modifiedIndex = Singleton<SimulationManager>.instance.m_currentBuildIndex++;
				}
			} else {
				data.m_flags &= ~NetSegment.Flags.Blocked;
				int num5 = Mathf.RoundToInt(num) << 4;
				if (num5 != 0) {
					num4 = (int)((byte)Mathf.Min((int)(data.m_trafficBuffer * 100) / num5, 100));
				}
			}
			data.m_trafficBuffer = 0;
			if (num4 > (int)data.m_trafficDensity) {
				data.m_trafficDensity = (byte)Mathf.Min((int)(data.m_trafficDensity + 5), num4);
			} else if (num4 < (int)data.m_trafficDensity) {
				data.m_trafficDensity = (byte)Mathf.Max((int)(data.m_trafficDensity - 5), num4);
			}
			/*Vector3 position = instance.m_nodes.m_buffer[(int)data.m_startNode].m_position;
			Vector3 position2 = instance.m_nodes.m_buffer[(int)data.m_endNode].m_position;*/
			Vector3 vector = (position + position2) * 0.5f;
			if (!this.IsUnderground()) {
				float num6 = Singleton<TerrainManager>.instance.WaterLevel(VectorUtils.XZ(vector));
				if (num6 > vector.y + 1f) {
					data.m_flags |= NetSegment.Flags.Flooded;
				} else {
					data.m_flags &= ~NetSegment.Flags.Flooded;
				}
			}
			if (!this.m_highwayRules) {
				DistrictManager instance2 = Singleton<DistrictManager>.instance;
				byte district = instance2.GetDistrict(vector);
				DistrictPolicies.CityPlanning cityPlanningPolicies = instance2.m_districts.m_buffer[(int)district].m_cityPlanningPolicies;
				if ((cityPlanningPolicies & DistrictPolicies.CityPlanning.HeavyTrafficBan) != DistrictPolicies.CityPlanning.None) {
					data.m_flags |= NetSegment.Flags.HeavyBan;
				} else {
					data.m_flags &= ~NetSegment.Flags.HeavyBan;
				}
				if ((cityPlanningPolicies & DistrictPolicies.CityPlanning.BikeBan) != DistrictPolicies.CityPlanning.None) {
					data.m_flags |= NetSegment.Flags.BikeBan;
				} else {
					data.m_flags &= ~NetSegment.Flags.BikeBan;
				}
				if ((cityPlanningPolicies & DistrictPolicies.CityPlanning.OldTown) != DistrictPolicies.CityPlanning.None) {
					data.m_flags |= NetSegment.Flags.CarBan;
				} else {
					data.m_flags &= ~NetSegment.Flags.CarBan;
				}
			}
			int num7 = (int)(100 - (data.m_trafficDensity - 100) * (data.m_trafficDensity - 100) / 100);
			int num8 = this.m_noiseAccumulation * num7 / 100;
			if (num8 != 0) {
				float num9 = Vector3.Distance(position, position2);
				int num10 = Mathf.FloorToInt(num9 / this.m_noiseRadius);
				for (int i = 0; i < num10; i++) {
					Vector3 position3 = Vector3.Lerp(position, position2, (float)(i + 1) / (float)(num10 + 1));
					Singleton<ImmaterialResourceManager>.instance.AddResource(ImmaterialResourceManager.Resource.NoisePollution, num8, position3, this.m_noiseRadius);
				}
			}
			if (data.m_trafficDensity >= 50 && data.m_averageLength < 25f && (instance.m_nodes.m_buffer[(int)data.m_startNode].m_flags & (NetNode.Flags.LevelCrossing | NetNode.Flags.TrafficLights)) == NetNode.Flags.TrafficLights && (instance.m_nodes.m_buffer[(int)data.m_endNode].m_flags & (NetNode.Flags.LevelCrossing | NetNode.Flags.TrafficLights)) == NetNode.Flags.TrafficLights) {
				GuideController properties = Singleton<GuideManager>.instance.m_properties;
				if (properties != null) {
					Singleton<NetManager>.instance.m_shortRoadTraffic.Activate(properties.m_shortRoadTraffic, segmentID);
				}
			}
		}

		internal static void OnLevelUnloading() {
			initDone = false;	
		}

		internal static void OnLevelLoading() {
			init();
		}

		internal static void AddTraffic(uint laneID, ushort val) {
			if (!initDone)
				return;
			currentLaneTrafficBuffer[laneID] = (ushort)Math.Min(65535u, (uint)currentLaneTrafficBuffer[laneID] + (uint)val);
		}
	}
}
