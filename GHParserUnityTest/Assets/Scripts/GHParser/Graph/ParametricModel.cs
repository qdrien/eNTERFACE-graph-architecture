﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using GHParser.GHElements;
using GHParser.Utils;
using GH_IO.Serialization;
using QuickGraph;
using QuickGraph.Algorithms;
using UnityEngine;
using Component = GHParser.GHElements.Component;
using Debug = UnityEngine.Debug;
using Rhino;

namespace GHParser.Graph
{
    public class ParametricModel
    {
        private DateTime _creationDate;

        private Guid _documentId;
        public string GHTemplateFile = "/GH files/base.ghx";
        private string _componentTemplateFile = "";

        public List<IoComponentTemplate> ComponentTemplates { get; private set; }

        //QUESTION: do we need those getter/setter? can't we just have the behaviour happen within this class?
        public BidirectionalGraph<Vertex, Edge> Graph { get; set; }

        public List<Group> Groups { get; set; }
        //public List<Vertex> MeshPorts { get; set; }

        public void SaveToGrasshopper(string relativePath)
        {
            //NOTE: Currently ignoring potential Exceptions (file not found, NPEs)

            //Create base archive and set "metadata"
            GH_Archive archive = new GH_Archive();
            archive.ReadFromFile(Application.streamingAssetsPath + GHTemplateFile);
            GH_Chunk root = archive.GetRootNode;
            GH_Chunk definition = root.FindChunk("Definition") as GH_Chunk;
            GH_Chunk header = definition.FindChunk("DocumentHeader") as GH_Chunk;
            header.RemoveItem("DocumentID");
            if (_documentId == Guid.Empty)
            {
                _documentId = Guid.NewGuid();
            }

            header.SetGuid("DocumentID", _documentId);
            GH_Chunk properties = definition.FindChunk("DefinitionProperties") as GH_Chunk;
            properties.RemoveItem("Date");
            properties.SetDate("Date", DateTime.UtcNow);

            if (relativePath[0] != '/')
            {
                relativePath = "/" + relativePath;
            }

            if (!relativePath.EndsWith(".gh") && !relativePath.EndsWith(".ghx"))
            {
                relativePath += ".ghx";
            }

            string[] levels = relativePath.Split('/');
            properties.RemoveItem("Name");
            properties.SetString("Name", levels[levels.Length - 1]);

            //Add groups first
            GH_Chunk definitionObjects = definition.FindChunk("DefinitionObjects") as GH_Chunk;

            int currentObjectIndex;
            for (currentObjectIndex = 0; currentObjectIndex < Groups.Count; currentObjectIndex++)
                //AddGHGroup(definitionObjects, currentObjectIndex, _groups[currentObjectIndex]);
            {
                Groups[currentObjectIndex].Add(definitionObjects, currentObjectIndex, null, null);
            }
            //we currently ignore the return value since we already are incrementing the index

            //Verify that the components Graph is acyclic
            if (!Graph.IsDirectedAcyclicGraph())
            {
                Debug.LogError("Current graph isn't acyclic whereas GH needs a DAG, aborting.");
                return;
            }

            //Add components in an order that will make loading the saved file faster
            foreach (Vertex vertex in Graph.TopologicalSort())
            {
                currentObjectIndex = vertex.Chunk.Add(definitionObjects, currentObjectIndex, Graph, vertex);
            }

            definitionObjects.RemoveItem("ObjectCount");
            definitionObjects.SetInt32("ObjectCount", currentObjectIndex);

            archive.WriteToFile(Application.streamingAssetsPath + relativePath, true, false);
            
            Debug.Log("Saved to file " + relativePath);
        }

