// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Linq;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// 体の脚の肌がどこまであるか: 1 つの衣装向けに作られたアバターは、その衣装が覆う肌を省いていることがある（腰を隠す
    /// スカートの下に腰がない、ブーツの中に下腿がない）。そのとき衣装は肌と同じところで切りっぱなしの断面で終わる。
    /// </summary>
    public static class BodyCoverage
    {
        /// <summary>
        /// 脚の肌が股関節からどれだけ下で始まるか（0: 股関節、1: 膝）と、足首からどれだけ上で終わるか（0:
        /// 足首かそれより下、1: 膝）。2 本の脚のうち悪いほう。肌のない脚があれば -1。
        /// </summary>
        public static (float top, float bottom) LegSpan(SkinnedMeshRenderer body, Animator animator)
        {
            var mesh = body.sharedMesh;
            if (mesh == null) return (-1, -1);
            var avatarBone = BodyFinder.AvatarBone(animator);
            var bpv = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            var world = MeshMath.WorldPositions(body);
            var up = animator.transform.up;
            float top = 0, bottom = 0;
            foreach (var (upperB, lowerB, footB) in new[]
                     {
                         (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
                         (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
                     })
            {
                var upper = animator.GetBoneTransform(upperB);
                var lower = animator.GetBoneTransform(lowerB);
                var foot = animator.GetBoneTransform(footB);
                if (upper == null || lower == null || foot == null) return (-1, -1);
                var onLeg = body.bones.Select(b =>
                {
                    var a = b == null ? null : avatarBone(b);
                    return a != null && a.IsChildOf(upper);
                }).ToArray();
                float hi = float.NegativeInfinity, lo = float.PositiveInfinity;
                for (int v = 0, w = 0; v < bpv.Length; v++)
                {
                    float sum = 0;
                    for (var k = 0; k < bpv[v]; k++, w++)
                        if (weights[w].boneIndex < onLeg.Length && onLeg[weights[w].boneIndex]) sum += weights[w].weight;
                    // （脚の肌: BodyFinder の数え方で）
                    if (sum < BodyFinder.OnTheLeg) continue;
                    var h = Vector3.Dot(world[v], up);
                    hi = Mathf.Max(hi, h);
                    lo = Mathf.Min(lo, h);
                }
                if (float.IsInfinity(hi)) return (-1, -1);
                float hip = Vector3.Dot(upper.position, up), knee = Vector3.Dot(lower.position, up), ankle = Vector3.Dot(foot.position, up);
                top = Mathf.Max(top, Mathf.Clamp01((hip - hi) / (hip - knee)));
                bottom = Mathf.Max(bottom, Mathf.Clamp01((lo - ankle) / (knee - ankle)));
            }
            return (top, bottom);
        }
    }
}
