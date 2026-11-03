// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using nadena.dev.ndmf;
using Unity.Collections;
using UnityEngine;

[assembly: ExportsPlugin(typeof(LegWeaver.SkinFollowPlugin))]

namespace LegWeaver
{
    /// <summary>
    /// ビルド時: 肌の除去（AAO、最適化フェーズ）がメッシュを変える前に、着ているものの下に残す体の肌へ、
    /// SkinFollow が持つウェイトを与える（体のメッシュの複製に対して）。エディタでは NDMF のプレビューが同じものを
    /// 見せる（SkinFollowPreview）。
    /// </summary>
    public class SkinFollowPlugin : Plugin<SkinFollowPlugin>
    {
        public override string QualifiedName => "legweaver.skin-follow";
        public override string DisplayName => "LegWeaver: skin follows the garment";

        protected override void Configure()
        {
            // 衣装が消された（または EditorOnly にされた）のに体に残った肌の削除は、AAO が削除する前に外す
            // （衣装を消しただけで、その下の肌が消えたままにならないように）
            InPhase(BuildPhase.Resolving).Run("Drop skin removal of removed garments", ctx =>
            {
                foreach (var owner in ctx.AvatarRootObject.GetComponentsInChildren<RemovalOwner>(true))
                {
                    if (owner.removal != null && Gone(owner.garment)) Object.DestroyImmediate(owner.removal);
                    Object.DestroyImmediate(owner);
                }
            });
            InPhase(BuildPhase.Transforming).Run("Skin follows the garment", ctx =>
            {
                foreach (var follow in ctx.AvatarRootObject.GetComponentsInChildren<SkinFollow>(true))
                {
                    if (follow.body != null && follow.body.sharedMesh != null)
                    {
                        var applied = Apply(follow.body.sharedMesh, follow);
                        if (applied != follow.body.sharedMesh)
                        {
                            follow.body.sharedMesh = applied;
                            ctx.AssetSaver.SaveAsset(applied);
                        }
                    }
                    Object.DestroyImmediate(follow);
                }
            }).PreviewingWith(new SkinFollowPreview());
        }

        /// <summary>
        /// 肌の削除の持ち主の衣装が、もうアバターに無いか（消された、または自身か親が EditorOnly でビルドから外れる）。
        /// 非表示なだけの衣装は含めない（メニューで後から表示する衣装の下の肌も消す）。
        /// </summary>
        public static bool Gone(GameObject garment)
        {
            if (garment == null) return true;
            for (var t = garment.transform; t != null; t = t.parent)
                if (t.CompareTag("EditorOnly")) return true;
            return false;
        }

        /// <summary>
        /// `follow` が `mesh` 用に持つウェイトの問題点（なければ null）: リストの長さがそろっていること、
        /// 頂点とボーンがそのメッシュのものであること（コンポーネントがほかのアバターのプレハブから
        /// 来たものかもしれない）。
        /// </summary>
        public static string Invalid(Mesh mesh, SkinFollow follow)
        {
            if (follow.vertices == null || follow.counts == null || follow.bones == null || follow.weights == null)
                return "data missing";
            if (follow.counts.Length != follow.vertices.Length) return "counts and vertices differ in length";
            var total = follow.counts.Sum(c => c);
            if (follow.counts.Any(c => c <= 0 || c > 255) || total != follow.bones.Length || total != follow.weights.Length)
                return "counts do not match the bones and weights";
            if (follow.vertices.Any(v => v < 0 || v >= mesh.vertexCount)) return "a vertex outside the mesh";
            var bones = mesh.bindposes.Length;
            if (follow.bones.Any(b => b < 0 || b >= bones)) return "a bone outside the mesh's";
            return null;
        }

        /// <summary>`follow` がその頂点用に持つウェイトを与えた `mesh` の複製（ウェイトが合わなければ `mesh`
        /// そのもの）。</summary>
        public static Mesh Apply(Mesh mesh, SkinFollow follow)
        {
            var invalid = Invalid(mesh, follow);
            if (invalid != null)
            {
                Debug.LogWarning($"[LegWeaver] SkinFollow on {follow.name} does not fit {mesh.name} ({invalid}): left as it is.");
                return mesh;
            }
            var given = new Dictionary<int, BoneWeight1[]>();
            for (int k = 0, at = 0; k < follow.vertices.Length; at += follow.counts[k], k++)
                given[follow.vertices[k]] = Enumerable.Range(at, follow.counts[k])
                    .Select(j => new BoneWeight1 { boneIndex = follow.bones[j], weight = follow.weights[j] }).ToArray();
            var copy = Object.Instantiate(mesh);
            copy.name = mesh.name;
            var bpv = mesh.GetBonesPerVertex();
            var all = mesh.GetAllBoneWeights();
            var counts = new List<byte>(bpv.Length);
            var weights = new List<BoneWeight1>(all.Length);
            for (int v = 0, a = 0; v < bpv.Length; a += bpv[v], v++)
            {
                if (given.TryGetValue(v, out var w))
                {
                    counts.Add((byte)w.Length);
                    weights.AddRange(w);
                }
                else
                {
                    counts.Add(bpv[v]);
                    for (var j = 0; j < bpv[v]; j++) weights.Add(all[a + j]);
                }
            }
            using var c = new NativeArray<byte>(counts.ToArray(), Allocator.Temp);
            using var ws = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp);
            copy.SetBoneWeights(c, ws);
            return copy;
        }
    }
}