        public void LoadFromGrasshopper(string relativePath)
        {
            try
            {
                bool success = ReadGHFile(Application.streamingAssetsPath + relativePath);
                if (!success)
                {
                    Debug.LogError("Failed to read the given file.");
                }

                LearnComponentTemplates();
                /*foreach (Vertex vertex in Graph.TopologicalSort())
                {
                    OutputPort port = vertex.Chunk as OutputPort;
                    if (port != null)
                    {
                        if (port.DefaultName.Equals("Mesh"))
                        {
                            Debug.LogWarning("mesh here: " + port.Guid);
                        }
                    }
                }*/
            }
            catch (FileNotFoundException e)
            {
                Debug.LogError("Given file not found. (" + relativePath + ")");
                Debug.LogError("Should have tried: " + Directory.GetCurrentDirectory() + relativePath);
                Debug.LogError("Also: " + Application.streamingAssetsPath + relativePath);
                Console.WriteLine(e);
                throw;
            }
        }

        public void LoadComponentTemplates(string templatesFile)
        {
            if (string.IsNullOrEmpty(templatesFile))
            {
                Debug.LogError("No component templates file given, cannot load templates.");
            }
            _componentTemplateFile = templatesFile;
            ComponentTemplates = new List<IoComponentTemplate>();

            FileStream stream;
            
            if (!File.Exists(Application.streamingAssetsPath + "/" + _componentTemplateFile))
            {
                stream = File.Create(Application.streamingAssetsPath + "/" + _componentTemplateFile);
                stream.Close();
                return;
            }

            IFormatter formatter = new BinaryFormatter();
            stream = new FileStream(Application.streamingAssetsPath + "/" + _componentTemplateFile, FileMode.Open, FileAccess.Read);
            if (stream.Length <= 0)
            {
                stream.Close();
                return;
            }
            List<IoComponentTemplate> templates = (List<IoComponentTemplate>)formatter.Deserialize(stream);
            stream.Close();

            foreach (IoComponentTemplate template in templates)
            {
                Debug.Log(template);
                ComponentTemplates.Add(template);
            }
        }

        private void LearnComponentTemplates()
        {
            if (string.IsNullOrEmpty(_componentTemplateFile))
            {
                Debug.LogError("No component templates file given, cannot learn templates.");
            }
            
            foreach (Vertex vertex in Graph.Vertices)
            {
                IoComponent component = vertex.Chunk as IoComponent;
                if (component != null && !ComponentTemplates.Any(o => o.TypeGuid.Equals(component.TypeGuid)))
                {
                    List<InputPort> inputPorts = new List<InputPort>();
                    List<OutputPort> outputPorts = new List<OutputPort>();
                        
                    foreach (Edge inEdge in Graph.InEdges(vertex))
                    {
                        InputPort inputPort = inEdge.Source.Chunk as InputPort;
                        inputPorts.Add(new InputPort(Guid.Empty, inputPort.Nickname, inputPort.DefaultName, inputPort.VisualBounds));
                    }
                        
                    foreach (Edge outEdge in Graph.OutEdges(vertex))
                    {
                        OutputPort outputPort = outEdge.Target.Chunk as OutputPort;
                        outputPorts.Add(new OutputPort(Guid.Empty, outputPort.Nickname, outputPort.DefaultName, outputPort.VisualBounds));
                    }

                    IoComponentTemplate newTemplate = new IoComponentTemplate(component.DefaultName, component.TypeGuid,
                        component.TypeName,
                        component.VisualBounds, component.Nickname, inputPorts, outputPorts);
                    ComponentTemplates.Add(newTemplate);
                    
                    Debug.Log("Learned a new template component: " + newTemplate);
                }
            }
            
            IFormatter formatter = new BinaryFormatter();
            Stream stream = new FileStream(Application.streamingAssetsPath + "/" + _componentTemplateFile, FileMode.Create, FileAccess.Write);

            formatter.Serialize(stream, ComponentTemplates);
            stream.Close();
        }

