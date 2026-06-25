using KKAPI;
using System;
using HarmonyLib;
using LogicFlows;
using System.Linq;
using KKAPI.Chara;
using MessagePack;
using UnityEngine;
using KKAPI.Maker;
using KKAPI.Studio;
using System.Collections;
using ExtensibleSaveFormat;
using System.Collections.Generic;
using KKAPI.Utilities;
using KKABMX.Core;

namespace AmazingNewBoneLogic
{
    public class AnblCharaController : CharaCustomFunctionController
    {
        public const int normalInputWindowID = 2233500;
        public const int advancedInputWindowID = 2233511;
        public const int renameWindowID = 2233522;
        public const int groupSelectWindowID = 2233533;
        public const int simpleModeWindowID = 2233544;
        public const int simpleModeAccBindDropID = 2233555;
        public const int simpleModeGroupAddID = 2233566;
        public const int confirmWindowID = 2233577;
        public const int boneEditorWindowID = 2233588;
        public const int transferPopupID = 2233599;

        public static readonly Vector2 defaultGraphSize = new Vector2(600, 900);
        public static readonly byte saveVersion = 2;

        public LogicFlowGraph lfg
        {
            get => getCurrentGraph();
            private set => setCurrentGraph(value);
        }

        public bool IsCurrentAdvanced => lfg != null && graphData[lfg].advanced;

        internal static Dictionary<LogicFlowGraph, Dictionary<int, List<object>>> serialisationData =
            new Dictionary<LogicFlowGraph, Dictionary<int, List<object>>>();

        private Dictionary<LogicFlowGraph, GraphData> graphData = new Dictionary<LogicFlowGraph, GraphData>();
        internal Dictionary<int, LogicFlowGraph> graphs = new Dictionary<int, LogicFlowGraph>();
        internal Dictionary<int, List<BoneEffectEdit>> boneEdits = new Dictionary<int, List<BoneEffectEdit>>();
        internal HashSet<string> activeBoneEditIds = new HashSet<string>();
        private ANBLBoneEffect _boneEffect;

        private Transform hoveredBoneTransform = null;
        private Transform lastHoveredBoneTransform = null;
        private static bool _highlightBonesMethodSearched = false;
        private static System.Reflection.MethodInfo _highlightBonesMethod = null;

        internal bool displayGraph = false;
        private bool lastCoordHadANBL = false;
        private static Material mat = new Material(Shader.Find("Hidden/Internal-Colored"));

        public PluginData loadedCardData = null;
        public PluginData loadedCoordData = null;
        public SerialisedNode lastSNode = null;

        //render bodge 
        private RenderTexture rTex;
        private Camera rCam;

        #region GameEvents

        protected override void OnCardBeingSaved(GameMode currentGameMode)
        {
            if (graphs.Count == 0 && boneEdits.Count == 0) return;
            PluginData data = new PluginData();
            if (graphs.Count > 0)
            {
                Dictionary<int, SerialisedGraph> sCharaGraphs = new Dictionary<int, SerialisedGraph>();
                Dictionary<int, SerialisedGraphData> sCharaGraphData = new Dictionary<int, SerialisedGraphData>();
                foreach (int outfit in graphs.Keys)
                {
                    if (graphs[outfit].getAllNodes().Count == Enum.GetNames(typeof(InputKey)).Length) continue;
                    sCharaGraphs.Add(outfit, SerialisedGraph.Serialise(graphs[outfit]));
                    sCharaGraphData.Add(outfit, SerialisedGraphData.Serialise(outfit, graphData[graphs[outfit]]));
                }

                data.data.Add("Graphs", MessagePackSerializer.Serialize(sCharaGraphs));
                data.data.Add("GraphData", MessagePackSerializer.Serialize(sCharaGraphData));
            }

            if (boneEdits.Count > 0)
            {
                data.data.Add("BoneEdits", MessagePackSerializer.Serialize(boneEdits));
            }

            data.data.Add("Version", saveVersion);
            SetExtendedData(data);
        }

        protected override void OnReload(GameMode currentGameMode, bool maintainState)
        {
            graphData.Clear();
            graphs.Clear();
            boneEdits.Clear();
            activeBoneEditIds.Clear();
            EnsureBoneEffectRegistered();

            PluginData data = maintainState ? loadedCardData : GetExtendedData();
            if (data == null)
            {
                return;
            }

            loadedCardData = data;

            byte version = 0;
            if (data.data.TryGetValue("Version", out var versionS) && versionS != null)
            {
                version = (byte)versionS;
            }

            if (version > 0 && version <= 2)
            {
                Dictionary<int, SerialisedGraph> sGraphs;
                Dictionary<int, SerialisedGraphData> sGraphData = null;
                if (data.data.TryGetValue("Graphs", out var graphsSerialised) && graphsSerialised != null)
                {
                    sGraphs =
                        MessagePackSerializer.Deserialize<Dictionary<int, SerialisedGraph>>((byte[])graphsSerialised);
                    if (data.data.TryGetValue("GraphData", out var graphDataSerialised) && graphDataSerialised != null)
                    {
                        sGraphData =
                            MessagePackSerializer.Deserialize<Dictionary<int, SerialisedGraphData>>(
                                (byte[])graphDataSerialised);
                    }

                    foreach (int outfit in sGraphs.Keys)
                    {
                        deserialiseGraph(version, outfit, sGraphs[outfit],
                            sGraphData != null ? sGraphData[outfit] : null);
                        AmazingNewBoneLogic.Logger.LogDebug($"Loaded Logic Graph for outfit {outfit}");
                    }

                    StartCoroutine(UpdateLater());

                    IEnumerator UpdateLater()
                    {
                        yield return null;
                        yield return null;
                        lfg?.ForceUpdate();
                    }
                }

                if (data.data.TryGetValue("BoneEdits", out var boneEditsSerialised) && boneEditsSerialised != null)
                {
                    boneEdits = MessagePackSerializer.Deserialize<Dictionary<int, List<BoneEffectEdit>>>((byte[])boneEditsSerialised);
                }
            }

            var boneController = ChaControl?.GetComponent<KKABMX.Core.BoneController>();
            if (boneController != null)
            {
                boneController.NeedsFullRefresh = true;
            }
            
            AmazingNewBoneLogic.UpdateMakerButtonVisibility();

            base.OnReload(currentGameMode, maintainState);
        }

        protected override void OnCoordinateBeingSaved(ChaFileCoordinate coordinate)
        {
            base.OnCoordinateBeingSaved(coordinate);

            PluginData data = new PluginData();
            int coord = ChaControl.fileStatus.coordinateType;
            bool hasGraph = graphs.ContainsKey(coord);
            bool hasBoneEdits = boneEdits.ContainsKey(coord) && boneEdits[coord].Count > 0;
            if (!hasGraph && !hasBoneEdits) return;

            if (hasGraph)
            {
                SerialisedGraph sGraph = SerialisedGraph.Serialise(graphs[coord]);
                SerialisedGraphData sGraphData = SerialisedGraphData.Serialise(coord, graphData[graphs[coord]]);
                data.data.Add("Graph", MessagePackSerializer.Serialize(sGraph));
                data.data.Add("GraphData", MessagePackSerializer.Serialize(sGraphData));
            }

            if (hasBoneEdits)
            {
                data.data.Add("BoneEdits", MessagePackSerializer.Serialize(boneEdits[coord]));
            }

            data.data.Add("Version", saveVersion);
            SetCoordinateExtendedData(coordinate, data);
        }

        protected override void OnCoordinateBeingLoaded(ChaFileCoordinate coordinate, bool maintainState)
        {
            base.OnCoordinateBeingLoaded(coordinate, maintainState);
            // Maker partial coordinate load fix
            if (MakerAPI.InsideAndLoaded)
            {
                // return if no accessories are being loaded
                if (GameObject.Find("cosFileControl")?.GetComponentInChildren<ChaCustom.CustomFileWindow>()
                        ?.tglCoordeLoadAcs.isOn == false) return;
            }

            int coordIdx = ChaControl.fileStatus.coordinateType;
            if (graphs.ContainsKey(coordIdx))
            {
                graphData.Remove(graphs[coordIdx]);
                graphs.Remove(coordIdx);
            }
            if (boneEdits.ContainsKey(coordIdx))
            {
                boneEdits.Remove(coordIdx);
            }
            activeBoneEditIds.Clear();
            EnsureBoneEffectRegistered();

            var abmxReg = CharacterApi.GetRegisteredBehaviour(typeof(BoneController));
            if (abmxReg != null && abmxReg.MaintainCoordinateState)
            {
                maintainState = true;
            }

            PluginData data = maintainState ? loadedCoordData : GetCoordinateExtendedData(coordinate);
            if (data == null)
            {
                return;
            }

            loadedCoordData = data;

            byte version = 0;
            if (data.data.TryGetValue("Version", out var versionS) && versionS != null)
            {
                version = (byte)versionS;
            }

            if (version <= 2)
            {
                if (data.data.TryGetValue("Graph", out var serialisedGraph) && serialisedGraph != null)
                {
                    SerialisedGraph sGraph =
                        MessagePackSerializer.Deserialize<SerialisedGraph>((byte[])serialisedGraph);
                    SerialisedGraphData sGraphData = null;
                    if (data.data.TryGetValue("GraphData", out var serialisedGraphData) && serialisedGraphData != null)
                    {
                        sGraphData =
                            MessagePackSerializer.Deserialize<SerialisedGraphData>((byte[])serialisedGraphData);
                    }

                    if (sGraph != null)
                    {
                        deserialiseGraph(version, coordIdx, sGraph, sGraphData);
                        AmazingNewBoneLogic.Logger.LogDebug($"Loaded Logic Graph for outfit {coordIdx}");
                    }

                    StartCoroutine(UpdateLater());

                    IEnumerator UpdateLater()
                    {
                        yield return null;
                        yield return null;
                        lfg?.ForceUpdate();
                    }
                }

                if (data.data.TryGetValue("BoneEdits", out var coordBoneEditsSerialised) && coordBoneEditsSerialised != null)
                {
                    boneEdits[coordIdx] = MessagePackSerializer.Deserialize<List<BoneEffectEdit>>((byte[])coordBoneEditsSerialised);
                }
            }

            var boneController = ChaControl?.GetComponent<KKABMX.Core.BoneController>();
            if (boneController != null)
            {
                boneController.NeedsFullRefresh = true;
            }

            AmazingNewBoneLogic.UpdateMakerButtonVisibility();
        }

        internal void AccessoryTransferred(int sourceSlot, int destinationSlot)
        {
            if (AmazingNewBoneLogic.Debug.Value)
                AmazingNewBoneLogic.Logger.LogInfo(
                    $"Transfering acc {sourceSlot + 1} to {destinationSlot + 1}...");

            // Check if there's anything to do
            int outfit = ChaControl.fileStatus.coordinateType;
            if (lfg == null || lfg.getNodeAt(sourceSlot + 1000000) == null) return;

            // Clear the destination node if we have any data of it
            LogicFlowOutput destNode = getOutput(destinationSlot, outfit);
            var data = graphData[lfg];
            if (destNode != null)
            {
                if (AmazingNewBoneLogic.Debug.Value)
                    AmazingNewBoneLogic.Logger.LogInfo($"Removing old data...");
                data.PurgeNode(destNode);
                lfg.RemoveNode(destNode.index);
            }

            // Copy any sensible data if the source node exists
            LogicFlowOutput sourceNode = getOutput(sourceSlot);
            if (sourceNode != null)
            {
                if (AmazingNewBoneLogic.Debug.Value)
                    AmazingNewBoneLogic.Logger.LogInfo($"Copying new data...");
                destNode = addOutput(destinationSlot, outfit);
                if (AmazingNewBoneLogic.Debug.Value && destNode == null)
                    AmazingNewBoneLogic.Logger.LogInfo($"Could not create new output!");
                if (sourceNode.inputAt(0) != null)
                {
                    destNode.SetInput(sourceNode.inputAt(0).index, 0);
                }

                GraphData.CopyAccData(sourceSlot, data, destinationSlot);
            }
        }

        internal void AccessoryKindChanged(int slot, bool forceRemove = false)
        {
            if (lfg == null) return;

            if (AmazingNewBoneLogic.Debug.Value)
                AmazingNewBoneLogic.Logger.LogInfo($"Acc in slot {slot + 1} changed kind!");
            if (forceRemove || ChaControl.objAccessory[slot] == null)
            {
                if (AmazingNewBoneLogic.Debug.Value) AmazingNewBoneLogic.Logger.LogInfo($"Removing...");
                int outfit = ChaControl.fileStatus.coordinateType;
                graphData[lfg].PurgeNode(getOutput(slot));
                lfg.RemoveNode(slot + 1000000);
            }
        }

        internal void AccessoriesCopied(int sourceOutfit, int destinationOutfit, IEnumerable<int> slots)
        {
            // Return only if data doesn't exist on either side of the operation
            if (!graphs.ContainsKey(sourceOutfit) && !graphs.ContainsKey(destinationOutfit)) return;

            // Create destination graph if it doesn't exist
            if (!graphs.ContainsKey(destinationOutfit))
            {
                createGraph(destinationOutfit);
            }

            if (AmazingNewBoneLogic.Debug.Value)
                AmazingNewBoneLogic.Logger.LogInfo(
                    $"Copying acc from outfit {sourceOutfit} to {destinationOutfit}...");

            // Create variables
            var dstGraph = graphs[destinationOutfit];
            var dstData = graphData[dstGraph];
            var srcGraph = graphs.ContainsKey(sourceOutfit) ? graphs[sourceOutfit] : null;
            var srcData = srcGraph == null ? null : graphData[srcGraph];

            // Copy data and nodes
            dstGraph.isLoading = true;
            foreach (int slot in slots)
            {
                int idxSlot = slot + 1000000;

                // Destroy any existing data on the destination outfit for this slot
                if (dstGraph.getNodeAt(idxSlot) != null)
                {
                    if (AmazingNewBoneLogic.Debug.Value)
                        AmazingNewBoneLogic.Logger.LogInfo($"Removing old data...");
                    dstData.PurgeNode(dstGraph.getNodeAt(idxSlot));
                    dstGraph.RemoveNode(idxSlot);
                }

                // Copy over data if it exists
                if (srcGraph == null || srcGraph.getNodeAt(idxSlot) == null) continue;
                if (AmazingNewBoneLogic.Debug.Value)
                    AmazingNewBoneLogic.Logger.LogInfo($"Copying new data...");
                LogicFlowOutput sOutput = (LogicFlowOutput)srcGraph.getNodeAt(idxSlot);
                if (sOutput == null) continue;
                List<int> iTree = sOutput.getInputTree();
                deserialiseNode(saveVersion, destinationOutfit,
                    SerialisedNode.Serialise(srcGraph.getNodeAt(idxSlot), srcGraph));
                foreach (int index in iTree)
                {
                    LogicFlowNode node = dstGraph.getNodeAt(index);
                    if (node == null)
                    {
                        var srcNode = srcGraph.getNodeAt(index);
                        deserialiseNode(saveVersion, destinationOutfit, SerialisedNode.Serialise(srcNode, srcGraph));
                        if (srcNode is LogicFlowNode_GRP)
                        {
                            var dstNode = (LogicFlowNode_GRP)dstGraph.getNodeAt(index);
                            var toRemove = new List<int>();
                            foreach (var set in dstNode.controlledNodes)
                            {
                                foreach (var idx in set.Value)
                                {
                                    if (dstGraph.getNodeAt(idx) == null)
                                    {
                                        toRemove.Add(idx);
                                    }
                                }

                                foreach (var idx in toRemove)
                                {
                                    dstNode.controlledNodes[set.Key].Remove(idx);
                                }

                                toRemove.Clear();
                                dstNode.setName(dstNode.getName());
                            }
                        }
                    }
                }

                GraphData.CopyAccData(slot, srcData, slot, dstData);
                dstData.MakeGraph();
            }

            dstGraph.isLoading = false;
        }

        public void OutfitChanged()
        {
            AmazingNewBoneLogic.Logger.LogDebug("Coordinate changed, applying data...");
            activeBoneEditIds.Clear();
            EnsureBoneEffectRegistered();
            StartCoroutine(UpdateLater());
        }
        
        private IEnumerator UpdateLater()
        {
            for (int i = 0; i < 2; i++) yield return null;
            if (lastCoordHadANBL)
            {
                lastCoordHadANBL = false;
                activeBoneEditIds.Clear();
                var boneController = ChaControl?.GetComponent<KKABMX.Core.BoneController>();
                if (boneController != null)
                {
                    boneController.NeedsFullRefresh = true;
                }
            }
            lfg?.ForceUpdate();
            AmazingNewBoneLogic.UpdateMakerButtonVisibility();
        }
        
        #endregion

        #region Deserialisation

        private void deserialiseGraph(int version, int outfit, SerialisedGraph sGraph,
            SerialisedGraphData sGraphData = null)
        {
            var newGraph = new LogicFlowGraph(new Rect(new Vector2(200, 200), sGraph.size));
            graphs.Add(outfit, newGraph);
            newGraph.isLoading = true;
            foreach (SerialisedNode sNode in sGraph.nodes.Where(x => x.type != SerialisedNode.NodeType.Gate_GRP))
                deserialiseNode(version, outfit, sNode);
            foreach (SerialisedNode sNode in sGraph.nodes.Where(x => x.type == SerialisedNode.NodeType.Gate_GRP))
                deserialiseNode(version, outfit, sNode);
            graphData.Add(graphs[outfit], new GraphData(this, newGraph, sGraphData));
            if (sGraphData == null) graphData[graphs[outfit]].advanced = true;
            newGraph.isLoading = false;
            if (AmazingNewBoneLogic.Debug.Value)
                AmazingNewBoneLogic.Logger.LogInfo($"Nodes in loaded graph: {newGraph.nodes.Count}");
        }

