// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// Blender が必要とする形のアバターの体のメッシュ: メッシュ自身の（バインド）空間でのメッシュ、
    /// その空間でのヒューマノイドの目印の位置、各頂点がどれだけ強く脚に属するか。
    /// SkinnedMeshRenderer の今のメッシュを使うので、メッシュやボーンが元の FBX と違うプレハブもそのまま扱える。
    /// </summary>
    public class AvatarBody
    {
        public GameObject Root;
        public Animator Animator;
        public SkinnedMeshRenderer Renderer;
        public Mesh Mesh;
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector2[] UV;
        public int[] Triangles;
        public float[] LegWeight;
        /// <summary>頂点ごと: 1 - 最大のボーンウェイト（1 本のボーンだけで動くところは 0、関節では最大 ~0.5）。
        /// </summary>
        public float[] JointMix;
        /// <summary>
        /// 頂点ごと: 足の指ごとに骨を持つアバターで、ウェイトの半分より多くを持つ足の指（左右の Toes の子ごとの枝。
        /// 1 から、右は 100 から）。指の骨が無い、またはどの指でもない頂点は 0。
        /// </summary>
        public int[] ToeDigit;
        /// <summary>体のレンダラーの骨の番号のうち、足の指の骨（Toes の子の枝）の物 -> その指の番号。</summary>
        public Dictionary<int, int> ToeDigitBones = new();
        /// <summary>頂点ごと: そこで衣装の下に着ているものの高さ（m）（UnderLayers）。素肌は 0。</summary>
        public float[] UnderHeight;
        /// <summary>頂点ごと: 着ているものが実際に肌の上にあるところは 1（その縁では 0..1）。</summary>
        public float[] UnderCovered;
        /// <summary>頂点ごと: そこで衣装の上に着ているものの高さ（m）（UnderLayers.Ceiling）。なければ 0。</summary>
        public float[] OverHeight;
        /// <summary>衣装の下に着ているもの。このメッシュの空間で表す（UnderSurface）。Blender 用。</summary>
        public Vector3[] UnderVerts = new Vector3[0];
        public int[] UnderTris = new int[0];
        /// <summary>UnderVerts の頂点ごと: この体のボーンでのウェイト（それらが体のボーンでなければ null）。</summary>
        public Dictionary<int, float>[] UnderWeights = new Dictionary<int, float>[0];
        /// <summary>頂点ごと: 着ているものがその上か隣にある（UnderLayers.Heights）。</summary>
        public bool[] UnderPresent;
        /// <summary>頂点ごと: 着ているものが実際にその上にある（余分な 1 周を含まない UnderPresent）。</summary>
        public bool[] UnderCore;
        public Dictionary<string, Vector3?> Landmarks = new();
        public float MetersPerUnit;

        static readonly (string key, HumanBodyBones bone)[] LandmarkBones =
        {
            ("hips", HumanBodyBones.Hips),
            ("spine", HumanBodyBones.Spine),
            ("chest", HumanBodyBones.Chest),
            ("upperLegL", HumanBodyBones.LeftUpperLeg),
            ("upperLegR", HumanBodyBones.RightUpperLeg),
            ("lowerLegL", HumanBodyBones.LeftLowerLeg),
            ("lowerLegR", HumanBodyBones.RightLowerLeg),
            ("footL", HumanBodyBones.LeftFoot),
            ("footR", HumanBodyBones.RightFoot),
            ("toesL", HumanBodyBones.LeftToes),
            ("toesR", HumanBodyBones.RightToes),
            ("upperArmL", HumanBodyBones.LeftUpperArm),
            ("upperArmR", HumanBodyBones.RightUpperArm),
            ("lowerArmL", HumanBodyBones.LeftLowerArm),
            ("lowerArmR", HumanBodyBones.RightLowerArm),
            ("handL", HumanBodyBones.LeftHand),
            ("handR", HumanBodyBones.RightHand),
            ("middleL", HumanBodyBones.LeftMiddleProximal),
            ("middleR", HumanBodyBones.RightMiddleProximal),
            ("thumbL", HumanBodyBones.LeftThumbProximal),
            ("thumbR", HumanBodyBones.RightThumbProximal),
            ("thumbTipL", HumanBodyBones.LeftThumbIntermediate),
            ("thumbTipR", HumanBodyBones.RightThumbIntermediate),
            ("middleTipL", HumanBodyBones.LeftMiddleDistal),
            ("middleTipR", HumanBodyBones.RightMiddleDistal),
            ("indexL", HumanBodyBones.LeftIndexProximal),
            ("indexR", HumanBodyBones.RightIndexProximal),
        };

        public static Animator FindHumanoid(GameObject root, out string error)
        {
            error = null;
            var animator = root.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            {
                error = "Humanoid の Animator が見つかりません（アバターのルートを指定してください）。";
                return null;
            }
            return animator;
        }

        /// <summary>脚のボーン: Hips とその下のすべて。ただし背骨のチェーンは除く。</summary>
        public static HashSet<Transform> LegFamily(Animator animator)
        {
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            var spine = animator.GetBoneTransform(HumanBodyBones.Spine);
            var set = new HashSet<Transform> { hips };
            foreach (var t in hips.GetComponentsInChildren<Transform>(true))
                if (spine == null || !t.IsChildOf(spine))
                    set.Add(t);
            return set;
        }

        /// <summary>体のレンダラーの候補。良いものから順に並べる（BodyFinder.Candidates）。</summary>
        public static List<SkinnedMeshRenderer> FindBodyCandidates(GameObject root, Animator animator) =>
            BodyFinder.Candidates(root, animator);

        public static AvatarBody Read(GameObject root, Animator animator, SkinnedMeshRenderer smr)
        {
            var body = new AvatarBody { Root = root, Animator = animator, Renderer = smr, Mesh = smr.sharedMesh };
            var mesh = body.Mesh;
            body.Vertices = mesh.vertices;
            body.Normals = mesh.normals;
            if (body.Normals.Length != body.Vertices.Length) body.Normals = new Vector3[body.Vertices.Length];
            body.UV = mesh.uv;
            if (body.UV.Length != body.Vertices.Length) body.UV = new Vector2[body.Vertices.Length];
            var tris = new List<int>();
            for (var s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles)
                    tris.AddRange(mesh.GetTriangles(s));
            body.Triangles = tris.ToArray();

            // （体のボーンをアバターのものとして扱う: ビルド時に統合される体は、自分用のアーマチュアの複製を持つ）
            var avatarBone = BodyFinder.AvatarBone(animator);
            var bones = smr.bones.Select(avatarBone).ToArray();
            var legFamily = LegFamily(animator);
            var isLeg = bones.Select(b => b != null && legFamily.Contains(b)).ToArray();
            var bpv = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            body.LegWeight = new float[body.Vertices.Length];
            body.JointMix = new float[body.Vertices.Length];
            // 足の指の骨: 左右の Toes の子ごとの枝の番号（Toes 自身とその外は 0）
            var digitOf = new Dictionary<Transform, int>();
            foreach (var (toes, start) in new[] { (animator.GetBoneTransform(HumanBodyBones.LeftToes), 1),
                                                  (animator.GetBoneTransform(HumanBodyBones.RightToes), 101) })
            {
                if (toes == null) continue;
                var n = start;
                foreach (Transform child in toes)
                {
                    foreach (var t in child.GetComponentsInChildren<Transform>(true)) digitOf[t] = n;
                    n++;
                }
            }
            var digitBone = bones.Select(b => b != null && digitOf.TryGetValue(b, out var d) ? d : 0).ToArray();
            body.ToeDigitBones = Enumerable.Range(0, digitBone.Length).Where(i => digitBone[i] > 0).ToDictionary(i => i, i => digitBone[i]);
            body.ToeDigit = new int[body.Vertices.Length];
            var w = 0;
            for (var v = 0; v < bpv.Length; v++)
            {
                float sum = 0, total = 0, max = 0;
                var perDigit = new Dictionary<int, float>();
                for (var k = 0; k < bpv[v]; k++, w++)
                {
                    total += weights[w].weight;
                    max = Mathf.Max(max, weights[w].weight);
                    var bi = weights[w].boneIndex;
                    if (bi < isLeg.Length && isLeg[bi]) sum += weights[w].weight;
                    if (bi < digitBone.Length && digitBone[bi] > 0)
                    {
                        perDigit.TryGetValue(digitBone[bi], out var cur);
                        perDigit[digitBone[bi]] = cur + weights[w].weight;
                    }
                }
                body.LegWeight[v] = total > 0 ? sum / total : 0;
                body.JointMix[v] = total > 0 ? 1f - max / total : 0;
                foreach (var kv in perDigit)
                    if (kv.Value > total * 0.5f) body.ToeDigit[v] = kv.Key;
            }

            var bindposes = mesh.bindposes;
            foreach (var (key, hb) in LandmarkBones)
            {
                var t = animator.GetBoneTransform(hb);
                body.Landmarks[key] = t == null ? null : MeshSpacePosition(t, bones, bindposes);
            }
            if (body.Landmarks["chest"] == null)
                body.Landmarks["chest"] = MeshSpacePosition(animator.GetBoneTransform(HumanBodyBones.UpperChest), bones, bindposes);
            foreach (var key in new[] { "hips", "upperLegL", "upperLegR", "lowerLegL", "lowerLegR", "footL", "footR" })
                if (body.Landmarks[key] == null)
                    throw new InvalidOperationException($"必要なボーン（{key}）がボディメッシュに見つかりません。");
            if (body.Landmarks["chest"] == null && body.Landmarks["spine"] == null)
                throw new InvalidOperationException("Spine / Chest ボーンが見つかりません。");

            // メッシュの単位 -> メートル。メッシュ空間での太ももの長さとシーンでの長さから求める
            var meshLen = Vector3.Distance(body.Landmarks["upperLegL"].Value, body.Landmarks["lowerLegL"].Value);
            var worldLen = Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg).position,
                animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg).position);
            body.MetersPerUnit = meshLen > 1e-8f ? worldLen / meshLen : 1f;
            return body;
        }

        /// <summary>メッシュのバインド空間でのボーンの位置（そのボーン自身のバインドポーズ、
        /// またはバインドされた最も近い祖先を通して求める）。</summary>
        static Vector3? MeshSpacePosition(Transform t, Transform[] bones, Matrix4x4[] bindposes)
        {
            if (t == null) return null;
            for (var cur = t; cur != null; cur = cur.parent)
            {
                var i = Array.IndexOf(bones, cur);
                if (i < 0 || i >= bindposes.Length) continue;
                var local = cur == t ? Vector3.zero : cur.InverseTransformPoint(t.position);
                return bindposes[i].inverse.MultiplyPoint3x4(local);
            }
            return null;
        }
    }
}