        private bool ReadGHFile(string absolutePath)
        {
            GH_Archive archive = new GH_Archive();
            archive.ReadFromFile(absolutePath);
            //potential unhandled FileNotFoundException (would be propagated to the calling method)

            GH_Chunk root = archive.GetRootNode;
            GH_Chunk def = root.FindChunk("Definition") as GH_Chunk;
            if (def == null)
            {
                Graph = null;
                Groups = null;
                return false;
            }

            GH_Chunk objs = def.FindChunk("DefinitionObjects") as GH_Chunk;
            if (objs == null)
            {
                Graph = null;
                Groups = null;
                return false;
            }

            int count = objs.GetInt32("ObjectCount");

            Debug.Log("Found " + count + " items in the given file.");

            Dictionary<Guid, Vertex> vertices = new Dictionary<Guid, Vertex>();

            Graph = new BidirectionalGraph<Vertex, Edge>();
            Groups = new List<Group>();
            List<Guid[]> edges = new List<Guid[]>();

            //For each component in the file (as a list)
            for (int i = 0; i < count; i++)
            {
                ReadGHChunk(i, objs, vertices, edges);
            }

            foreach (KeyValuePair<Guid, Vertex> vertex in vertices)
            {
                Graph.AddVertex(vertex.Value);
            }

            foreach (Guid[] edge in edges)
            {
                Graph.AddEdge(new Edge
                {
                    Source = vertices[edge[0]],
                    Target = vertices[edge[1]]
                });
            }

            TraverseForDefaultNames(Graph);
            return true;
        }

        private void ReadGHChunk(int i, GH_Chunk objs,
            Dictionary<Guid, Vertex> vertices, List<Guid[]> edges)
        {
            Debug.Log("==================================");
            Debug.Log("Item " + i);

            //Get the object chunk, note that this requires an index.
            GH_Chunk obj = objs.FindChunk("Object", i) as GH_Chunk;
            if (obj == null)
            {
                return;
            }

            string typeName = obj.GetString("Name"); //Component type as string (e.g. Slider, DegToRad) 

            Guid typeGuid = obj.GetGuid("GUID");

            Debug.Log("Object of type " + typeName + "(" + typeGuid + ")");

            GH_Chunk container = obj.FindChunk("Container") as GH_Chunk;
            if (container == null)
            {
                return;
            }

            //TODO: other types of components should be handled, e.g. "Addition" does not have the same structure as normal IOC
            
            /*========== Group ==========*/
            if (GHConstants.Group.Equals(typeGuid))
            {
                Groups.Add(new Group(obj));
            }
            /*========== Cluster ==========*/
            else if (GHConstants.Cluster.Equals(typeGuid))
            {
                //TODO: handle clusters
                //(can we "uncluster" them? probably not simply by reading the file but can we tell GH to do so from a plugin?)
                //simply show a warning that clusters aren't supported?
                //may want to store the data so that it can be saved later, even though that data is not processed by this tool
                Debug.LogWarning("Clusters aren't currently supported, ignoring this one.");
            }
            /*========== Component ==========*/
            else
            {
                /*========== IO Component ==========*/
                if (container.ChunkExists("param_input", 0) || container.ChunkExists("param_output", 0))
                {
                    new IoComponent(obj, vertices, edges, IoComponent.IoComponentType.Type1);
                }
                /*========== Parameter Component ==========*/
                else if (container.ChunkExists("ParameterData"))
                {
                    Debug.LogWarning("ParameterData");
                    new IoComponent(obj, vertices, edges, IoComponent.IoComponentType.Type2);
                }
                /*========== Primitive Component ==========*/
                else
                {
                    PrimitiveComponent.CreatePrimitive(obj, vertices, edges);
                }
            }
        }

        private static void TraverseForDefaultNames(BidirectionalGraph<Vertex, Edge> graph)
        {
            foreach (Vertex vertex in graph.Roots())
            {
                TraverseForDefaultNames(vertex, graph);
            }
        }