        private void deserialiseNode(int version, int outfit, SerialisedNode sNode)
        {
            if (AmazingNewBoneLogic.Debug.Value)
            {
                AmazingNewBoneLogic.Logger.LogInfo(
                    $"Deserialising node: Outfit {outfit}, {sNode.type}, {sNode.name}, {sNode.index}");
            }
            lastSNode = sNode;

            LogicFlowNode node = null;
            switch (sNode.type)
            {
                case SerialisedNode.NodeType.Gate_NOT:
                    node = new LogicFlowNode_NOT(graphs[outfit], sNode.index)
                        { label = sNode.name ?? "NOT", toolTipText = "NOT" };
                    if (sNode.data?.Count > 0) node.SetInput(sNode.data[0], 0);
                    break;
                case SerialisedNode.NodeType.Gate_AND:
                    node = new LogicFlowNode_AND(graphs[outfit], sNode.index)
                        { label = sNode.name ?? "AND", toolTipText = "AND" };
                    for (int i = 0; i < sNode.data?.Count; i++)
                    {
                        node.SetInput(sNode.data[i], i);
                    }

                    break;
                case SerialisedNode.NodeType.Gate_OR:
                    node = new LogicFlowNode_OR(graphs[outfit], sNode.index)
                        { label = sNode.name ?? "OR", toolTipText = "OR" };
                    for (int i = 0; i < sNode.data?.Count; i++)
                    {
                        node.SetInput(sNode.data[i], i);
                    }

                    break;
                case SerialisedNode.NodeType.Gate_XOR:
                    node = new LogicFlowNode_XOR(graphs[outfit], sNode.index)
                        { label = sNode.name ?? "XOR", toolTipText = "XOR" };
                    for (int i = 0; i < sNode.data?.Count; i++)
                    {
                        node.SetInput(sNode.data[i], i);
                    }

                    break;
                case SerialisedNode.NodeType.Gate_GRP:
                    node = new LogicFlowNode_GRP(this, graphs[outfit], sNode.index, sNode.name);
                    if (sNode.data?.Count > 0) node.SetInput(sNode.data[0], 0);
                    if (sNode.data2?.Count > 0) (node as LogicFlowNode_GRP).state = (int)sNode.data2[0];
                    if (sNode.data3 != null)
                    {
                        foreach (var kvp in sNode.data3)
                        {
                            var hashEntry = new HashSet<int>();
                            foreach (var idx in kvp.Value) hashEntry.Add(idx);
                            (node as LogicFlowNode_GRP).controlledNodes.Add(kvp.Key, hashEntry);
                        }

                        (node as LogicFlowNode_GRP).calcTooltip();
                    }

                    break;
                case SerialisedNode.NodeType.Input:
                    node = addInput((InputKey)sNode.index, sNode.position, outfit, sNode.name);
                    break;
                case SerialisedNode.NodeType.Output:
                    if (version == 1 && sNode.data.Count > 1)
                    {
                        node = addOutput(sNode.data[0], outfit);
                        StartCoroutine(SetInputLater(sNode.data[0] + 1000000, outfit, sNode.data[1]));
                    }
                    else
                    {
                        node = addOutput(sNode.index - 1000000, outfit, sNode.name);
                        if (node == null) AmazingNewBoneLogic.Logger.
                                LogMessage($"No output could be added for outfit {outfit}, slot {sNode.index - 999999}!");
                        if (sNode.data?.Count > 0) StartCoroutine(SetInputLater(sNode.index, outfit, sNode.data[0]));
                    }

                    IEnumerator SetInputLater(int _nodeIdx, int _outfit, int _inputIdx)
                    {
                        yield return null;
                        graphs[_outfit].getNodeAt(_nodeIdx).SetInput(_inputIdx, 0);
                    }

                    break;
                case SerialisedNode.NodeType.AdvancedInput:
                    node = deserialiseAdvancedInputNode(outfit, sNode, (AdvancedInputType)sNode.data2[0]);
                    if (sNode.name != null) node.label = sNode.name;
                    break;
            }

            if (node != null)
            {
                node.enabled = sNode.enabled;
                node.setPosition(sNode.position);
            }
        }

        private LogicFlowNode deserialiseAdvancedInputNode(int outfit, SerialisedNode sNode,
            AdvancedInputType advancedInputType)
        {
            LogicFlowNode node = null;
            switch (advancedInputType)
            {
                case AdvancedInputType.HandPtn:
                    node = addAdvancedInputHands((bool)sNode.data2[1], (bool)sNode.data2[2], (int)sNode.data2[3],
                        sNode.position, outfit, sNode.index);
                    break;
                case AdvancedInputType.EyesOpn:
                    node = addAdvancedInputEyeThreshold((bool)sNode.data2[1], (float)sNode.data2[2], sNode.position,
                        outfit, sNode.index);
                    break;
                case AdvancedInputType.MouthOpn:
                    node = addAdvancedInputMouthThreshold((bool)sNode.data2[1], (float)sNode.data2[2], sNode.position,
                        outfit, sNode.index);
                    break;
                case AdvancedInputType.EyesPtn:
                    node = addAdvancedInputEyePattern((int)sNode.data2[1], sNode.position, outfit, sNode.index);
                    break;
                case AdvancedInputType.MouthPtn:
                    node = addAdvancedInputMouthPattern((int)sNode.data2[1], sNode.position, outfit, sNode.index);
                    break;
                case AdvancedInputType.EyebrowPtn:
                    node = addAdvancedInputEyebrowPattern((int)sNode.data2[1], sNode.position, outfit, sNode.index);
                    break;
                case AdvancedInputType.Accessory:
                    node = addAdvancedInputAccessory((int)sNode.data2[1], sNode.position, outfit, sNode.index);
                    break;
                case AdvancedInputType.AlwaysOn:
                    node = addAdvancedInputAlwaysOn(sNode.position, outfit, sNode.index);
                    break;
            }

            return node;
        }

        #endregion

        #region Graph Construction

        private LogicFlowGraph getCurrentGraph()
        {
            if (!graphs.ContainsKey(ChaControl.fileStatus.coordinateType)) return null;
            lastCoordHadANBL = true;
            return graphs[ChaControl.fileStatus.coordinateType];
        }

        private void setCurrentGraph(LogicFlowGraph g)
        {
            int coord = ChaControl.fileStatus.coordinateType;
            if (graphs.ContainsKey(coord))
            {
                graphs[coord] = g;
            }
            else
            {
                graphs.Add(coord, g);
            }
        }

        public void UpdateGraphKeybinds()
        {
            graphs.Values.ToList().ForEach(graph =>
            {
                graph.KeyNodeDelete = AmazingNewBoneLogic.UIDeleteNodeKey.Value;
                graph.KeyNodeDisable = AmazingNewBoneLogic.UIDisableNodeKey.Value;
                graph.KeySelectTree = AmazingNewBoneLogic.UISelectedTreeKey.Value;
                graph.KeySelectNetwork = AmazingNewBoneLogic.UISelectNetworkKey.Value;
            });
        }

        internal LogicFlowGraph createGraph(int? outfit = null)
        {
            if (!outfit.HasValue) outfit = ChaControl.fileStatus.coordinateType;
            if (graphs == null) graphs = new Dictionary<int, LogicFlowGraph>();

            if (graphData == null) graphData = new Dictionary<LogicFlowGraph, GraphData>();
            graphs[outfit.Value] = new LogicFlowGraph(new Rect(new Vector2(100, 10), defaultGraphSize));

            // set input keycodes
            graphs[outfit.Value].KeyNodeDelete = AmazingNewBoneLogic.UIDeleteNodeKey.Value;
            graphs[outfit.Value].KeyNodeDisable = AmazingNewBoneLogic.UIDisableNodeKey.Value;
            graphs[outfit.Value].KeySelectTree = AmazingNewBoneLogic.UISelectedTreeKey.Value;
            graphs[outfit.Value].KeySelectNetwork = AmazingNewBoneLogic.UISelectNetworkKey.Value;

            // create simple mode data
            graphData[graphs[outfit.Value]] = new GraphData(this, graphs[outfit.Value]);

            float topY = 900;

            int i = 1;
            foreach (InputKey key in Enum.GetValues(typeof(InputKey)))
            {
                addInput(key, new Vector2(10, topY - 50 * i), outfit.Value);
                i++;
            }

            return graphs[outfit.Value];
        }

        internal LogicFlowInput addInput(InputKey key, Vector2 pos, int? outfit = null, string name = null)
        {
            LogicFlowGraph g;
            if (!outfit.HasValue) g = lfg;
            else g = graphs[outfit.Value];
            if (g == null) return null;

            LogicFlowInput node = null;
            switch ((int)key)
            {
                case 1001:
                    node = new LogicFlowInput_Func(() => getClothState(0, 0), g, (int)key) { label = name ?? "Top On" };
                    break;
                case 1002:
                    node = new LogicFlowInput_Func(() => getClothState(0, 1), g, (int)key) { label = name ?? "Top ½" };
                    break;
                case 1003:
                    node = new LogicFlowInput_Func(() => getClothState(1, 0), g, (int)key) { label = name ?? "Btm On" };
                    break;
                case 1004:
                    node = new LogicFlowInput_Func(() => getClothState(1, 1), g, (int)key) { label = name ?? "Btm ½" };
                    break;
                case 1005:
                    node = new LogicFlowInput_Func(() => getClothState(2, 0), g, (int)key) { label = name ?? "Bra On" };
                    break;
                case 1006:
                    node = new LogicFlowInput_Func(() => getClothState(2, 1), g, (int)key) { label = name ?? "Bra ½" };
                    break;
                case 1007:
                    node = new LogicFlowInput_Func(() => getClothState(3, 0), g, (int)key)
                        { label = name ?? "UWear On" };
                    break;
                case 1008:
                    node = new LogicFlowInput_Func(() => getClothState(3, 1), g, (int)key)
                        { label = name ?? "UWear ½" };
                    break;
                case 1009:
                    node = new LogicFlowInput_Func(() => getClothState(3, 2), g, (int)key)
                        { label = name ?? "UWear ¼" };
                    break;
                case 1010:
                    node = new LogicFlowInput_Func(() => getClothState(4, 0), g, (int)key)
                        { label = name ?? "Glove On" };
                    break;
                case 1011:
                    node = new LogicFlowInput_Func(() => getClothState(4, 1), g, (int)key)
                        { label = name ?? "Glove ½" };
                    break;
                case 1012:
                    node = new LogicFlowInput_Func(() => getClothState(4, 2), g, (int)key)
                        { label = name ?? "Glove ¼" };
                    break;
                case 1013:
                    node = new LogicFlowInput_Func(() => getClothState(5, 0), g, (int)key)
                        { label = name ?? "PHose On" };
                    break;
                case 1014:
                    node = new LogicFlowInput_Func(() => getClothState(5, 1), g, (int)key)
                        { label = name ?? "PHose ½" };
                    break;
                case 1015:
                    node = new LogicFlowInput_Func(() => getClothState(5, 2), g, (int)key)
                        { label = name ?? "PHose ¼" };
                    break;
                case 1016:
                    node = new LogicFlowInput_Func(() => getClothState(6, 0), g, (int)key)
                        { label = name ?? "LWear On" };
                    break;
#if KKS
                case 1018:
                    node = new LogicFlowInput_Func(() => getClothState(7, 0), g, (int)key)
                        { label = name ?? "Shoes On" };
                    break;
#else
                case 1017:
                    node = new LogicFlowInput_Func(() => getShoeState(7, 0, 0), g, (int)key) {label =
 name ?? "Indoor On" };
                    break;
                case 1018:
                    node = new LogicFlowInput_Func(() => getShoeState(8, 0, 1), g, (int)key) {label =
 name ?? "Outdoor On" };
                    break;
#endif
                default:
                    break;
            }

            if (node != null)
            {
                node.setPosition(pos);
                node.toolTipText = key.ToString();
                node.deletable = false;
            }

            return node;
        }

        /// <summary>
        /// Returns the LogicFlowNode that is the passed ClothingSlot and ClothingState.
        /// If the State does not have a default Input-Node automatically constructs a MetaInput. 
        /// </summary>
        /// <param name="clothingSlot"></param>
        /// <param name="clothingState"></param>
        /// <param name="outfit"></param>
        /// <returns></returns>
        public LogicFlowNode getInput(int clothingSlot, int clothingState, int? outfit = null)
        {
            if (!outfit.HasValue) outfit = ChaControl.fileStatus.coordinateType;
            InputKey? key = GetInputKey(clothingSlot, clothingState);
            if (key.HasValue)
            {
                if (graphs[outfit.Value]?.getNodeAt((int)key.Value) == null) return null;
                return graphs[outfit.Value].getNodeAt((int)key.Value);
            }

            return constructMetaInput(clothingSlot, outfit);
        }

        /// <summary>
        /// Auto constructs a Node which outputs if one or the other state of a clothingSlot is active
        /// </summary>
        /// <param name="clothingSlot"></param>
        /// <param name="outfit"></param>
        /// <returns></returns>
        public LogicFlowNode_NOT constructMetaInput(int clothingSlot, int? outfit = null)
        {
            if (!outfit.HasValue) outfit = ChaControl.fileStatus.coordinateType;
            if (!graphs.ContainsKey(outfit.Value)) return null;
            LogicFlowGraph graph = graphs[outfit.Value];
            LogicFlowNode or;
            switch (clothingSlot)
            {
                case 0:
                case 1:
                case 2:
                    or = connectInputs(new List<int> { 0, 1 }, clothingSlot, outfit.Value, graph);
                    break;
                case 3:
                case 4:
                case 5:
                    or = connectInputs(new List<int> { 0, 1, 2 }, clothingSlot, outfit.Value, graph);
                    break;
                case 6:
                case 7:
                case 8:
                    or = connectInputs(new List<int> { 0 }, clothingSlot, outfit.Value, graph);
                    break;
                default: return null;
            }

            LogicFlowNode_NOT not = addNotForInput(or.index, outfit.Value);
            not.setPosition(or.getPosition() + new Vector2(75, 0));
            return not;
        }

        /// <summary>
        /// Auto constructs a Or gate connected to the passed inputs
        /// </summary>
        /// <param name="inId1"></param>
        /// <param name="inId2"></param>
        /// <param name="outfit"></param>
        /// <returns></returns>
        internal LogicFlowNode_OR addOrGateForInputs(int inId1, int inId2, int outfit)
        {
            if (!graphs.ContainsKey(outfit)) return null;
            LogicFlowNode logicFlowNode = graphs[outfit].getAllNodes().Find(node =>
            {
                if (node is null) return false;
                if (node is LogicFlowNode_OR)
                {
                    LogicFlowNode in1 = node.inputAt(0);
                    LogicFlowNode in2 = node.inputAt(1);
                    if (in1 == null || in2 == null) return false;
                    return (in1.index == inId1 && in2.index == inId2) || (in1.index == inId2 && in2.index == inId1);
                }

                return false;
            });

            if (logicFlowNode != null) return logicFlowNode as LogicFlowNode_OR;
            else
            {
                LogicFlowNode_OR or = addGate(outfit, 2) as LogicFlowNode_OR;
                or.SetInput(inId1, 0);
                or.SetInput(inId2, 1);
                return or;
            }
        }

        /// <summary>
        /// Auto constructs a And gate connected to the passed inputs
        /// </summary>
        /// <param name="inId1"></param>
        /// <param name="inId2"></param>
        /// <param name="outfit"></param>
        /// <returns></returns>
        internal LogicFlowNode_AND addAndGateForInputs(int inId1, int inId2, int outfit)
        {
            if (!graphs.ContainsKey(outfit)) return null;
            LogicFlowNode logicFlowNode = graphs[outfit].getAllNodes().Find(node =>
            {
                if (node is null) return false;
                if (node is LogicFlowNode_AND)
                {
                    LogicFlowNode in1 = node.inputAt(0);
                    LogicFlowNode in2 = node.inputAt(1);
                    if (in1 == null || in2 == null) return false;
                    return (in1.index == inId1 && in2.index == inId2) || (in1.index == inId2 && in2.index == inId1);
                }

                return false;
            });

            if (logicFlowNode != null) return logicFlowNode as LogicFlowNode_AND;
            else
            {
                LogicFlowNode_AND and = addGate(outfit, 1) as LogicFlowNode_AND;
                and.SetInput(inId1, 0);
                and.SetInput(inId2, 1);
                return and;
            }
        }

        /// <summary>
        /// Auto constructs a Not gate connected to the passed input
        /// </summary>
        /// <param name="inId"></param>
        /// <param name="outfit"></param>
        /// <returns></returns>
        internal LogicFlowNode_NOT addNotForInput(int inId, int outfit)
        {
            if (!graphs.ContainsKey(outfit)) return null;
            LogicFlowNode logicFlowNode = graphs[outfit].getAllNodes().Find(node =>
                node != null && node is LogicFlowNode_NOT && node.inputAt(0) != null && node.inputAt(0).index == inId);

            if (logicFlowNode != null) return logicFlowNode as LogicFlowNode_NOT;
            else
            {
                LogicFlowNode_NOT not = addGate(outfit, 0) as LogicFlowNode_NOT;
                not.SetInput(inId, 0);
                return not;
            }
        }

        /// <summary>
        /// Autoconstructs nodes that give an output that turns on for the given states on the given clothingslot.
        /// </summary>
        /// <param name="statesThatTurnTheOutputOn">List of States (0-3) that should turn the output on.</param>
        /// <param name="clothingSlot"></param>
        /// <param name="outfit"></param>
        /// <param name="graph"></param>
        /// <returns></returns>
        private LogicFlowNode connectInputs(List<int> statesThatTurnTheOutputOn, int clothingSlot, int outfit,
            LogicFlowGraph graph)
        {
            switch (statesThatTurnTheOutputOn.Count)
            {
                case 0:
                    return null;
                case 1:
                    LogicFlowNode input = getInput(clothingSlot, statesThatTurnTheOutputOn[0], outfit);
                    return input;
                case 2:
                    LogicFlowNode input1 = getInput(clothingSlot, statesThatTurnTheOutputOn[0], outfit);
                    LogicFlowNode input2 = getInput(clothingSlot, statesThatTurnTheOutputOn[1], outfit);
                    LogicFlowNode_OR or0 = addOrGateForInputs(input1.index, input2.index, outfit);
                    or0.setPosition(input1.getPosition() + new Vector2(75, 0));
                    return or0;
                case 3:
                    LogicFlowNode input_1 = getInput(clothingSlot, statesThatTurnTheOutputOn[0], outfit);
                    LogicFlowNode input_2 = getInput(clothingSlot, statesThatTurnTheOutputOn[1], outfit);
                    LogicFlowNode input_3 = getInput(clothingSlot, statesThatTurnTheOutputOn[2], outfit);

                    LogicFlowNode_OR or1 = addOrGateForInputs(input_1.index, input_2.index, outfit);
                    LogicFlowNode_OR or2 = addOrGateForInputs(or1.index, input_3.index, outfit);

                    or1.setPosition(input_1.getPosition() + new Vector2(75, 0));

                    or2.setPosition(or1.getPosition() + new Vector2(75, 0));
                    return or2;
                case 4:
                    return null;
                default:
                    return null;
            }
        }

        #region Advanced Inputs

        public LogicFlowNode addAdvancedInputAccessory(int slot, Vector2 pos, int? outfit = null, int? index = null)
        {
            LogicFlowGraph g;
            if (!outfit.HasValue) g = lfg;
            else g = graphs[outfit.Value];
            if (g == null) return null;

            LogicFlowInput_Func node =
                new LogicFlowInput_Func(
                    () =>
                    {
                        return ChaControl.fileStatus.showAccessory.Length > slot
                            ? ChaControl.fileStatus.showAccessory[slot]
                            : false;
                    }, g, index) { label = $"Slot {slot + 1}" };

            if (node != null)
            {
                node.setPosition(pos);
                node.toolTipText = $"Accessory Slot {slot + 1}";
                node.deletable = true;
            }

            if (!serialisationData.ContainsKey(g)) serialisationData.Add(g, new Dictionary<int, List<object>>());
            serialisationData[g].Add(node.index, new List<object> { AdvancedInputType.Accessory, slot });

            return node;
        }

