// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// エディタの各部で共有する幾何計算: スキンのあるレンダラーが頂点を描く位置、三角形上の最近点と重心座標、
    /// 線分までの距離、メッシュの頂点の隣接。
    /// </summary>
    public static class MeshMath
    {
        /// <summary>レンダラーが今頂点を描いている位置。ワールド空間で。</summary>
        public static Vector3[] WorldPositions(SkinnedMeshRenderer smr)
        {
            var local = SkinnedPositions(smr);
            for (var i = 0; i < local.Length; i++) local[i] = smr.transform.TransformPoint(local[i]);
            return local;
        }

        /// <summary>AAO が評価するとおりの、レンダラーのローカル空間での頂点位置（今のポーズとシェイプ）。</summary>
        public static Vector3[] SkinnedPositions(SkinnedMeshRenderer smr)
        {
            var mesh = smr.sharedMesh;
            var verts = mesh.vertices;
            var dv = new Vector3[verts.Length];
            for (var s = 0; s < mesh.blendShapeCount; s++)
            {
                var w = smr.GetBlendShapeWeight(s);
                if (Mathf.Abs(w) < 1e-4f) continue;
                var frames = mesh.GetBlendShapeFrameCount(s);
                var f = frames - 1;
                while (f > 0 && mesh.GetBlendShapeFrameWeight(s, f - 1) >= w) f--;
                var fw = mesh.GetBlendShapeFrameWeight(s, f);
                mesh.GetBlendShapeFrameVertices(s, f, dv, null, null);
                var k = fw != 0 ? w / fw : 0;
                for (var i = 0; i < verts.Length; i++) verts[i] += dv[i] * k;
            }
            var bones = smr.bones;
            var bind = mesh.bindposes;
            var toLocal = smr.transform.worldToLocalMatrix;
            var mats = new Matrix4x4[bones.Length];
            for (var b = 0; b < bones.Length; b++)
                mats[b] = bones[b] != null ? toLocal * bones[b].localToWorldMatrix * bind[b] : Matrix4x4.identity;
            var bpv = mesh.GetBonesPerVertex();
            // ボーンウェイトのないレンダラーは、MeshRenderer と同じくメッシュを自身の空間で描く
            if (bpv.Length < verts.Length) return verts;
            var weights = mesh.GetAllBoneWeights();
            var result = new Vector3[verts.Length];
            for (int v = 0, w = 0; v < verts.Length; v++)
            {
                var p = Vector3.zero;
                for (var j = 0; j < bpv[v]; j++, w++)
                    p += mats[weights[w].boneIndex].MultiplyPoint3x4(verts[v]) * weights[w].weight;
                result[v] = bpv[v] == 0 ? verts[v] : p;
            }
            return result;
        }

        /// <summary>三角形 abc 上で `p` に最も近い点。</summary>
        public static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;
            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;
            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            var va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6));
            var denom = 1f / (va + vb + vc);
            return a + ab * (vb * denom) + ac * (vc * denom);
        }

        /// <summary>abc での `p`（三角形上の点）の重心座標。</summary>
        public static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            var den = d00 * d11 - d01 * d01;
            if (Mathf.Abs(den) < 1e-20f) return new Vector3(1, 0, 0);
            var y = (d11 * d20 - d01 * d21) / den;
            var z = (d00 * d21 - d01 * d20) / den;
            return new Vector3(1 - y - z, y, z);
        }

        /// <summary>`p` から線分 ab までの距離。</summary>
        public static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            var t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude;
        }

        /// <summary>
        /// 三角形のリスト（頂点 `n` 個）の頂点ごと: 三角形の辺に沿った隣の頂点。辺の片側ごとに 1 回、
        /// 三角形の順に並ぶ（共有する辺では重複する）。
        /// </summary>
        public static List<int>[] Neighbours(int[] tris, int n)
        {
            var nb = new List<int>[n];
            for (var v = 0; v < n; v++) nb[v] = new List<int>();
            for (var i = 0; i < tris.Length; i += 3)
                for (var e = 0; e < 3; e++)
                {
                    nb[tris[i + e]].Add(tris[i + (e + 1) % 3]);
                    nb[tris[i + (e + 1) % 3]].Add(tris[i + e]);
                }
            return nb;
        }
    }
}
