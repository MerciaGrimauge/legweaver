// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// 衣装の下に着ているものの表面。体のメッシュ自身の空間（Blender が作業する空間）で表す:
    /// その各頂点を体の上の点（最も近い体の三角形、重心座標、体の法線に沿った高さ）として持ち、
    /// 体のメッシュの頂点と法線から組み立て直す。肌から `Reach` より離れた頂点は、その三角形と一緒に省く（ゆるいフリル:
    /// 衣装はそれに沿わない）。各頂点は、その上に作る衣装の頂点のために自身のウェイト（体のボーンでの）を持つ。
    /// </summary>
    public static class UnderSurface
    {
        public const float Reach = 0.012f;

        public static (Vector3[] verts, int[] tris, Dictionary<int, float>[] weights) InBodySpace(AvatarBody body,
            IEnumerable<SkinnedMeshRenderer> layers)
        {
            var list = layers?.Where(l => l != null && l.sharedMesh != null).ToList() ?? new List<SkinnedMeshRenderer>();
            if (list.Count == 0) return (new Vector3[0], new int[0], new Dictionary<int, float>[0]);
            var bw = MeshMath.WorldPositions(body.Renderer);
            var bt = body.Triangles;
            // 体の頂点 -> その三角形。および体の頂点のグリッド
            var around = new List<int>[bw.Length];
            for (var i = 0; i < bw.Length; i++) around[i] = new List<int>();
            for (var f = 0; f < bt.Length; f += 3)
                for (var c = 0; c < 3; c++) around[bt[f + c]].Add(f);
            const float cell = 0.01f;
            Vector3Int Key(Vector3 p) => Vector3Int.FloorToInt(p / cell);
            var grid = new Dictionary<Vector3Int, List<int>>();
            for (var i = 0; i < bw.Length; i++)
            {
                var k = Key(bw[i]);
                if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                l.Add(i);
            }
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var weights = new List<Dictionary<int, float>>();
            foreach (var layer in list)
            {
                var lw = MeshMath.WorldPositions(layer);
                var lweights = UnderLayers.VertexWeights(body, layer);
                var index = new int[lw.Length];
                for (var v = 0; v < lw.Length; v++)
                {
                    index[v] = -1;
                    var p = lw[v];
                    var k = Key(p);
                    // 最も近い体の頂点、次にその三角形上の最近点
                    var best = float.MaxValue;
                    var bestF = -1;
                    var bestQ = Vector3.zero;
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dz = -1; dz <= 1; dz++)
                            {
                                if (!grid.TryGetValue(k + new Vector3Int(dx, dy, dz), out var l)) continue;
                                foreach (var i in l)
                                    foreach (var f in around[i])
                                    {
                                        var q = MeshMath.ClosestOnTriangle(p, bw[bt[f]], bw[bt[f + 1]], bw[bt[f + 2]]);
                                        var d = (q - p).sqrMagnitude;
                                        if (d >= best) continue;
                                        best = d;
                                        bestF = f;
                                        bestQ = q;
                                    }
                            }
                    if (bestF < 0 || best > Reach * Reach) continue;
                    var bary = MeshMath.Barycentric(bestQ, bw[bt[bestF]], bw[bt[bestF + 1]], bw[bt[bestF + 2]]);
                    // そこでの体の（ワールドの）法線に沿った高さ、次にメッシュ空間での同じもの
                    var nWorld = Vector3.zero;
                    var pMesh = Vector3.zero;
                    var nMesh = Vector3.zero;
                    for (var c = 0; c < 3; c++)
                    {
                        var bv = bt[bestF + c];
                        nWorld += body.Renderer.transform.TransformDirection(body.Normals[bv]) * bary[c];
                        pMesh += body.Vertices[bv] * bary[c];
                        nMesh += body.Normals[bv] * bary[c];
                    }
                    var h = Vector3.Dot(p - bestQ, nWorld.normalized);
                    index[v] = verts.Count;
                    verts.Add(pMesh + nMesh.normalized * (h / body.MetersPerUnit));
                    weights.Add(lweights[v]);
                }
                var lt = layer.sharedMesh.triangles;
                for (var f = 0; f < lt.Length; f += 3)
                    if (index[lt[f]] >= 0 && index[lt[f + 1]] >= 0 && index[lt[f + 2]] >= 0)
                        tris.AddRange(new[] { index[lt[f]], index[lt[f + 1]], index[lt[f + 2]] });
            }
            return (verts.ToArray(), tris.ToArray(), weights.ToArray());
        }
    }
}