        private static void TraverseForDefaultNames(Vertex vertex, BidirectionalGraph<Vertex, Edge> graph)
        {
            List<Edge> outEdges = new List<Edge>(graph.OutEdges(vertex));

            //should be more OOP-friendly (polymorphism should be the proper way to handle this)
            PrimitiveComponent primitiveComponent = vertex.Chunk as PrimitiveComponent;
            if (primitiveComponent != null)
            {
                if (string.IsNullOrEmpty(primitiveComponent.Nickname))
                {
                    if (outEdges.Count <= 0)
                    {
                        primitiveComponent.DefaultName = primitiveComponent.TypeName;
                        return;
                    }

                    Vertex nextVertex = outEdges[0].Target;
                    InputPort nextInput = nextVertex.Chunk as InputPort;
                    if (nextInput != null)
                    {
                        primitiveComponent.DefaultName = nextInput.DefaultNameToGive;
                    }
                    else
                    {
                        PrimitiveComponent nextPrimitive = nextVertex.Chunk as PrimitiveComponent;
                        if (nextPrimitive != null)
                        {
                            primitiveComponent.DefaultName = nextPrimitive.DefaultName;
                        }
                        else
                        {
                            Debug.LogError("PROBLEM: next chunk " + nextVertex.Chunk.Guid + " that follows " + vertex.Chunk.Guid + " is neither an input port nor a primitive");
                            Debug.LogError(nextVertex.Chunk.GetType());
                        }
                    }
                }
            }

            //NOTE: following lines should never be called, its just there for debugging purposes
            else if (!(vertex.Chunk is Port))
            {
                //can only be an IoComponent then
                IoComponent component = vertex.Chunk as IoComponent;
                if (component != null && string.IsNullOrEmpty(component.Nickname))
                {
                    Debug.LogError("PROBLEM: IoComponent without a nickname");
                    Debug.LogError(vertex.Chunk);
                }
            }

            foreach (Edge outEdge in outEdges)
            {
                TraverseForDefaultNames(outEdge.Target, graph);
            }
        }

        //actually, GH does not seem to care at all, groups can contain references to the guid of a deleted component
        public static void RemoveEmptyGroups(List<Group> groups)
        {
            bool deletedSomething;
            int safeguard = 0;
            do
            {
                deletedSomething = false;
                safeguard++;

                List<Group> toRemove = new List<Group>();
                foreach (Group group in groups)
                {
                    if (group.Constituents.Count <= 0)
                    {
                        Debug.Log(group.Guid + " was empty, removing.");
                        toRemove.Add(group);
                        deletedSomething = true;
                    }
                }

                foreach (Group target in toRemove)
                {
                    foreach (Group group in groups)
                    {
                        if (group.Constituents.Contains(target.Guid))
                        {
                            Debug.Log(group.Guid + " contained " + target.Guid);
                            group.Constituents.Remove(target.Guid);
                        }
                    }

                    groups.Remove(target);
                }
            } while (deletedSomething && safeguard < 50);

            if (safeguard >= 50)
            {
                Debug.LogError("Safeguard reached, is this a bug or is the hierarchy of groups really that deep?");
            }
        }

        public RectangleF FindBounds()
        {
            float minX = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;

            foreach (Vertex vertex in Graph.Vertices)
            {
                Component component = vertex.Chunk as Component;
                if (component != null)
                {
                    RectangleF bounds = component.VisualBounds;
                    if (bounds.X < minX)
                    {
                        minX = bounds.X;
                    }

                    if (bounds.Y < minY)
                    {
                        minY = bounds.Y;
                    }

                    if (bounds.X + bounds.Width > maxX)
                    {
                        maxX = bounds.X + bounds.Width;
                    }

                    if (bounds.Y + bounds.Height > maxY)
                    {
                        maxY = bounds.Y + bounds.Height;
                    }
                }
            }

            return new RectangleF(minX, minY, maxX - minX, maxY - minY);
        }

        public void DownloadDotVizImage()
        {
            string url = "https://graphviz.glitch.me/graphviz?layout=dot&format=png&mode=download&graph=" +
                         Uri.EscapeUriString(GenerateDotViz(Graph));
            Debug.Log(
                "\n\nurl to a .png image:\n" + url);
            Process.Start(url);
        }

