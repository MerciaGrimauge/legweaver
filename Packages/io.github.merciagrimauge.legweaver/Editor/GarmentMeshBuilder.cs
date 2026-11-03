// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace LegWeaver
{
    [Serializable]
    public class BlenderResult
    {
        public bool ok;
        public string error;
        public int vertexCount;
        public int triangleCount;
        public int bodyVertexCount;
        public int coveredCount;
        /// <summary>揺れる垂れ（ガーターのリボン）: （出力の頂点, 垂れの番号, 根元からの割合）の並び。無ければ null。</summary>
        public float[] swing;
    }

    /// <summary>
    /// Blender の出力から衣装のメッシュを組み立てる。各頂点は体の三角形上の点に結びつく。
    /// ボーンウェイトとブレンドシェイプはその点で体から補間し、バインドポーズは体のものを使うので、
    /// 衣装はその下の肌とまったく同じように変形する。
    /// </summary>
    public class GarmentMeshBuilder
    {
        public Mesh Mesh;
        /// <summary>体の頂点ごと: 衣装の縁から内側への距離（m）。覆われていなければ負。</summary>
        public float[] Covered;
        public List<string> SyncedBlendShapes = new();
        /// <summary>各サブメッシュが描く領域（裏地のサブメッシュがあればその後から）。</summary>
        public List<GarmentRegion> Regions = new();
        /// <summary>肌の写し（BuildSkinLining）: 衣装の形に体の UV を持たせたもの。</summary>
        public Mesh SkinMesh;
        /// <summary>SkinMesh のサブメッシュごと: 描くのに使う体のマテリアルの番号。</summary>
        public List<int> SkinMaterialSlots = new();
        /// <summary>SkinMesh のサブメッシュごと: 足の指先の物（テクスチャを使わず単色の肌で描く）。</summary>
        public List<bool> SkinPlain = new();

        int[] _bindTri;
        Vector3[] _bary;
        int[] _region;
        int[] _tris;
        /// <summary>頂点ごと: その頂点を作る元になった、着ているものの三角形（なければ -1）。</summary>
        int[] _utri;
        Vector2[] _bodyUV;

        public static BlenderResult ReadResult(string jsonPath) =>
            JsonUtility.FromJson<BlenderResult>(File.ReadAllText(jsonPath));

        /// <param name="worn">衣装を上に重ねるメッシュ（着衣モード）: そこではそれらのウェイトを使う</param>
        /// <param name="lining">全三角形を持つサブメッシュを先頭に足す（2 層タイツの裏地）</param>
        /// <param name="joinedToes">つま先をまとめて包む（指ごとに分けない）: 足の指の骨のウェイトを布の上でならす</param>
        public void Build(AvatarBody body, BlenderResult result, string binPath, string name,
            IReadOnlyList<SkinnedMeshRenderer> worn = null, bool lining = false, bool joinedToes = false)
        {
            var m = result.vertexCount;
            var k = result.triangleCount;
            // ファイルは個数どおりの中身をちょうど持たなければならない（それらの個数をもとに何かを確保する前に
            // 確かめる）: 頂点ごとに 76 バイト（位置・法線・UV・体の三角形・重心座標・伸び・脚の座標・着ているものの
            // 三角形と重心座標）、三角形ごとに 16（角・領域）、体の頂点ごとに 4（覆われているか）
            var expected = 76L * m + 16L * k + 4L * body.Vertices.Length;
            var actual = new FileInfo(binPath).Length;
            if (m <= 0 || k <= 0 || actual != expected)
                throw new InvalidDataException($"Blender の出力が壊れています（頂点 {m}・三角形 {k}、{actual} バイト、期待 {expected} バイト）。");
            var pos = new Vector3[m];
            var nrm = new Vector3[m];
            var uv = new Vector2[m];
            var bindTri = new int[m];
            var bary = new Vector3[m];
            var tris = new int[k * 3];
            var stretch = new float[m];
            var region = new int[k];
            var legCoord = new Vector2[m];
            var utri = new int[m];
            var ubary = new Vector3[m];
            Covered = new float[body.Vertices.Length];
            using (var br = new BinaryReader(File.OpenRead(binPath)))
            {
                Vector3 V3() => new(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                for (var i = 0; i < m; i++) pos[i] = V3();
                for (var i = 0; i < m; i++) nrm[i] = V3();
                for (var i = 0; i < m; i++) uv[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
                for (var i = 0; i < m; i++) bindTri[i] = br.ReadInt32();
                for (var i = 0; i < m; i++) bary[i] = V3();
                for (var i = 0; i < k * 3; i++) tris[i] = br.ReadInt32();
                for (var i = 0; i < Covered.Length; i++) Covered[i] = br.ReadSingle();
                for (var i = 0; i < m; i++) stretch[i] = br.ReadSingle();
                for (var i = 0; i < k; i++) region[i] = br.ReadInt32();
                for (var i = 0; i < m; i++) legCoord[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
                for (var i = 0; i < m; i++) utri[i] = br.ReadInt32();
                for (var i = 0; i < m; i++) ubary[i] = V3();
            }
            var bodyTris = body.Triangles.Length / 3;
            if (bindTri.Any(t => t < 0 || t >= bodyTris) || tris.Any(t => t < 0 || t >= m))
                throw new InvalidDataException("Blender の出力が壊れています（範囲外の三角形の番号）。");

            var src = body.Mesh;
            var corner = new int[m * 3];
            for (var i = 0; i < m; i++)
                for (var c = 0; c < 3; c++)
                    corner[i * 3 + c] = body.Triangles[bindTri[i] * 3 + c];

            var mesh = new Mesh { name = name };
            mesh.indexFormat = m > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = pos;
            mesh.normals = nrm;
            // UV0.x は伸びによる透過の係数を持つ（lilToon のアルファマスクがランプを通して読む）。
            // UV1 は柄のための脚の座標（LegPattern）
            mesh.uv = stretch.Select(s => new Vector2(GarmentMaterial.RampU(s), 0.5f)).ToArray();
            mesh.uv2 = legCoord.Select(LegPattern.UV).ToArray();
            // UV4: 脚に沿う向き（0 股関節、1 膝、2 足首、3 指）と脚を回る向き（周回数）。柄に使う
            mesh.SetUVs(4, legCoord);
            var submeshes = new List<int[]>();
            if (lining) submeshes.Add(tris);
            Regions.Clear();
            foreach (GarmentRegion r in Enum.GetValues(typeof(GarmentRegion)))
            {
                var part = Enumerable.Range(0, k).Where(t => region[t] == (int)r).SelectMany(t => new[] { tris[t * 3], tris[t * 3 + 1], tris[t * 3 + 2] }).ToArray();
                if (part.Length == 0) continue;
                submeshes.Add(part);
                Regions.Add(r);
            }
            mesh.subMeshCount = submeshes.Count;
            for (var i = 0; i < submeshes.Count; i++) mesh.SetTriangles(submeshes[i], i);
            BuildBoneWeights(mesh, src, corner, bary, MadeOverUnder(body, utri, ubary, WornWeights(body, corner, bary, worn)),
                joinedToes ? body.ToeDigitBones : null, tris, pos);
            mesh.bindposes = src.bindposes;
            BuildBlendShapes(mesh, src, corner, bary);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            Mesh = mesh;
            (_bindTri, _bary, _region, _bodyUV, _tris, _utri) = (bindTri, bary, region, uv, tris, utri);
        }

        /// <summary>
        /// 肌の写し: 衣装の複製（頂点・ウェイト・シェイプが同じなので、衣装とまったく同じに動く）に体の UV を持たせ、
        /// 各部分が乗る体のマテリアルごとに分けたもの。衣装の下に何かを着ているところ（それは
        /// 見えたままでなければならない）と、`lined` が除く部分（不透明な部分はどのみち肌を隠す）では省く。
        /// </summary>
        /// <param name="keepWorn">着ているものの下の肌を残す（その上は透ける）: そこには裏地を付けない</param>
        /// <param name="inner">
        /// 体の頂点ごと: 前に作った、肌にもっと近い衣装（親指サック）の下にある。三角形のすべての頂点がそこに結び付く所には
        /// 裏地を付けない（裏地はこの衣装の面にあり、下の衣装を隠す。縁にかかる三角形は残すので、肌は出ない）
        /// </param>
        /// <param name="plain">体の頂点ごと: 足の指先（そこに結び付いた三角形は、別のサブメッシュにして単色の肌で描く）。</param>
        public void BuildSkinLining(AvatarBody body, Func<GarmentRegion, bool> lined, bool keepWorn = true, bool[] inner = null,
            bool[] plain = null)
        {
            var mesh = Mesh;
            var tris = _tris; // 領域を並べた順（mesh.triangles はサブメッシュごとにまとまっている）
            // 体の三角形 -> 体のサブメッシュ（AvatarBody.Triangles は三角形のサブメッシュを順に並べる）
            var src = body.Mesh;
            var subOf = new List<int>();
            for (var s = 0; s < src.subMeshCount; s++)
                if (src.GetTopology(s) == MeshTopology.Triangles)
                    subOf.AddRange(Enumerable.Repeat(s, (int)src.GetIndexCount(s) / 3));
            // 角の体上の点にちょうど着ているものがある。肌は 1 周分広く残る（UnderPresent）ので、
            // 裏地はそこに残った肌の縁に重なり、切れ目を隠す
            bool Worn(int v)
            {
                if (body.UnderCore == null || !keepWorn) return false;
                // 着ているものの上に作ったもの（それが谷間にあるところでは、その下の体上の点が外に出ることがある）
                if (_utri != null && _utri[v] >= 0) return true;
                for (var c = 0; c < 3; c++)
                    if (_bary[v][c] > 1e-4f && body.UnderCore[body.Triangles[_bindTri[v] * 3 + c]]) return true;
                return false;
            }
            bool Inner(int v)
            {
                if (inner == null) return false;
                for (var c = 0; c < 3; c++)
                    if (_bary[v][c] > 1e-4f && !inner[body.Triangles[_bindTri[v] * 3 + c]]) return false;
                return true;
            }
            // 足の指先: 布の形は体の指と 1 対 1 に対応しない（指の間を埋める・指に沿わせる）ので、体の UV を割り当てると
            // 爪のペディキュアなどの模様が伸び、指を曲げるとさらに崩れる。結び付け先の角がすべて指先の体の頂点なら
            // 指先の頂点とし、3 頂点のうち 2 つ以上が指先の三角形を指先の物とする
            bool Plain(int v)
            {
                if (plain == null) return false;
                for (var c = 0; c < 3; c++)
                    if (_bary[v][c] > 1e-4f && !plain[body.Triangles[_bindTri[v] * 3 + c]]) return false;
                return true;
            }
            // 鍵: 体のサブメッシュ × 2 + 指先なら 1
            var groups = new SortedDictionary<int, List<int>>();
            // 領域のサブメッシュの三角形（各三角形の領域は Blender が書いたとおり）
            for (var t = 0; t < _region.Length; t++)
            {
                if (!lined((GarmentRegion)_region[t])) continue;
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                // 衣装の頂点は体の頂点の上にあるので、これらは取り除いた肌をすべて、それ以上に覆う
                if (Worn(a) || Worn(b) || Worn(c)) continue;
                if (Inner(a) && Inner(b) && Inner(c)) continue;
                var tip = (Plain(a) ? 1 : 0) + (Plain(b) ? 1 : 0) + (Plain(c) ? 1 : 0) >= 2;
                var key = subOf[_bindTri[a]] * 2 + (tip ? 1 : 0);
                if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<int>();
                l.AddRange(new[] { a, b, c });
            }
            SkinMaterialSlots = groups.Keys.Select(k => k / 2).ToList();
            SkinPlain = groups.Keys.Select(k => k % 2 == 1).ToList();
            if (groups.Count == 0)
            {
                SkinMesh = null;
                return;
            }
            var skin = UnityEngine.Object.Instantiate(mesh);
            skin.name = mesh.name + "_Skin";
            skin.uv = _bodyUV;
            skin.uv2 = null;
            skin.subMeshCount = groups.Count;
            var i = 0;
            foreach (var l in groups.Values) skin.SetTriangles(l, i++);
            skin.RecalculateTangents();
            SkinMesh = skin;
        }

        /// <summary>
        /// 衣装が着ているものの上を通るところ（体上の点で UnderCovered）: その点でのそのメッシュのウェイトと、
        /// それをどれだけ使うか（実際にあるところでは全部、縁を過ぎたところですぐ 0 まで薄れる）。
        /// </summary>
        static (Dictionary<int, float>[] weights, float[] amount) WornWeights(AvatarBody body, int[] corner, Vector3[] bary,
            IReadOnlyList<SkinnedMeshRenderer> worn)
        {
            if (worn == null || worn.Count == 0 || body.UnderCovered == null) return (null, null);
            var m = bary.Length;
            var amount = new float[m];
            var skin = MeshMath.SkinnedPositions(body.Renderer);
            var points = new Vector3[m];
            var normals = new Vector3[m];
            for (var i = 0; i < m; i++)
            {
                float h = 0;
                var p = Vector3.zero;
                var n = Vector3.zero;
                for (var c = 0; c < 3; c++)
                {
                    h += body.UnderCovered[corner[i * 3 + c]] * bary[i][c];
                    p += skin[corner[i * 3 + c]] * bary[i][c];
                    n += body.Normals[corner[i * 3 + c]] * bary[i][c];
                }
                amount[i] = Mathf.Clamp01(h);
                points[i] = amount[i] > 0 ? body.Renderer.transform.TransformPoint(p) : Vector3.positiveInfinity;
                normals[i] = body.Renderer.transform.TransformDirection(n).normalized;
            }
            return (UnderLayers.WeightsAt(body, worn, points, normals), amount);
        }

        /// <summary>
        /// Blender が下に着ているものの表面から作った頂点（lw_under.snap_to_under）は、
        /// 作る元になった点でのそのウェイトで完全に動く: どのポーズでもその上に留まる。
        /// </summary>
        static (Dictionary<int, float>[] weights, float[] amount) MadeOverUnder(AvatarBody body, int[] utri, Vector3[] ubary,
            (Dictionary<int, float>[] weights, float[] amount) worn)
        {
            var m = utri.Length;
            var weights = worn.weights ?? new Dictionary<int, float>[m];
            var amount = worn.amount ?? new float[m];
            for (var i = 0; i < m; i++)
            {
                var f = utri[i] * 3;
                if (utri[i] < 0 || f + 2 >= body.UnderTris.Length) continue;
                var acc = new Dictionary<int, float>();
                for (var c = 0; c < 3 && acc != null; c++)
                {
                    var w = body.UnderWeights[body.UnderTris[f + c]];
                    if (w == null) acc = null;
                    else
                        foreach (var kv in w)
                        {
                            acc.TryGetValue(kv.Key, out var cur);
                            acc[kv.Key] = cur + kv.Value * ubary[i][c];
                        }
                }
                if (acc == null) continue;
                weights[i] = acc;
                amount[i] = 1f;
            }
            return (weights, amount);
        }

        /// <summary>
        /// 結びついた点で体から補間したウェイト（衣装が着ているメッシュの上を通るところでは、
        /// そのメッシュのウェイトへ寄せる）。上位 4 ボーン。
        /// </summary>
        /// <param name="blendBones">
        /// 足の指の骨 -> 指の番号。指と指の間の布のウェイトをならす（つま先をまとめて包む布が、指ごとに違う角度に
        /// 曲げたとき指ごとに裂けないように: 指の間を渡る布は両隣の指の中間で動く）。null なら何もしない
        /// </param>
        static void BuildBoneWeights(Mesh mesh, Mesh src, int[] corner, Vector3[] bary,
            (Dictionary<int, float>[] weights, float[] amount) worn, Dictionary<int, int> blendBones = null, int[] tris = null,
            Vector3[] pos = null)
        {
            var srcBpv = src.GetBonesPerVertex();
            var srcW = src.GetAllBoneWeights();
            var start = new int[srcBpv.Length];
            for (int v = 0, s = 0; v < srcBpv.Length; s += srcBpv[v], v++) start[v] = s;

            var m = bary.Length;
            var full = new Dictionary<int, float>[m];
            for (var i = 0; i < m; i++)
            {
                var acc = new Dictionary<int, float>();
                for (var c = 0; c < 3; c++)
                {
                    var b = bary[i][c];
                    if (b <= 0) continue;
                    var v = corner[i * 3 + c];
                    for (var j = 0; j < srcBpv[v]; j++)
                    {
                        var w = srcW[start[v] + j];
                        acc.TryGetValue(w.boneIndex, out var cur);
                        acc[w.boneIndex] = cur + w.weight * b;
                    }
                }
                var over = worn.weights?[i];
                if (over != null && worn.amount[i] > 0)
                {
                    var f = worn.amount[i];
                    foreach (var key in acc.Keys.ToList()) acc[key] *= 1f - f;
                    foreach (var kv in over)
                    {
                        acc.TryGetValue(kv.Key, out var cur);
                        acc[kv.Key] = cur + kv.Value * f;
                    }
                }
                full[i] = acc;
            }

            if (blendBones != null && blendBones.Count > 0 && tris != null)
                BlendWeights(full, blendBones, tris, pos);

            var bpv = new NativeArray<byte>(m, Allocator.Temp);
            var all = new List<BoneWeight1>(m * 4);
            for (var i = 0; i < m; i++)
            {
                var top = full[i].OrderByDescending(kv => kv.Value).Take(4).ToList();
                var sum = top.Sum(kv => kv.Value);
                if (sum <= 0)
                {
                    top = new List<KeyValuePair<int, float>> { new(0, 1f) };
                    sum = 1f;
                }
                bpv[i] = (byte)top.Count;
                foreach (var kv in top) all.Add(new BoneWeight1 { boneIndex = kv.Key, weight = kv.Value / sum });
            }
            var weights = new NativeArray<BoneWeight1>(all.ToArray(), Allocator.Temp);
            mesh.SetBoneWeights(bpv, weights);
            bpv.Dispose();
            weights.Dispose();
        }

        /// <summary>
        /// 足の指の骨のウェイトを持つ頂点（と、その 2 周外）のウェイトを、隣の頂点の平均と半分ずつ混ぜる（4 回。多いと
        /// 強く曲げた指に布が付いていかない。指と指の間だけに絞ると、境目で小さな三角形が裏返る）。同じ位置の頂点
        /// （UV の継ぎ目で分かれた物）は毎回そろえる（継ぎ目で裂けないように）。
        /// </summary>
        static void BlendWeights(Dictionary<int, float>[] full, Dictionary<int, int> blendBones, int[] tris, Vector3[] pos)
        {
            var m = full.Length;
            var nbrs = new HashSet<int>[m];
            for (var i = 0; i < m; i++) nbrs[i] = new HashSet<int>();
            for (var t = 0; t < tris.Length; t += 3)
                for (var a = 0; a < 3; a++)
                    for (var b = 0; b < 3; b++)
                        if (a != b) nbrs[tris[t + a]].Add(tris[t + b]);
            var same = new Dictionary<Vector3Int, List<int>>();
            for (var i = 0; i < m; i++)
            {
                var key = Vector3Int.RoundToInt(pos[i] * 1e5f);
                if (!same.TryGetValue(key, out var l)) same[key] = l = new List<int>();
                l.Add(i);
            }
            var zone = new HashSet<int>(Enumerable.Range(0, m).Where(i => full[i].Keys.Any(blendBones.ContainsKey)));
            for (var ring = 0; ring < 2; ring++)
                zone.UnionWith(zone.SelectMany(i => nbrs[i]).ToList());
            for (var round = 0; round < 4; round++)
            {
                var next = new Dictionary<int, Dictionary<int, float>>();
                foreach (var i in zone)
                {
                    var acc = new Dictionary<int, float>();
                    foreach (var kv in full[i]) acc[kv.Key] = kv.Value * 0.5f;
                    var nb = nbrs[i];
                    if (nb.Count == 0) continue;
                    foreach (var j in nb)
                        foreach (var kv in full[j])
                        {
                            acc.TryGetValue(kv.Key, out var cur);
                            acc[kv.Key] = cur + kv.Value * 0.5f / nb.Count;
                        }
                    next[i] = acc;
                }
                foreach (var kv in next) full[kv.Key] = kv.Value;
                foreach (var group in same.Values)
                {
                    if (group.Count < 2 || !zone.Contains(group[0])) continue;
                    var avg = new Dictionary<int, float>();
                    foreach (var i in group)
                        foreach (var kv in full[i])
                        {
                            avg.TryGetValue(kv.Key, out var cur);
                            avg[kv.Key] = cur + kv.Value / group.Count;
                        }
                    foreach (var i in group) full[i] = new Dictionary<int, float>(avg);
                }
            }
        }

        void BuildBlendShapes(Mesh mesh, Mesh src, int[] corner, Vector3[] bary)
        {
            var n = src.vertexCount;
            var m = bary.Length;
            var dv = new Vector3[n];
            var dn = new Vector3[n];
            var ov = new Vector3[m];
            var on = new Vector3[m];
            SyncedBlendShapes.Clear();
            for (var s = 0; s < src.blendShapeCount; s++)
            {
                var shapeName = src.GetBlendShapeName(s);
                var frames = src.GetBlendShapeFrameCount(s);
                var frameData = new List<(float weight, Vector3[] v, Vector3[] n)>();
                var any = false;
                for (var f = 0; f < frames; f++)
                {
                    src.GetBlendShapeFrameVertices(s, f, dv, dn, null);
                    for (var i = 0; i < m; i++)
                    {
                        Vector3 a = default, b = default;
                        for (var c = 0; c < 3; c++)
                        {
                            var v = corner[i * 3 + c];
                            a += dv[v] * bary[i][c];
                            b += dn[v] * bary[i][c];
                        }
                        ov[i] = a;
                        on[i] = b;
                        if (!any && a.sqrMagnitude > 1e-14f) any = true;
                    }
                    frameData.Add((src.GetBlendShapeFrameWeight(s, f), (Vector3[])ov.Clone(), (Vector3[])on.Clone()));
                }
                if (!any) continue;
                foreach (var (weight, v, nn) in frameData)
                    mesh.AddBlendShapeFrame(shapeName, weight, v, nn, null);
                SyncedBlendShapes.Add(shapeName);
            }
        }
    }
}
