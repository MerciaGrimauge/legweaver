// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// 揺れる垂れ（ガーターのリボン）: 垂れごとに根元から先へ骨を 3 つ連ねて、垂れの頂点をその骨に結び付け、
    /// 根元の骨に VRChat の揺れ物（VRCPhysBone）を付ける。垂れは Blender が頂点ごとに（番号, 根元からの割合）で示す。
    /// </summary>
    public static class SwingTails
    {
        const int Joints = 3;

        /// <summary>
        /// `garment` のメッシュに垂れの骨を足す。`swing` は（出力の頂点, 垂れの番号, 根元からの割合）の並び。
        /// メッシュは今の姿勢がバインドポーズであるとして骨を置く。足した垂れの数を返す。
        /// </summary>
        public static int Apply(SkinnedMeshRenderer garment, float[] swing)
        {
            if (swing == null || swing.Length < 3) return 0;
            var mesh = garment.sharedMesh;
            var world = MeshMath.WorldPositions(garment);
            var tails = new SortedDictionary<int, List<(int v, float t)>>();
            for (var i = 0; i + 2 < swing.Length; i += 3)
            {
                var id = (int)swing[i + 1];
                if (!tails.TryGetValue(id, out var l)) tails[id] = l = new List<(int, float)>();
                l.Add(((int)swing[i], swing[i + 2]));
            }
            var bpv = mesh.GetBonesPerVertex().ToArray();
            var weights = mesh.GetAllBoneWeights().ToArray();
            var start = new int[bpv.Length];
            for (int v = 1; v < bpv.Length; v++) start[v] = start[v - 1] + bpv[v - 1];
            var bones = garment.bones.ToList();
            var bind = mesh.bindposes.ToList();
            var replaced = new Dictionary<int, BoneWeight1[]>();
            var toMesh = garment.transform.localToWorldMatrix;
            foreach (var (id, verts) in tails)
            {
                Vector3 Mean(Func<float, bool> which)
                {
                    var sel = verts.Where(x => which(x.t)).Select(x => world[x.v]).ToList();
                    return sel.Count > 0 ? sel.Aggregate(Vector3.zero, (a, b) => a + b) / sel.Count : Vector3.zero;
                }
                var root = Mean(t => t <= 1e-4f);
                var tip = Mean(t => t >= 1f - 1e-4f);
                // 親: 根元の頂点で最も重いボーン（太ももの骨の写し）
                var heaviest = verts.Where(x => x.t <= 1e-4f)
                    .SelectMany(x => weights.Skip(start[x.v]).Take(bpv[x.v]))
                    .GroupBy(w => w.boneIndex).OrderByDescending(gr => gr.Sum(w => w.weight)).First().Key;
                Transform parent = bones[heaviest];
                var first = bones.Count;
                for (var k = 0; k < Joints; k++)
                {
                    var j = new GameObject($"LegWeaver_Ribbon{id}_{k}").transform;
                    j.SetParent(parent, false);
                    j.position = Vector3.Lerp(root, tip, k / (float)(Joints - 1));
                    j.rotation = Quaternion.LookRotation(tip - root, Vector3.up);
                    bones.Add(j);
                    bind.Add(j.worldToLocalMatrix * toMesh);
                    parent = j;
                }
                // 割合 t を骨の間に配る（根元は最初の骨だけ）
                foreach (var (v, t) in verts)
                {
                    var f = Mathf.Clamp01(t) * (Joints - 1);
                    var k = Mathf.Min((int)f, Joints - 2);
                    var b = f - k;
                    replaced[v] = new[]
                        {
                            new BoneWeight1 { boneIndex = first + k, weight = 1f - b },
                            new BoneWeight1 { boneIndex = first + k + 1, weight = b },
                        }
                        .Where(w => w.weight > 1e-4f).OrderByDescending(w => w.weight).ToArray();
                }
                AddPhysBone(bones[first]);
            }
            var newBpv = new List<byte>();
            var newWeights = new List<BoneWeight1>();
            for (var v = 0; v < bpv.Length; v++)
            {
                var ws = replaced.TryGetValue(v, out var r) ? r : weights.Skip(start[v]).Take(bpv[v]).ToArray();
                newBpv.Add((byte)ws.Length);
                newWeights.AddRange(ws);
            }
            mesh.bindposes = bind.ToArray();
            using (var a = new NativeArray<byte>(newBpv.ToArray(), Allocator.Temp))
            using (var w = new NativeArray<BoneWeight1>(newWeights.ToArray(), Allocator.Temp))
                mesh.SetBoneWeights(a, w);
            garment.bones = bones.ToArray();
            return tails.Count;
        }

        /// <summary>
        /// `root` に VRCPhysBone を付ける（VRChat SDK があれば。参照を持たないよう型は名前で探す）。リボンらしく、
        /// 少し垂れて軽く揺れる値にする。付けられなければ警告だけ。
        /// </summary>
        static void AddPhysBone(Transform root)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone"))
                .FirstOrDefault(t => t != null);
            if (type == null)
            {
                Debug.LogWarning("[LegWeaver] VRChat SDK の VRCPhysBone が見つからないので、リボンの垂れは揺れません。");
                return;
            }
            var pb = root.gameObject.AddComponent(type);
            void Set(string name, object value)
            {
                var f = type.GetField(name);
                if (f != null && f.FieldType.IsInstanceOfType(value)) f.SetValue(pb, value);
            }
            Set("rootTransform", root);
            Set("pull", 0.2f);
            Set("spring", 0.3f);
            Set("stiffness", 0.1f);
            Set("gravity", 0.3f);
        }
    }
}