        private string GenerateDotViz(BidirectionalGraph<Vertex, Edge> graph)
        {
            List<Vertex> roots = new List<Vertex>(graph.Roots());
            IEnumerable<Vertex> vertices = graph.Vertices;
            IEnumerable<Edge> edges = graph.Edges;

            StringBuilder output = new StringBuilder("digraph G {" +
                                                     "\nnode [shape=box style=filled];" +
                                                     "\nedge [penwidth=2 color=\"black: invis:black\"];");
            foreach (Edge edge in edges)
            {
                output.Append("\n" + edge + "; ");
            }

            foreach (Vertex vertex in vertices)
            {
                output.Append("\n\"" + vertex.Chunk.Guid + "\" [");

                if (vertex.Chunk is InputPort)
                {
                    output.Append("shape=house style=\"\" ");
                }
                else if (vertex.Chunk is OutputPort)
                {
                    output.Append("shape=invhouse style=\"\" ");
                }
                else if (vertex.Chunk is BooleanToggleComponent)
                {
                    output.Append(
                        ((BooleanToggleComponent) vertex.Chunk).Value ? "color=\"green\" " : "color=\"red\" ");
                }

                if (roots.Contains(vertex))
                {
                    output.Append("peripheries=2 ");
                }

                output.Append("label=" + vertex.Chunk);

                output.Append("]");
            }

            output.Append("\n}");
            return output.ToString();
        }

        public bool AddEdge(string start, string end)
        {
            Vertex startVertex = null, endVertex = null;
            
            foreach (Vertex vertex in Graph.Vertices)
            {
                if (vertex.Chunk.Guid.ToString().Equals(start)) startVertex = vertex;
                else if (vertex.Chunk.Guid.ToString().Equals(end)) endVertex = vertex;

                if (startVertex != null && endVertex != null)
                    break;
            }

            if (startVertex == null || endVertex == null)
            {
                Debug.LogError("Can't find one of the 2 vertices, can't add the edge.");
                return false;
            }
            
            Edge newEdge = new Edge
            {
                Source = startVertex,
                Target = endVertex
            };
            
            Graph.AddEdge(newEdge);

            if (!Graph.IsDirectedAcyclicGraph())
            {
                Debug.LogError("This would introduce a cycle.");
                Graph.RemoveEdge(newEdge);
                return false;
            }

            return true;
        }

        public bool RemoveEdge(string edgeName)
        {
            string[] vertices = edgeName.Split(new[] {"->"}, StringSplitOptions.None);
            foreach (Edge edge in Graph.Edges)
            {
                if (edge.Source.Chunk.Guid.ToString().Equals(vertices[0]) &&
                    edge.Target.Chunk.Guid.ToString().Equals(vertices[1]))
                {
                    return Graph.RemoveEdge(edge);
                }
            }

            return false;
        }

        public bool UnplugInput(string inputPortGuid)
        {
            foreach (Vertex vertex in Graph.Vertices)
            {
                if (vertex.Chunk.Guid.ToString().Equals(inputPortGuid))
                {
                    foreach (Edge inEdge in Graph.InEdges(vertex))
                    {
                        Graph.RemoveEdge(inEdge);
                    }

                    return true;
                }
            }

            return false;
        }

        public bool UnplugOutput(string outputPortGuid)
        {
            foreach (Vertex vertex in Graph.Vertices)
            {
                if (vertex.Chunk.Guid.ToString().Equals(outputPortGuid))
                {
                    foreach (Edge outEdge in Graph.OutEdges(vertex))
                    {
                        Graph.RemoveEdge(outEdge);
                    }

                    return true;
                }
            }

            return false;
        }

