﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using ColossalFramework;
using ICities;
using TrafficManager.Traffic;
using TrafficManager.TrafficLight;
using UnityEngine;
using Random = UnityEngine.Random;
using Timer = System.Timers.Timer;
using TrafficManager.State;

namespace TrafficManager {
	public class SerializableDataExtension : SerializableDataExtensionBase {
		private const string DataId = "TrafficManager_v1.0";

		private static ISerializableData _serializableData;
		private static Configuration _configuration;
		public static bool StateLoaded;

		public override void OnCreated(ISerializableData serializableData) {
			_serializableData = serializableData;
		}

		public override void OnReleased() {
		}
		
		public override void OnLoadData() {
			Log.Warning("Loading Traffic Manager: PE Data");
			byte[] data = _serializableData.LoadData(DataId);
			DeserializeData(data);

			// load options
			byte[] options = _serializableData.LoadData("TMPE_Options");
			if (options != null) {
				if (options.Length >= 1) {
					Options.setSimAccuracy(options[0]);
				}

				if (options.Length >= 2) {
					Options.setLaneChangingRandomization(options[1]);
				}

				if (options.Length >= 3) {
					Options.setRecklessDrivers(options[2]);
				}

				if (options.Length >= 4) {
					Options.setRelaxedBusses(options[3] == (byte)1);
				}

				if (options.Length >= 5) {
					Options.setNodesOverlay(options[4] == (byte)1);
				}

				if (options.Length >= 6) {
					Options.setMayEnterBlockedJunctions(options[5] == (byte)1);
				}

				if (options.Length >= 7) {
					if (!LoadingExtension.IsPathManagerCompatible) {
						Options.setAdvancedAI(false);
					} else {
						Options.setAdvancedAI(options[6] == (byte)1);
					}
				}

				if (options.Length >= 8) {
					Options.setHighwayRules(options[7] == (byte)1);
				}

				if (options.Length >= 9) {
					Options.setCarCityTrafficSensitivity((float)Math.Round(Convert.ToSingle(options[8]) * 0.01f, 2));
				}

				if (options.Length >= 10) {
					Options.setCarHighwayTrafficSensitivity((float)Math.Round(Convert.ToSingle(options[9]) * 0.01f, 2));
				}

				if (options.Length >= 11) {
					Options.setTruckCityTrafficSensitivity((float)Math.Round(Convert.ToSingle(options[10]) * 0.01f, 2));
				}

				if (options.Length >= 12) {
					Options.setTruckHighwayTrafficSensitivity((float)Math.Round(Convert.ToSingle(options[11]) * 0.01f, 2));
				}
			}

			// load toggled traffic lights
			//byte[] trafficLight = _serializableData.LoadData("TMPE_Options");
		}

		private static void DeserializeData(byte[] data) {
			try {
				if (data != null && data.Length != 0) {
					Log.Message("Loading Data from New Load Routine!");
					var memoryStream = new MemoryStream();
					memoryStream.Write(data, 0, data.Length);
					memoryStream.Position = 0;

					var binaryFormatter = new BinaryFormatter();
					_configuration = (Configuration)binaryFormatter.Deserialize(memoryStream);
				} else {
					Log.Warning("No data to deserialize!");
				}
			} catch (Exception e) {
				Log.Error($"Error deserializing data: {e.Message}");
			}
			
			LoadDataState();
			TrafficPriority.HandleAllVehicles();
		}