        public LogicFlowNode addAdvancedInputHands(bool leftright, bool anim, int pattern, Vector2 pos,
            int? outfit = null, int? index = null)
        {
            LogicFlowGraph g;
            if (!outfit.HasValue) g = lfg;
            else g = graphs[outfit.Value];
            if (g == null) return null;

            int sideKey = leftright ? 1 : 0;
            LogicFlowInput_Func node;
            if (anim)
            {
                node = new LogicFlowInput_Func(() => !ChaControl.GetEnableShapeHand(sideKey), g, index)
                    { label = leftright ? "R Hand" : "L Hand" };
            }
            else
            {
                node = new LogicFlowInput_Func(
                    () => ChaControl.GetEnableShapeHand(sideKey) && ChaControl.GetShapeHandIndex(sideKey, 0) == pattern,
                    g, index) { label = leftright ? "Hand-R" : "Hand-L" };
            }

            if (node != null)
            {
                string side = leftright ? "right" : "left";
                string option = anim ? "Animation" : $"Pattern {pattern + 1}";

                node.setPosition(pos);
                node.toolTipText = $"Hand: {side} - {option}";
                node.deletable = true;
            }

            if (!serialisationData.ContainsKey(g)) serialisationData.Add(g, new Dictionary<int, List<object>>());
            serialisationData[g].Add(node.index,
                new List<object> { AdvancedInputType.HandPtn, leftright, anim, pattern });

            return node;
        }

        public LogicFlowNode addAdvancedInputEyeThreshold(bool moreThan, float threshold, Vector2 pos,
            int? outfit = null, int? index = null)
        {
            LogicFlowGraph g;
            if (!outfit.HasValue) g = lfg;
            else g = graphs[outfit.Value];
            if (g == null) return null;

            LogicFlowInput_Func node;
            if (moreThan)
            {
                node = new LogicFlowInput_Func(() => ChaControl.GetEyesOpenMax() > threshold, g, index)
                    { label = "EyeOpen" };
            }
            else
            {
                node = new LogicFlowInput_Func(() => ChaControl.GetEyesOpenMax() <= threshold, g, index)
                    { label = "EyeOpen" };
            }

            if (node != null)
            {
                string comp = moreThan ? "More than" : "Less or equal to";
                string value = threshold.ToString("0.00");

                node.setPosition(pos);
                node.toolTipText = $"Eye Openess: {comp} {value}";
                node.deletable = true;
            }

            if (!serialisationData.ContainsKey(g)) serialisationData.Add(g, new Dictionary<int, List<object>>());
            serialisationData[g].Add(node.index, new List<object> { AdvancedInputType.EyesOpn, moreThan, threshold });

            return node;
        }

        public LogicFlowNode addAdvancedInputEyePattern(int pattern, Vector2 pos, int? outfit = null, int? index = null)
        {
            LogicFlowGraph g;
            if (!outfit.HasValue) g = lfg;
            else g = graphs[outfit.Value];
            if (g == null) return null;

            LogicFlowInput_Func node = new LogicFlowInput_Func(() => ChaControl.GetEyesPtn() == pattern, g, index)
                { label = "Eye Ptn" };

            if (node != null)
            {
                node.setPosition(pos);
                node.toolTipText = $"Eye Pattern: {pattern + 1}";
                node.deletable = true;
            }

            if (!serialisationData.ContainsKey(g)) serialisationData.Add(g, new Dictionary<int, List<object>>());
            serialisationData[g].Add(node.index, new List<object> { AdvancedInputType.EyesPtn, pattern });

            return node;
        }

        public LogicFlowNode addAdvancedInputMouthThreshold(bool moreThan, float threshold, Vector2 pos,
            int? outfit = null, int? index = null)
        {
            LogicFlowGraph g;
            if (!outfit.HasValue) g = lfg;
            else g = graphs[outfit.Value];
            if (g == null) return null;

            LogicFlowInput_Func node;
            if (moreThan)
            {
                node = new LogicFlowInput_Func(() => ChaControl.GetMouthOpenMax() > threshold, g, index)
                    { label = "MouthOpn" };
            }
            else
            {
                node = new LogicFlowInput_Func(() => ChaControl.GetMouthOpenMax() <= threshold, g, index)
                    { label = "MouthOpn" };
            }

            if (node != null)
            {
                string comp = moreThan ? "More than" : "Less or equal to";
                string value = threshold.ToString("0.00");

                node.setPosition(pos);
                node.toolTipText = $"Mouth Openess: {comp} {value}";
                node.deletable = true;
            }

            if (!serialisationData.ContainsKey(g)) serialisationData.Add(g, new Dictionary<int, List<object>>());
            serialisationData[g].Add(node.index, new List<object> { AdvancedInputType.MouthOpn, moreThan, threshold });

            return node;
        }

        public LogicFlowNode addAdvancedInputMouthPattern(int pattern, Vector2 pos, int? outfit = null,
            int? index = null)
        {
            LogicFlowGraph g;
            if (!outfit.HasValue) g = lfg;
            else g = graphs[outfit.Value];
            if (g == null) return null;

            LogicFlowInput_Func node = new LogicFlowInput_Func(() => ChaControl.GetMouthPtn() == pattern, g, index)
                { label = "MouthPtn" };

            if (node != null)
            {
                node.setPosition(pos);
                node.toolTipText = $"Mouth Pattern: {pattern + 1}";
                node.deletable = true;
            }

            if (!serialisationData.ContainsKey(g)) serialisationData.Add(g, new Dictionary<int, List<object>>());
            serialisationData[g].Add(node.index, new List<object> { AdvancedInputType.MouthPtn, pattern });

            return node;
        }

        public LogicFlowNode addAdvancedInputEyebrowPattern(int pattern, Vector2 pos, int? outfit = null,
            int? index = null)
        {
            LogicFlowGraph g;
            if (!outfit.HasValue) g = lfg;
            else g = graphs[outfit.Value];
            if (g == null) return null;

            LogicFlowInput_Func node = new LogicFlowInput_Func(() => ChaControl.GetEyebrowPtn() == pattern, g, index)
                { label = "Eyebrow" };

            if (node != null)
            {
                node.setPosition(pos);
                node.toolTipText = $"Eyebrow Pattern: {pattern + 1}";
                node.deletable = true;
            }

            if (!serialisationData.ContainsKey(g)) serialisationData.Add(g, new Dictionary<int, List<object>>());
            serialisationData[g].Add(node.index, new List<object> { AdvancedInputType.EyebrowPtn, pattern });

            return node;
        }

        public LogicFlowNode addAdvancedInputAlwaysOn(Vector2 pos, int? outfit = null, int? index = null)
        {
            LogicFlowGraph g;
            if (!outfit.HasValue) g = lfg;
            else g = graphs[outfit.Value];
            if (g == null) return null;

            LogicFlowInput_Func node = new LogicFlowInput_Func(() => true, g, index)
                { label = "Always On" };

            if (node != null)
            {
                node.setPosition(pos);
                node.toolTipText = "Always On";
                node.deletable = true;
            }

            if (!serialisationData.ContainsKey(g)) serialisationData.Add(g, new Dictionary<int, List<object>>());
            serialisationData[g].Add(node.index, new List<object> { AdvancedInputType.AlwaysOn });

            return node;
        }

        #endregion

        /// <summary>
        /// Adds a ouput node for an accessory slot 
        /// </summary>
        /// <param name="slot">Accessory Slot</param>
        /// <param name="outfit">Outfit Slot</param>
        public LogicFlowOutput addOutput(int slot, int? outfit = null, string name = null)
        {
            if (!outfit.HasValue) outfit = ChaControl.fileStatus.coordinateType;

            if (graphs.ContainsKey(outfit.Value))
            {
                int idxSlot = 1000000 + slot;
                var existingNode = graphs[outfit.Value].getNodeAt(idxSlot);
                if (existingNode != null) return (LogicFlowOutput)existingNode;
                var graph = graphs[outfit.Value];
                LogicFlowOutput output =
                    new LogicFlowOutput_Action((value) => setAccessoryState(slot, value), graph, key: idxSlot)
                        { label = name ?? $"Slot {slot + 1}", toolTipText = $"Slot {slot + 1}" };
                int numOut = graphs[outfit.Value].getAllNodes().Where(x => x is LogicFlowOutput).Count();
                output.setPosition(OutputPos(numOut + 1));
                if (graph.getSize().x < output.getPosition().x + 80f)
                    graph.setSize(new Vector2(output.getPosition().x + 80f, graph.getSize().y));

                output.nodeDeletedEvent += (object sender, NodeDeletedEventArgs e) =>
                    AmazingNewBoneLogic.Logger.LogInfo($"Removed Slot {slot} on outfit {outfit.Value}");
                return output;
            }

            return null;
        }

        public LogicFlowOutput getOutput(int slot, int? outfit = null)
        {
            if (!outfit.HasValue) outfit = ChaControl.fileStatus.coordinateType;
            if (graphs.TryGetValue(outfit.Value, out LogicFlowGraph graph) && graph != null)
            {
                if (graph.getNodeAt(1000000 + slot) == null) return null;
                return graph.getNodeAt(1000000 + slot) as LogicFlowOutput;
            }

            return null;
        }

        /// <summary>
        /// Add a logic gate node to the current graph
        /// </summary>
        /// <param name="type">0 = NOT, 1 = AND, 2 = OR, 3 = XOR, 4 = GRP</param>
        public LogicFlowGate addGate(byte type)
        {
            return addGate(ChaControl.fileStatus.coordinateType, type);
        }

        /// <summary>
        /// Add a logic gate node to the specifed graph
        /// </summary>
        /// <param name="outfit">Outfit Slot</param>
        /// <param name="type">0 = NOT, 1 = AND, 2 = OR, 3 = XOR, 4 = GRP</param>
        public LogicFlowGate addGate(int outfit, byte type, string name = null)
        {
            if (graphs.ContainsKey(outfit))
            {
                LogicFlowGate gate;
                switch (type)
                {
                    default:
                        gate = new LogicFlowNode_NOT(graphs[outfit]) { label = name ?? "NOT", toolTipText = "NOT" };
                        break;
                    case 1:
                        gate = new LogicFlowNode_AND(graphs[outfit]) { label = name ?? "AND", toolTipText = "AND" };
                        break;
                    case 2:
                        gate = new LogicFlowNode_OR(graphs[outfit]) { label = name ?? "OR", toolTipText = "OR" };
                        break;
                    case 3:
                        gate = new LogicFlowNode_XOR(graphs[outfit]) { label = name ?? "XOR", toolTipText = "XOR" };
                        break;
                    case 4:
                        gate = new LogicFlowNode_GRP(this, graphs[outfit], name: name);
                        break;
                }

                gate.setPosition(graphs[outfit].getSize() / 2 - (gate.getSize() / 2));

                return gate;
            }

            return null;
        }

        #endregion

        #region General Methods

        public bool getClothState(int clothType, byte stateValue)
        {
            return ChaControl.fileStatus.clothesState[clothType] == stateValue;
        }

#if KK
        public bool getShoeState(int clothType, byte stateValue, int shoe) {
            var file = ChaControl.fileStatus;
            return file.shoesType == shoe && file.clothesState[clothType] == stateValue;
        }
#endif

        public void setAccessoryState(int graphKey, bool stateValue)
        {
            int coord = ChaControl.fileStatus.coordinateType;
            if (boneEdits.TryGetValue(coord, out var edits) && edits != null)
            {
                var edit = edits.FirstOrDefault(e => e.GraphKey == graphKey);
                if (edit != null)
                {
                    SetBoneEditActive(edit.Id, stateValue);
                }
            }
        }

        public void Show(bool resetPostion)
        {
            if (lfg == null) createGraph();
            displayGraph = true;
            AnblCameraComponent acc = rCam.GetOrAddComponent<AnblCameraComponent>();
            acc.OnPostRenderEvent += postRenderEvent;
            lfg.setUIScaleModifier(AmazingNewBoneLogic.UIScaleModifier.Value);
            GameCursor c = GameCursor.Instance;
            if (resetPostion)
            {
                lfg.setPosition(new Vector2(100, 10));
            }
            else
            {
                simpleWindowRect.x = Mathf.Clamp(simpleWindowRect.x, 0, Mathf.Max(0, Screen.width - simpleWindowRect.width));
                simpleWindowRect.y = Mathf.Clamp(simpleWindowRect.y, 0, Mathf.Max(0, Screen.height - simpleWindowRect.height));
            }

            if (c != null)
            {
                c.SetCursorLock(false);
            }
        }

        public void Hide()
        {
            displayGraph = false;
            displayBoneEditor = false;
            UpdateHighlight(null);
            if (MakerAPI.InsideAndLoaded) AmazingNewBoneLogic.SidebarToggle.Value = false;
            AnblCameraComponent acc = rCam.GetComponent<AnblCameraComponent>();
            if (acc != null) acc.OnPostRenderEvent -= postRenderEvent;
        }

        private void postRenderEvent(object sender, CameraEventArgs e)
        {
            if (lfg == null) return;
            if (displayGraph)
            {
                // init GL
                GL.PushMatrix();
                mat.SetPass(0);
                GL.LoadOrtho();

                lfg.draw();

                // end GL
                GL.PopMatrix();
            }
        }

        protected override void Start()
        {
            rTex = new RenderTexture(Screen.width, Screen.height, 32);
            // Create a new camera
            GameObject renderCameraObject = new GameObject("ANBL_UI_Camera");
            renderCameraObject.transform.SetParent(this.transform);
            rCam = renderCameraObject.AddComponent<Camera>();
            rCam.targetTexture = rTex;
            rCam.clearFlags = CameraClearFlags.SolidColor;
            rCam.backgroundColor = Color.clear;
            rCam.cullingMask = 0;

            base.Start();
            EnsureBoneEffectRegistered();
        }

        protected override void OnDestroy()
        {
            var boneController = ChaControl?.GetComponent<KKABMX.Core.BoneController>();
            if (boneController != null && _boneEffect != null)
            {
                boneController.RemoveBoneEffect(_boneEffect);
            }
            base.OnDestroy();
        }

        public void EnsureBoneEffectRegistered()
        {
            if (_boneEffect == null)
            {
                _boneEffect = new ANBLBoneEffect();
            }
            var boneController = ChaControl.GetComponent<KKABMX.Core.BoneController>();
            if (boneController != null)
            {
                boneController.AddBoneEffect(_boneEffect);
            }
        }

        public void SetBoneEditActive(string editId, bool active)
        {
            if (active)
            {
                activeBoneEditIds.Add(editId);
            }
            else
            {
                activeBoneEditIds.Remove(editId);
            }
        }

        public IEnumerable<string> GetAllConfiguredBoneNames()
        {
            int coord = ChaControl.fileStatus.coordinateType;
            if (!boneEdits.TryGetValue(coord, out var edits) || edits == null)
                return Enumerable.Empty<string>();

            return edits
                .Where(e => !string.IsNullOrEmpty(e.BoneName))
                .Select(e => e.BoneName)
                .Distinct();
        }

        public BoneModifierData GetAggregateModifier(string bone, int coordinate)
        {
            if (!boneEdits.TryGetValue(coordinate, out var edits) || edits == null)
                return null;

            var activeEdits = edits
                .Where(e => e.BoneName == bone && activeBoneEditIds.Contains(e.Id) && e.Modifier != null)
                .ToList();

            if (activeEdits.Count == 0)
                return null;

            Vector3 scale = Vector3.one;
            float length = 1f;
            Vector3 position = Vector3.zero;
            Vector3 rotation = Vector3.zero;

            foreach (var edit in activeEdits)
            {
                var mod = edit.Modifier;
                scale = Vector3.Scale(scale, mod.ScaleModifier);
                length *= mod.LengthModifier;
                position += mod.PositionModifier;
                rotation += mod.RotationModifier;
            }

            return new BoneModifierData(scale, length, position, rotation);
        }

        protected override void Update()
        {
            if (lfg == null) return;

            if (displayGraph)
            {
                if ((!MakerAPI.InsideMaker && !StudioAPI.InsideStudio) || Input.GetKeyDown(KeyCode.F1) ||
                    Input.GetKeyDown(KeyCode.Escape))
                {
                    Hide();
                    return;
                }

                lfg.update();
                SyncNamesAndDeletedNodes();
                if (MakerAPI.InsideAndLoaded)
                {
                    if (lfg.eatingInput
                        && AmazingNewBoneLogic.Instance.getMakerCursorMangaer() != null
                        && AmazingNewBoneLogic.Instance.getMakerCursorMangaer().isActiveAndEnabled == true)
                    {
                        AmazingNewBoneLogic.Instance.getMakerCursorMangaer().enabled = false;
                    }

                    if (!lfg.eatingInput
                        && AmazingNewBoneLogic.Instance.getMakerCursorMangaer() != null
                        && AmazingNewBoneLogic.Instance.getMakerCursorMangaer().isActiveAndEnabled == false)
                    {
                        AmazingNewBoneLogic.Instance.getMakerCursorMangaer().enabled = true;
                    }
                }
            }
            else lfg.backgroundUpdate();

            base.Update();
        }

        internal static int OutputCol(int num)
        {
            return (num + (num / 35) - 1) / 18;
        }

        /// <summary>
        /// Calculate the position of the n-th output
        /// </summary>
        /// <param name="num">The 1-indexed number of the output</param>
        /// <returns>The calculated position</returns>
        internal static Vector2 OutputPos(int num)
        {
            int col = OutputCol(num);
            return new Vector2(
                defaultGraphSize.x - 80f + col * 100f,
                defaultGraphSize.y - 50f * (num % 35 - num % 35 / 19 * 18 + (num % 35 == 0 ? 17 : 0)) - col % 2 * 25
            );
        }

        #region OnGUI

        private Rect normalInputRect = new Rect(0, 0, 130, 20);

#if KKS
        private bool kkcompatibility = false;
#endif
        private bool fullCharacter = false;
        private string studioAddOutputTextInput = "1";
        private bool showHelp = false;
        private Vector2 showHelpScroll = new Vector2();
        private int helpPage = 0;

        private List<List<string>> helpText = new List<List<string>>()
        {
            new List<string> {
                "Basics:",
                "Connect nodes by dragging from the right triangle (output) to left triangle (input) of another\n" +
                "Disconnect nodes dragging the connection away from the input or by RIGHT CLICKING the input\n" +
                "RED nodes/Connections means its currently OFF\n" +
                "GREEN nodes/Connections means its currenlty ON\n" +
                "Nodes with RED BORDER have missing inputs\n" +
                "Nodes with a YELLOW body are currently selected"
            },
            new List<string> {
                "Controls:",
                "Move Nodes by CLICKING and DRAGGING them\n" +
                "Resize the window on its BOTTOM RIGHT corner\n" +
                "Select MULTIPLE nodes by holding SHIFT\n" +
                "Select a group of nodes with a box (left drag on empty space)\n" +
                "Unselect all nodes by left clicking empty space\n" +
                "Delete selected nodes by pressing DEL\n" +
                "Disable selected nodes by pressing ALT+D\n" +
                "Disabled nodes will output FALSE, no matter the input\n" +
                "Select all downstream nodes of the selected nodes by pressing T (Tree)\n" +
                "Select all influenced nodes of the selected nodes by pressing N (Network)\n" +
                "Right-click nodes (not their labels) to rename them"
            },
            new List<string> {
                "Basic Nodes:",
                "INPUTS turn on/off according to the clothing state they represent\n" +
                "ACCESSORY INPUTS turn on/off according to the accessory slot they represent\n" +
                "Add ACCESSORY INPUTS by clicking the button in the accessory UI\n" +
                "OUTPUTS control the according accessory slot\n" +
                "Add OUTPUTs by clicking the button in the accessory UI\n" +
                "NOT-GATES output the opposite of their input\n" +
                "AND-GATES turn on if BOTH inputs are on\n" +
                "OR-GATES turn on if ONE OR BOTH inputs are on\n" +
                "XOR-GATES turn on if EXACTLY ONE input is on"
            },
            new List<string> {
                "Group Nodes:",
                "Add these via the menu on the right\n" +
                "You can connect them to however many nodes you want them to control\n" +
                "They have an integer state as indicated by their label, you can cycle this state via the < and > buttons\n" +
                "Right click the output handle of the node to specify active outputs for the current state\n" +
                "If they receive no input or an ON input, they will output as normal, however, on an OFF input, they will only output OFF"
            },
            new List<string> {
                "Advanced Input Nodes:",
                "HAND PATTERN is on if the specified hand is set to the specified pattern\n" +
                "EYE PATTERN is on if the eyes are set to the specified pattern\n" +
                "EYE THRESHOLD is on if the eye are MORE or LESS OR EQUALLY open compared to the specified threshold\n" +
                "MOUTH PATTERN is on if the moth is set to the specified pattern\n" +
                "MOUTH THRESHOLD is on if the mouth is MORE or LESS OR EQUALLY open compared to the specified theshold\n" +
                "EYEBROW PATTERN is on if the eyesbrows are set to the specified pattern"
            },
            new List<string> {
                "ASS Data Conversion",
                "Feature is experimental, no guarantees!!\n" +
                "Tries to convert Accessory State Sync data saved in the card to a ANBL graph\n" +
                "Only Accessories with ONE or TWO connected clothing slots are supported\n" +
                "The generated nodes will not be sorted properly and overlap\n" +
                "The generated graph can often be simplified a lot"
            }
        };

