// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// 衣装の下に着ている物: 腰と脚の肌に沿うほかのメッシュ（素体のショーツ・パンティストッキング・下着）。
    /// 衣装はその上に、その高さだけ肌から離して重ねる。肌から離れて立つ服（スカート）はそのままにし、
    /// 衣装はその下に入る。
    /// </summary>
    public static class UnderLayers
    {
        /// <summary>層が肌の上に着ていると数えられる、肌からの最大の距離。</summary>
        const float Reach = 0.010f; // m
        /// <summary>選んだ層の肌からの高さを測る最大の距離（フリル、お尻の割れ目をまたぐ部分）。</summary>
        const float HeightReach = 0.025f; // m
        /// <summary>
        /// 層の頂点を最も近い肌の頂点に対応させる距離（m）（とそれを探す格子のセル）:
        /// 腰と太ももでの体のメッシュの頂点の間隔くらい。
        /// </summary>
        const float SkinMatch = 0.015f;
        /// <summary>体の覆われ具合を着ているメッシュへ移すときの同じ値（取りこぼせないので少し広い）。</summary>
        const float CoverMatch = 0.02f;
        /// <summary>肌からの光線を当てて調べる、層の三角形の格子のセル（m）。</summary>
        const float TriangleCell = 0.01f;
        /// <summary>
        /// これより多くの格子のセルにまたがる三角形（遠くに外れた頂点がある）は格子に入れない: 高さは肌から
        /// 数センチ以内で測り、そこではそのような三角形は着ている物の一部ではない。
        /// </summary>
        const int MaxCellsPerTriangle = 4096;

        static bool TooBig(Vector3Int lo, Vector3Int hi) =>
            (long)(hi.x - lo.x + 1) * (hi.y - lo.y + 1) * (hi.z - lo.z + 1) > MaxCellsPerTriangle;

        public struct Candidate
        {
            public SkinnedMeshRenderer Renderer;
            /// <summary>腰か脚の肌に沿う: 既定で衣装の下に着る。</summary>
            public bool Under;
            /// <summary>太ももをどこまで下りるか: 股関節で 0、膝で 1（95 パーセンタイル）。</summary>
            public float Down;
            /// <summary>腰より下で脚の上にある頂点のうち、肌から 10 mm 以内にある割合。</summary>
            public float Clinging;
            /// <summary>
            /// スパッツ（またはレギンス）: 太ももを下まで肌に密着する。タイツとは一緒に着ないので、見た目がどうなろうと
            /// 無い物として扱う（衣装の下にも上にもしない）。
            /// </summary>
            public bool Spats;
        }

        /// <summary>下着は肌からこの距離（中央値）にある。ショーツやブルマーはもっと離れる。</summary>
        const float UnderwearHeight = 0.004f; // m
        const int MinOnSkin = 30;
        /// <summary>着ている物の下の肌を、その外側何周まで残すか（UnderPresent）。</summary>
        const int PresentRings = 3;
        /// <summary>
        /// 下着は太ももの上端（膝までの道のりのこの割合）で終わる。ドロワーズやブルマーはもっと下まであり、
        /// タイツの上に着る。
        /// </summary>
        const float DrawersDown = 0.22f;
        /// <summary>スパッツは全体が密着する（ほぼすべてが 10 mm 以内）。ブルマーやドロワーズは膨らむ。</summary>
        const float SpatsClinging = 0.96f;

        /// <summary>
        /// 一部が腰か脚の肌の上にあるメッシュ。Under（既定で衣装の下に着る）= 下着かパンティストッキング:
        /// 腰より下の部分の少なくとも半分が肌の上にある（スカートはウエストバンドでしか触れない）、腰を覆う
        /// （靴下は覆わない）、薄い（肌からの高さの中央値が 4 mm 以下。ショーツはもっと離れる）、
        /// 太ももの上端で終わる（もっと下まであるドロワーズはタイツの上に着る）。
        /// 腰より下だけを数えるので、ブラとショーツが一体のメッシュも含まれる。
        /// </summary>
        public static List<Candidate> Detect(AvatarBody body)
        {
            var result = new List<Candidate>();
            var (bp, bn) = World(body.Renderer, body.Normals);
            var grid = Grid(bp, SkinMatch);
            var anim = body.Animator;
            Vector3 P(HumanBodyBones b) => anim.GetBoneTransform(b).position;
            var hipMid = (P(HumanBodyBones.LeftUpperLeg) + P(HumanBodyBones.RightUpperLeg)) * 0.5f;
            var up = (hipMid - (P(HumanBodyBones.LeftFoot) + P(HumanBodyBones.RightFoot)) * 0.5f).normalized;
            var chest = anim.GetBoneTransform(HumanBodyBones.Chest) ?? anim.GetBoneTransform(HumanBodyBones.Spine);
            var waist = Vector3.Dot(chest.position - hipMid, up) * 0.5f; // 股関節より上
            foreach (var smr in body.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == body.Renderer || smr.sharedMesh == null || !smr.enabled || !smr.gameObject.activeInHierarchy) continue;
                // （前に作った衣装は置き換える物で、新しい衣装の下には着ない）
                if (GarmentGenerator.IsMade(smr)) continue;
                var (lp, _) = World(smr, null);
                var below = 0;
                var heights = new List<float>();
                var hips = 0;
                var overLegs = 0;
                foreach (var u in lp)
                {
                    if (Vector3.Dot(u - hipMid, up) > waist) continue;
                    below++;
                    var i = Nearest(grid, bp, u, SkinMatch);
                    if (i < 0 || body.LegWeight[i] < 0.3f) continue;
                    overLegs++;
                    var h = Vector3.Dot(u - bp[i], bn[i]);
                    if (h <= 0 || h > Reach) continue;
                    heights.Add(h);
                    if (Vector3.Dot(bp[i] - hipMid, up) > 0) hips++;
                }
                if (heights.Count < MinOnSkin) continue;
                heights.Sort();
                var down = DownTheThighs(anim, lp, bp, grid);
                var clinging = heights.Count / (float)Mathf.Max(overLegs, 1);
                var onHips = heights.Count >= below * 0.5f && hips >= MinOnSkin;
                var spats = onHips && down >= DrawersDown && clinging >= SpatsClinging;
                result.Add(new Candidate
                {
                    Renderer = smr,
                    Down = down,
                    Clinging = clinging,
                    Spats = spats,
                    Under = onHips && heights[heights.Count / 2] <= UnderwearHeight && down < DrawersDown,
                });
            }
            return result;
        }

        /// <summary>
        /// 層が太ももをどこまで下りるか（脚の上で肌から 25 mm 以内にある頂点）: それぞれの太ももに沿って
        /// 股関節（0）から膝（1）までで、95 パーセンタイル。
        /// </summary>
        static float DownTheThighs(Animator anim, Vector3[] lp, Vector3[] bp, Dictionary<Vector3Int, List<int>> grid)
        {
            var legs = new[]
            {
                (anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg).position, anim.GetBoneTransform(HumanBodyBones.LeftLowerLeg).position),
                (anim.GetBoneTransform(HumanBodyBones.RightUpperLeg).position, anim.GetBoneTransform(HumanBodyBones.RightLowerLeg).position),
            };
            var along = new List<float>();
            foreach (var u in lp)
            {
                var i = Nearest(grid, bp, u, SkinMatch);
                if (i < 0 || (u - bp[i]).sqrMagnitude > HeightReach * HeightReach) continue;
                // 近い方の太ももの軸
                var (hip, knee) = legs.OrderBy(l => MeshMath.DistanceToSegment(u, l.Item1, l.Item2)).First();
                var axis = knee - hip;
                var t = Vector3.Dot(u - hip, axis) / axis.sqrMagnitude;
                if (t > 0) along.Add(t);
            }
            if (along.Count == 0) return 0f;
            along.Sort();
            return along[(int)(along.Count * 0.95f)];
        }

        /// <summary>点が入る、層の三角形の格子のセル。</summary>
        static Vector3Int TriangleKey(Vector3 p) => Vector3Int.FloorToInt(p / TriangleCell);

        /// <summary>
        /// 層の三角形（ワールド空間、今描かれている形）と、それぞれの範囲が覆うセルの格子
        /// （格子に対して大きすぎる三角形は入れない: TooBig）。
        /// </summary>
        static (List<(Vector3 a, Vector3 b, Vector3 c)> tris, Dictionary<Vector3Int, List<int>> grid) TriangleGrid(
            List<SkinnedMeshRenderer> layers)
        {
            var tris = new List<(Vector3 a, Vector3 b, Vector3 c)>();
            var grid = new Dictionary<Vector3Int, List<int>>();
            foreach (var l in layers)
            {
                var (lp, _) = World(l, null);
                var t = l.sharedMesh.triangles;
                for (var f = 0; f < t.Length; f += 3)
                {
                    var id = tris.Count;
                    tris.Add((lp[t[f]], lp[t[f + 1]], lp[t[f + 2]]));
                    var lo = TriangleKey(Vector3.Min(Vector3.Min(lp[t[f]], lp[t[f + 1]]), lp[t[f + 2]]));
                    var hi = TriangleKey(Vector3.Max(Vector3.Max(lp[t[f]], lp[t[f + 1]]), lp[t[f + 2]]));
                    if (TooBig(lo, hi)) continue;
                    for (var x = lo.x; x <= hi.x; x++)
                        for (var y = lo.y; y <= hi.y; y++)
                            for (var z = lo.z; z <= hi.z; z++)
                            {
                                var k = new Vector3Int(x, y, z);
                                if (!grid.TryGetValue(k, out var g)) grid[k] = g = new List<int>();
                                g.Add(id);
                            }
                }
            }
            return (tris, grid);
        }

        /// <summary>
        /// 体の頂点ごと: 法線に沿った層の肌からの高さ（m）（層が無い所は 0）。衣装が段にならずに坂で層を
        /// 乗り越えるよう、層の縁の少し外まで滑らかに広げる。
        /// </summary>
        /// <param name="covered">体の頂点ごと: 本当に層が上にある所で 1、縁の内側の最後の 1〜2 頂点で 0 へ
        /// なだらかに下がる（そこでは衣装が層のウェイトを使う）</param>
        /// <param name="present">体の頂点ごと: 着ている物が上か近くにある（本当にある所より
        /// PresentRings 周広い）</param>
        /// <param name="core">体の頂点ごと: 着ている物が本当に上にある（広げた周を除いた present）</param>
        public static float[] Heights(AvatarBody body, IEnumerable<SkinnedMeshRenderer> layers, out float[] covered,
            out bool[] present, out bool[] core)
        {
            var n = body.Vertices.Length;
            var height = new float[n];
            covered = new float[n];
            present = new bool[n];
            core = new bool[n];
            var list = layers?.Where(l => l != null && l.sharedMesh != null).ToList() ?? new List<SkinnedMeshRenderer>();
            if (list.Count == 0) return height;
            var (bp, bn) = World(body.Renderer, body.Normals);

            var (tris, grid) = TriangleGrid(list);
            for (var v = 0; v < n; v++)
            {
                var p = bp[v];
                var d = bn[v];
                var lo = TriangleKey(Vector3.Min(p, p + d * HeightReach));
                var hi = TriangleKey(Vector3.Max(p, p + d * HeightReach));
                var seen = new HashSet<int>();
                for (var x = lo.x; x <= hi.x; x++)
                    for (var y = lo.y; y <= hi.y; y++)
                        for (var z = lo.z; z <= hi.z; z++)
                        {
                            if (!grid.TryGetValue(new Vector3Int(x, y, z), out var g)) continue;
                            foreach (var id in g)
                            {
                                if (!seen.Add(id)) continue;
                                var (a, b, c) = tris[id];
                                if (Ray(p, d, a, b, c, out var t) && t <= HeightReach) height[v] = Mathf.Max(height[v], t);
                            }
                        }
            }

            // 光線は肌から立つ薄い部分（フリルのひだ）の間をすり抜けるので、層の各頂点も、
            // 最も近い肌の頂点を自分の高さまで上げる
            var bodyGrid = Grid(bp, SkinMatch);
            foreach (var l in list)
            {
                var (lp, _) = World(l, null);
                foreach (var u in lp)
                {
                    var i = Nearest(bodyGrid, bp, u, SkinMatch);
                    if (i < 0) continue;
                    var h = Vector3.Dot(u - bp[i], bn[i]);
                    if (h > 0 && h <= HeightReach) height[i] = Mathf.Max(height[i], h);
                }
            }

            // 縁の外へ滑らかに広げる: 「少なくとも隣の平均」を数回繰り返す
            var nb = MeshMath.Neighbours(body.Triangles, n);
            // 分かれた頂点（UV の継ぎ目）は位置が同じなので、同じ高さにする
            var weld = new Dictionary<Vector3, List<int>>();
            for (var i = 0; i < n; i++)
            {
                if (!weld.TryGetValue(body.Vertices[i], out var w)) weld[body.Vertices[i]] = w = new List<int>();
                w.Add(i);
            }
            var cov = new float[n];
            for (var i = 0; i < n; i++) cov[i] = height[i] > 0 ? 1f : 0f;
            var near = cov.Select(c => c > 0).ToArray();
            // 3 周広げる: 腰が曲がると（座ると）着ている物は肌の上を滑り、その上の衣装（そこには裏地が無い）も
            // 一緒に動く。1 周だと、消した肌が縁の後ろから透けて見える
            for (var r = 0; r < PresentRings; r++)
            {
                var grown = (bool[])near.Clone();
                for (var i = 0; i < n; i++)
                    if (!near[i] && nb[i].Any(j => near[j])) grown[i] = true;
                near = grown;
                foreach (var w in weld.Values)
                {
                    var any = w.Any(i => near[i]);
                    foreach (var i in w) near[i] = any;
                }
            }
            present = near;
            core = cov.Select(c => c > 0).ToArray();
            for (var round = 0; round < 2; round++)
            {
                var next = (float[])cov.Clone();
                for (var i = 0; i < n; i++)
                    if (nb[i].Count > 0)
                        next[i] = Mathf.Min(cov[i], nb[i].Average(j => cov[j]));
                foreach (var w in weld.Values)
                {
                    var m = w.Min(i => next[i]);
                    foreach (var i in w) next[i] = m;
                }
                cov = next;
            }
            covered = cov;
            for (var round = 0; round < 4; round++)
            {
                var next = (float[])height.Clone();
                for (var i = 0; i < n; i++)
                    if (nb[i].Count > 0)
                        next[i] = Mathf.Max(height[i], nb[i].Average(j => height[j]));
                foreach (var w in weld.Values)
                {
                    var m = w.Max(i => next[i]);
                    foreach (var i in w) next[i] = m;
                }
                height = next;
            }
            return height;
        }

        /// <summary>
        /// 体の頂点ごと: `layers` のうち最も近い物が肌からどれだけ上にあるか（m、法線に沿って）。10 mm 以内に
        /// 無ければ 0。衣装の上に着る物（靴下・靴・ショーツ）用で、衣装はその下に収まらなければならない。
        /// 衣装がちょうど縁で外へはみ出さないよう、縁の 1 周外まで広げる。
        /// </summary>
        public static float[] Ceiling(AvatarBody body, IEnumerable<SkinnedMeshRenderer> layers)
        {
            var n = body.Vertices.Length;
            var ceiling = new float[n];
            var list = layers?.Where(l => l != null && l.sharedMesh != null).ToList() ?? new List<SkinnedMeshRenderer>();
            if (list.Count == 0) return ceiling;
            var (bp, bn) = World(body.Renderer, body.Normals);
            var (tris, grid) = TriangleGrid(list);
            for (var v = 0; v < n; v++)
            {
                var p = bp[v];
                var d = bn[v];
                var lo = TriangleKey(Vector3.Min(p, p + d * Reach));
                var hi = TriangleKey(Vector3.Max(p, p + d * Reach));
                var best = float.MaxValue;
                for (var x = lo.x; x <= hi.x; x++)
                    for (var y = lo.y; y <= hi.y; y++)
                        for (var z = lo.z; z <= hi.z; z++)
                        {
                            if (!grid.TryGetValue(new Vector3Int(x, y, z), out var g)) continue;
                            foreach (var id in g)
                            {
                                var (a, b, c) = tris[id];
                                if (Ray(p, d, a, b, c, out var t) && t <= Reach) best = Mathf.Min(best, t);
                            }
                        }
                if (best < float.MaxValue) ceiling[v] = best;
            }
            // 隣の最も低い天井で、1 周外へ広げる
            var spread = (float[])ceiling.Clone();
            var bt = body.Triangles;
            for (var f = 0; f < bt.Length; f += 3)
                for (var k = 0; k < 3; k++)
                {
                    int a = bt[f + k], b = bt[f + (k + 1) % 3];
                    foreach (var (x, y) in new[] { (a, b), (b, a) })
                        if (ceiling[y] > 0 && (spread[x] == 0 || ceiling[y] < spread[x])) spread[x] = ceiling[y];
                }
            return spread;
        }

        /// <summary>SkinRemover が扱う形にした、着ているメッシュ（アバターとボーンは体と同じ）。</summary>
        public static AvatarBody AsBody(AvatarBody body, SkinnedMeshRenderer layer)
        {
            var mesh = layer.sharedMesh;
            var tris = new List<int>();
            for (var s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles)
                    tris.AddRange(mesh.GetTriangles(s));
            return new AvatarBody
            {
                Root = body.Root,
                Animator = body.Animator,
                Renderer = layer,
                Mesh = mesh,
                Vertices = mesh.vertices,
                Triangles = tris.ToArray(),
                MetersPerUnit = body.MetersPerUnit,
            };
        }

        /// <summary>
        /// 着ているメッシュの頂点ごと: 衣装の縁からどれだけ内側にあるか（m、負 = 覆われていない）。
        /// 2 cm 以内で最も近い体の頂点から取る。
        /// </summary>
        public static float[] CoveredFromBody(AvatarBody body, float[] bodyCovered, SkinnedMeshRenderer layer)
        {
            var (bp, _) = World(body.Renderer, null);
            var grid = Grid(bp, CoverMatch);
            var (lp, _) = World(layer, null);
            var result = new float[lp.Length];
            for (var i = 0; i < lp.Length; i++)
            {
                var j = Nearest(grid, bp, lp[i], CoverMatch);
                result[i] = j < 0 ? -1f : bodyCovered[j];
            }
            return result;
        }

        /// <summary>
        /// ワールド空間の各点（`normals` の向きの肌の上）に最も近い、着ているメッシュの表面の点でのボーンウェイト
        /// （体のボーンの番号で）。1 cm 以内に無いか、そのボーンが体のものでなければ null。着ている物は自分の
        /// ウェイトで動くので、その上の衣装は同じように動くときだけそれに付いていく。
        /// </summary>
        public static Dictionary<int, float>[] WeightsAt(AvatarBody body, IEnumerable<SkinnedMeshRenderer> layers,
            Vector3[] points, Vector3[] normals)
        {
            var result = new Dictionary<int, float>[points.Length];
            foreach (var layer in layers)
            {
                var mesh = layer.sharedMesh;
                var (lp, _) = World(layer, null);
                var t = mesh.triangles;
                var bpv = mesh.GetBonesPerVertex();
                if (bpv.Length < lp.Length) continue;
                var all = mesh.GetAllBoneWeights();
                var start = new int[bpv.Length];
                for (int v = 0, a = 0; v < bpv.Length; a += bpv[v], v++) start[v] = a;
                var map = BoneMap(body, layer);
                var grid = new Dictionary<Vector3Int, List<int>>();
                Vector3Int Key(Vector3 q) => Vector3Int.FloorToInt(q / Reach);
                for (var f = 0; f < t.Length; f += 3)
                {
                    var k = Key((lp[t[f]] + lp[t[f + 1]] + lp[t[f + 2]]) / 3f);
                    if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>();
                    l.Add(f);
                }
                var bestD = new float[points.Length];
                for (var i = 0; i < points.Length; i++)
                {
                    if (points[i].x == float.PositiveInfinity) continue;
                    var k = Key(points[i]);
                    var best = Reach * Reach;
                    var bestF = -1;
                    var bestQ = Vector3.zero;
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dz = -1; dz <= 1; dz++)
                            {
                                if (!grid.TryGetValue(k + new Vector3Int(dx, dy, dz), out var l)) continue;
                                foreach (var f in l)
                                {
                                    var q = MeshMath.ClosestOnTriangle(points[i], lp[t[f]], lp[t[f + 1]], lp[t[f + 2]]);
                                    var d = (q - points[i]).sqrMagnitude;
                                    if (d >= best) continue;
                                    // この肌の上に着ている物だけ: 同じ向きで、肌の後ろにない（くぼみでは、最も近い部分が
                                    // 向かい合う肌の上の物で、別のボーンで動くことがある）
                                    var fn = Vector3.Cross(lp[t[f + 1]] - lp[t[f]], lp[t[f + 2]] - lp[t[f]]);
                                    if (Vector3.Dot(fn, normals[i]) < 0.3f * fn.magnitude ||
                                        Vector3.Dot(q - points[i], normals[i]) < -0.002f) continue;
                                    best = d;
                                    bestF = f;
                                    bestQ = q;
                                }
                            }
                    if (bestF < 0 || (result[i] != null && best >= bestD[i])) continue;
                    var bary = MeshMath.Barycentric(bestQ, lp[t[bestF]], lp[t[bestF + 1]], lp[t[bestF + 2]]);
                    var acc = new Dictionary<int, float>();
                    var ok = true;
                    for (var c = 0; c < 3 && ok; c++)
                    {
                        var v = t[bestF + c];
                        for (var j = 0; j < bpv[v]; j++)
                        {
                            var w = all[start[v] + j];
                            var bi = w.boneIndex < map.Length ? map[w.boneIndex] : -1;
                            if (bi < 0)
                            {
                                ok = false;
                                break;
                            }
                            acc.TryGetValue(bi, out var cur);
                            acc[bi] = cur + w.weight * bary[c];
                        }
                    }
                    if (!ok) continue;
                    result[i] = acc;
                    bestD[i] = best;
                }
            }
            return result;
        }

        /// <summary>
        /// 層のボーンを体のボーン（または体のボーンのうち最も近い祖先）で表したもの。無ければ -1。衣装は
        /// アーマチュアの複製を持つことが多く、アバターのビルド時にアバターのものへ統合される: その場合ボーンは
        /// 名前で対応させる。
        /// </summary>
        public static int[] BoneMap(AvatarBody body, SkinnedMeshRenderer layer)
        {
            var bodyBones = body.Renderer.bones;
            var byName = new Dictionary<string, int>();
            for (var i = 0; i < bodyBones.Length; i++)
                if (bodyBones[i] != null && !byName.ContainsKey(bodyBones[i].name)) byName[bodyBones[i].name] = i;
            return layer.bones.Select(b =>
            {
                for (var cur = b; cur != null; cur = cur.parent)
                {
                    var i = System.Array.IndexOf(bodyBones, cur);
                    if (i >= 0) return i;
                    if (byName.TryGetValue(cur.name, out i)) return i;
                }
                return -1;
            }).ToArray();
        }

        /// <summary>各頂点の体のボーンの番号でのウェイト。体のものでないボーンがあれば null。</summary>
        public static Dictionary<int, float>[] VertexWeights(AvatarBody body, SkinnedMeshRenderer layer)
        {
            var mesh = layer.sharedMesh;
            var result = new Dictionary<int, float>[mesh.vertexCount];
            var bpv = mesh.GetBonesPerVertex();
            if (bpv.Length < result.Length) return result;
            var all = mesh.GetAllBoneWeights();
            var map = BoneMap(body, layer);
            for (int v = 0, a = 0; v < result.Length; a += bpv[v], v++)
            {
                var acc = new Dictionary<int, float>();
                for (var j = 0; j < bpv[v] && acc != null; j++)
                {
                    var w = all[a + j];
                    var bi = w.boneIndex < map.Length ? map[w.boneIndex] : -1;
                    if (bi < 0) acc = null;
                    else
                    {
                        acc.TryGetValue(bi, out var cur);
                        acc[bi] = cur + w.weight;
                    }
                }
                result[v] = acc;
            }
            return result;
        }

        static (Vector3[] pos, Vector3[] nrm) World(SkinnedMeshRenderer r, Vector3[] normals)
        {
            var local = MeshMath.SkinnedPositions(r);
            var pos = local.Select(p => r.transform.TransformPoint(p)).ToArray();
            var nrm = normals?.Select(v => r.transform.TransformDirection(v).normalized).ToArray();
            return (pos, nrm);
        }

        static Dictionary<Vector3Int, List<int>> Grid(Vector3[] p, float cell)
        {
            var g = new Dictionary<Vector3Int, List<int>>();
            for (var i = 0; i < p.Length; i++)
            {
                var k = Vector3Int.FloorToInt(p[i] / cell);
                if (!g.TryGetValue(k, out var l)) g[k] = l = new List<int>();
                l.Add(i);
            }
            return g;
        }

        static int Nearest(Dictionary<Vector3Int, List<int>> g, Vector3[] p, Vector3 q, float cell)
        {
            var k = Vector3Int.FloorToInt(q / cell);
            var best = -1;
            var bestD = cell * cell;
            for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    for (var z = -1; z <= 1; z++)
                    {
                        if (!g.TryGetValue(k + new Vector3Int(x, y, z), out var l)) continue;
                        foreach (var i in l)
                        {
                            var d = (p[i] - q).sqrMagnitude;
                            if (d < bestD)
                            {
                                bestD = d;
                                best = i;
                            }
                        }
                    }
            return best;
        }

        /// <summary>Möller–Trumbore 法、両面。t は単位方向に沿った距離。</summary>
        static bool Ray(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0;
            var e1 = b - a;
            var e2 = c - a;
            var h = Vector3.Cross(d, e2);
            var det = Vector3.Dot(e1, h);
            if (Mathf.Abs(det) < 1e-14f) return false;
            var inv = 1f / det;
            var s = o - a;
            var u = Vector3.Dot(s, h) * inv;
            if (u < 0 || u > 1) return false;
            var q = Vector3.Cross(s, e1);
            var v = Vector3.Dot(d, q) * inv;
            if (v < 0 || u + v > 1) return false;
            t = Vector3.Dot(e2, q) * inv;
            return t > 0;
        }
    }
}