        public static OrderedDictionary OrderGroupsWithDepth(List<Group> groups)
        {
            StringBuilder toPrint = new StringBuilder("got ");
            foreach (Group group in groups)
            {
                toPrint.Append(group.Guid.ToString().Substring(0, 5) + " -> ");
            }

            Debug.Log(toPrint.ToString());

            OrderedDictionary orderedGroups = new OrderedDictionary(groups.Count);
            List<Guid> unprocessedGuids = new List<Guid>(groups.Count);
            int layer = 1;
            foreach (Group group in groups)
            {
                unprocessedGuids.Add(group.Guid);
            }

            List<Group> toBeSorted = new List<Group>(groups);
            int safeGuard = 0;
            while (toBeSorted.Count > 0 && safeGuard < 50)
            {
                List<int> sortedThisIteration = new List<int>();
                for (int groupIndex = 0; groupIndex < toBeSorted.Count; groupIndex++)
                {
                    Group group = toBeSorted[groupIndex];
                    bool precedenceConstraintFound = false;
                    foreach (Guid guid in group.Constituents)
                    {
                        if (unprocessedGuids.Contains(guid))
                        {
                            Debug.Log("Must process " + guid.ToString().Substring(0, 5) + " before " +
                                      group.Guid.ToString().Substring(0, 5));
                            precedenceConstraintFound = true;
                            break;
                        }
                    }

                    if (!precedenceConstraintFound)
                    {
                        orderedGroups.Add(group, layer);
                        //uncommenting the next line can speed up the process
                        //but the depth level would then become incorrect
                        //unprocessedGuids.Remove(group.Guid);
                        sortedThisIteration.Add(groupIndex);
                    }
                }

                for (int i = sortedThisIteration.Count - 1; i >= 0; i--)
                {
                    int toRemove = sortedThisIteration[i];
                    unprocessedGuids.Remove(toBeSorted[toRemove].Guid);
                    toBeSorted.RemoveAt(toRemove);
                }

                layer++;

                safeGuard++;
                if (safeGuard >= 50)
                {
                    Debug.LogError("safeguard reached, can't seem to sort these groups, wtf?");
                }
            }

            toPrint = new StringBuilder("ordered: ");
            foreach (Group group in orderedGroups.Keys)
            {
                toPrint.Append(group.Guid.ToString().Substring(0, 5) + " -> ");
            }

            Debug.Log(toPrint.ToString());

            return orderedGroups;
        }

        public void RemoveVertex(Vertex vertex)
        {
            Graph.RemoveVertex(vertex);
        }