        private int? renamedNode = null;
        private Rect renameRect = new Rect();
        private string renameName = "";

        internal LogicFlowNode_GRP groupToSetActives = null;
        private List<LogicFlowNode> groupConnections = null;
        private Rect groupScrollRect = new Rect();
        private Vector2 groupScrollPos = Vector2.zero;

        private const float simpleMinWidth = 720f;
        private const float simpleMinHeight = 480f;
        private Rect simpleWindowRect = new Rect(150, 150, simpleMinWidth, simpleMinHeight);
        private Vector2 simpleWindowScrollPosAcs = Vector2.zero;
        private Vector2 simpleWindowScrollPosGrp = Vector2.zero;

        private int? simpleAccBeingBound = null;
        private Rect simpleAccBindRect = new Rect();
        private Vector2 simpleAccBindScrollPos = Vector2.zero;
        private static GUIStyle bindStateStyleOff = null;
        private static GUIStyle bindStateStyleOn = null;

        private static GUIStyle whiteBox = null;

        private int? grpBeingAddedTo = null;
        private Rect grpAddRect = new Rect();
        private Vector2 grpAddScrollPos = Vector2.zero;
        private string grpAddFilter = "";

        private bool isConfirming = false;
        private Rect confirmRect = new Rect();
        private string confirmTitle = "";
        private string confirmText = "";
        private ConfirmFunc onConfirm = null;

        private delegate void ConfirmFunc();

        void OnGUI()
        {
            if (lfg == null) return;

            if (bindStateStyleOff == null || bindStateStyleOn == null)
            {
                InitGUI();
            }

            // Main simple window
            if (displayGraph && !graphData[lfg].advanced)
            {
                var solidSkin = IMGUIUtils.SolidBackgroundGuiSkin;
                simpleWindowRect = GUILayout.Window(simpleModeWindowID, simpleWindowRect, (windowId) =>
                    {
                        var data = graphData[lfg];

                        // Negative spacing to get the title row to be on the title
                        GUILayout.Space(-22);

                        // Title row
                        GUILayout.BeginHorizontal();
                        {
                            GUILayout.Space(-6);
                            if (GUILayout.Button("Clear", bindStateStyleOff))
                            {
                                confirmRect =
                                    new Rect(
                                        simpleWindowRect.position + Event.current.mousePosition - new Vector2(150, 30),
                                        new Vector2(300, 60));
                                confirmTitle = "Clearing data";
                                confirmText = "ALL simple mode data will be erased!";
                                onConfirm = () =>
                                {
                                    var grpNodes = lfg.nodes.Values.ToList();
                                    foreach (var node in grpNodes)
                                    {
                                        if (!(node is LogicFlowInput)) lfg.RemoveNode(node.index);
                                    }

                                    lfg.setSize(defaultGraphSize);
                                    graphData[lfg] = new GraphData(this, lfg);
                                };
                                isConfirming = true;
                            }

                            GUILayout.FlexibleSpace();
                             if (GUILayout.Button("Bone Editor"))
                             {
                                 displayBoneEditor = !displayBoneEditor;
                                 if (displayBoneEditor)
                                 {
                                     BuildBoneCache();
                                     TryPositionBoneEditorWindow();
                                 }
                             }
                            if (GUILayout.Button("Adv. Mode"))
                            {
                                confirmRect =
                                    new Rect(
                                        simpleWindowRect.position + Event.current.mousePosition - new Vector2(150, 30),
                                        new Vector2(300, 60));
                                confirmTitle = "Switching modes";
                                confirmText =
                                    "Edit graph in advanced mode? Simple mode data will be retained and can be returned to.";
                                onConfirm = () =>
                                {
                                    graphData[lfg].advanced = true;
                                    AmazingNewBoneLogic.UpdateMakerButtonVisibility();
                                };
                                isConfirming = true;
                            }

                            if (GUILayout.Button("X")) Hide();
                            GUILayout.Space(-6);
                        }
                        GUILayout.EndHorizontal();

                        // Info row
                        GUILayout.Space(-5);
                        GUILayout.BeginHorizontal();
                        {
                            GUILayout.Label(
                                $" {ChaControl.fileParam.fullname} - Outfit {ChaControl.fileStatus.coordinateType}");
                            GUILayout.FlexibleSpace();
                        }
                        GUILayout.EndHorizontal();
                        GUILayout.Space(5);

                        // Main window area
                        {
                            var wS = simpleWindowRect.size;
                            var size = new Vector2(wS.x / 2f - 12.5f, wS.y - 50f);

                            // Bone Edits list
                            var acsRect = new Rect(new Vector2(10f, 40f), size);
                            GUI.Box(acsRect, "");
                            GUILayout.BeginArea(acsRect, "");
                            simpleWindowScrollPosAcs = GUILayout.BeginScrollView(simpleWindowScrollPosAcs, false, true);
                            {
                                int coordVal = ChaControl.fileStatus.coordinateType;
                                if (!boneEdits.TryGetValue(coordVal, out var editsList) || editsList == null)
                                {
                                    editsList = new List<BoneEffectEdit>();
                                    boneEdits[coordVal] = editsList;
                                }

                                if (editsList.Count == 0)
                                {
                                    GUILayout.Label("No bone edits defined for this outfit!");
                                    if (GUILayout.Button("Open Bone Editor", GUILayout.Height(30)))
                                    {
                                        displayBoneEditor = true;
                                        BuildBoneCache();
                                        TryPositionBoneEditorWindow();
                                    }
                                }
                                else
                                {
                                    int numBtns = 0;
                                    var editsInGroups = data.GetAllChildIndices();
                                    for (int i = 0; i < editsList.Count; i++)
                                    {
                                        var edit = editsList[i];
                                        if (editsInGroups.Contains(edit.GraphKey)) continue;
                                        numBtns++;
                                        var boxSize = new Vector2(acsRect.size.x - 18f, 70);
                                        GUILayout.Space(2);
                                        float yStart = (numBtns - 1) * (boxSize.y + 2f);
                                        // Only draw visible buttons to save on some garbage
                                        if (yStart + boxSize.y > simpleWindowScrollPosAcs.y &&
                                            yStart < simpleWindowScrollPosAcs.y + acsRect.height)
                                        {
                                            GUILayout.Box("", solidSkin.window,
                                                new[]
                                                {
                                                    GUILayout.Width(boxSize.x), GUILayout.Height(boxSize.y)
                                                });
                                            GUILayout.Space(-(boxSize.y + 3));
                                            GUILayout.BeginVertical();
                                            {
                                                GUILayout.BeginHorizontal();
                                                {
                                                    GUILayout.Space(5);
                                                    GUILayout.Label(edit.Name);
                                                    GUILayout.FlexibleSpace();
                                                    GUILayout.Label(edit.BoneName);
                                                    GUILayout.Space(3);
                                                }
                                                GUILayout.EndHorizontal();
                                                var offset = new Vector2(
                                                    15f,
                                                    numBtns * (boxSize.y + 2f) + 35f - simpleWindowScrollPosAcs.y
                                                );
                                                DoBindings(edit.GraphKey + 1000000, offset);
                                            }
                                            GUILayout.EndVertical();
                                        }
                                        else
                                        {
                                            GUILayout.Space(boxSize.y - 2f);
                                        }

                                        GUILayout.Space(2);
                                    }

                                    GUILayout.Space(5);
                                }
                            }
                            GUILayout.EndScrollView();
                            GUILayout.EndArea();

                            // Group list
                            var grpNodes = lfg.nodes.Values
                                .OfType<LogicFlowNode_GRP>().ToList();
                            grpNodes.Sort((x, y) => x.label.CompareTo(y.label));
                            var grpRect = new Rect(new Vector2(wS.x / 2f + 2.5f, 40f), size);
                            GUI.Box(grpRect, "");
                            GUILayout.BeginArea(grpRect, "");
                            simpleWindowScrollPosGrp = GUILayout.BeginScrollView(simpleWindowScrollPosGrp, false, true);
                            {
                                GUILayout.BeginHorizontal();
                                {
                                    GUILayout.FlexibleSpace();
                                    if (GUILayout.Button("Add Group"))
                                    {
                                        addGate(4);
                                    }

                                    GUILayout.Space(-3);
                                }
                                GUILayout.EndHorizontal();
                                int sumChildren = 0;
                                float baseHeight = 60f;
                                float childHeight = 25f;
                                float baseBoxHeight = 117f;
                                for (int i = 0; i < grpNodes.Count; i++)
                                {
                                    LogicFlowNode_GRP node = grpNodes[i];
                                    List<int> grpChildren = null;
                                    bool hasChildren = data.TryGetChildren(node.index, out grpChildren) &&
                                                       grpChildren.Count > 0;
                                    int numChildren = grpChildren != null ? grpChildren.Count + 1 : 1;
                                    sumChildren += numChildren;
                                    var grpBoxSize = new Vector2(grpRect.size.x - 18f,
                                        baseBoxHeight + numChildren * childHeight - 4f);
                                    float yStart = baseHeight + i * baseBoxHeight +
                                                   (sumChildren - numChildren) * childHeight;
                                    if (yStart + grpBoxSize.y > simpleWindowScrollPosGrp.y &&
                                        yStart < simpleWindowScrollPosGrp.y + grpRect.height)
                                    {
                                        GUILayout.Box("", solidSkin.window,
                                            new[] { GUILayout.Width(grpBoxSize.x), GUILayout.Height(grpBoxSize.y) });
                                        GUILayout.Space(-(grpBoxSize.y + 4));
                                        GUILayout.BeginVertical();
                                        {
                                            GUILayout.BeginHorizontal();
                                            {
                                                // Group title
                                                GUILayout.Space(5);
                                                GUILayout.Label(node.getName());
                                                GUILayout.FlexibleSpace();
                                                if (StudioAPI.InsideStudio &&
                                                    TimelineCompatibility.IsTimelineAvailable())
                                                {
                                                    if (GUILayout.Button("Animate"))
                                                    {
                                                        AmazingNewBoneLogic.Logger.LogMessage(
                                                            "Group node selected!");
                                                        TimelineHelper.SelectGroup(node);
                                                    }
                                                }

                                                if (GUILayout.Button("Rename"))
                                                {
                                                    renamedNode = node.index;
                                                    renameRect =
                                                        new Rect(
                                                            Event.current.mousePosition - new Vector2(120, 20) +
                                                            grpRect.position + simpleWindowRect.position -
                                                            simpleWindowScrollPosGrp,
                                                            new Vector2(240, 40));
                                                    renameName = node.getName();
                                                }

                                                if (GUILayout.Button("X"))
                                                {
                                                    data.RemoveGroup(node.index);
                                                    lfg.RemoveNode(node.index);
                                                }

                                                GUILayout.Space(-2);
                                            }
                                            GUILayout.EndHorizontal();
                                            {
                                                // Bindings
                                                var offset = new Vector2(
                                                    simpleWindowRect.width / 2f + 8f,
                                                    (i + 1) * baseBoxHeight +
                                                    (sumChildren - numChildren) * (childHeight) + baseHeight -
                                                    simpleWindowScrollPosGrp.y - 45f
                                                );
                                                DoBindings(node.index, offset);
                                            }
                                            GUILayout.Space(3);
                                            GUILayout.BeginHorizontal();
                                            {
                                                // State selection
                                                GUILayout.Space(5);
                                                GUILayout.BeginVertical();
                                                {
                                                    GUILayout.Space(-6);
                                                    GUILayout.Label($"Group state: {node.state}");
                                                    GUILayout.Space(-2);
                                                    GUILayout.Label($"Min: {node.Min}, Max: {node.Max}");
                                                }
                                                GUILayout.EndVertical();
                                                GUILayout.BeginVertical();
                                                {
                                                    GUILayout.Space(-6);
                                                    GUILayout.Label("Select");
                                                    GUILayout.Space(-2);
                                                    GUILayout.BeginHorizontal();
                                                    {
                                                        if (GUILayout.Button("Prev"))
                                                        {
                                                            node.state--;
                                                        }

                                                        if (GUILayout.Button("Next"))
                                                        {
                                                            node.state++;
                                                        }
                                                    }
                                                    GUILayout.EndHorizontal();
                                                }
                                                GUILayout.EndVertical();
                                                GUILayout.Space(5);
                                            }
                                            GUILayout.EndHorizontal();
                                            {
                                                // Child management
                                                if (hasChildren)
                                                {
                                                    grpChildren.Sort();
                                                    var leftText = new GUIStyle(GUI.skin.label)
                                                    {
                                                        wordWrap = false,
                                                        alignment = TextAnchor.MiddleLeft,
                                                    };
                                                    foreach (var child in grpChildren)
                                                    {
                                                        int coordVal = ChaControl.fileStatus.coordinateType;
                                                        var edit = boneEdits.TryGetValue(coordVal, out var listEdits) ? listEdits.FirstOrDefault(e => e.GraphKey == child) : null;
                                                        string editName = edit != null ? edit.Name : $"Bone Edit {child}";
                                                        GUILayout.BeginHorizontal();
                                                        {
                                                            GUILayout.Space(5);
                                                            GUILayout.Label(
                                                                $"{editName}",
                                                                leftText, GUILayout.MaxWidth(grpBoxSize.x - 75));
                                                            GUILayout.FlexibleSpace();
                                                            bool isOn = node.controlledNodes.TryGetValue(node.state,
                                                                out var stateSet) && stateSet.Contains(child + 1000000);
                                                            if (GUILayout.Button(isOn ? "On" : "Off",
                                                                    isOn ? bindStateStyleOn : bindStateStyleOff))
                                                            {
                                                                if (isOn)
                                                                {
                                                                    node.removeActiveNode(getOutput(child));
                                                                }
                                                                else
                                                                {
                                                                    node.addActiveNode(child);
                                                                }
                                                            }

                                                            if (GUILayout.Button("X"))
                                                            {
                                                                data.RemoveChild(node.index, child);
                                                                // To finish the group
                                                                GUILayout.EndHorizontal();
                                                                break;
                                                            }

                                                            GUILayout.Space(5);
                                                        }
                                                        GUILayout.EndHorizontal();
                                                    }
                                                }

                                                GUILayout.BeginHorizontal();
                                                {
                                                    GUILayout.Space(5);
                                                    if (!hasChildren) GUILayout.Label("No Children!");
                                                    GUILayout.FlexibleSpace();
                                                    if (GUILayout.Button(" + "))
                                                    {
                                                        grpBeingAddedTo = node.index;
                                                        grpAddFilter = "";
                                                        grpAddRect = new Rect(
                                                            simpleWindowRect.x + simpleWindowRect.width - 335f,
                                                            simpleWindowRect.y + baseHeight +
                                                            sumChildren * (childHeight) + baseBoxHeight * (i + 1) -
                                                            simpleWindowScrollPosGrp.y,
                                                            300f,
                                                            240f
                                                        );
                                                    }

                                                    GUILayout.Space(5);
                                                }
                                                GUILayout.EndHorizontal();
                                            }
                                        }
                                        GUILayout.EndVertical();
                                    }
                                    else
                                    {
                                        GUILayout.Space(grpBoxSize.y - 1f);
                                    }

                                    GUILayout.Space(5);
                                }
                            }
                            GUILayout.EndScrollView();
                            GUILayout.EndArea();

                            void DoBindings(int idx, Vector2 dropOffset)
                            {
                                var binding = data.GetNodeBinding(idx);
                                GUILayout.BeginHorizontal();
                                {
                                    GUILayout.Space(5);
                                    GUILayout.BeginVertical();
                                    {
                                        GUILayout.Space(-6);
                                        GUILayout.Label("Bound to:");
                                        GUILayout.Space(-2);
                                        string boundBtnLabel = binding != null ? binding.Value.ToString() : "None";
                                        if (GUILayout.Button(boundBtnLabel))
                                        {
                                            simpleAccBeingBound = idx;
                                            simpleAccBindRect = new Rect(
                                                simpleWindowRect.x + dropOffset.x,
                                                simpleWindowRect.y + dropOffset.y,
                                                120f,
                                                160f
                                            );
                                        }
                                    }
                                    GUILayout.EndVertical();
                                    if (binding != null)
                                    {
                                        GUILayout.BeginVertical();
                                        {
                                            GUILayout.Space(-6);
                                            GUILayout.Label("ON States:");
                                            GUILayout.Space(-2);
                                            GUILayout.BeginHorizontal();
                                            var bindStates = GraphData.bindingStates[binding.Value];
                                            if (binding != BindingType.Shoes)
                                            {
                                                if (GUILayout.Button("On",
                                                        isBound(0) ? bindStateStyleOn : bindStateStyleOff))
                                                {
                                                    toggleBoundState(0);
                                                }

                                                if (bindStates.Count > 1 && GUILayout.Button("Half",
                                                        isBound(1) ? bindStateStyleOn : bindStateStyleOff))
                                                {
                                                    toggleBoundState(1);
                                                }

                                                if (bindStates.Count > 2 && GUILayout.Button("Hang",
                                                        isBound(2) ? bindStateStyleOn : bindStateStyleOff))
                                                {
                                                    toggleBoundState(2);
                                                }

                                                if (GUILayout.Button("Off",
                                                        isBound(3) ? bindStateStyleOn : bindStateStyleOff))
                                                {
                                                    toggleBoundState(3);
                                                }
                                            }
                                            else
                                            {
#if KKS
                                                if (GUILayout.Button("On",
                                                        isBound(0) ? bindStateStyleOn : bindStateStyleOff))
                                                {
                                                    toggleBoundState(0);
                                                }
#else
                                            if (GUILayout.Button("Indoors", isBound(0) ? bindStateStyleOn : bindStateStyleOff)) {
                                                toggleBoundState(0);
                                            }
                                            if (GUILayout.Button("Outdoors", isBound(1) ? bindStateStyleOn : bindStateStyleOff)) {
                                                toggleBoundState(1);
                                            }
#endif
                                                if (GUILayout.Button("Off",
                                                        isBound(3) ? bindStateStyleOn : bindStateStyleOff))
                                                {
                                                    toggleBoundState(3);
                                                }
                                            }

                                            GUILayout.EndHorizontal();

                                            bool isBound(int shift)
                                            {
                                                return data.GetBoundState(idx, shift);
                                            }

                                            void toggleBoundState(int shift)
                                            {
                                                if (isBound(shift))
                                                {
                                                    data.SetBoundState(idx, shift, false);
                                                }
                                                else
                                                {
                                                    data.SetBoundState(idx, shift, true);
                                                }
                                            }
                                        }
                                        GUILayout.EndVertical();
                                    }
                                    else
                                    {
                                        GUILayout.FlexibleSpace();
                                    }

                                    GUILayout.Space(3);
                                }
                                GUILayout.EndHorizontal();
                            }
                        }

                        GUI.Box(new Rect(simpleWindowRect.size - new Vector2(13, 13), new Vector2(13, 13)), "",
                            whiteBox);
                        simpleWindowRect = IMGUIUtils.DragResizeEatWindow(simpleModeWindowID, simpleWindowRect);
                    }, $"ANBL v{AmazingNewBoneLogic.Version} - Simple Mode", solidSkin.window);
                var sWP = simpleWindowRect.position;
                var sWS = simpleWindowRect.size;
                simpleWindowRect.position = new Vector2(
                    Mathf.Clamp(sWP.x, -sWS.x * 0.9f, Screen.width - sWS.x * 0.1f),
                    Mathf.Clamp(sWP.y, -sWS.y * 0.9f, Screen.height - sWS.y * 0.1f)
                );
                simpleWindowRect.size = new Vector2(
                    Mathf.Max(simpleMinWidth, simpleWindowRect.width),
                    Mathf.Max(simpleMinHeight, simpleWindowRect.height)
                );
            }
            // Advanced mode window
            else if (displayGraph && graphData[lfg].advanced)
            {
                var solidSkin = IMGUIUtils.SolidBackgroundGuiSkin;
                Rect guiRect = new Rect(screenToGUI(lfg.positionUI), new Vector2(lfg.sizeUI.x, -lfg.sizeUI.y));

                if (showAdvancedInputWindow)
                {
                    advancedInputWindowRect = GUI.Window(advancedInputWindowID, advancedInputWindowRect,
                        CustomInputWindowFunction, "", KKAPI.Utilities.IMGUIUtils.SolidBackgroundGuiSkin.window);
                }

                GUIStyle headerTextStyle = new GUIStyle(GUI.skin.label);
                headerTextStyle.normal.textColor = Color.black;
                headerTextStyle.alignment = TextAnchor.MiddleLeft;
                if (lfg.getUIScale() < 1f)
                {
                    headerTextStyle.fontSize = (int)(14 * lfg.getUIScale());
                    headerTextStyle.padding.top = 0;
                    headerTextStyle.padding.bottom = 0;
                    headerTextStyle.padding.left = 1;
                }

                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), rTex);

