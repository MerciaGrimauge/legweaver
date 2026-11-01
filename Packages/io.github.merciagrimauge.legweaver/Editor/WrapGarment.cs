// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// 体以外のメッシュをニットで包む衣装: 一部のアバターが脚の代わりに持つ下半身（たとえば幽霊の尾）。
    /// メッシュ自身の頂点を、すき間と生地の厚みの分だけ法線に沿って外へ動かしたもの:
    /// 頂点 1 つにつき衣装の頂点 1 つなので、ウェイトとシェイプキーはメッシュのものになり、
    /// メッシュとまったく同じに動く。メッシュのオブジェクトの下に置くので、
    /// メッシュが表示されている間だけ表示される（脚とそのような下半身を切り替えるアバターでは、
    /// 合うほうの衣装が表示される）。
    /// </summary>
    public static class WrapGarment
    {
        /// <summary>候補のボーンウェイトのうち、少なくともこの割合が腰またはその固有のチェーン（脚と背骨以外）に乗る。
        /// </summary>
        const float HipsShare = 0.8f;
        /// <summary>候補の上端は、腰から最大で太もも何本分まで上にあってよいか。</summary>
        const float TopAboveHips = 0.3f;
        /// <summary>候補は、腰から少なくとも太もも何本分下まで届く。</summary>
        const float ReachBelowHips = 0.5f;
        /// <summary>
        /// 候補は、股関節の前と後ろの両方に、少なくとも脚の間隔のこの割合だけ出ている（脚を囲む。
        /// 尾は後ろにだけ垂れる）。
        /// </summary>
        const float RoundTheLegs = 0.25f;
        /// <summary>候補の開いた縁（あれば）は、すべて上端から高さのこの割合以内にある。</summary>
        const float OpenAtTop = 1f / 3f;
        /// <summary>位置を共有する頂点の法線がほぼ打ち消し合うところ（薄いひれの縁）では、その頂点自身の法線を使う。
        /// </summary>
        const float CancelledNormal = 0.2f;

        /// <summary>
        /// 包めそうなメッシュ。見込みの高い順: 体でもこのツールが作ったものでもない。腰、
        /// または腰からぶら下がる脚でも背骨でもないボーン（尾自身のチェーン）で動く。腰より下にあり膝より下まで届く。
        /// 脚を囲む（両脚分の幅があり、脚の前と後ろにある）。底が閉じていて、
        /// 開いた縁（あれば）は上端の近くにある（スカートは裾が開いている）。
        /// </summary>
        public static List<SkinnedMeshRenderer> Candidates(GameObject root, Animator animator, SkinnedMeshRenderer body)
        {
            var avatarBone = BodyFinder.AvatarBone(animator);
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            var legs = new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg }.Select(animator.GetBoneTransform).ToArray();
            var knee = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
            if (hips == null || knee == null || legs.Any(l => l == null)) return new List<SkinnedMeshRenderer>();
            var thigh = Vector3.Distance(legs[0].position, knee.position);
            var found = new List<(SkinnedMeshRenderer r, float span)>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (smr == body || mesh == null || mesh.vertexCount == 0 || GarmentGenerator.IsMade(smr)) continue;
                // ボーンごと: 腰またはその固有のチェーン。脚と背骨は除く
                var counts = smr.bones.Select(b =>
                {
                    var a = avatarBone(b);
                    return a != null && a.IsChildOf(hips) && (spine == null || !a.IsChildOf(spine)) && !legs.Any(l => a.IsChildOf(l));
                }).ToArray();
                float lower = 0, total = 0;
                foreach (var w in mesh.GetAllBoneWeights())
                {
                    total += w.weight;
                    if (w.boneIndex < counts.Length && counts[w.boneIndex]) lower += w.weight;
                }
                if (total <= 0 || lower / total < HipsShare) continue;
                var world = MeshMath.WorldPositions(smr);
                var ys = world.Select(p => p.y).ToArray();
                float top = ys.Max(), bottom = ys.Min();
                if (top > hips.position.y + thigh * TopAboveHips || hips.position.y - bottom < thigh * ReachBelowHips) continue;
                if (bottom > knee.position.y) continue;
                // 脚を囲む: 両脚分の幅があり、股関節の後ろだけでなく前にもある
                var spacing = Vector3.Distance(legs[0].position, legs[1].position);
                var side = (legs[0].position - legs[1].position).normalized;
                var front = Vector3.Cross(side, Vector3.up).normalized;
                var mid = (legs[0].position + legs[1].position) * 0.5f;
                var across = world.Select(p => Vector3.Dot(p - mid, side)).ToArray();
                if (across.Max() < spacing * 0.5f || across.Min() > -spacing * 0.5f) continue;
                var along = world.Select(p => Vector3.Dot(p - mid, front)).ToArray();
                if (along.Max() < spacing * RoundTheLegs || along.Min() > -spacing * RoundTheLegs) continue;
                // 底が閉じている
                if (OpenVertices(mesh).Any(v => ys[v] < top - (top - bottom) * OpenAtTop)) continue;
                found.Add((smr, top - bottom));
            }
            return found.OrderByDescending(f => f.span).Select(f => f.r).ToList();
        }

        /// <summary>メッシュの開いた縁の上の頂点（位置で溶接する: 分割された法線と UV は縁ではない）。</summary>
        static HashSet<int> OpenVertices(Mesh mesh)
        {
            var verts = mesh.vertices;
            var weld = new Dictionary<Vector3, int>();
            var id = verts.Select(v => weld.TryGetValue(v, out var i) ? i : weld[v] = weld.Count).ToArray();
            var edges = new Dictionary<(int, int), int>();
            var tris = mesh.triangles;
            for (var f = 0; f < tris.Length; f += 3)
                for (var k = 0; k < 3; k++)
                {
                    int a = id[tris[f + k]], b = id[tris[f + (k + 1) % 3]];
                    var e = a < b ? (a, b) : (b, a);
                    edges[e] = edges.TryGetValue(e, out var c) ? c + 1 : 1;
                }
            var openIds = new HashSet<int>(edges.Where(kv => kv.Value == 1).SelectMany(kv => new[] { kv.Key.Item1, kv.Key.Item2 }));
            return new HashSet<int>(Enumerable.Range(0, verts.Length).Where(v => openIds.Contains(id[v])));
        }

        /// <summary>
        /// 包むメッシュ: `target` のメッシュを法線に沿って `offsetMeters` だけ外へ動かしたもの（位置を共有する頂点では
        /// 法線を平均するので、継ぎ目は閉じたまま）。ウェイト・バインドポーズ・シェイプキー付きで、
        /// サブメッシュは 1 つ。
        /// </summary>
        public static Mesh Build(SkinnedMeshRenderer target, float offsetMeters, string name)
        {
            var src = target.sharedMesh;
            var verts = src.vertices;
            var normals = src.normals.Length == verts.Length ? src.normals : RecalculatedNormals(src);
            var offset = offsetMeters / MetersPerUnit(target);
            // 位置ごと: そこの法線の和と、そこを共有する頂点の数
            var byPosition = new Dictionary<Vector3, (Vector3 sum, int count)>();
            for (var i = 0; i < verts.Length; i++)
            {
                var (sum, count) = byPosition.TryGetValue(verts[i], out var e) ? e : (Vector3.zero, 0);
                byPosition[verts[i]] = (sum + normals[i].normalized, count + 1);
            }
            var moved = new Vector3[verts.Length];
            for (var i = 0; i < verts.Length; i++)
            {
                var (sum, count) = byPosition[verts[i]];
                var dir = sum.magnitude > CancelledNormal * count ? sum.normalized : normals[i].normalized;
                moved[i] = verts[i] + dir * offset;
            }

            var mesh = new Mesh { name = name, indexFormat = src.indexFormat };
            mesh.vertices = moved;
            mesh.normals = normals;
            mesh.triangles = Enumerable.Range(0, src.subMeshCount)
                .Where(s => src.GetTopology(s) == MeshTopology.Triangles).SelectMany(src.GetTriangles).ToArray();
            // UV0.x: 伸びによる透過（なし: ニットそのまま）、UV1: 柄の座標なし
            mesh.uv = Enumerable.Repeat(new Vector2(GarmentMaterial.RampU(1f), 0.5f), verts.Length).ToArray();
            mesh.uv2 = new Vector2[verts.Length];
            mesh.SetBoneWeights(src.GetBonesPerVertex(), src.GetAllBoneWeights());
            mesh.bindposes = src.bindposes;
            var dv = new Vector3[verts.Length];
            var dn = new Vector3[verts.Length];
            var dt = new Vector3[verts.Length];
            for (var s = 0; s < src.blendShapeCount; s++)
                for (var f = 0; f < src.GetBlendShapeFrameCount(s); f++)
                {
                    src.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                    mesh.AddBlendShapeFrame(src.GetBlendShapeName(s), src.GetBlendShapeFrameWeight(s, f), dv, dn, dt);
                }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        /// <summary>
        /// メッシュの単位あたりのメートル数をボーンから求める: 各ボーンのワールド行列にバインドポーズを掛けると
        /// メッシュ空間からワールドへ移すので、その拡大縮小がメッシュのものになる（ポーズとシェイプキーでは
        /// 変わらない）。ボーン全体での中央値。
        /// </summary>
        static float MetersPerUnit(SkinnedMeshRenderer r)
        {
            var bind = r.sharedMesh.bindposes;
            var scales = r.bones.Select((b, i) => b == null || i >= bind.Length ? 0f : (b.localToWorldMatrix * bind[i]).lossyScale.x)
                .Where(x => x > 0).OrderBy(x => x).ToList();
            if (scales.Count > 0) return scales[scales.Count / 2];
            return Mathf.Max(r.transform.lossyScale.x, 1e-6f);
        }

        static Vector3[] RecalculatedNormals(Mesh src)
        {
            var copy = Object.Instantiate(src);
            copy.RecalculateNormals();
            var n = copy.normals;
            Object.DestroyImmediate(copy);
            return n;
        }

        /// <summary>
        /// 包むメッシュを `target` のオブジェクトの下に置き、同じボーンで動かし（それと一緒に表示・非表示になる）、
        /// シェイプキーを合わせてそれと同期させる。
        /// </summary>
        public static SkinnedMeshRenderer Install(SkinnedMeshRenderer target, Mesh mesh, Material[] materials, string objectName)
        {
            var go = new GameObject(objectName);
            Undo.RegisterCreatedObjectUndo(go, "LegWeaver: Generate");
            go.transform.SetParent(target.transform, false);
            var r = go.AddComponent<SkinnedMeshRenderer>();
            r.sharedMesh = mesh;
            r.bones = target.bones;
            r.rootBone = target.rootBone;
            r.sharedMaterials = materials;
            GarmentInstaller.CopyRendering(target, r);
            var shapes = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToList();
            for (var i = 0; i < shapes.Count; i++) r.SetBlendShapeWeight(i, target.GetBlendShapeWeight(i));
            GarmentInstaller.AddBlendshapeSync(go, target, shapes);
            return r;
        }
    }
}
