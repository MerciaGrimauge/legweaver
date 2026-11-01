// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-FileCopyrightText: 2022 bd_ (the bone matching of Modular Avatar this file follows)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// どのレンダラーがアバターの体か、またレンダラーの各ボーンがアバターのどのボーンにあたるか。
    /// </summary>
    public static class BodyFinder
    {
        /// <summary>
        /// アバターのアーマチュア: Hips の親。Hips がアバターの直下にある（または親がない）場合は Hips そのもの。
        /// GarmentInstaller がこれを複製し、AvatarBone がボーンをこれと照らし合わせる。
        /// </summary>
        public static Transform Armature(Animator animator)
        {
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null) return null;
            return hips.parent != null && hips.parent != animator.transform ? hips.parent : hips;
        }

        /// <summary>
        /// レンダラーのボーンにあたるアバター自身のボーン: アバターのアーマチュアの中にあればそのボーン自身、
        /// なければアーマチュアの同じ名前のボーン。アバターの部品が自分用のアーマチュアの複製を持ち、
        /// ビルド時にアバターのものへ統合されることがある（Modular Avatar の Merge Armature。
        /// これも同じく名前でボーンを対応づける）: そのボーンはアバターのボーンと同じ場所にある。
        /// アーマチュアに該当するボーンがなければ null。
        /// </summary>
        public static Func<Transform, Transform> AvatarBone(Animator animator)
        {
            var armature = Armature(animator);
            if (armature == null) return b => b;
            var byName = new Dictionary<string, Transform>();
            // 幅優先: 同じ名前のボーンが 2 つあれば、上に近いほう
            var queue = new Queue<Transform>();
            queue.Enqueue(armature);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (!byName.ContainsKey(t.name)) byName[t.name] = t;
                foreach (Transform c in t) queue.Enqueue(c);
            }
            return b =>
            {
                if (b == null) return null;
                if (b.IsChildOf(armature)) return b;
                var merged = MergedBone(b);
                if (merged != null) return merged;
                return byName.TryGetValue(b.name, out var a) ? a : null;
            };
        }

        /// <summary>
        /// `bone` の上にある Modular Avatar の Merge Armature がそれを統合する先: コンポーネントの下でのパスを、
        /// 各段の名前からコンポーネントの接頭辞と接尾辞を外したうえで、統合先からたどる（MA 自身の MapBone の規則。
        /// MapBone は public ではない）。そのようなコンポーネントがない場合、
        /// またはパスがどこにも行き着かない場合は null。これは Modular Avatar の ModularAvatarMergeArmature.MapBone に
        /// 従う（MIT License, Copyright (c) 2022 bd_; THIRD_PARTY_NOTICES.md を参照）。
        /// </summary>
        static Transform MergedBone(Transform bone)
        {
#if LW_MA
            var merge = bone.GetComponentInParent<nadena.dev.modular_avatar.core.ModularAvatarMergeArmature>(true);
            var target = merge != null ? merge.mergeTargetObject : null;
            if (target == null) return null;
            var steps = new List<string>();
            for (var t = bone; t != null && t != merge.transform; t = t.parent) steps.Add(t.name);
            steps.Reverse();
            var pointer = target.transform;
            foreach (var step in steps)
            {
                if (!step.StartsWith(merge.prefix) || !step.EndsWith(merge.suffix) ||
                    step.Length == merge.prefix.Length + merge.suffix.Length) return null;
                pointer = pointer.Find(step.Substring(merge.prefix.Length, step.Length - merge.prefix.Length - merge.suffix.Length));
                if (pointer == null) return null;
            }
            return pointer;
#else
            return null;
#endif
        }

        /// <summary>
        /// 体のレンダラーの候補。良いものから順に並べる。体は脚全体を太ももから膝下まで覆う:
        /// メッシュは太ももの頂点数と膝下の頂点数の小さいほうで数える（靴・ニーハイ・ショートパンツはどちらか
        /// 一方しか覆わない）。そのうち、ほかのどれもその内側にないものを先にする（サイハイソックスやレギンスも脚を肌の
        /// 上から覆う。ローポリの体はそれらより頂点が少ないことがある）。表示されているものを非表示のもの（オフにした
        /// 予備の体）より先にし、このツールが作ったものは決して候補にしない。
        /// </summary>
        public static List<SkinnedMeshRenderer> Candidates(GameObject root, Animator animator)
        {
            var avatarBone = AvatarBone(animator);
            var upper = new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg }.Select(animator.GetBoneTransform).ToArray();
            var lower = new[] { HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg }.Select(animator.GetBoneTransform).ToArray();
            var feet = new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot }.Select(animator.GetBoneTransform).ToArray();
            var found = new List<Leg>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (GarmentGenerator.IsMade(smr)) continue;
                var leg = Leg.Of(smr, avatarBone, upper, lower, feet);
                if (leg != null) found.Add(leg);
            }
            var ranked = new List<SkinnedMeshRenderer>();
            foreach (var group in found.GroupBy(l => Shown(l.Renderer, root.transform))
                         .OrderByDescending(g => g.Key))
            {
                var legs = group.ToList();
                // どちらがどちらの内側にあるかで比べる（判断できるだけの量があるとき）
                var told = legs.Where(l => l.Points.Length >= MinPoints).ToList();
                var inner = told.Where(a => !told.Any(b => b != a && a.Outside(b))).ToList();
                ranked.AddRange(inner.OrderByDescending(l => l.Score).Concat(legs.Except(inner).OrderByDescending(l => l.Score))
                    .Select(l => l.Renderer));
            }
            return ranked;
        }

        /// <summary>
        /// アバターが `r` を表示するか: 有効で、それ自身と `root` までの親がアクティブであること（プレハブアセットでも
        /// 判定する。プレハブアセットではヒエラルキー上で何もアクティブとみなされない）。
        /// </summary>
        static bool Shown(Renderer r, Transform root)
        {
            if (!r.enabled) return false;
            for (var t = r.transform; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t == root) break;
            }
            return true;
        }

        /// <summary>頂点は、太ももまたは下腿のボーンがウェイトの少なくともこれだけを持つとき、そこに属する。</summary>
        internal const float OnTheLeg = 0.5f;
        /// <summary>メッシュごとに標本にする脚の頂点数（内側と外側を見分けるのに足りる数）。</summary>
        const int Samples = 3000;
        /// <summary>メッシュを比べるのに要る標本点（およびほかのメッシュと対応づいた点）の最少数。</summary>
        const int MinPoints = 20;
        /// <summary>点がボーンからこれだけ（m）遠くにあるとき、ほかのメッシュの外側にあるとみなす。</summary>
        const float OutsideBy = 0.0001f;
        /// <summary>メッシュは、ほかのメッシュと対応づいた点のうちこの割合が外側にあるとき、その外側にあるとみなす。
        /// </summary>
        const float OutsideShare = 0.6f;
        /// <summary>2 つのメッシュの点がこれだけ（m）近ければ、脚の同じ場所とみなす。</summary>
        const float SamePlace = 0.02f;

        /// <summary>レンダラーの脚の頂点: その位置と、脚のボーンからの距離。</summary>
        class Leg
        {
            public SkinnedMeshRenderer Renderer;
            public int Score;
            /// <summary>太ももまたは下腿が持つ頂点のワールド位置（標本）。</summary>
            public Vector3[] Points;
            /// <summary>それらがぶら下がるボーン（股関節から膝、膝から足首）からの距離（m）。</summary>
            public float[] Radius;
            Dictionary<Vector3Int, List<int>> _grid;

            enum Part
            {
                Other,
                Thigh,
                BelowKnee,
            }

            public static Leg Of(SkinnedMeshRenderer smr, Func<Transform, Transform> avatarBone, Transform[] upper,
                Transform[] lower, Transform[] feet)
            {
                var mesh = smr.sharedMesh;
                if (mesh == null) return null;
                // ボーンごと: どちらの脚（側 0 は左、1 は右）のどの部分か
                var boneOf = smr.bones.Select(b =>
                {
                    var a = avatarBone(b);
                    if (a == null) return (part: Part.Other, side: 0);
                    for (var s = 0; s < 2; s++)
                        if (lower[s] != null && a.IsChildOf(lower[s])) return (part: Part.BelowKnee, side: s);
                    for (var s = 0; s < 2; s++)
                        if (upper[s] != null && a.IsChildOf(upper[s])) return (part: Part.Thigh, side: s);
                    return (part: Part.Other, side: 0);
                }).ToArray();
                var bpv = mesh.GetBonesPerVertex();
                var weights = mesh.GetAllBoneWeights();
                var where = new (Part part, int side)[bpv.Length];
                int thigh = 0, below = 0, w = 0;
                for (var v = 0; v < bpv.Length; v++)
                {
                    float t = 0, b = 0;
                    var side = 0;
                    for (var k = 0; k < bpv[v]; k++, w++)
                    {
                        var bi = weights[w].boneIndex;
                        if (bi >= boneOf.Length) continue;
                        var (part, s) = boneOf[bi];
                        if (part == Part.Thigh) t += weights[w].weight;
                        else if (part == Part.BelowKnee) b += weights[w].weight;
                        if (part != Part.Other) side = s;
                    }
                    if (t >= OnTheLeg) { thigh++; where[v] = (Part.Thigh, side); }
                    if (b >= OnTheLeg) { below++; where[v] = (Part.BelowKnee, side); }
                }
                var score = Mathf.Min(thigh, below);
                if (score <= 0) return null;

                var world = MeshMath.WorldPositions(smr);
                var legVerts = Enumerable.Range(0, world.Length).Where(v => where[v].part != Part.Other).ToList();
                var step = Mathf.Max(1, legVerts.Count / Samples);
                var points = new List<Vector3>();
                var radius = new List<float>();
                for (var i = 0; i < legVerts.Count; i += step)
                {
                    var v = legVerts[i];
                    var (p, s) = where[v];
                    var a = p == Part.Thigh ? upper[s] : lower[s];
                    var e = p == Part.Thigh ? lower[s] : feet[s];
                    if (a == null || e == null) continue;
                    points.Add(world[v]);
                    radius.Add(MeshMath.DistanceToSegment(world[v], a.position, e.position));
                }
                return new Leg { Renderer = smr, Score = score, Points = points.ToArray(), Radius = radius.ToArray() };
            }

            /// <summary>2 つが脚の同じ部分を覆うところで、このメッシュが `other` の外側にあるか。</summary>
            public bool Outside(Leg other)
            {
                int matched = 0, outside = 0;
                for (var i = 0; i < Points.Length; i++)
                {
                    var j = other.Nearest(Points[i]);
                    if (j < 0) continue;
                    matched++;
                    if (Radius[i] - other.Radius[j] > OutsideBy) outside++;
                }
                return matched >= MinPoints && outside >= matched * OutsideShare;
            }

            int Nearest(Vector3 p)
            {
                if (_grid == null)
                {
                    _grid = new Dictionary<Vector3Int, List<int>>();
                    for (var i = 0; i < Points.Length; i++)
                    {
                        var k = Vector3Int.FloorToInt(Points[i] / SamePlace);
                        if (!_grid.TryGetValue(k, out var l)) _grid[k] = l = new List<int>();
                        l.Add(i);
                    }
                }
                var key = Vector3Int.FloorToInt(p / SamePlace);
                var best = -1;
                var bd = SamePlace * SamePlace;
                for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dz = -1; dz <= 1; dz++)
                            if (_grid.TryGetValue(key + new Vector3Int(dx, dy, dz), out var l))
                                foreach (var i in l)
                                {
                                    var d = (Points[i] - p).sqrMagnitude;
                                    if (d < bd) (bd, best) = (d, i);
                                }
                return best;
            }
        }
    }
}