                GUI.Label(
                    new Rect(screenToGUI(lfg.positionUI + new Vector2(10, lfg.sizeUI.y + (lfg.getUIScale() * 20) + 15)),
                        new Vector2(250, 25)), $"AmazingNewBoneLogic v{AmazingNewBoneLogic.Version}",
                    headerTextStyle);
                var closeRect =
                    new Rect(screenToGUI(lfg.positionUI + lfg.sizeUI + new Vector2(-65, (lfg.getUIScale() * 28) + 4)),
                        new Vector2(60, (lfg.getUIScale() * 10) + 10));
                var simpleRect = new Rect(closeRect.position - new Vector2(closeRect.width + 5, 0), closeRect.size);
                if (GUI.Button(simpleRect, "Simple"))
                {
                    confirmRect = new Rect(Event.current.mousePosition - new Vector2(150, 30), new Vector2(300, 60));
                    confirmTitle = "Switching modes";
                    confirmText = "Apply simple mode data? ALL advanced mode edits will be lost!";
                    onConfirm = () =>
                    {
                        graphData[lfg].advanced = false;
                        AmazingNewBoneLogic.UpdateMakerButtonVisibility();
                    };
                    isConfirming = true;
                }

                if (GUI.Button(closeRect, "Close"))
                {
                    Hide();
                }

                normalInputRect.position = screenToGUI(lfg.positionUI + lfg.sizeUI + new Vector2(5, 0));
                normalInputRect = GUILayout.Window(normalInputWindowID, normalInputRect, (windowId) =>
                {
                    GUILayout.BeginVertical();

                    // add nodes buttons
                    if (GUILayout.Button("Add NOT Gate", GUILayout.Height(30))) addGate(0);
                    if (GUILayout.Button("Add AND Gate", GUILayout.Height(30))) addGate(1);
                    if (GUILayout.Button("Add OR Gate", GUILayout.Height(30))) addGate(2);
                    if (GUILayout.Button("Add XOR Gate", GUILayout.Height(30))) addGate(3);
                    if (GUILayout.Button("Add Group Node", GUILayout.Height(30))) addGate(4);
                    GUILayout.Space(8);
                    if (GUILayout.Button("Bone Editor", GUILayout.Height(30)))
                    {
                        displayBoneEditor = !displayBoneEditor;
                        if (displayBoneEditor) BuildBoneCache();
                    }
                    GUILayout.Space(8);
                    if (GUILayout.Button("Advanced Inputs", GUILayout.Height(30)))
                        showAdvancedInputWindow = !showAdvancedInputWindow;
                    GUILayout.Space(8);
#if KKS
                    kkcompatibility = GUILayout.Toggle(kkcompatibility, "KK Compatiblity");
#endif
                    if (GUILayout.Button(fullCharacter ? "◀ All Outfits ▶" : "◀ Current Outfit ▶"))
                        fullCharacter = !fullCharacter;
                    if (GUILayout.Button("Load from ASS"))
                    {
                        if (fullCharacter) TranslateFromAssForCharacter();
                        else if (ExtendedSave.GetExtendedDataById(ChaControl.nowCoordinate, "madevil.kk.ass") == null)
                            TranslateFromAssForCharacter(ChaControl.fileStatus.coordinateType);
                        else TranslateFromAssForCoordinate();
                    }

                    GUILayout.Space(8);
                    if (GUILayout.Button("Show Help")) showHelp = !showHelp;
                    GUILayout.Space(8);

                    #region Studio Widgets

                    if (StudioAPI.InsideStudio)
                    {

                        if (TimelineCompatibility.IsTimelineAvailable())
                        {
                            if (GUILayout.Button("Animate Group"))
                            {
                                if (lfg.selectedNodes.Count != 1)
                                {
                                    AmazingNewBoneLogic.Logger.LogMessage("Select one Group node to animate!");
                                }
                                else
                                {
                                    var selected = lfg.getNodeAt(lfg.selectedNodes[0]);
                                    if (!(selected is LogicFlowNode_GRP))
                                    {
                                        AmazingNewBoneLogic.Logger.LogMessage("Select one Group node to animate!");
                                    }
                                    else
                                    {
                                        AmazingNewBoneLogic.Logger.LogMessage(
                                            "Activated interpolable: ANBL -> Group state");
                                        TimelineHelper.SelectGroup((LogicFlowNode_GRP)selected);
                                    }
                                }
                            }
                        }
                    }

                    #endregion

                    GUILayout.EndVertical();
                }, "", solidSkin.window, GUILayout.ExpandWidth(false));
                IMGUIUtils.EatInputInRect(normalInputRect);

                #region HELP

                if (showHelp)
                {
                    var helpRect = new Rect(screenToGUI(lfg.positionUI + new Vector2(-255, lfg.sizeUI.y)),
                        new Vector2(250, 350));
                    GUI.Box(
                        new Rect(screenToGUI(lfg.positionUI + new Vector2(-255, lfg.sizeUI.y)), new Vector2(250, 350)),
                        $"Help ( {helpPage + 1} / {helpText.Count} )", IMGUIUtils.SolidBackgroundGuiSkin.window);
                    GUILayout.BeginArea(helpRect);
                    GUILayout.BeginHorizontal();
                    {
                        GUILayout.Space(-0.5f);
                        if (GUILayout.Button("<")) {
                            helpPage--;
                            if (helpPage < 0) helpPage = helpText.Count;
                        }
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(">")) {
                            helpPage++;
                            if (helpPage >= helpText.Count) helpPage = 0;
                        }
                        GUILayout.Space(-5);
                    }
                    GUILayout.EndHorizontal();
                    showHelpScroll = GUILayout.BeginScrollView(showHelpScroll);
                    GUILayout.BeginVertical();

                    GUIStyle helpLableStyle = new GUIStyle(GUI.skin.box);
                    helpLableStyle.alignment = TextAnchor.MiddleCenter;
                    helpLableStyle.wordWrap = true;

                    foreach (string line in helpText[helpPage])
                    {
                        GUILayout.Label(line, helpLableStyle);
                    }

                    GUILayout.EndVertical();
                    GUILayout.EndScrollView();
                    GUILayout.EndArea();
                    IMGUIUtils.EatInputInRect(helpRect);
                }

                #endregion

                lfg.ongui();

                #region Advanced Mode Events

                {
                    Event e = Event.current;
                    if (e.isMouse && e.type == EventType.MouseUp && guiRect.Contains(e.mousePosition, true))
                    {
                        foreach (var kvp in getCurrentGraph().nodes)
                        {
                            {
                                // Renaming
                                if (e.button == 1 && kvp.Value.mouseOver)
                                {
                                    AmazingNewBoneLogic.Logger.LogDebug($"Renaming ({kvp.Value.label})!");
                                    renamedNode = kvp.Key;
                                    if (kvp.Value is LogicFlowNode_GRP grp) renameName = grp.getName();
                                    else renameName = kvp.Value.label;
                                    renameRect = new Rect(e.mousePosition - new Vector2(120, 20), new Vector2(240, 40));
                                    break;
                                }
                            }
                            {
                                // Group activation selection
                                if (e.button == 1 && kvp.Value.outputHovered && kvp.Value is LogicFlowNode_GRP grp)
                                {
                                    AmazingNewBoneLogic.Logger.LogDebug(
                                        $"Selecting outputs for ({grp.getName()})...");
                                    groupToSetActives = grp;
                                    groupScrollRect = new Rect(e.mousePosition - new Vector2(-5, 120),
                                        new Vector2(120, 240));
                                    groupConnections = getCurrentGraph().nodes.Values
                                        .Where(x => x.inputs.Any(y => y == grp.index)).ToList();
                                    groupConnections.Sort((x, y) => y.positionUI.y.CompareTo(x.positionUI.y));
                                    break;
                                }
                            }
                            {
                                // New node connection
                                if ((e.button == 0 || e.button == 1) && kvp.Value.inputHovered != null &&
                                    kvp.Value is LogicFlowOutput output)
                                {
                                    AmazingNewBoneLogic.Logger.LogDebug(
                                        $"New connection to ({output.label}), syncing!");
                                    StartCoroutine(updateLater());
                                    break;

                                    IEnumerator updateLater()
                                    {
                                        yield return null;
                                        output.forceUpdate();
                                    }
                                }
                            }
                        }
                    }
                }

