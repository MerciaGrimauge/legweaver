// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// アバターの脚の肌の平均色: 脚の大部分を描くマテリアルのメインテクスチャを脚の頂点の UV で標本にし、
    /// マテリアルの色を掛けたもの。2 層タイツの裏地に使う。裏地は着用者の肌と色合いが合って初めて肌に見える。
    /// </summary>
    public static class SkinColor
    {
        const int SampleSize = 128;
        /// <summary>頂点は、その部位のボーンがウェイトの少なくともこれだけを持つとき、その部位の肌とみなす。</summary>
        const float OnThePart = 0.9f;

        public static Color Estimate(AvatarBody body, Color fallback)
        {
            var mesh = body.Mesh;
            var materials = body.Renderer.sharedMaterials;
            // 脚の頂点が最も多いサブメッシュ
            var best = -1;
            var bestVerts = new List<int>();
            for (var s = 0; s < mesh.subMeshCount && s < materials.Length; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var verts = new HashSet<int>();
                foreach (var i in mesh.GetTriangles(s))
                    if (body.LegWeight[i] >= OnThePart) verts.Add(i);
                if (verts.Count > bestVerts.Count)
                {
                    best = s;
                    bestVerts = new List<int>(verts);
                }
            }
            if (best < 0 || materials[best] == null) return fallback;
            var (tint, tex) = MainTexture(materials[best]);
            if (tex == null) return Opaque(tint);
            var read = Readable(tex);
            var sum = Color.clear;
            foreach (var i in bestVerts) sum += Sample(read, body.UV[i]);
            Object.DestroyImmediate(read);
            if (bestVerts.Count == 0) return Opaque(tint);
            return Opaque(sum / bestVerts.Count * tint);
        }

        /// <summary>
        /// 体の頂点 verts の所で、マテリアル mat が描く色の平均（メインテクスチャにマテリアルの色を掛けたもの）。
        /// 頂点もマテリアルも無ければ fallback。
        /// </summary>
        public static Color At(AvatarBody body, Material mat, IReadOnlyCollection<int> verts, Color fallback)
        {
            if (mat == null || verts.Count == 0) return fallback;
            var (tint, tex) = MainTexture(mat);
            if (tex == null) return Opaque(tint);
            var read = Readable(tex);
            var sum = Color.clear;
            foreach (var i in verts) sum += Sample(read, body.UV[i]);
            Object.DestroyImmediate(read);
            return Opaque(sum / verts.Count * tint);
        }

        static Color Opaque(Color c)
        {
            c.a = 1f;
            return c;
        }

        /// <summary>マテリアルの色とメインテクスチャ（そのようなプロパティがなければ白となし）。</summary>
        static (Color tint, Texture tex) MainTexture(Material mat) =>
            (mat.HasProperty("_Color") ? mat.GetColor("_Color") : Color.white,
             mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null);

        /// <summary>読み取ったテクスチャの UV での色。UV は 0..1 に折り返す（タイル）。</summary>
        static Color Sample(Texture2D read, Vector2 uv) => read.GetPixelBilinear(uv.x - Mathf.Floor(uv.x), uv.y - Mathf.Floor(uv.y));

        /// <summary>
        /// 脚がほかの肌とどれだけ違う色に塗られているか（脚の肌の色と胴・腕の肌の色の中央値どうしの L*a*b* での距離:
        /// 色は、各頂点でそれを描くマテリアルのメインテクスチャにマテリアルの色を掛けたもの）。
        /// 体にタイツやソックスが描かれていると大きくなる: そのとき上に重ねた透ける衣装からは描かれたものが見える。
        /// どちらかに頂点がなければ -1。
        /// </summary>
        public static float LegContrast(SkinnedMeshRenderer smr, Animator animator)
        {
            var mesh = smr.sharedMesh;
            if (mesh == null) return -1;
            var avatarBone = BodyFinder.AvatarBone(animator);
            Transform B(HumanBodyBones b) => animator.GetBoneTransform(b);
            var legs = new[] { B(HumanBodyBones.LeftUpperLeg), B(HumanBodyBones.RightUpperLeg) }.Where(t => t != null).ToArray();
            var arms = new[] { B(HumanBodyBones.LeftUpperArm), B(HumanBodyBones.RightUpperArm) }.Where(t => t != null).ToArray();
            var torso = new[] { B(HumanBodyBones.Spine), B(HumanBodyBones.Chest), B(HumanBodyBones.UpperChest) }.Where(t => t != null).ToArray();
            // ボーンごと: 1 は脚、2 は胴または腕、0 はどちらでもない
            var part = smr.bones.Select(b =>
            {
                var a = b == null ? null : avatarBone(b);
                if (a == null) return 0;
                if (legs.Any(a.IsChildOf)) return 1;
                if (arms.Any(a.IsChildOf) || torso.Contains(a)) return 2;
                return 0;
            }).ToArray();
            var bpv = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var of = new int[bpv.Length];
            for (int v = 0, w = 0; v < bpv.Length; v++)
            {
                float l = 0, r = 0;
                for (var k = 0; k < bpv[v]; k++, w++)
                {
                    var bi = weights[w].boneIndex;
                    if (bi >= part.Length) continue;
                    if (part[bi] == 1) l += weights[w].weight;
                    else if (part[bi] == 2) r += weights[w].weight;
                }
                of[v] = l >= OnThePart ? 1 : r >= OnThePart ? 2 : 0;
            }
            var uv = mesh.uv;
            if (uv.Length != bpv.Length) return -1;
            var materials = smr.sharedMaterials;
            var read = new Dictionary<Texture, Texture2D>();
            var leg = new List<Vector3>();
            var rest = new List<Vector3>();
            for (var s = 0; s < mesh.subMeshCount && s < materials.Length; s++)
            {
                var mat = materials[s];
                if (mat == null || mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var (tint, tex) = MainTexture(mat);
                if (tex != null && !read.ContainsKey(tex)) read[tex] = Readable(tex);
                foreach (var i in new HashSet<int>(mesh.GetTriangles(s)))
                {
                    if (of[i] == 0) continue;
                    var c = tex == null ? tint : Sample(read[tex], uv[i]) * tint;
                    (of[i] == 1 ? leg : rest).Add(Lab(c));
                }
            }
            foreach (var t in read.Values) Object.DestroyImmediate(t);
            if (leg.Count == 0 || rest.Count == 0) return -1;
            return (Median(leg) - Median(rest)).magnitude;
        }

        /// <summary>SampleSize でのテクスチャのピクセル（sRGB の値）。レンダーテクスチャを通して読む（テクスチャは読み
        /// 取り可能でないことがある）。破棄は呼び出し側が行う。</summary>
        static Texture2D Readable(Texture tex)
        {
            var rt = RenderTexture.GetTemporary(SampleSize, SampleSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = rt;
            var read = new Texture2D(SampleSize, SampleSize, TextureFormat.RGBA32, false, false);
            read.ReadPixels(new Rect(0, 0, SampleSize, SampleSize), 0, 0);
            read.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return read;
        }

        static Vector3 Median(List<Vector3> v)
        {
            float M(System.Func<Vector3, float> f) => v.Select(f).OrderBy(x => x).ElementAt(v.Count / 2);
            return new Vector3(M(c => c.x), M(c => c.y), M(c => c.z));
        }

        /// <summary>sRGB の色の CIE L*a*b*（D65）。</summary>
        internal static Vector3 Lab(Color c)
        {
            float Lin(float u) => u <= 0.04045f ? u / 12.92f : Mathf.Pow((u + 0.055f) / 1.055f, 2.4f);
            float r = Lin(Mathf.Clamp01(c.r)), g = Lin(Mathf.Clamp01(c.g)), b = Lin(Mathf.Clamp01(c.b));
            var x = (0.4124564f * r + 0.3575761f * g + 0.1804375f * b) / 0.95047f;
            var y = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
            var z = (0.0193339f * r + 0.1191920f * g + 0.9503041f * b) / 1.08883f;
            float F(float t) => t > 216f / 24389f ? Mathf.Pow(t, 1f / 3f) : (24389f / 27f * t + 16f) / 116f;
            return new Vector3(116f * F(y) - 16f, 500f * (F(x) - F(y)), 200f * (F(y) - F(z)));
        }
    }
}
