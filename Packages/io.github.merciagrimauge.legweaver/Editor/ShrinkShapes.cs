// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// 縮めるシェイプキー: 衣装に合わせて肌をつぶして隠すシェイプ（アバターは衣装を着てこれをオンにした状態で
    /// 保存されていることが多い）。名前ではなく、シェイプがメッシュに何をするかで見つける:
    /// 三角形の面積が 5% 未満しか残らない、または内側へ引かれて 40% 未満しか残らない（靴に合わせて細くした足首）、
    /// または頂点を 10 mm より遠く、かつその最長の辺の 2 倍より遠くへ引く（針のようにする）。
    /// 体型のシェイプ（脚を細くする）は面積の大半を保つ。
    /// </summary>
    public static class ShrinkShapes
    {
        const int MinVertices = 10;

        /// <summary>今オンになっている縮めるシェイプの番号。</summary>
        public static List<int> FindActive(SkinnedMeshRenderer smr, float metersPerUnit)
        {
            var result = new List<int>();
            var mesh = smr.sharedMesh;
            if (mesh == null) return result;
            var probe = new Probe(mesh);
            for (var s = 0; s < mesh.blendShapeCount; s++)
                if (Mathf.Abs(smr.GetBlendShapeWeight(s)) >= 1e-4f && probe.Collapsed(s, metersPerUnit).Count >= MinVertices)
                    result.Add(s);
            return result;
        }

        /// <summary>有効な縮めるシェイプをオフにする（元に戻せる）。その名前を返す。</summary>
        public static List<string> TurnOff(SkinnedMeshRenderer smr, float metersPerUnit)
        {
            var names = new List<string>();
            var found = FindActive(smr, metersPerUnit);
            if (found.Count == 0) return names;
            Undo.RecordObject(smr, "Turn Off Shrink Shapes");
            foreach (var s in found)
            {
                smr.SetBlendShapeWeight(s, 0);
                names.Add(smr.sharedMesh.GetBlendShapeName(s));
            }
            return names;
        }

        /// <summary>メッシュのシェイプキーを 1 つずつ（ウェイト最大で）調べ、
        /// 縮めるシェイプとなるつぶれ方をしているかを見る。</summary>
        public class Probe
        {
            readonly Mesh _mesh;
            readonly Vector3[] _rest, _normals, _dv;
            readonly int[] _t;
            readonly float[] _edge;

            public Probe(Mesh mesh)
            {
                _mesh = mesh;
                _rest = mesh.vertices;
                _normals = mesh.normals;
                _t = mesh.triangles;
                _edge = new float[_rest.Length];
                for (var f = 0; f < _t.Length; f += 3)
                    for (var c = 0; c < 3; c++)
                    {
                        int a = _t[f + c], b = _t[f + (c + 1) % 3];
                        var len = (_rest[a] - _rest[b]).magnitude;
                        _edge[a] = Mathf.Max(_edge[a], len);
                        _edge[b] = Mathf.Max(_edge[b], len);
                    }
                _dv = new Vector3[_rest.Length];
            }

            /// <summary>シェイプ `s` がつぶす頂点（10 個以上あれば縮めるシェイプ）。</summary>
            public HashSet<int> Collapsed(int s, float metersPerUnit)
            {
                _mesh.GetBlendShapeFrameVertices(s, _mesh.GetBlendShapeFrameCount(s) - 1, _dv, null, null);
                var hit = new HashSet<int>();
                for (var i = 0; i < _rest.Length; i++)
                {
                    var moved = _dv[i].magnitude;
                    if (moved * metersPerUnit > 0.01f && moved > _edge[i] * 2f) hit.Add(i);
                }
                for (var f = 0; f < _t.Length; f += 3)
                {
                    Vector3 a = _rest[_t[f]], b = _rest[_t[f + 1]], c = _rest[_t[f + 2]];
                    var before = Vector3.Cross(b - a, c - a).magnitude;
                    var after = Vector3.Cross(b + _dv[_t[f + 1]] - a - _dv[_t[f]], c + _dv[_t[f + 2]] - a - _dv[_t[f]]).magnitude;
                    if (before <= 0) continue;
                    // 内側へ引く: 肌の法線に逆らう向き
                    var inward = _normals.Length == _rest.Length &&
                                 Vector3.Dot(_dv[_t[f]], _normals[_t[f]]) + Vector3.Dot(_dv[_t[f + 1]], _normals[_t[f + 1]]) +
                                 Vector3.Dot(_dv[_t[f + 2]], _normals[_t[f + 2]]) < 0;
                    if (after < before * 0.05f || inward && after < before * 0.4f)
                        for (var k = 0; k < 3; k++) hit.Add(_t[f + k]);
                }
                return hit;
            }

            /// <summary>最後に調べたシェイプ: ウェイト最大で各頂点がどう動くか。</summary>
            public Vector3[] Moves => _dv;
        }
    }
}