                #endregion
            }

            #region Temporary IMGUI Elements

            {
                var solidSkin = IMGUIUtils.SolidBackgroundGuiSkin;
                var mouse = Event.current.mousePosition;
                Vector2 expansion;
                // Node renaming
                expansion = new Vector2(30, 10);
                if (renamedNode != null &&
                    !new Rect(renameRect.position - expansion, renameRect.size + 2 * expansion).Contains(mouse))
                {
                    renamedNode = null;
                }

                if (renamedNode != null)
                {
                    renameRect = GUILayout.Window(renameWindowID, renameRect, (x) =>
                        {
                            GUILayout.BeginVertical();
                            renameName = GUILayout.TextArea(renameName);
                            bool entered = false;
                            if (renameName.Contains("\n"))
                            {
                                renameName = renameName.Replace("\n", "").Trim();
                                entered = true;
                            }

                            if (GUILayout.Button("Rename") || entered)
                            {
                                renameName = renameName.Trim();
                                switch (lfg.nodes[renamedNode.GetValueOrDefault()])
                                {
                                    case LogicFlowNode_GRP grp:
                                        grp.setName(renameName);
                                        break;
                                    case LogicFlowNode other:
                                        other.label = renameName;
                                        break;
                                }

                                renamedNode = null;
                            }

                            GUILayout.EndVertical();
                        }, $"Renaming '{lfg.nodes[renamedNode.GetValueOrDefault()].label}'", solidSkin.window);
                    GUI.BringWindowToFront(renameWindowID);
                    IMGUIUtils.EatInputInRect(renameRect);
                }

                // Group connection selection
                expansion = new Vector2(10, 10);
                if (groupToSetActives != null &&
                    !new Rect(groupScrollRect.position - expansion, groupScrollRect.size + 2 * expansion)
                        .Contains(mouse))
                {
                    groupToSetActives = null;
                }

                if (groupToSetActives != null)
                {
                    groupScrollRect = GUILayout.Window(groupSelectWindowID, groupScrollRect, (x) =>
                        {
                            groupScrollPos = GUILayout.BeginScrollView(groupScrollPos, false, true,
                                solidSkin.horizontalScrollbar, solidSkin.verticalScrollbar, GUILayout.Width(240f));
                            foreach (var node in groupConnections)
                            {
                                bool active = groupToSetActives.getActive(node);
                                if (GUILayout.Button(
                                        (active ? "--- " : "") + node.label + (active ? ": On ---" : ": Off"),
                                        solidSkin.button))
                                {
                                    if (active)
                                    {
                                        groupToSetActives.removeActiveNode(node);
                                    }
                                    else
                                    {
                                        groupToSetActives.addActiveNode(node);
                                    }
                                }
                            }

                            GUILayout.EndScrollView();
                        }, $"Accs for state {groupToSetActives.state}", solidSkin.window);
                    GUI.BringWindowToFront(groupSelectWindowID);
                    IMGUIUtils.EatInputInRect(groupScrollRect);
                }

                // Binding selector
                expansion = new Vector2(10, 30);
                if (simpleAccBeingBound != null &&
                    !new Rect(simpleAccBindRect.position - expansion, simpleAccBindRect.size + 2 * expansion)
                        .Contains(mouse))
                {
                    simpleAccBeingBound = null;
                }

                if (simpleAccBeingBound != null)
                {
                    GUILayout.Window(simpleModeAccBindDropID, simpleAccBindRect, (windowId) =>
                    {
                        // More opaque background
                        var boxRect = new Rect(Vector2.zero, simpleAccBindRect.size);
                        GUI.Box(boxRect, "");
                        GUI.Box(boxRect, "");
                        GUI.Box(boxRect, "");

                        simpleAccBindScrollPos = GUILayout.BeginScrollView(simpleAccBindScrollPos);
                        {
                            if (GUILayout.Button("None"))
                            {
                                graphData[lfg].SetNodeBinding(simpleAccBeingBound.Value, null);
                                graphData[lfg].SetBoundStates(simpleAccBeingBound.Value, 0);
                                simpleAccBeingBound = null;
                            }

                            foreach (var opt in Enum.GetValues(typeof(BindingType)))
                            {
                                if (GUILayout.Button(Enum.GetName(typeof(BindingType), opt)))
                                {
                                    graphData[lfg].SetNodeBinding(simpleAccBeingBound.Value, (BindingType)opt);
                                    graphData[lfg].SetBoundStates(simpleAccBeingBound.Value, 0);
                                    simpleAccBeingBound = null;
                                }
                            }
                        }
                        GUILayout.EndScrollView();
                    }, "", solidSkin.box);
                    GUI.BringWindowToFront(simpleModeAccBindDropID);
                    KKAPI.Utilities.IMGUIUtils.EatInputInRect(simpleAccBindRect);
                }

                // New child selection
                expansion = new Vector2(10, 30);
                if (grpBeingAddedTo != null &&
                    !new Rect(grpAddRect.position - expansion, grpAddRect.size + 2 * expansion).Contains(mouse))
                {
                    grpBeingAddedTo = null;
                }

                if (grpBeingAddedTo != null)
                {
                    GUILayout.Window(simpleModeGroupAddID, grpAddRect, (windowId) =>
                    {
                        // More opaque background
                        var boxRect = new Rect(Vector2.zero, grpAddRect.size);
                        GUI.Box(boxRect, "");
                        GUI.Box(boxRect, "");
                        GUI.Box(boxRect, "");

                        var allChildren = graphData[lfg].GetAllChildIndices();
                        var leftText = new GUIStyle(GUI.skin.button)
                        {
                            wordWrap = false,
                            alignment = TextAnchor.MiddleLeft,
                        };
                        grpAddScrollPos = GUILayout.BeginScrollView(grpAddScrollPos);
                        {
                            GUILayout.BeginHorizontal();
                            {
                                GUILayout.Label("Filter ", GUILayout.ExpandWidth(false));
                                grpAddFilter = GUILayout.TextField(grpAddFilter);
                            }
                            GUILayout.EndHorizontal();
                            int coordVal = ChaControl.fileStatus.coordinateType;
                            if (boneEdits.TryGetValue(coordVal, out var editsList) && editsList != null)
                            {
                                foreach (var edit in editsList)
                                {
                                    if (allChildren.Contains(edit.GraphKey)) continue;
                                    if (grpAddFilter.Length > 0 && edit.Name.IndexOf(grpAddFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    if (GUILayout.Button($"{edit.Name}", leftText))
                                    {
                                        graphData[lfg].AddChild(grpBeingAddedTo.Value, edit.GraphKey);
                                        grpBeingAddedTo = null;
                                        break;
                                    }
                                }
                            }
                        }
                        GUILayout.EndScrollView();
                    }, "", solidSkin.box);
                    GUI.BringWindowToFront(simpleModeGroupAddID);
                    IMGUIUtils.EatInputInRect(grpAddRect);
                }

                // Confirmation window
                expansion = new Vector2(10, 10);
                if (isConfirming &&
                    !new Rect(confirmRect.position - expansion, confirmRect.size + 2 * expansion).Contains(mouse))
                {
                    isConfirming = false;
                }

                if (isConfirming)
                {
                    confirmRect = GUILayout.Window(confirmWindowID, confirmRect, (windowId) =>
                    {
                        GUILayout.Label(confirmText);
                        GUILayout.BeginHorizontal();
                        {
                            if (GUILayout.Button("Cancel"))
                            {
                                isConfirming = false;
                            }

                            if (GUILayout.Button("Confirm", bindStateStyleOff))
                            {
                                onConfirm.Invoke();
                                isConfirming = false;
                            }
                        }
                        GUILayout.EndHorizontal();
                    }, confirmTitle, solidSkin.window, GUILayout.MaxWidth(300f));
                    GUI.BringWindowToFront(confirmWindowID);
                    KKAPI.Utilities.IMGUIUtils.EatInputInRect(confirmRect);
                }
            }

            #endregion

            if (displayBoneEditor)
            {
                var solidSkin = IMGUIUtils.SolidBackgroundGuiSkin;
                boneEditorWindowRect = GUILayout.Window(boneEditorWindowID, boneEditorWindowRect, DrawBoneEditorWindow, "ANBL Bone Editor", solidSkin.window);
                
                // Keep window on screen
                var bEP = boneEditorWindowRect.position;
                var bES = boneEditorWindowRect.size;
                boneEditorWindowRect.position = new Vector2(
                    Mathf.Clamp(bEP.x, -bES.x * 0.9f, Screen.width - bES.x * 0.1f),
                    Mathf.Clamp(bEP.y, -bES.y * 0.9f, Screen.height - bES.y * 0.1f)
                );
                
                KKAPI.Utilities.IMGUIUtils.EatInputInRect(boneEditorWindowRect);

                if (showTransferPopup)
                {
                    transferPopupRect = GUILayout.Window(transferPopupID, transferPopupRect, DrawTransferPopup, "Select Destination Slot", solidSkin.window);
                    GUI.BringWindowToFront(transferPopupID);
                    KKAPI.Utilities.IMGUIUtils.EatInputInRect(transferPopupRect);
                }
            }
        }

        void InitGUI()
        {
            var offCol = new Color(221 / 255f, 60 / 255f, 60 / 255f);
            bindStateStyleOff = new GUIStyle(GUI.skin.button);
            bindStateStyleOff.normal.textColor = offCol;
            bindStateStyleOff.hover.textColor = offCol;
            bindStateStyleOff.active.textColor = offCol;
            bindStateStyleOff.focused.textColor = offCol;
            var onCol = new Color(34 / 255f, 195 / 255f, 34 / 255f);
            bindStateStyleOn = new GUIStyle(GUI.skin.button);
            bindStateStyleOn.normal.textColor = onCol;
            bindStateStyleOn.hover.textColor = onCol;
            bindStateStyleOn.active.textColor = onCol;
            bindStateStyleOn.focused.textColor = onCol;

            whiteBox = new GUIStyle(GUI.skin.box);
            int size = 13;
            int half = Mathf.FloorToInt((size + 1) / 2f);
            Texture2D newBg = new Texture2D(size, size, TextureFormat.RGBA32, true);
            for (int i = 0; i < half; i++)
            {
                for (int j = 0; j < half; j++)
                {
                    var col = Color.clear;
                    if (i + j == 3)
                    {
                        col = Color.white;
                    }
                    else if (i + j > 3)
                    {
                        col = new Color(1, 1, 1, 0.8f);
                    }

                    newBg.SetPixel(i, j, col);
                    newBg.SetPixel(i, size - j - 1, col);
                    newBg.SetPixel(size - i - 1, j, col);
                    newBg.SetPixel(size - i - 1, size - j - 1, col);
                }
            }

            newBg.Apply();
            whiteBox.normal.background = newBg;
            whiteBox.hover.background = newBg;
            whiteBox.active.background = newBg;
            whiteBox.focused.background = newBg;
        }

        #endregion

        #region Advanced Input GUI

        private bool showAdvancedInputWindow = false;
        private Rect advancedInputWindowRect = new Rect(25, 25, 360, 400);

        private Vector2 advanceInputWindowScroll = new Vector2();

        // Hand
        private bool advinpHandSide = false;
        private bool advinpHandAnim = false;

        private string advinpHandPatternText = "1";

        // Eye Open
        private float advinpEyeOpenThreshold = 0.5f;

        private bool advinpEyeOpenThresholdMore = false;

        // Eye Pattern
        private string advinpEyePatternText = "1";

        // Eyebrow pattern
        private string advinpEyebrowPatternText = "1";

        // Mouth Open
        private float advinpMouthOpenThreshold = 0.5f;

        private bool advinpMouthOpenThresholdMore = false;

        // Mouth Pattern
        private string advinpMouthPatternText = "1";

        private void CustomInputWindowFunction(int id)
        {
            GUIStyle buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.padding = new RectOffset(0, 0, 0, 0);
            buttonStyle.alignment = TextAnchor.MiddleCenter;
            GUIStyle buttonStyleGreen = new GUIStyle(GUI.skin.button);
            buttonStyleGreen.normal.textColor = Color.green;
            GUIStyle buttonStyleRed = new GUIStyle(GUI.skin.button);
            buttonStyleRed.normal.textColor = Color.red;
            GUIStyle labelStyle = new GUIStyle(GUI.skin.box);
            labelStyle.alignment = TextAnchor.MiddleCenter;

            if (GUI.Button(new Rect(advancedInputWindowRect.width - 18, 2, 15, 15), "X", buttonStyle))
            {
                showAdvancedInputWindow = false;
            }

            GUILayout.BeginArea(
                new Rect(5, 20, advancedInputWindowRect.width - 10, advancedInputWindowRect.height - 30));
            advanceInputWindowScroll = GUILayout.BeginScrollView(advanceInputWindowScroll);
            GUILayout.BeginVertical();

            #region Always On Input

            GUILayout.Label("Always On", labelStyle);
            if (GUILayout.Button("Add Always On Input", buttonStyleGreen))
            {
                addAdvancedInputAlwaysOn(lfg.getSize() / 2);
            }
            GUILayout.Space(15);

            #endregion

            #region Hand Pattern Input

            GUILayout.Label("Hand Pattern", labelStyle);
            if (GUILayout.Button(advinpHandSide ? "◀ Right ▶" : "◀ Left ▶"))
            {
                advinpHandSide = !advinpHandSide;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(advinpHandAnim ? "◀ Animation ▶" : "◀ Pattern ▶"))
            {
                advinpHandAnim = !advinpHandAnim;
            }

            if (!advinpHandAnim)
            {
                advinpHandPatternText = GUILayout.TextField(advinpHandPatternText, GUILayout.Width(50));
                if (GUILayout.Button("+", GUILayout.Width(25)) && int.TryParse(advinpHandPatternText, out int handPt1))
                    advinpHandPatternText = (handPt1 + 1).ToString();
                if (GUILayout.Button("-", GUILayout.Width(25)) &&
                    int.TryParse(advinpHandPatternText, out int handPt2) &&
                    handPt2 > 1) advinpHandPatternText = (handPt2 - 1).ToString();
                if (GUILayout.Button("?", GUILayout.Width(25)))
                    advinpHandPatternText = (ChaControl.GetShapeHandIndex(advinpHandSide ? 0 : 1, 0) + 1).ToString();
            }

            GUILayout.EndHorizontal();
            bool eval = advinpHandAnim
                ? !ChaControl.GetEnableShapeHand(advinpHandSide ? 0 : 1)
                : ChaControl.GetEnableShapeHand(advinpHandSide ? 0 : 1) &&
                  int.TryParse(advinpHandPatternText, out int p) &&
                  ChaControl.GetShapeHandIndex(advinpHandSide ? 0 : 1, 0) == p - 1;
            if (GUILayout.Button("Add Hand Pattern Input", eval ? buttonStyleGreen : buttonStyleRed))
            {
                int pattern = 1;
                if (advinpHandAnim || (!advinpHandAnim && int.TryParse(advinpHandPatternText, out pattern)))
                    addAdvancedInputHands(advinpHandSide, advinpHandAnim, pattern - 1, lfg.getSize() / 2);
            }

            #endregion

            GUILayout.Space(15);

            #region Eye Open Threshold

            GUILayout.Label("Eye Openess Threshold", labelStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(advinpEyeOpenThresholdMore ? "◀ More Than ▶" : "◀ Less Than ▶"))
            {
                advinpEyeOpenThresholdMore = !advinpEyeOpenThresholdMore;
            }

            if (GUILayout.Button("?", GUILayout.Width(25))) advinpEyeOpenThreshold = ChaControl.GetEyesOpenMax();
            advinpEyeOpenThreshold = GUILayout.HorizontalSlider(advinpEyeOpenThreshold, 0, 1);
            GUILayout.Label(advinpEyeOpenThreshold.ToString("0.00"), GUILayout.Width(30));
            GUILayout.EndHorizontal();
            eval = advinpEyeOpenThresholdMore
                ? ChaControl.GetEyesOpenMax() > advinpEyeOpenThreshold
                : ChaControl.GetEyesOpenMax() <= advinpEyeOpenThreshold;
            if (GUILayout.Button("Add Eye Threshold Input", eval ? buttonStyleGreen : buttonStyleRed))
            {
                addAdvancedInputEyeThreshold(advinpEyeOpenThresholdMore, advinpEyeOpenThreshold, lfg.getSize() / 2);
            }

            #endregion

            GUILayout.Space(15);

            #region Eye Pattern Input

            GUILayout.Label("Eye Pattern", labelStyle);
            GUILayout.BeginHorizontal();
            advinpEyePatternText = GUILayout.TextField(advinpEyePatternText);
            if (GUILayout.Button("+", GUILayout.Width(25)) && int.TryParse(advinpEyePatternText, out int eyePt1))
                advinpEyePatternText = (eyePt1 + 1).ToString();
            if (GUILayout.Button("-", GUILayout.Width(25)) && int.TryParse(advinpEyePatternText, out int eyePt2) &&
                eyePt2 > 1) advinpEyePatternText = (eyePt2 - 1).ToString();
            if (GUILayout.Button("?", GUILayout.Width(25)))
                advinpEyePatternText = (ChaControl.GetEyesPtn() + 1).ToString();
            GUILayout.EndHorizontal();
            eval = int.TryParse(advinpEyePatternText, out p) && ChaControl.GetEyesPtn() == p - 1;
            if (GUILayout.Button("Add Eye Pattern Input", eval ? buttonStyleGreen : buttonStyleRed) &&
                int.TryParse(advinpEyePatternText, out int pt)) addAdvancedInputEyePattern(pt - 1, lfg.getSize() / 2);

            #endregion

            GUILayout.Space(15);

            #region Eyebrow Pattern Input

            GUILayout.Label("Eyebrow Pattern", labelStyle);
            GUILayout.BeginHorizontal();
            advinpEyebrowPatternText = GUILayout.TextField(advinpEyebrowPatternText);
            if (GUILayout.Button("+", GUILayout.Width(25)) && int.TryParse(advinpEyebrowPatternText, out int eyebPt1))
                advinpEyebrowPatternText = (eyebPt1 + 1).ToString();
            if (GUILayout.Button("-", GUILayout.Width(25)) && int.TryParse(advinpEyebrowPatternText, out int eyebPt2) &&
                eyebPt2 > 1) advinpEyebrowPatternText = (eyebPt2 - 1).ToString();
            if (GUILayout.Button("?", GUILayout.Width(25)))
                advinpEyebrowPatternText = (ChaControl.GetEyebrowPtn() + 1).ToString();
            GUILayout.EndHorizontal();
            eval = int.TryParse(advinpEyebrowPatternText, out p) && ChaControl.GetEyebrowPtn() == p - 1;
            if (GUILayout.Button("Add Eyebrow Pattern Input", eval ? buttonStyleGreen : buttonStyleRed) &&
                int.TryParse(advinpEyebrowPatternText, out pt))
                addAdvancedInputEyebrowPattern(pt - 1, lfg.getSize() / 2);

            #endregion

            GUILayout.Space(15);

            #region Mouth Open Threshold

            GUILayout.Label("Mouth Openess Threshold", labelStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(advinpMouthOpenThresholdMore ? "◀ More Than ▶" : "◀ Less Than ▶"))
            {
                advinpMouthOpenThresholdMore = !advinpMouthOpenThresholdMore;
            }

            if (GUILayout.Button("?", GUILayout.Width(25))) advinpMouthOpenThreshold = ChaControl.GetMouthOpenMax();
            advinpMouthOpenThreshold = GUILayout.HorizontalSlider(advinpMouthOpenThreshold, 0, 1);
            GUILayout.Label(advinpMouthOpenThreshold.ToString("0.00"), GUILayout.Width(30));
            GUILayout.EndHorizontal();
            eval = advinpMouthOpenThresholdMore
                ? ChaControl.GetMouthOpenMax() > advinpMouthOpenThreshold
                : ChaControl.GetMouthOpenMax() <= advinpMouthOpenThreshold;
            if (GUILayout.Button("Add Mouth Threshold Input", eval ? buttonStyleGreen : buttonStyleRed))
            {
                addAdvancedInputMouthThreshold(advinpMouthOpenThresholdMore, advinpMouthOpenThreshold,
                    lfg.getSize() / 2);
            }

            #endregion

            GUILayout.Space(15);

            #region Mouth Pattern Input

            GUILayout.Label("Mouth Pattern", labelStyle);
            GUILayout.BeginHorizontal();
            advinpMouthPatternText = GUILayout.TextField(advinpMouthPatternText);
            if (GUILayout.Button("+", GUILayout.Width(25)) && int.TryParse(advinpMouthPatternText, out int mouthPt1))
                advinpMouthPatternText = (mouthPt1 + 1).ToString();
            if (GUILayout.Button("-", GUILayout.Width(25)) && int.TryParse(advinpMouthPatternText, out int mouthPt2) &&
                mouthPt2 > 1) advinpMouthPatternText = (mouthPt2 - 1).ToString();
            if (GUILayout.Button("?", GUILayout.Width(25)))
                advinpMouthPatternText = (ChaControl.GetMouthPtn() + 1).ToString();
            GUILayout.EndHorizontal();
            eval = int.TryParse(advinpMouthPatternText, out p) && ChaControl.GetMouthPtn() == p - 1;
            if (GUILayout.Button("Add Mouth Pattern Input", eval ? buttonStyleGreen : buttonStyleRed) &&
                int.TryParse(advinpMouthPatternText, out pt)) addAdvancedInputMouthPattern(pt - 1, lfg.getSize() / 2);

            #endregion


            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            advancedInputWindowRect = KKAPI.Utilities.IMGUIUtils.DragResizeEatWindow(id, advancedInputWindowRect);
        }

        #endregion

        private Vector2 screenToGUI(Vector2 screenPos)
        {
            return new Vector2(screenPos.x, Screen.height - screenPos.y);
        }
#if KKS
        // KK -> KKS compatibility switcher
        private int OutfitKK2KKS(int slot)
        {
            switch (slot)
            {
                case 0: return 4;
                case 1: return 3;
                case 2: return 5;
                case 3: return 1;
                case 4: return 6;
                case 5: return 0;
                case 6: return 2;
                default: return slot;
            }
        }
#endif
        public static InputKey? GetInputKey(int clothingSlot, int clothingState)
        {
            switch (clothingSlot)
            {
                case 0:
                    if (clothingState == 0) return InputKey.TopOn;
                    if (clothingState == 1) return InputKey.TopShift;
                    return null;
                case 1:
                    if (clothingState == 0) return InputKey.BottomOn;
                    if (clothingState == 1) return InputKey.BottomShift;
                    return null;
                case 2:
                    if (clothingState == 0) return InputKey.BraOn;
                    if (clothingState == 1) return InputKey.BraShift;
                    return null;
                case 3:
                    if (clothingState == 0) return InputKey.UnderwearOn;
                    if (clothingState == 1) return InputKey.UnderwearShift;
                    if (clothingState == 2) return InputKey.UnderwearHang;
                    return null;
                case 4:
                    if (clothingState == 0) return InputKey.GlovesOn;
                    if (clothingState == 1) return InputKey.GlovesShift;
                    if (clothingState == 2) return InputKey.GlovesShift;
                    return null;
                case 5:
                    if (clothingState == 0) return InputKey.PantyhoseOn;
                    if (clothingState == 1) return InputKey.PantyhoseShift;
                    if (clothingState == 2) return InputKey.PantyhoseHang;
                    return null;
                case 6:
                    if (clothingState == 0) return InputKey.LegwearOn;
                    return null;
#if KK
                case 7:
                    if (clothingState == 0) return InputKey.ShoesIndoorOn;
                    return null;
                case 8:
                    if (clothingState == 0) return InputKey.ShoesOutdoorOn;
                    return null;
#else
                case 8:
                    if (clothingState == 0) return InputKey.ShoesOn;
                    return null;
#endif
                default:
                    return null;
            }
        }

        #endregion

        #region ASS Data tranlation

        /// <summary>
        /// check is this trigger is valid (either existing state or off)
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        private bool validTrigger(TriggerProperty t)
        {
            if (t.ClothingState == 3) return true;
            else return GetInputKey(t.ClothingSlot, t.ClothingState).HasValue;
        }

        private void CreateNodesForAssData(Dictionary<int, Dictionary<int, TriggerProperty[]>> data, int outfit)
        {
            AmazingNewBoneLogic.Logger.LogInfo($"Creating Logic for {data.Keys.Count} slots on outfit {outfit}");
            if (!graphs.TryGetValue(outfit, out LogicFlowGraph graph)) graph = createGraph(outfit);
            graphData[graphs[outfit]].advanced = true;

            List<int> doneSlots = new List<int>();

            // Translation
            foreach (int slot in data.Keys)
            {
                // clothing slot -> trigger property
                Dictionary<int, TriggerProperty[]> slotData = data[slot];

                AmazingNewBoneLogic.Logger.LogDebug($"Converting TriggerProperties for accessory slot {slot}");

                LogicFlowNode output = getOutput(slot, outfit);
                if (output == null)
                {
                    output = addOutput(slot, outfit);
                }

                // no priorities, only one clothing slot
                if (slotData.Keys.Count == 1)
                {
                    int clothingSlot = slotData.Keys.First();
                    TriggerProperty[] trigs = slotData[clothingSlot];
                    List<int> statesThatTurnTheOutputOn = new List<int>();
                    foreach (TriggerProperty trig in trigs)
                    {
                        if (trig.Visible && validTrigger(trig)) statesThatTurnTheOutputOn.Add(trig.ClothingState);
                    }

                    AmazingNewBoneLogic.Logger.LogDebug(
                        $"Found 1 clothing slot [{clothingSlot}] for acc {slot}: States that turn the ouput on: {statesThatTurnTheOutputOn}");
                    switch (statesThatTurnTheOutputOn.Count)
                    {
                        case 0:
                            break;
                        case 1:
                        case 2:
                        case 3:
                            output.SetInput(connectInputs(statesThatTurnTheOutputOn, clothingSlot, outfit, graph).index,
                                0);
                            break;
                        case 4:
                            break;
                    }
                }

                if (slotData.Keys.Count == 2)
                {
                    List<TriggerProperty[]> triggerProperties = new List<TriggerProperty[]>();
                    List<int> outfitSlots = new List<int>();
                    foreach (int key in slotData.Keys)
                    {
                        triggerProperties.Add(slotData[key]);
                        outfitSlots.Add(key);
                    }

                    AmazingNewBoneLogic.Logger.LogDebug($"Found 2 clothing slots {outfitSlots} for acc {slot}:");
                    for (int i = 0; i < 4; i++) // for each state in outfitSlot1
                    {
                        TriggerProperty A = triggerProperties[0][i];
                        if (!validTrigger(A)) continue; // if A is a trigger that can never be true
                        TriggerProperty[] outfitSlot2 = triggerProperties[1];

                        List<int> statesThatTurnTheOutputOn = new List<int>();
                        foreach (TriggerProperty B in outfitSlot2)
                        {
                            if (
                                (A.Visible && B.Visible) // both claim visible
                                || (A.Visible &&
                                    (A.Priority >=
                                     B.Priority)) // A claims visible and has a higher or same priority as B
                                || (B.Visible &&
                                    (A.Priority < B.Priority)) // B claims visible and has a higher priority as A
                            )
                            {
                                if (validTrigger(B)) statesThatTurnTheOutputOn.Add(B.ClothingState);
                            }
                        }

                        AmazingNewBoneLogic.Logger.LogDebug(
                            $"-> ClothingSlot A ({outfitSlots[0]}) State {i} claims visible={A.Visible} and priority={A.Priority}; B ({outfitSlots[1]}) claims visible={outfitSlot2.Select(trig => trig.Visible)} and priority={outfitSlot2.Select(trig => trig.Priority)}, therefor sates that turn the output on {statesThatTurnTheOutputOn} ");
                        switch (statesThatTurnTheOutputOn.Count)
                        {
                            case 0:
                                AmazingNewBoneLogic.Logger.LogDebug(
                                    $"--> ClothingSlot A has full priority, and visible is {A.Visible}");
                                if (i == 3 && output is LogicFlowNode_OR)
                                {
                                    graph.getAllNodes().ForEach(node =>
                                    {
                                        if (node.inputAt(0) != null && node.inputAt(0).index == output.index)
                                        {
                                            node.SetInput(output.inputAt(1).index, 0);
                                        }
                                        else if (node.inputAt(1) != null && node.inputAt(1).index == output.index)
                                        {
                                            node.SetInput(output.inputAt(1).index, 1);
                                        }
                                    });
                                    graph.RemoveNode(output.index);
                                }

                                break;
                            case 1:
                            case 2:
                            case 3:
                                AmazingNewBoneLogic.Logger.LogDebug(
                                    $"--> ClothingSlot B has priority on states {statesThatTurnTheOutputOn}");
                                LogicFlowNode connectedInputs = connectInputs(statesThatTurnTheOutputOn, outfitSlots[1],
                                    outfit, graph);
                                LogicFlowNode_AND and = addAndGateForInputs(connectedInputs.index,
                                    getInput(A.ClothingSlot, A.ClothingState, outfit).index, outfit);
                                and.setPosition(connectedInputs.getPosition() + new Vector2(75, 0));
                                if (i != 3)
                                {
                                    LogicFlowNode_OR or = addGate(outfit, 2) as LogicFlowNode_OR;
                                    or.setPosition(and.getPosition() + new Vector2(75, 0));
                                    or.SetInput(and.index, 1);
                                    output.SetInput(or.index, 0);
                                    output = or;
                                }
                                else
                                {
                                    output.SetInput(and.index, 0);
                                }

                                break;
                            case 4:
                                AmazingNewBoneLogic.Logger.LogDebug(
                                    $"--> ClothingSlot A has full priority, and visible is {A.Visible}");
                                if (i != 3)
                                {
                                    LogicFlowNode input = getInput(A.ClothingSlot, A.ClothingState, outfit);
                                    LogicFlowNode_OR or = addGate(outfit, 2) as LogicFlowNode_OR;
                                    or.setPosition(input.getPosition() + new Vector2(75, 0));
                                    or.SetInput(input.index, 1);
                                    output.SetInput(or.index, 0);
                                    output = or;
                                }
                                else
                                {
                                    output.SetInput(getInput(A.ClothingSlot, A.ClothingState, outfit).index, 0);
                                }

                                break;
                        }
                    }
                }

                if (slotData.Keys.Count > 2)
                {
                    AmazingNewBoneLogic.Logger.LogWarning(
                        $"Found {slotData.Keys.Count} clothing slots ({slotData.Keys.ToList()}) connected to slot {slot}. Due to the complexity of the translation a max or 2 clothing slots is supported.");
                }

                doneSlots.Add(slot);
            }
        }

        public void TranslateFromAssForCharacter(int? OutfitSlot = null, ChaFile chaFile = null)
        {
            if (chaFile == null) chaFile = MakerAPI.LastLoadedChaFile ?? ChaFileControl;
            PluginData _pluginData = ExtendedSave.GetExtendedDataById(chaFile, "madevil.kk.ass");
            if (_pluginData == null)
            {
                AmazingNewBoneLogic.Logger.LogInfo($"No ASS Data found on {chaFile.charaFileName}" +
                                                        (OutfitSlot.HasValue ? $" and slot {OutfitSlot.Value}" : ""));
                return;
            }

            List<TriggerProperty> _triggers = new List<TriggerProperty>();
            if (_pluginData.data.TryGetValue("TriggerPropertyList", out var ByteData) && ByteData != null)
            {
                _triggers = MessagePackSerializer.Deserialize<List<TriggerProperty>>((byte[])ByteData);
#if KKS
                //AmazingNewBoneLogic.Logger.LogInfo(MakerAPI.LastLoadedChaFile.GetLastErrorCode());
                //AmazingNewBoneLogic.Logger.LogInfo(ChaFileControl.GetLastErrorCode());
                if (kkcompatibility)
                {
                    _triggers.ForEach(t => t.Coordinate = OutfitKK2KKS(t.Coordinate));
                }
#endif
                if (OutfitSlot.HasValue) _triggers = _triggers.Where(x => x.Coordinate == OutfitSlot.Value).ToList();
            }

            if (_triggers.IsNullOrEmpty())
            {
                AmazingNewBoneLogic.Logger.LogInfo($"No Valid TriggerProperties found on {chaFile.charaFileName}" +
                                                        (OutfitSlot.HasValue ? $" and slot {OutfitSlot.Value}" : ""));
                return;
            }

            AmazingNewBoneLogic.Logger.LogInfo(
                $"Found {_triggers.Count} valid TriggerProperties on {chaFile.charaFileName}" +
                (OutfitSlot.HasValue ? $" and slot {OutfitSlot.Value}" : ""));
            // <Outfit, <AccessorySlot, <ClothingSlot, <ClothingState>>>>
            Dictionary<int, Dictionary<int, Dictionary<int, TriggerProperty[]>>> triggersForSlotForOutfit =
                new Dictionary<int, Dictionary<int, Dictionary<int, TriggerProperty[]>>>();
            foreach (TriggerProperty tp in _triggers)
            {
                if (tp.ClothingSlot >= 9) // There is only clothing slots 0-8, so >=9 indicates a custom group
                {
                    AmazingNewBoneLogic.Logger.LogInfo(
                        $"Coustom group trigger property found for Accessory {tp.Slot}, ClothingSlot {tp.ClothingSlot}; This is not supported (yet)!");
                    continue;
                }

                if (!triggersForSlotForOutfit.ContainsKey(tp.Coordinate))
                    triggersForSlotForOutfit.Add(tp.Coordinate,
                        new Dictionary<int, Dictionary<int, TriggerProperty[]>>());
                if (!triggersForSlotForOutfit[tp.Coordinate].ContainsKey(tp.Slot))
                    triggersForSlotForOutfit[tp.Coordinate].Add(tp.Slot, new Dictionary<int, TriggerProperty[]>());
                if (!triggersForSlotForOutfit[tp.Coordinate][tp.Slot].ContainsKey(tp.ClothingSlot))
                    triggersForSlotForOutfit[tp.Coordinate][tp.Slot].Add(tp.ClothingSlot, new TriggerProperty[4]);
                triggersForSlotForOutfit[tp.Coordinate][tp.Slot][tp.ClothingSlot][tp.ClothingState] = tp;
            }

            foreach (int key in triggersForSlotForOutfit.Keys)
            {
                CreateNodesForAssData(triggersForSlotForOutfit[key], key);
            }
        }

        public void TranslateFromAssForCoordinate(ChaFileCoordinate coordinate = null)
        {
            if (coordinate != null)
            {
                coordinate = ChaControl.nowCoordinate;
            }

            PluginData _pluginData = ExtendedSave.GetExtendedDataById(coordinate, "madevil.kk.ass");
            if (_pluginData == null)
            {
                AmazingNewBoneLogic.Logger.LogInfo($"No ASS Data found on {coordinate.coordinateName}");
                return;
            }

            AmazingNewBoneLogic.Logger.LogInfo("Reading ASS Data");
            List<TriggerProperty> _triggers = new List<TriggerProperty>();
            if (_pluginData.data.TryGetValue("TriggerPropertyList", out var ByteData) && ByteData != null)
            {
                _triggers = MessagePackSerializer.Deserialize<List<TriggerProperty>>((byte[])ByteData);
            }

            if (_triggers.IsNullOrEmpty())
            {
                AmazingNewBoneLogic.Logger.LogInfo($"No TriggerProperties found on {coordinate.coordinateName}");
                return;
            }

            AmazingNewBoneLogic.Logger.LogInfo(
                $"Found {_triggers.Count} valid TriggerProperties on {coordinate.coordinateName}");
            AmazingNewBoneLogic.Logger.LogInfo($"Processing ASS Data: {_triggers.Count} TriggerProperties found");
            // <AccessorySlot, <ClothingSlot, <ClothingState>>>
            Dictionary<int, Dictionary<int, TriggerProperty[]>> triggersForSlot =
                new Dictionary<int, Dictionary<int, TriggerProperty[]>>();
            foreach (TriggerProperty tp in _triggers)
            {
                if (tp.ClothingSlot >= 9) // There is only clothing slots 0-8, so >=9 indicates a custom group
                {
                    AmazingNewBoneLogic.Logger.LogInfo(
                        $"Custom group trigger property found for Accessory {tp.Slot}, ClothingSlot {tp.ClothingSlot}; This is not supported (yet)!");
                    continue;
                }

                if (!triggersForSlot.ContainsKey(tp.Slot))
                    triggersForSlot.Add(tp.Slot, new Dictionary<int, TriggerProperty[]>());
                if (!triggersForSlot[tp.Slot].ContainsKey(tp.ClothingSlot))
                    triggersForSlot[tp.Slot].Add(tp.ClothingSlot, new TriggerProperty[4]);
                triggersForSlot[tp.Slot][tp.ClothingSlot][tp.ClothingState] = tp;
            }

            CreateNodesForAssData(triggersForSlot, ChaControl.fileStatus.coordinateType);
        }

        #endregion

        #region Bone Editor GUI

        // Bone Editor UI state
        internal bool displayBoneEditor = false;
        private Rect boneEditorWindowRect = new Rect(100, 100, 870, 600);
        private bool boneEditorPositioned = false;

        private void TryPositionBoneEditorWindow()
        {
            if (!boneEditorPositioned)
            {
                float rightPosition = simpleWindowRect.x + simpleWindowRect.width;
                if (rightPosition + boneEditorWindowRect.width <= Screen.width)
                {
                    boneEditorWindowRect.x = rightPosition;
                }
                else
                {
                    boneEditorWindowRect.x = Mathf.Max(0f, simpleWindowRect.x - boneEditorWindowRect.width);
                }
                boneEditorWindowRect.y = simpleWindowRect.y;
                boneEditorPositioned = true;
            }
        }
        private Vector2 boneListScrollPos = Vector2.zero;
        private Vector2 boneTreeScrollPos = Vector2.zero;
        private Vector2 modifierScrollPos = Vector2.zero;
        private string boneEffectSearch = "";
        private string boneTreeSearch = "";
        private BoneEffectEdit selectedBoneEdit = null;
        private Transform selectedBoneTransform = null;
        private HashSet<GameObject> openedBones = new HashSet<GameObject>();
        private BoneEffectEdit clipboardBoneEdit = null;
        private bool deleteMode = false;
        private bool transferMode = false;
        private HashSet<string> selectedForTransfer = new HashSet<string>();
        private List<Transform> cachedBones = new List<Transform>();
        
        // Transfer destination coordinate selection popup state
        private bool showTransferPopup = false;
        private Rect transferPopupRect = new Rect(400, 200, 250, 250);

        private void BuildBoneCache()
        {
            cachedBones.Clear();
            if (ChaControl.objBodyBone != null)
                TraverseAndCache(ChaControl.objBodyBone.transform);
            if (ChaControl.objHeadBone != null)
                TraverseAndCache(ChaControl.objHeadBone.transform);
        }

        private void TraverseAndCache(Transform t)
        {
            if (t == null) return;
            cachedBones.Add(t);
            for (int i = 0; i < t.childCount; i++)
            {
                TraverseAndCache(t.GetChild(i));
            }
        }

        private Transform FindBoneTransform(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return null;
            return cachedBones.FirstOrDefault(b => b != null && b.name == boneName);
        }

        private void DrawBoneEditorWindow(int windowId)
        {
            if (Event.current.type == EventType.Layout)
            {
                hoveredBoneTransform = null;
            }

            var solidSkin = IMGUIUtils.SolidBackgroundGuiSkin;
            int coord = ChaControl.fileStatus.coordinateType;
            if (!boneEdits.TryGetValue(coord, out var list) || list == null)
            {
                list = new List<BoneEffectEdit>();
                boneEdits[coord] = list;
            }

            GUILayout.BeginHorizontal();

            // Column 1: BoneEffect List
            GUILayout.BeginVertical(GUILayout.Width(250));
            {
                 GUILayout.BeginHorizontal();
                 GUILayout.Label("Bone Edits", GUI.skin.box, GUILayout.ExpandWidth(true));
                 if (GUILayout.Button("Clear", GUILayout.Width(50)))
                 {
                     confirmRect = new Rect(
                         boneEditorWindowRect.position + Event.current.mousePosition - new Vector2(150, 30),
                         new Vector2(300, 60));
                     confirmTitle = "Clearing Bone Edits";
                     confirmText = "ALL bone effect definitions will be erased!";
                     onConfirm = () =>
                     {
                         foreach (var edit in list)
                         {
                             lfg.RemoveNode(1000000 + edit.GraphKey);
                             activeBoneEditIds.Remove(edit.Id);
                         }
                         list.Clear();
                         selectedBoneEdit = null;
                         var boneController = ChaControl?.GetComponent<KKABMX.Core.BoneController>();
                         if (boneController != null) boneController.NeedsFullRefresh = true;
                     };
                     isConfirming = true;
                 }
                 GUILayout.EndHorizontal();
                
                // Search bar
                GUILayout.BeginHorizontal();
                GUILayout.Label("Filter:", GUILayout.Width(45));
                boneEffectSearch = GUILayout.TextField(boneEffectSearch);
                GUILayout.EndHorizontal();

                // Delete/Transfer mode prompt/buttons
                if (transferMode)
                {
                    GUILayout.Label("TRANSFER MODE: Select edits", GUI.skin.box);
                }

                boneListScrollPos = GUILayout.BeginScrollView(boneListScrollPos, GUILayout.ExpandHeight(true));
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var edit = list[i];
                        
                        // Apply filter
                        if (!string.IsNullOrEmpty(boneEffectSearch) && 
                            edit.Name.IndexOf(boneEffectSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                            edit.BoneName.IndexOf(boneEffectSearch, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        GUILayout.BeginHorizontal();
                        
                        if (transferMode)
                        {
                            bool isSelected = selectedForTransfer.Contains(edit.Id);
                            if (GUILayout.Toggle(isSelected, "", GUILayout.Width(20)))
                            {
                                selectedForTransfer.Add(edit.Id);
                            }
                            else
                            {
                                selectedForTransfer.Remove(edit.Id);
                            }
                        }

                        GUI.color = (selectedBoneEdit == edit) ? Color.cyan : Color.white;
                        if (GUILayout.Button($"{edit.Name}\n<size=10>({edit.BoneName})</size>", new GUIStyle(GUI.skin.button) { richText = true, alignment = TextAnchor.MiddleLeft }))
                        {
                            selectedBoneEdit = edit;
                            selectedBoneTransform = FindBoneTransform(edit.BoneName);
                        }
                        GUI.color = Color.white;

                        // Quick X button (only when deleteMode is true)
                        if (deleteMode)
                        {
                            if (GUILayout.Button("X", GUILayout.Width(25), GUILayout.Height(30)))
                            {
                                list.RemoveAt(i);
                                activeBoneEditIds.Remove(edit.Id);
                                lfg.RemoveNode(1000000 + edit.GraphKey);
                                if (selectedBoneEdit == edit) selectedBoneEdit = null;
                                i--;
                                var boneController = ChaControl?.GetComponent<KKABMX.Core.BoneController>();
                                if (boneController != null) boneController.NeedsFullRefresh = true;
                                GUILayout.EndHorizontal();
                                continue;
                            }
                        }

                        GUILayout.EndHorizontal();
                    }
                }
                GUILayout.EndScrollView();

                // Column 1 Bottom Buttons
                GUILayout.Space(5);
                if (transferMode)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Cancel"))
                    {
                        transferMode = false;
                        selectedForTransfer.Clear();
                    }
                    if (GUILayout.Button("Transfer"))
                    {
                        showTransferPopup = true;
                    }
                    GUILayout.EndHorizontal();
                }
                else
                {
                    // Regular buttons: Export, Import, Delete, Transfer
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Export")) ExportEdits(list);
                    if (GUILayout.Button("Import")) ImportEdits(list);
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    
                    GUI.color = deleteMode ? Color.red : Color.white;
                    if (GUILayout.Button(deleteMode ? "Done" : "Delete"))
                    {
                        deleteMode = !deleteMode;
                    }
                    GUI.color = Color.white;

                    if (GUILayout.Button("Transfer"))
                    {
                        transferMode = true;
                        selectedForTransfer.Clear();
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndVertical();

            GUILayout.Box("", GUILayout.Width(2), GUILayout.ExpandHeight(true)); // separator

            // Column 2: Collapsible Bone Tree
            GUILayout.BeginVertical(GUILayout.Width(308));
            {
                GUILayout.Label("Bone Hierarchy", GUI.skin.box);
                
                // Search bar
                GUILayout.BeginHorizontal();
                GUILayout.Label("Search:", GUILayout.Width(50));
                boneTreeSearch = GUILayout.TextField(boneTreeSearch);
                GUILayout.EndHorizontal();

                boneTreeScrollPos = GUILayout.BeginScrollView(boneTreeScrollPos, GUILayout.ExpandHeight(true));
                {
                    if (!string.IsNullOrEmpty(boneTreeSearch))
                    {
                        var matchingBones = cachedBones
                            .Where(b => b != null && b.name.IndexOf(boneTreeSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                            .Take(100);
                        
                        foreach (var t in matchingBones)
                        {
                            RenderBoneTree(t.gameObject, 0);
                        }
                    }
                    else
                    {
                        if (ChaControl.objBodyBone != null)
                            RenderBoneTree(ChaControl.objBodyBone, 0);
                        if (ChaControl.objHeadBone != null)
                            RenderBoneTree(ChaControl.objHeadBone, 0);
                    }
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndVertical();

            GUILayout.Box("", GUILayout.Width(2), GUILayout.ExpandHeight(true)); // separator

            // Column 3: Modifier Sliders
            GUILayout.BeginVertical(GUILayout.Width(312));
            {
                GUILayout.Label("Modifications", GUI.skin.box);
                modifierScrollPos = GUILayout.BeginScrollView(modifierScrollPos, GUILayout.ExpandHeight(true));
                {
                    if (selectedBoneEdit == null)
                    {
                        GUILayout.FlexibleSpace();
                        GUILayout.Label("Select a bone edit from the left list to modify its parameters.", new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter });
                        if (selectedBoneTransform != null)
                        {
                            GUILayout.Space(20);
                            if (GUILayout.Button($"Create new bone edit for\n{selectedBoneTransform.name}", GUILayout.Height(55)))
                            {
                                int nextKey = (list.Count > 0) ? list.Max(e => e.GraphKey) + 1 : 0;
                                string baseName = selectedBoneTransform.name;
                                string proposedName = baseName;
                                int suffix = 2;
                                while (list.Any(e => e.Name.Equals(proposedName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    proposedName = $"{baseName} {suffix}";
                                    suffix++;
                                }

                                var newEdit = new BoneEffectEdit(selectedBoneTransform.name)
                                {
                                    Name = proposedName,
                                    GraphKey = nextKey
                                };
                                list.Add(newEdit);
                                selectedBoneEdit = newEdit;
                                addOutput(newEdit.GraphKey, coord, newEdit.Name);
                                var boneController = ChaControl?.GetComponent<KKABMX.Core.BoneController>();
                                if (boneController != null) boneController.NeedsFullRefresh = true;
                            }
                        }
                        GUILayout.FlexibleSpace();
                    }
                    else
                    {
                        GUILayout.Label("Edit Details", GUI.skin.label);
                        GUILayout.BeginHorizontal();
                        GUILayout.Label("Name:", GUILayout.Width(50));
                        selectedBoneEdit.Name = GUILayout.TextField(selectedBoneEdit.Name);
                        GUILayout.EndHorizontal();

                        GUILayout.Label($"Target Bone: {selectedBoneEdit.BoneName}", GUI.skin.box);

                        GUILayout.Space(10);
                        GUILayout.Label("Modifiers", GUI.skin.label);

                        var mod = selectedBoneEdit.Modifier;
                        mod.ScaleModifier = DrawVector3Sliders("Scale", mod.ScaleModifier, 0.0f, 5.0f, 1.0f, ref stepSizeScale, ref stepSizeScaleStr, ref linkScale);
                        mod.LengthModifier = DrawSingleSlider("Length", mod.LengthModifier, 0.0f, 5.0f, 1.0f, ref stepSizeLength, ref stepSizeLengthStr);
                        mod.PositionModifier = DrawVector3Sliders("Position", mod.PositionModifier, -10.0f, 10.0f, 0.0f, ref stepSizePosition, ref stepSizePositionStr, ref linkPosition);
                        mod.RotationModifier = DrawVector3Sliders("Rotation", mod.RotationModifier, -360.0f, 360.0f, 0.0f, ref stepSizeRotation, ref stepSizeRotationStr, ref linkRotation);

                        GUILayout.Space(15);
                        GUILayout.BeginHorizontal();
                        if (GUILayout.Button("Copy Modifiers"))
                        {
                            clipboardBoneEdit = selectedBoneEdit.Clone();
                        }
                        if (GUILayout.Button("Paste Modifiers") && clipboardBoneEdit != null)
                        {
                            clipboardBoneEdit.Modifier.CopyTo(selectedBoneEdit.Modifier);
                        }
                        GUILayout.EndHorizontal();

                    }
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            if (Event.current.type == EventType.Repaint && hoveredBoneTransform != lastHoveredBoneTransform)
            {
                UpdateHighlight(hoveredBoneTransform);
                lastHoveredBoneTransform = hoveredBoneTransform;
            }

            UnityEngine.GUI.DragWindow();
        }

        private void UpdateHighlight(Transform bone)
        {
            if (!_highlightBonesMethodSearched)
            {
                _highlightBonesMethodSearched = true;
                var type = Type.GetType("SliderHighlight.SliderHighlightPlugin, SliderHighlight")
                        ?? Type.GetType("SliderHighlight.SliderHighlightPlugin, KKS_SliderHighlight")
                        ?? Type.GetType("SliderHighlight.SliderHighlightPlugin, KK_SliderHighlight");
                if (type != null)
                {
                    _highlightBonesMethod = AccessTools.Method(type, "HighlightBones");
                }
            }

            if (_highlightBonesMethod != null)
            {
                StopAllCoroutines();
                StartCoroutine(KKAPI.Utilities.CoroutineUtils.CreateCoroutine(new WaitForFixedUpdate(), () =>
                {
                    var hoveredBones = bone != null ? bone.GetComponentsInChildren<Transform>() : null;
                    _highlightBonesMethod.Invoke(null, new object[] { hoveredBones });
                }));
            }
        }

        private float stepSizeScale = 0.1f;
        private string stepSizeScaleStr = "0.1";
        private bool linkScale = false;
        private float stepSizeLength = 0.01f;
        private string stepSizeLengthStr = "0.01";
        private float stepSizePosition = 0.01f;
        private string stepSizePositionStr = "0.01";
        private bool linkPosition = false;
        private float stepSizeRotation = 1.0f;
        private string stepSizeRotationStr = "1";
        private bool linkRotation = false;

        private Vector3 DrawVector3Sliders(string label, Vector3 value, float min, float max, float def, ref float stepSize, ref string stepSizeStr, ref bool linkMode)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(55));
            
            GUILayout.Label("Step", GUILayout.Width(30));
            stepSizeStr = GUILayout.TextField(stepSizeStr, GUILayout.Width(40));
            if (float.TryParse(stepSizeStr, out float parsedStep)) stepSize = parsedStep;
            
            if (GUILayout.Button("-", GUILayout.Width(18))) 
            {
                if (decimal.TryParse(stepSizeStr, out decimal decStep) && decStep > 0m)
                {
                    decStep /= 10m;
                    stepSize = (float)decStep;
                    stepSizeStr = decStep.ToString("0.############################");
                }
                else
                {
                    stepSize /= 10f;
                    stepSizeStr = stepSize.ToString("0.######");
                }
            }
            if (GUILayout.Button("+", GUILayout.Width(18)))
            {
                if (decimal.TryParse(stepSizeStr, out decimal decStep))
                {
                    decStep *= 10m;
                    stepSize = (float)decStep;
                    stepSizeStr = decStep.ToString("0.############################");
                }
                else
                {
                    stepSize *= 10f;
                    stepSizeStr = stepSize.ToString("0.######");
                }
            }
            
            GUILayout.FlexibleSpace();
            
            GUI.color = linkMode ? Color.yellow : Color.white;
            if (GUILayout.Button("Link", GUILayout.Width(38)))
            {
                linkMode = !linkMode;
            }
            GUI.color = Color.white;
            
            if (GUILayout.Button("Reset", GUILayout.Width(45)))
            {
                value = new Vector3(def, def, def);
            }
            GUILayout.EndHorizontal();

            float oldX = value.x;
            float oldY = value.y;
            float oldZ = value.z;

            value.x = DrawSliderRow("  X", value.x, min, max, stepSize, def, linkMode);
            value.y = DrawSliderRow("  Y", value.y, min, max, stepSize, def, linkMode);
            value.z = DrawSliderRow("  Z", value.z, min, max, stepSize, def, linkMode);

            if (linkMode)
            {
                if (value.x != oldX) value = new Vector3(value.x, value.x, value.x);
                else if (value.y != oldY) value = new Vector3(value.y, value.y, value.y);
                else if (value.z != oldZ) value = new Vector3(value.z, value.z, value.z);
            }

            GUILayout.EndVertical();
            return value;
        }

        private float DrawSingleSlider(string label, float value, float min, float max, float def, ref float stepSize, ref string stepSizeStr)
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(55));
            
            GUILayout.Label("Step", GUILayout.Width(30));
            stepSizeStr = GUILayout.TextField(stepSizeStr, GUILayout.Width(40));
            if (float.TryParse(stepSizeStr, out float parsedStep)) stepSize = parsedStep;
            
            if (GUILayout.Button("-", GUILayout.Width(18))) 
            {
                if (decimal.TryParse(stepSizeStr, out decimal decStep) && decStep > 0m)
                {
                    decStep /= 10m;
                    stepSize = (float)decStep;
                    stepSizeStr = decStep.ToString("0.############################");
                }
                else
                {
                    stepSize /= 10f;
                    stepSizeStr = stepSize.ToString("0.######");
                }
            }
            if (GUILayout.Button("+", GUILayout.Width(18)))
            {
                if (decimal.TryParse(stepSizeStr, out decimal decStep))
                {
                    decStep *= 10m;
                    stepSize = (float)decStep;
                    stepSizeStr = decStep.ToString("0.############################");
                }
                else
                {
                    stepSize *= 10f;
                    stepSizeStr = stepSize.ToString("0.######");
                }
            }
            
            GUILayout.FlexibleSpace();
            
            if (GUILayout.Button("Reset", GUILayout.Width(45)))
            {
                value = def;
            }
            GUILayout.EndHorizontal();

            value = DrawSliderRow("", value, min, max, stepSize, def, false);

            GUILayout.EndVertical();
            return value;
        }

        private float DrawSliderRow(string subLabel, float value, float min, float max, float stepSize, float def, bool isLinked)
        {
            GUILayout.BeginHorizontal();
            
            if (isLinked) GUI.contentColor = Color.yellow;
            if (!string.IsNullOrEmpty(subLabel)) GUILayout.Label(subLabel, GUILayout.Width(20));
            if (isLinked) GUI.contentColor = Color.white;
            
            value = GUILayout.HorizontalSlider(value, min, max, GUILayout.MinWidth(40), GUILayout.MaxWidth(200));
            
            string valStr = GUILayout.TextField(value.ToString("F3"), GUILayout.Width(45));
            if (float.TryParse(valStr, out float val))
            {
                value = val;
            }
            
            if (GUILayout.Button("-", GUILayout.Width(18))) value -= stepSize;
            if (GUILayout.Button("+", GUILayout.Width(18))) value += stepSize;
            
            if (GUILayout.Button(def.ToString("G"), GUILayout.Width(22))) value = def;
            
            GUILayout.EndHorizontal();
            return value;
        }

        private void RenderBoneTree(GameObject go, int indent)
        {
            if (go == null) return;
            
            GUILayout.BeginHorizontal();
            GUILayout.Space(indent * 15f);
            
            bool hasChildren = go.transform.childCount > 0;
            bool isExpanded = openedBones.Contains(go);
            
            if (hasChildren)
            {
                if (GUILayout.Button(isExpanded ? "▼" : "▶", GUI.skin.label, GUILayout.Width(15)))
                {
                    if (isExpanded) openedBones.Remove(go);
                    else openedBones.Add(go);
                }
            }
            else
            {
                GUILayout.Space(19);
            }
            
            GUI.color = (selectedBoneTransform == go.transform) ? Color.cyan : Color.white;
            if (GUILayout.Button(go.name, GUI.skin.label))
            {
                selectedBoneTransform = go.transform;
                selectedBoneEdit = null;
            }
            if (Event.current.type == EventType.Repaint && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
            {
                hoveredBoneTransform = go.transform;
            }
            GUI.color = Color.white;
            
            GUILayout.EndHorizontal();
            
            if (isExpanded && hasChildren)
            {
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    RenderBoneTree(go.transform.GetChild(i).gameObject, indent + 1);
                }
            }
        }

        private bool copyBoneLogicOnTransfer = true;

        private void DrawTransferPopup(int windowId)
        {
            GUILayout.Label("Select destination coordinate slot:");
            
            for (int i = 0; i < 7; i++) // typically 0 to 6 slots
            {
                if (i == ChaControl.fileStatus.coordinateType) continue; // skip current
                
                if (GUILayout.Button($"Outfit {i + 1}"))
                {
                    TransferSelectedEditsTo(i);
                    showTransferPopup = false;
                    transferMode = false;
                    selectedForTransfer.Clear();
                }
            }
            
            GUILayout.Space(10);
            copyBoneLogicOnTransfer = GUILayout.Toggle(copyBoneLogicOnTransfer, "Copy Bone Logic");
            
            GUILayout.Space(10);
            if (GUILayout.Button("Cancel"))
            {
                showTransferPopup = false;
            }
            
            UnityEngine.GUI.DragWindow();
        }

        private void TransferSelectedEditsTo(int destOutfit)
        {
            int coord = ChaControl.fileStatus.coordinateType;
            if (!boneEdits.TryGetValue(coord, out var currentEdits) || currentEdits == null) return;
            
            if (!boneEdits.TryGetValue(destOutfit, out var destEdits) || destEdits == null)
            {
                destEdits = new List<BoneEffectEdit>();
                boneEdits[destOutfit] = destEdits;
            }
            
            int nextKey = (destEdits.Count > 0) ? destEdits.Max(e => e.GraphKey) + 1 : 0;
            
            LogicFlows.LogicFlowGraph srcGraph = null;
            LogicFlows.LogicFlowGraph dstGraph = null;
            GraphData srcData = null;
            GraphData dstData = null;

            if (copyBoneLogicOnTransfer)
            {
                if (!graphs.ContainsKey(coord)) createGraph(coord);
                if (!graphs.ContainsKey(destOutfit)) createGraph(destOutfit);
                
                srcGraph = graphs[coord];
                dstGraph = graphs[destOutfit];
                srcData = graphData[srcGraph];
                dstData = graphData[dstGraph];
                
                dstGraph.isLoading = true;
            }

            int count = 0;
            foreach (var id in selectedForTransfer)
            {
                var edit = currentEdits.FirstOrDefault(e => e.Id == id);
                if (edit != null)
                {
                    var clone = edit.Clone();
                    int srcKey = clone.GraphKey;
                    int dstKey = nextKey++;
                    clone.GraphKey = dstKey;
                    destEdits.Add(clone);
                    count++;

                    if (copyBoneLogicOnTransfer && srcGraph != null && dstGraph != null)
                    {
                        int srcIdxSlot = srcKey + 1000000;
                        int dstIdxSlot = dstKey + 1000000;
                        
                        if (dstGraph.getNodeAt(dstIdxSlot) != null)
                        {
                            dstData.PurgeNode(dstGraph.getNodeAt(dstIdxSlot));
                            dstGraph.RemoveNode(dstIdxSlot);
                        }

                        LogicFlows.LogicFlowOutput sOutput = srcGraph.getNodeAt(srcIdxSlot) as LogicFlows.LogicFlowOutput;
                        if (sOutput != null)
                        {
                            var sNode = SerialisedNode.Serialise(sOutput, srcGraph);
                            sNode.index = dstIdxSlot;
                            deserialiseNode(saveVersion, destOutfit, sNode);

                            List<int> iTree = sOutput.getInputTree();
                            foreach (int index in iTree)
                            {
                                LogicFlows.LogicFlowNode node = dstGraph.getNodeAt(index);
                                if (node == null)
                                {
                                    var srcNode = srcGraph.getNodeAt(index);
                                    deserialiseNode(saveVersion, destOutfit, SerialisedNode.Serialise(srcNode, srcGraph));
                                    
                                    if (srcNode is LogicFlowNode_GRP)
                                    {
                                        var dstNode = (LogicFlowNode_GRP)dstGraph.getNodeAt(index);
                                        var toRemove = new List<int>();
                                        foreach (var set in dstNode.controlledNodes)
                                        {
                                            foreach (var idx in set.Value)
                                            {
                                                if (dstGraph.getNodeAt(idx) == null)
                                                {
                                                    toRemove.Add(idx);
                                                }
                                            }
                                            foreach (var idx in toRemove)
                                            {
                                                dstNode.controlledNodes[set.Key].Remove(idx);
                                            }
                                            toRemove.Clear();
                                            dstNode.setName(dstNode.getName());
                                        }
                                    }
                                }
                            }
                            GraphData.CopyAccData(srcKey, srcData, dstKey, dstData);
                        }
                    }
                }
            }

            if (copyBoneLogicOnTransfer && dstGraph != null)
            {
                dstData.MakeGraph();
                dstGraph.isLoading = false;
            }
            
            AmazingNewBoneLogic.Logger.LogMessage($"Transferred {count} bone edits to Outfit {destOutfit + 1}");
        }

        private void ExportEdits(List<BoneEffectEdit> edits)
        {
            try
            {
                var dialog = new System.Windows.Forms.SaveFileDialog
                {
                    Filter = "ANBL Bone Edits (*.json)|*.json",
                    Title = "Export Bone Edits",
                    FileName = "bone_edits.json"
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var bytes = MessagePackSerializer.Serialize(edits);
                    var base64 = Convert.ToBase64String(bytes);
                    System.IO.File.WriteAllText(dialog.FileName, base64, System.Text.Encoding.UTF8);
                    AmazingNewBoneLogic.Logger.LogMessage($"Exported {edits.Count} edits to {dialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                AmazingNewBoneLogic.Logger.LogError($"Failed to export edits: {ex}");
            }
        }

        private void ImportEdits(List<BoneEffectEdit> currentList)
        {
            try
            {
                var dialog = new System.Windows.Forms.OpenFileDialog
                {
                    Filter = "ANBL Bone Edits (*.json)|*.json",
                    Title = "Import Bone Edits"
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var base64 = System.IO.File.ReadAllText(dialog.FileName);
                    var bytes = Convert.FromBase64String(base64);
                    var imported = MessagePackSerializer.Deserialize<List<BoneEffectEdit>>(bytes);

                    if (imported != null)
                    {
                        int nextKey = (currentList.Count > 0) ? currentList.Max(e => e.GraphKey) + 1 : 0;
                        foreach (var edit in imported)
                        {
                            edit.Id = Guid.NewGuid().ToString(); // Assign new unique ID to avoid clashes
                            edit.GraphKey = nextKey++;
                            currentList.Add(edit);
                        }
                        AmazingNewBoneLogic.Logger.LogMessage($"Imported {imported.Count} edits from {dialog.FileName}");
                        var boneController = ChaControl?.GetComponent<KKABMX.Core.BoneController>();
                        if (boneController != null) boneController.NeedsFullRefresh = true;
                    }
                }
            }
            catch (Exception ex)
            {
                AmazingNewBoneLogic.Logger.LogError($"Failed to import edits: {ex}");
            }
        }

        private void SyncNamesAndDeletedNodes()
        {
            if (lfg == null) return;
            int coord = ChaControl.fileStatus.coordinateType;
            if (!boneEdits.TryGetValue(coord, out var edits) || edits == null) return;

            // 1. Sync names from logic graph nodes to bone edits
            foreach (var node in lfg.getAllNodes().OfType<LogicFlowOutput>())
            {
                int graphKey = node.index - 1000000;
                var edit = edits.FirstOrDefault(e => e.GraphKey == graphKey);
                if (edit != null)
                {
                    if (edit.Name != node.label)
                    {
                        edit.Name = node.label;
                    }
                }
            }

            // 2. We no longer auto-delete bone edits here to prevent immediate deletion upon creation.
            // Users must delete bone edits manually using the Bone Editor UI.
        }

        #endregion
    }

    public enum InputKey
    {
        TopOn = 1001,
        TopShift = 1002,
        BottomOn = 1003,
        BottomShift = 1004,
        BraOn = 1005,
        BraShift = 1006,
        UnderwearOn = 1007,
        UnderwearShift = 1008,
        UnderwearHang = 1009,
        GlovesOn = 1010,
        GlovesShift = 1011,
        GlovesHang = 1012,
        PantyhoseOn = 1013,
        PantyhoseShift = 1014,
        PantyhoseHang = 1015,
        LegwearOn = 1016,
#if KKS
        ShoesOn = 1018,
#else
        ShoesIndoorOn = 1017,
        ShoesOutdoorOn = 1018
#endif
    }

    public enum AdvancedInputType
    {
        Trigger, // not implemented (yet)
        Accessory,
        HandPtn,
        EyesPtn,
        EyesOpn,
        MouthPtn,
        MouthOpn,
        EyebrowPtn,
        AlwaysOn
    }
}