        public void AddMeshScripts()
        {
            List<Vertex> meshPorts = new List<Vertex>();
            
            foreach (Vertex vertex in Graph.Vertices)
            {
                OutputPort port = vertex.Chunk as OutputPort;
                if (port != null && port.DefaultName.Equals("Mesh"))
                {
                    meshPorts.Add(vertex);
                    Debug.LogWarning("mesh here: " + port.Guid);
                }
            }

            bool alreadyStreaming = false;
            float maxX = float.NegativeInfinity;
            Vertex rightmostMesh = null;
            
            foreach (Vertex vertex in meshPorts)
            {
                foreach (Edge outEdge in Graph.OutEdges(vertex))
                {
                    Component component = outEdge.Target.Chunk as Component;
                    if (component != null && component.TypeGuid.Equals(GHConstants.Script))
                    {
                        Debug.Log("There is already a script linked to port " + vertex.Chunk.Guid);
                        
                        if (alreadyStreaming)
                        {
                            Debug.LogError("Too many scripts.");
                        }

                        alreadyStreaming = true;
                        break;
                    }
                }

                if (!alreadyStreaming)
                {
                    OutputPort outputPort = vertex.Chunk as OutputPort;
                    if (outputPort.VisualBounds.X > maxX)
                    {
                        maxX = outputPort.VisualBounds.X;
                        rightmostMesh = vertex;
                    }
                }
            }

            if (!alreadyStreaming)
            {
                                    
                Guid instanceGuid = Guid.NewGuid();
                
                Vertex rightMostMeshComponentVertex = Graph.InEdge(rightmostMesh, 0).Source;

                Component rightMostMeshComponent = rightMostMeshComponentVertex.Chunk as Component;
                string streamingScript = "using(var args = new Rhino.Runtime.NamedParametersEventArgs())\n" +
                                         "{\n" +
                                         "args.Set(\"mesh\", mesh);\n" +
                                        "Rhino.Runtime.HostUtils.ExecuteNamedCallback(\"Unity:FromGrasshopper\", args);\n" +
                                        "}";
                IoComponent ioComponent = new IoComponent("C# Script", GHConstants.Script, "C# Script", new RectangleF(rightMostMeshComponent.VisualBounds.X + rightMostMeshComponent.VisualBounds.Width + 50,rightMostMeshComponent.VisualBounds.Y,84,44), instanceGuid, "MeshSteaming", streamingScript, IoComponent.IoComponentType.Type2, new Guid("84fa917c-1ed8-4db3-8be1-7bdc4a6495a2"), Guid.Empty);
                Vertex newVertex = new Vertex(ioComponent);
                Graph.AddVertex(newVertex);

                Guid inputGuid = Guid.NewGuid();
                InputPort inputPort = new InputPort(inputGuid, "mesh", "", new RectangleF(rightMostMeshComponent.VisualBounds.X + 2,rightMostMeshComponent.VisualBounds.Y + 2,33,27));
                inputPort.TypeHintID = new Guid("794a1f9d-21d5-4379-b987-9e8bbf433912");
                Graph.AddVertex(new Vertex(inputPort));
                AddEdge(inputGuid.ToString(), instanceGuid.ToString());
                AddEdge(rightmostMesh.Chunk.Guid.ToString(), inputGuid.ToString());

                string tmpPath = "/GH files/test-copy.ghx";
                SaveToGrasshopper(tmpPath);
                //todo: now start grasshopper and open that file
                
                string absolutePath = Application.streamingAssetsPath + tmpPath;
//                Debug.Log(absolutePath);
//                Debug.Log(absolutePath.Replace("/", "\\\\"));
                string rhinoCommand = "_Open \""+ absolutePath.Replace("/", "\\\\") +"\"";
                Debug.Log("Opening the saved file in Rhino.");
                RhinoInside.Launch();
                RhinoApp.RunScript(rhinoCommand, false);
            }
        }
        
        /*private GameObject AddMeshScript(IoComponentTemplate template, string componentName)
        {
            Guid instanceGuid = Guid.NewGuid();
            IoComponent ioComponent = new IoComponent(template.DefaultName, template.TypeGuid, template.TypeName, template.VisualBounds, instanceGuid, componentName, template.ScriptSource, template.ComponentType, template.InputId, template.OutputId);
            Vertex newVertex = new Vertex(ioComponent);
            _parametricModel.Graph.AddVertex(newVertex);
        
            foreach (InputPort inputPort in template.InputPorts)
            {
                InputPort newInputPort = new InputPort(Guid.NewGuid(), inputPort.Nickname, inputPort.DefaultName, inputPort.VisualBounds);
                newInputPort.TypeHintID = inputPort.TypeHintID;
                _parametricModel.Graph.AddVertex(new Vertex(newInputPort));
                _parametricModel.AddEdge(newInputPort.Guid.ToString(), instanceGuid.ToString());
            }
        
            foreach (OutputPort outputPort in template.OutputPorts)
            {
                OutputPort newOutputPort = new OutputPort(Guid.NewGuid(), outputPort.Nickname, outputPort.DefaultName, outputPort.VisualBounds);
                _parametricModel.Graph.AddVertex(new Vertex(newOutputPort));
                _parametricModel.AddEdge(instanceGuid.ToString(), newOutputPort.Guid.ToString());
            }
        
            Quaternion savedRotation = DrawingSurface.rotation;
            DrawingSurface.rotation = Quaternion.identity;

            GameObject newComponent = AddComponent(DrawingSurface, newVertex, _parametricModel.FindBounds(),
                _parametricModel.Graph);

            DrawingSurface.rotation = savedRotation;

            return newComponent;
        }*/
    }
}