		private static void LoadDataState() {
			Log.Message("Loading State from Config");
			if (_configuration == null) {
				Log.Warning("Configuration NULL, Couldn't load save data. Possibly a new game?");
				return;
			}

			// load priority segments
			if (_configuration.PrioritySegments != null) {
				Log.Message($"Loading {_configuration.PrioritySegments.Count()} priority segments");
				foreach (var segment in _configuration.PrioritySegments) {
					if (segment.Length < 3)
						continue;
#if DEBUG
					bool debug = segment[0] == 13630;
#endif

					if ((PrioritySegment.PriorityType)segment[2] == PrioritySegment.PriorityType.None) {
#if DEBUG
						if (debug)
							Log.Message($"Loading priority segment: Not adding 'None' priority segment: {segment[1]} @ node {segment[0]}");
#endif
						continue;
					}

					if ((Singleton<NetManager>.instance.m_nodes.m_buffer[segment[0]].m_flags & NetNode.Flags.Created) == NetNode.Flags.None) {
#if DEBUG
						if (debug)
							Log.Message($"Loading priority segment: node {segment[0]} is invalid");
#endif
						continue;
					}
					if ((Singleton<NetManager>.instance.m_segments.m_buffer[segment[1]].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) {
#if DEBUG
						if (debug)
							Log.Message($"Loading priority segment: segment {segment[1]} @ node {segment[0]} is invalid");
#endif
						continue;
					}
					if (TrafficPriority.IsPrioritySegment((ushort)segment[0], (ushort)segment[1])) {
#if DEBUG
						if (debug)
							Log.Message($"Loading priority segment: segment {segment[1]} @ node {segment[0]} is already a priority segment");
#endif
						TrafficPriority.GetPrioritySegment((ushort)segment[0], (ushort)segment[1]).Type = (PrioritySegment.PriorityType)segment[2];
						continue;
					}
#if DEBUG
					Log.Message($"Adding Priority Segment of type: {segment[2].ToString()} to segment {segment[1]} @ node {segment[0]}");
#endif
					TrafficPriority.AddPrioritySegment((ushort)segment[0], (ushort)segment[1], (PrioritySegment.PriorityType)segment[2]);
				}
			} else {
				Log.Warning("Priority segments data structure undefined!");
			}

			// load nodes with traffic light simulation
			if (_configuration.NodeDictionary != null) {
				Log.Message($"Loading {_configuration.NodeDictionary.Count()} traffic light simulations");
				foreach (var node in _configuration.NodeDictionary) {
					if (node.Length < 4)
						continue;
					if (TrafficLightSimulation.GetNodeSimulation((ushort)node[0]) != null)
						continue;
					if ((Singleton<NetManager>.instance.m_nodes.m_buffer[node[0]].m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
						continue;
#if DEBUG
					Log.Message($"Adding node simulation {node[0]}");
#endif
					try {
						TrafficLightSimulation.AddNodeToSimulation((ushort)node[0]);
						var nodeDict = TrafficLightSimulation.GetNodeSimulation((ushort)node[0]);

						nodeDict.ManualTrafficLights = Convert.ToBoolean(node[1]);
						nodeDict.TimedTrafficLights = Convert.ToBoolean(node[2]);
						nodeDict.TimedTrafficLightsActive = Convert.ToBoolean(node[3]);
					} catch (Exception e) {
						// if we failed, just means it's old corrupt data. Ignore it and continue.
						Log.Warning("Error loading data from the NodeDictionary: " + e.Message);
					}
				}
			} else {
				Log.Warning("Traffic light simulation data structure undefined!");
			}

			// Load live traffic lights
			/*if (_configuration.ManualSegments != null) {
				Log.Message($"Loading {_configuration.ManualSegments.Count()} live traffic lights");
				foreach (var segmentData in _configuration.ManualSegments) {
					if (segmentData.Length < 10)
						continue;

					if ((Singleton<NetManager>.instance.m_nodes.m_buffer[segmentData[0]].m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
						continue;
					if ((Singleton<NetManager>.instance.m_segments.m_buffer[segmentData[1]].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
						continue;
					if (TrafficLightsManual.IsSegmentLight((ushort)segmentData[0], (ushort)segmentData[1]))
						continue;
#if DEBUG
					Log.Message($"Adding Light to node {segmentData[0]}, segment {segmentData[1]}");
#endif
					try {
						Flags.setNodeTrafficLight((ushort)segmentData[0], true);
						TrafficLightsManual.AddSegmentLight((ushort)segmentData[0], (ushort)segmentData[1], RoadBaseAI.TrafficLightState.Green);
						var segment = TrafficLightsManual.GetSegmentLight((ushort)segmentData[0], (ushort)segmentData[1]);
						segment.CurrentMode = (ManualSegmentLight.Mode)segmentData[2];
						segment.LightLeft = (RoadBaseAI.TrafficLightState)segmentData[3];
						segment.LightMain = (RoadBaseAI.TrafficLightState)segmentData[4];
						segment.LightRight = (RoadBaseAI.TrafficLightState)segmentData[5];
						segment.LightPedestrian = (RoadBaseAI.TrafficLightState)segmentData[6];
						segment.LastChange = (uint)segmentData[7];
						segment.LastChangeFrame = (uint)segmentData[8];
						segment.PedestrianEnabled = Convert.ToBoolean(segmentData[9]);
					} catch (Exception e) {
						// if we failed, just means it's old corrupt data. Ignore it and continue.
						Log.Warning("Error loading data from the ManualSegments: " + e.Message);
					}
				}
			} else {
				Log.Warning("Live traffic lights data structure undefined!");
			}*/

			var timedStepCount = 0;
			var timedStepSegmentCount = 0;

			NetManager netManager = Singleton<NetManager>.instance;

			if (_configuration.TimedNodes != null && _configuration.TimedNodeGroups != null) {
				Log.Message($"Loading {_configuration.TimedNodes.Count()} timed traffic lights");
				for (var i = 0; i < _configuration.TimedNodes.Count; i++) {
					try {
						var nodeid = (ushort)_configuration.TimedNodes[i][0];
						if ((Singleton<NetManager>.instance.m_nodes.m_buffer[nodeid].m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
							continue;
						Flags.setNodeTrafficLight(nodeid, true);
#if DEBUG
						Log.Message($"Adding Timed Node {i} at node {nodeid}");
#endif

						var nodeGroup = new List<ushort>();
						for (var j = 0; j < _configuration.TimedNodeGroups[i].Length; j++) {
							nodeGroup.Add(_configuration.TimedNodeGroups[i][j]);
						}

						if (TrafficLightsTimed.IsTimedLight(nodeid)) continue;
						TrafficLightsTimed.AddTimedLight(nodeid, nodeGroup);
						var timedNode = TrafficLightsTimed.GetTimedLight(nodeid);
						var node = netManager.m_nodes.m_buffer[nodeid];

						timedNode.CurrentStep = _configuration.TimedNodes[i][1];

						for (var j = 0; j < _configuration.TimedNodes[i][2]; j++) {
							var cfgstep = _configuration.TimedNodeSteps[timedStepCount];
							// old (pre 1.3.0):
							//   cfgstep[0]: time of step
							//   cfgstep[1]: number of segments
							// new (post 1.3.0):
							//   cfgstep[0]: min. time of step
							//   cfgstep[1]: max. time of step
							//   cfgstep[2]: number of segments

							int minTime = 1;
							int maxTime = 1;
							int numSegments = 0;
							float waitFlowBalance = 1f;

							if (cfgstep.Length == 2) {
								minTime = cfgstep[0];
								maxTime = cfgstep[0];
								numSegments = cfgstep[1];
							} else if (cfgstep.Length >= 3) {
								minTime = cfgstep[0];
								maxTime = cfgstep[1];
								numSegments = cfgstep[2];
								if (cfgstep.Length == 4) {
									waitFlowBalance = (float)cfgstep[3] / 10f;
								}
							}

							timedNode.AddStep(minTime, maxTime, waitFlowBalance);
							var step = timedNode.Steps[j];

							for (var s = 0; s < 8; s++) {
								var segmentId = node.GetSegment(s);
								if (segmentId <= 0)
									continue;

								bool tooFewSegments = (timedStepSegmentCount >= _configuration.TimedNodeStepSegments.Count);

								var leftLightState = tooFewSegments ? RoadBaseAI.TrafficLightState.Red : (RoadBaseAI.TrafficLightState)_configuration.TimedNodeStepSegments[timedStepSegmentCount][0];
								var mainLightState = tooFewSegments ? RoadBaseAI.TrafficLightState.Red : (RoadBaseAI.TrafficLightState)_configuration.TimedNodeStepSegments[timedStepSegmentCount][1];
								var rightLightState = tooFewSegments ? RoadBaseAI.TrafficLightState.Red : (RoadBaseAI.TrafficLightState)_configuration.TimedNodeStepSegments[timedStepSegmentCount][2];
								var pedLightState = tooFewSegments ? RoadBaseAI.TrafficLightState.Red : (RoadBaseAI.TrafficLightState)_configuration.TimedNodeStepSegments[timedStepSegmentCount][3];
								ManualSegmentLight.Mode? mode = null;
								if (_configuration.TimedNodeStepSegments[timedStepSegmentCount].Length >= 5) {
									mode = (ManualSegmentLight.Mode)_configuration.TimedNodeStepSegments[timedStepSegmentCount][4];
								}

								//ManualSegmentLight segmentLight = new ManualSegmentLight(step.NodeId, step.segmentIds[k], mainLightState, leftLightState, rightLightState, pedLightState);
									step.segmentLightStates[segmentId].LightLeft = leftLightState;
								step.segmentLightStates[segmentId].LightMain = mainLightState;
								step.segmentLightStates[segmentId].LightRight = rightLightState;
								step.segmentLightStates[segmentId].LightPedestrian = pedLightState;
								if (mode != null)
									step.segmentLightStates[segmentId].CurrentMode = (ManualSegmentLight.Mode)mode;

								timedStepSegmentCount++;
							}
							timedStepCount++;
						}

						if (Convert.ToBoolean(_configuration.TimedNodes[i][3])) {
							timedNode.Start();
						}
					} catch (Exception e) {
						// ignore, as it's probably corrupt save data. it'll be culled on next save
						Log.Warning("Error loading data from the TimedNodes: " + e.Message);
					}
				}
			} else {
				Log.Warning("Timed traffic lights data structure undefined!");
			}

			var trafficLightDefs = _configuration.NodeTrafficLights.Split(',');

			Log.Message($"Loading junction traffic light data");
			if (trafficLightDefs.Length <= 1) {
				// old method
				Log.Message($"Using old method to load traffic light data");

				var saveDataIndex = 0;
				for (var i = 0; i < Singleton<NetManager>.instance.m_nodes.m_buffer.Length; i++) {
					//Log.Message($"Adding NodeTrafficLights iteration: {i1}");
					try {
						if ((Singleton<NetManager>.instance.m_nodes.m_buffer[i].Info.m_class.m_service != ItemClass.Service.Road &&
							Singleton<NetManager>.instance.m_nodes.m_buffer[i].Info.m_class.m_service != ItemClass.Service.PublicTransport) ||
							(Singleton<NetManager>.instance.m_nodes.m_buffer[i].m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
							continue;

						// prevent overflow
						if (_configuration.NodeTrafficLights.Length > saveDataIndex) {
							var trafficLight = _configuration.NodeTrafficLights[saveDataIndex];
#if DEBUG
							Log.Message("Setting traffic light flag for node " + i + ": " + (trafficLight == '1'));
#endif
							Flags.setNodeTrafficLight((ushort)i, trafficLight == '1');
						}
						++saveDataIndex;
					} catch (Exception e) {
						// ignore as it's probably bad save data.
						Log.Warning("Error setting the NodeTrafficLights (old): " + e.Message);
					}
				}
			} else {
				// new method
				foreach (var split in trafficLightDefs.Select(def => def.Split(':')).Where(split => split.Length > 1)) {
					try {
						Log.Message($"Traffic light split data: {split[0]} , {split[1]}");
						var nodeId = Convert.ToUInt16(split[0]);
						uint flag = Convert.ToUInt16(split[1]);

						Flags.setNodeTrafficLight(nodeId, flag > 0);
					} catch (Exception e) {
						// ignore as it's probably bad save data.
						Log.Warning("Error setting the NodeTrafficLights (new): " + e.Message);
					}
				}
			}

			if (_configuration.LaneFlags != null) {
				Log.Message($"Loading lane arrow data");
#if DEBUG
				Log.Message($"LaneFlags: {_configuration.LaneFlags}");
#endif
				var lanes = _configuration.LaneFlags.Split(',');

				if (lanes.Length <= 1)
					return;
				foreach (var split in lanes.Select(lane => lane.Split(':')).Where(split => split.Length > 1)) {
					try {
						Log.Message($"Split Data: {split[0]} , {split[1]}");
						var laneIndex = Convert.ToUInt32(split[0]);
						uint flags = Convert.ToUInt32(split[1]);

						//make sure we don't cause any overflows because of bad save data.
						if (Singleton<NetManager>.instance.m_lanes.m_buffer.Length <= laneIndex)
							continue;

						if (flags > ushort.MaxValue)
							continue;

						if ((Singleton<NetManager>.instance.m_lanes.m_buffer[laneIndex].m_flags & (ushort)NetLane.Flags.Created) == 0 || Singleton<NetManager>.instance.m_lanes.m_buffer[laneIndex].m_segment == 0)
							continue;

						Singleton<NetManager>.instance.m_lanes.m_buffer[laneIndex].m_flags = fixLaneFlags(Singleton<NetManager>.instance.m_lanes.m_buffer[laneIndex].m_flags);

						uint laneArrowFlags = flags & Flags.lfr;
						uint origFlags = (Singleton<NetManager>.instance.m_lanes.m_buffer[laneIndex].m_flags & Flags.lfr);
#if DEBUG
						Log.Message("Setting flags for lane " + laneIndex + " to " + flags + " (" + ((Flags.LaneArrows)(laneArrowFlags)).ToString() + ")");
						if ((origFlags | laneArrowFlags) == origFlags) { // only load if setting differs from default
							Log.Message("Flags for lane " + laneIndex + " are original (" + ((NetLane.Flags)(origFlags)).ToString() + ")");
						}
#endif
						Flags.setLaneArrowFlags(laneIndex, (Flags.LaneArrows)(laneArrowFlags));
					} catch (Exception e) {
						Log.Error($"Error loading Lane Split data. Length: {split.Length} value: {split}\nError: {e.Message}");
					}
				}
			} else {
				Log.Warning("Lane arrow data structure undefined!");
			}
		}

		private static ushort fixLaneFlags(ushort flags) {
			ushort ret = 0;
			if ((flags & (ushort)NetLane.Flags.Created) != 0)
				ret |= (ushort)NetLane.Flags.Created;
			if ((flags & (ushort)NetLane.Flags.Deleted) != 0)
				ret |= (ushort)NetLane.Flags.Deleted;
			if ((flags & (ushort)NetLane.Flags.Inverted) != 0)
				ret |= (ushort)NetLane.Flags.Inverted;
			if ((flags & (ushort)NetLane.Flags.JoinedJunction) != 0)
				ret |= (ushort)NetLane.Flags.JoinedJunction;
			if ((flags & (ushort)NetLane.Flags.Forward) != 0)
				ret |= (ushort)NetLane.Flags.Forward;
			if ((flags & (ushort)NetLane.Flags.Left) != 0)
				ret |= (ushort)NetLane.Flags.Left;
			if ((flags & (ushort)NetLane.Flags.Right) != 0)
				ret |= (ushort)NetLane.Flags.Right;
			if ((flags & (ushort)NetLane.Flags.Stop) != 0)
				ret |= (ushort)NetLane.Flags.Stop;
			if ((flags & (ushort)NetLane.Flags.StartOneWayLeft) != 0)
				ret |= (ushort)NetLane.Flags.StartOneWayLeft;
			if ((flags & (ushort)NetLane.Flags.StartOneWayRight) != 0)
				ret |= (ushort)NetLane.Flags.StartOneWayRight;
			if ((flags & (ushort)NetLane.Flags.EndOneWayLeft) != 0)
				ret |= (ushort)NetLane.Flags.EndOneWayLeft;
			if ((flags & (ushort)NetLane.Flags.EndOneWayRight) != 0)
				ret |= (ushort)NetLane.Flags.EndOneWayRight;
			return ret;
		}

		public override void OnSaveData() {
			Log.Warning("Saving Mod Data.");
			var configuration = new Configuration();

			if (TrafficPriority.PrioritySegments != null) {
				for (ushort i = 0; i < Singleton<NetManager>.instance.m_segments.m_size; i++) {
					SavePrioritySegment(i, configuration);
				}
			}

			for (ushort i = 0; i < Singleton<NetManager>.instance.m_nodes.m_size; i++) {
				if (TrafficLightSimulation.LightSimulationByNodeId != null) {
					SaveTrafficLightSimulation(i, configuration);
				}

				/*if (TrafficLightsManual.ManualSegments != null) {
					SaveManualTrafficLight(i, configuration);
				}*/

				if (TrafficLightsTimed.TimedScripts != null) {
					SaveTimedTrafficLight(i, configuration);
				}

				SaveNodeLights(i, configuration);
			}

			if (LoadingExtension.IsPathManagerCompatible) {
				for (uint i = 0; i < Singleton<NetManager>.instance.m_lanes.m_buffer.Length; i++) {
					SaveLaneData(i, configuration);
				}
			}

			var binaryFormatter = new BinaryFormatter();
			var memoryStream = new MemoryStream();

			try {
				binaryFormatter.Serialize(memoryStream, configuration);
				memoryStream.Position = 0;
				Log.Message($"Save data byte length {memoryStream.Length}");
				_serializableData.SaveData(DataId, memoryStream.ToArray());

				// save options
				_serializableData.SaveData("TMPE_Options", new byte[] { (byte)Options.simAccuracy, (byte)Options.laneChangingRandomization, (byte)Options.recklessDrivers, (byte)(Options.relaxedBusses ? 1 : 0), (byte) (Options.nodesOverlay ? 1 : 0), (byte)(Options.mayEnterBlockedJunctions ? 1 : 0), (byte)(Options.advancedAI ? 1 : 0), (byte)(Options.highwayRules ? 1 : 0), Convert.ToByte(Math.Round(Options.carCityTrafficSensitivity * 100f)), Convert.ToByte(Math.Round(Options.carHighwayTrafficSensitivity * 100f)), Convert.ToByte(Math.Round(Options.truckCityTrafficSensitivity * 100f)), Convert.ToByte(Math.Round(Options.truckHighwayTrafficSensitivity * 100f)) });
			} catch (Exception ex) {
				Log.Error("Unexpected error saving data: " + ex.Message);
			} finally {
				memoryStream.Close();
			}
		}

		private static void SaveLaneData(uint i, Configuration configuration) {
			try {
				NetLane lane = Singleton<NetManager>.instance.m_lanes.m_buffer[i];
				//NetLane.Flags flags = (NetLane.Flags)lane.m_flags;
				/*if ((flags & NetLane.Flags.LeftForwardRight) == NetLane.Flags.None) // only save lanes with explicit lane arrows
					return;*/
				var laneSegmentId = lane.m_segment;
				if (laneSegmentId <= 0)
					return;
				if ((lane.m_flags & (ushort)NetLane.Flags.Created) == 0 || laneSegmentId == 0)
					return;
				NetSegment segment = Singleton<NetManager>.instance.m_segments.m_buffer[laneSegmentId];
				if ((segment.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
					return;

				//if (TrafficPriority.PrioritySegments.ContainsKey(laneSegmentId)) {
				Flags.LaneArrows? laneArrows = Flags.getLaneArrowFlags(i);
				if (laneArrows != null) {
					uint laneArrowInt = (uint)laneArrows;
					Log.Message($"Saving lane data for lane {i}, segment {laneSegmentId}, setting to {laneArrows.ToString()} ({laneArrowInt})");
                    configuration.LaneFlags += $"{i}:{laneArrowInt},";
				}
				//}
			} catch (Exception e) {
				Log.Error($"Error saving NodeLaneData {e.Message}");
			}
		}

		private static bool SaveNodeLights(int i, Configuration configuration) {
			try {
				if (!Flags.mayHaveTrafficLight((ushort)i))
					return true;

				bool hasTrafficLight = Flags.isNodeTrafficLight((ushort)i);
				if (hasTrafficLight) {
					Log.Message($"Saving that node {i} has a traffic light");
				} else {
					Log.Message($"Saving that node {i} does not have a traffic light");
				}
				configuration.NodeTrafficLights += $"{i}:{Convert.ToUInt16(hasTrafficLight)},";
				return false;
			} catch (Exception e) {
				Log.Error($"Error Adding Node Lights and Crosswalks {e.Message}");
				return true;
			}
		}

		private static void SaveTimedTrafficLight(int i, Configuration configuration) {
			try {
				if (!TrafficLightsTimed.TimedScripts.ContainsKey((ushort)i))
					return;

				var timedNode = TrafficLightsTimed.GetTimedLight((ushort)i);
				timedNode.handleNewSegments();

				configuration.TimedNodes.Add(new[]
				{
					timedNode.nodeId, timedNode.CurrentStep, timedNode.NumSteps(),
					Convert.ToInt32(timedNode.IsStarted())
				});

				var nodeGroup = new ushort[timedNode.NodeGroup.Count];

				for (var j = 0; j < timedNode.NodeGroup.Count; j++) {
					nodeGroup[j] = timedNode.NodeGroup[j];
				}

				configuration.TimedNodeGroups.Add(nodeGroup);

				// get segment ids which are still defined but for which real road segments are missing
				NetManager netManager = Singleton<NetManager>.instance;

				for (var j = 0; j < timedNode.NumSteps(); j++) {
					int validCount = 0;
					var node = netManager.m_nodes.m_buffer[i];
					for (var s = 0; s < 8; s++) {
						var segmentId = node.GetSegment(s);
						if (segmentId <= 0)
							continue;
						
						var segLight = timedNode.Steps[j].segmentLightStates.ContainsKey(segmentId) ? timedNode.Steps[j].segmentLightStates[segmentId] : null;
						configuration.TimedNodeStepSegments.Add(new[]
						{
							(int) (segLight == null ? RoadBaseAI.TrafficLightState.Red : segLight.LightLeft),
							(int) (segLight == null ? RoadBaseAI.TrafficLightState.Red : segLight.LightMain),
							(int) (segLight == null ? RoadBaseAI.TrafficLightState.Red : segLight.LightRight),
							(int) (segLight == null ? RoadBaseAI.TrafficLightState.Red : segLight.LightPedestrian),
							(int) segLight.CurrentMode
						});

						++validCount;
					}

					configuration.TimedNodeSteps.Add(new[]
					{
						timedNode.Steps[j].minTime,
						timedNode.Steps[j].maxTime,
						validCount,
						(int)(timedNode.Steps[j].waitFlowBalance*10)
					});
				}
			} catch (Exception e) {
				Log.Error($"Error adding TimedTrafficLights to save {e.Message}");
			}
		}

		private static void SaveManualTrafficLight(ushort segmentId, Configuration configuration) {
			try {
				if (!TrafficLightsManual.ManualSegments.ContainsKey(segmentId))
					return;

				if (TrafficLightsManual.ManualSegments[segmentId].Node1 != 0) {
					var manualSegment = TrafficLightsManual.ManualSegments[segmentId].Instance1;

					configuration.ManualSegments.Add(new[]
					{
						manualSegment.nodeId,
						manualSegment.SegmentId,
						(int) manualSegment.CurrentMode,
						(int) manualSegment.LightLeft,
						(int) manualSegment.LightMain,
						(int) manualSegment.LightRight,
						(int) manualSegment.LightPedestrian,
						(int) manualSegment.LastChange,
						(int) manualSegment.LastChangeFrame,
						Convert.ToInt32(manualSegment.PedestrianEnabled)
					});
				}
				if (TrafficLightsManual.ManualSegments[segmentId].Node2 == 0)
					return;
				var manualSegmentLight = TrafficLightsManual.ManualSegments[segmentId].Instance2;

				configuration.ManualSegments.Add(new[]
				{
					manualSegmentLight.nodeId,
					manualSegmentLight.SegmentId,
					(int) manualSegmentLight.CurrentMode,
					(int) manualSegmentLight.LightLeft,
					(int) manualSegmentLight.LightMain,
					(int) manualSegmentLight.LightRight,
					(int) manualSegmentLight.LightPedestrian,
					(int) manualSegmentLight.LastChange,
					(int) manualSegmentLight.LastChangeFrame,
					Convert.ToInt32(manualSegmentLight.PedestrianEnabled)
				});
			} catch (Exception e) {
				Log.Error($"Error saving ManualTraffic Lights {e.Message}");
			}
		}

		private static void SaveTrafficLightSimulation(int i, Configuration configuration) {
			try {
				if (!TrafficLightSimulation.LightSimulationByNodeId.ContainsKey((ushort)i))
					return;
				var nodeSim = TrafficLightSimulation.LightSimulationByNodeId[(ushort)i];

				/*if (nodeSim == null)
					return;*/

				Log.Message($"Saving traffic light simulation at node {i}, timed: {nodeSim.TimedTrafficLights}, active: {nodeSim.TimedTrafficLightsActive}");

				configuration.NodeDictionary.Add(new[]
				{
					nodeSim.nodeId, Convert.ToInt32(nodeSim.ManualTrafficLights),
					Convert.ToInt32(nodeSim.TimedTrafficLights),
					Convert.ToInt32(nodeSim.TimedTrafficLightsActive)
				});
			} catch (Exception e) {
				Log.Error($"Error adding Nodes to Dictionary {e.Message}");
			}
		}

		private static void SavePrioritySegment(ushort segmentId, Configuration configuration) {
			try {
				if (TrafficPriority.PrioritySegments[segmentId] == null) {
					return;
				}

				if (TrafficPriority.PrioritySegments[segmentId].Node1 != 0 && TrafficPriority.PrioritySegments[segmentId].Instance1.Type != PrioritySegment.PriorityType.None) {
					Log.Message($"Saving Priority Segment of type: {TrafficPriority.PrioritySegments[segmentId].Instance1.Type} @ node {TrafficPriority.PrioritySegments[segmentId].Node1}, seg. {segmentId}");
                    configuration.PrioritySegments.Add(new[]
					{
						TrafficPriority.PrioritySegments[segmentId].Node1, segmentId,
						(int) TrafficPriority.PrioritySegments[segmentId].Instance1.Type
					});
				}

				if (TrafficPriority.PrioritySegments[segmentId].Node2 == 0 || TrafficPriority.PrioritySegments[segmentId].Instance2.Type == PrioritySegment.PriorityType.None)
					return;

				Log.Message($"Saving Priority Segment of type: {TrafficPriority.PrioritySegments[segmentId].Instance2.Type} @ node {TrafficPriority.PrioritySegments[segmentId].Node2}, seg. {segmentId}");
				configuration.PrioritySegments.Add(new[] {
					TrafficPriority.PrioritySegments[segmentId].Node2, segmentId,
					(int) TrafficPriority.PrioritySegments[segmentId].Instance2.Type
				});
			} catch (Exception e) {
				Log.Error($"Error adding Priority Segments to Save: {e.ToString()}");
			}
		}
	}
}
