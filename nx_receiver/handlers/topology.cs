// tools: list_edges, list_faces
// Edge and face listings so the AI can pick geometry for blends, chamfers, shells and sketches.
using System;
using System.Collections.Generic;
using NXOpen;

namespace NxAiBridge.Handlers
{
    public class TopologyTool : ITool
    {
        public object Run(string tool, Args a)
        {
            Nx.Work();
            Body body = Nx.ResolveBody(a.Str("body", null));
            string bodyRef = Nx.BodyRef(body);
            List<object> items = new List<object>();
            if (tool == "list_edges")
            {
                Edge[] edges = body.GetEdges();
                for (int i = 0; i < edges.Length; i++) items.Add(Nx.EdgeInfo(bodyRef, i, edges[i]));
                return R.Obj("body", bodyRef, "count", items.Count, "edges", items);
            }
            Face[] faces = body.GetFaces();
            for (int i = 0; i < faces.Length; i++) items.Add(Nx.FaceInfo(bodyRef, i, faces[i]));
            return R.Obj("body", bodyRef, "count", items.Count, "faces", items);
        }
    }
}
