// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// 衣装が隠す肌のまわりに、AAO の "Remove Mesh By Box" の箱を合わせる: 不透明な衣装の下と、
    /// 肌の写しが肌の代わりになる透ける衣装の下（GarmentGenerator）。衣装の縁から少なくとも `margin` 内側にある
    /// 頂点だけを使うので、裾の下の肌は残り、縁にすき間が見えることはない。箱は脚のボーンに沿った（および骨盤を横切る）
    /// 薄切り。残すべき頂点も含んでしまう薄切りは分割し、分割しても解決しなければ捨てる。
    /// </summary>
    public static class SkinRemover
    {
        public struct Box
        {
            public Vector3 Center;
            public Vector3 Size;
            public Quaternion Rotation;
        }

        const int SlicesPerSegment = 10;
        const int MaxSplitDepth = 3;

        public static List<Box> FitBoxes(AvatarBody body, float[] covered, float marginMeters)
        {
            var smr = body.Renderer;
            var local = MeshMath.SkinnedPositions(smr);
            var remove = new bool[local.Length];
            for (var i = 0; i < local.Length; i++) remove[i] = covered[i] >= marginMeters;
            // 余白の帯から衣装の下深くまで届く細長い三角形は棘として残り、関節が折れたときに突き出る: 深い角を持ち、
            // すべての角が少なくとも余白の 4 分の 1 だけ内側にある三角形も取り除く
            var tris = body.Triangles;
            var extra = new List<int>();
            for (var t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if ((remove[a] || remove[b] || remove[c]) &&
                    Mathf.Min(covered[a], Mathf.Min(covered[b], covered[c])) >= marginMeters * 0.25f)
                    extra.AddRange(new[] { a, b, c });
            }
            foreach (var i in extra) remove[i] = true;

            var anim = body.Animator;
            Vector3 L(HumanBodyBones b)
            {
                var t = anim.GetBoneTransform(b);
                return t == null ? Vector3.zero : smr.transform.InverseTransformPoint(t.position);
            }
            var hipL = L(HumanBodyBones.LeftUpperLeg);
            var hipR = L(HumanBodyBones.RightUpperLeg);
            var hipMid = (hipL + hipR) * 0.5f;
            var ankMid = (L(HumanBodyBones.LeftFoot) + L(HumanBodyBones.RightFoot)) * 0.5f;
            var up = (hipMid - ankMid).normalized;
            var lateral = Vector3.ProjectOnPlane(hipL - hipR, up).normalized;

            var keep = Enumerable.Range(0, local.Length).Where(i => !remove[i]).Select(i => local[i]).ToList();
            var boxes = new List<Box>();

            // 骨盤: 股関節より上を水平に薄切りにする
            var pelvis = Enumerable.Range(0, local.Length)
                .Where(i => remove[i] && Vector3.Dot(local[i] - hipMid, up) > 0).Select(i => local[i]).ToList();
            AddSlices(boxes, pelvis, keep, up, lateral, SlicesPerSegment);

            foreach (var side in new[] { 1, -1 })
            {
                var chain = new List<Vector3>
                {
                    side > 0 ? hipL : hipR,
                    L(side > 0 ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg),
                    L(side > 0 ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot),
                };
                var pts = Enumerable.Range(0, local.Length)
                    .Where(i => remove[i] && Vector3.Dot(local[i] - hipMid, up) <= 0
                                && Mathf.Sign(Vector3.Dot(local[i] - hipMid, lateral)) == side)
                    .Select(i => local[i]).ToList();
                if (pts.Count == 0) continue;
                // 足の区間は、足首より前で覆われた最も遠い点で終わる
                var ankle = chain[2];
                var fwd = Vector3.ProjectOnPlane(pts.Where(p => Vector3.Dot(p - ankle, up) < 0)
                    .Select(p => p - ankle).Aggregate(Vector3.zero, (a, b) => a + b), up).normalized;
                if (fwd.sqrMagnitude > 0.5f)
                    chain.Add(pts.OrderByDescending(p => Vector3.Dot(p - ankle, fwd)).First());

                var groups = new List<Vector3>[chain.Count - 1];
                for (var s = 0; s < groups.Length; s++) groups[s] = new List<Vector3>();
                foreach (var p in pts) groups[NearestSegment(chain, p)].Add(p);
                for (var s = 0; s < groups.Length; s++)
                    AddSlices(boxes, groups[s], keep, (chain[s] - chain[s + 1]).normalized, lateral, SlicesPerSegment);
            }
            // （レンダラーのローカル単位: そのトランスフォームの拡大縮小でメートルになる）
            var scale = smr.transform.lossyScale;
            var metersPerLocal = Mathf.Max((Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f, 1e-6f);
            Refine(boxes, body.Triangles, local, remove, keep, RefineCell / metersPerLocal);
            return boxes;
        }

        /// <summary>Refine の小さな箱が残った三角形を覆うときのセル（m）。</summary>
        const float RefineCell = 0.02f;

        /// <summary>
        /// 残すべき肌も捕らえてしまう薄切りは捨てるので、除去に穴が残る（衣装の下に残った肌は、
        /// 脚を曲げたときに突き抜けることがある）。取り除くべきなのにまだ箱の中にない三角形を小さな箱で覆う:
        /// 2 cm のセルごとに 1 つ、セルの箱では残すべき肌を捕らえてしまうところでは三角形ごとに 1 つ。
        /// </summary>
        /// <param name="cell">レンダラーのローカル単位での RefineCell（そのトランスフォームが拡大縮小することがある）
        /// </param>
        static void Refine(List<Box> boxes, int[] tris, Vector3[] local, bool[] remove, List<Vector3> keep, float cell)
        {
            var inBox = local.Select(p => boxes.Any(b => Contains(b, p))).ToArray();
            var missing = new List<int>();
            for (var t = 0; t < tris.Length / 3; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                if (remove[a] && remove[b] && remove[c] && !(inBox[a] && inBox[b] && inBox[c])) missing.Add(t);
            }
            Vector3Int Cell(Vector3 p) => new(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell),
                Mathf.FloorToInt(p.z / cell));
            foreach (var cluster in missing.GroupBy(t => Cell((local[tris[t * 3]] + local[tris[t * 3 + 1]] + local[tris[t * 3 + 2]]) / 3f)))
            {
                var verts = cluster.SelectMany(t => new[] { tris[t * 3], tris[t * 3 + 1], tris[t * 3 + 2] }).Select(i => local[i]).ToList();
                if (TryAabb(verts, keep, out var box))
                {
                    boxes.Add(box);
                    continue;
                }
                foreach (var t in cluster)
                    if (TryAabb(new List<Vector3> { local[tris[t * 3]], local[tris[t * 3 + 1]], local[tris[t * 3 + 2]] }, keep, out var small))
                        boxes.Add(small);
            }
        }

        static bool TryAabb(List<Vector3> pts, List<Vector3> keep, out Box box)
        {
            const float eps = 1e-5f;
            var min = pts.Aggregate(Vector3.Min) - Vector3.one * eps;
            var max = pts.Aggregate(Vector3.Max) + Vector3.one * eps;
            box = new Box { Center = (min + max) * 0.5f, Size = max - min, Rotation = Quaternion.identity };
            foreach (var p in keep)
                if (p.x >= min.x && p.x <= max.x && p.y >= min.y && p.y <= max.y && p.z >= min.z && p.z <= max.z)
                    return false;
            return true;
        }

        /// <summary>AAO の RemoveMeshInBox.BoundingBox.ContainsVertex と同じ判定。</summary>
        static bool Contains(Box b, Vector3 p)
        {
            var l = Quaternion.Inverse(b.Rotation) * (p - b.Center);
            var h = b.Size / 2;
            return Mathf.Abs(l.x) <= h.x && Mathf.Abs(l.y) <= h.y && Mathf.Abs(l.z) <= h.z;
        }

        static int NearestSegment(List<Vector3> chain, Vector3 p)
        {
            var best = 0;
            var bestD = float.MaxValue;
            for (var s = 0; s < chain.Count - 1; s++)
            {
                var a = chain[s];
                var ab = chain[s + 1] - a;
                var t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f));
                var d = (a + ab * t - p).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = s;
                }
            }
            return best;
        }

        /// <summary>点を `axis` に沿って薄切りにし、薄切りごとに箱を 1 つ足す（axis/lateral に向きを合わせる）。
        /// </summary>
        static void AddSlices(List<Box> boxes, List<Vector3> pts, List<Vector3> keep, Vector3 axis, Vector3 lateral,
            int slices)
        {
            if (pts.Count < 3) return;
            var x = Vector3.ProjectOnPlane(lateral, axis).normalized;
            if (x.sqrMagnitude < 0.5f) x = Vector3.Cross(axis, Vector3.forward).normalized;
            var rot = Quaternion.LookRotation(Vector3.Cross(x, axis), axis);
            var inv = Quaternion.Inverse(rot);
            var local = pts.Select(p => inv * p).ToList();
            var keepLocal = keep.Select(p => inv * p).ToList();
            var minY = local.Min(p => p.y);
            var maxY = local.Max(p => p.y);
            var step = (maxY - minY) / slices;
            for (var s = 0; s < slices; s++)
                AddSlice(boxes, local, keepLocal, rot, minY + step * s, minY + step * (s + 1), 0);
        }

        static void AddSlice(List<Box> boxes, List<Vector3> pts, List<Vector3> keep, Quaternion rot, float y0,
            float y1, int depth)
        {
            var inSlice = pts.Where(p => p.y >= y0 && p.y <= y1).ToList();
            if (inSlice.Count < 3) return;
            var min = inSlice.Aggregate(Vector3.Min);
            var max = inSlice.Aggregate(Vector3.Max);
            const float eps = 1e-5f;
            min -= Vector3.one * eps;
            max += Vector3.one * eps;
            var blocked = keep.Any(p => p.x > min.x && p.x < max.x && p.y > min.y && p.y < max.y && p.z > min.z && p.z < max.z);
            if (blocked)
            {
                if (depth >= MaxSplitDepth) return;
                var mid = (y0 + y1) * 0.5f;
                AddSlice(boxes, pts, keep, rot, y0, mid, depth + 1);
                AddSlice(boxes, pts, keep, rot, mid, y1, depth + 1);
                return;
            }
            boxes.Add(new Box { Center = rot * ((min + max) * 0.5f), Size = max - min, Rotation = rot });
        }
    }
}
