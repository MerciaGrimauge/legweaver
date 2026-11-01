// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// ポーズに応じた伸びのシェーダー（Shaders/custom.hlsl）用のデータ。なめらかに変わるよう頂点ごとに持つ:
    /// - タンジェント: 脚に沿う単位ベクトルを、立ちポーズでスキニングしたとき長さ 1 になるよう拡大縮小したもの。
    ///   ブレンドスキニングは関節が曲がるところでこれを短くするので、c = 1 - length が各頂点の曲がりを表す。
    /// - UV3.x: k。c の単位あたりにそこのニットがどれだけ伸びるか。衣装自身のアーマチュアで脚（股関節・膝・足首）を
    ///   曲げ、各頂点のまわりの面積を立ちポーズと比べて求める。
    /// 脚だけ: 腕では伸びがボーンの接するところ（指の付け根、肘の裏）に出るが、タンジェントはそれを捉えないので、
    /// 腕の衣装は静的な伸びのマスクだけを使う。
    /// </summary>
    public static class PoseStretch
    {
        const float MaxK = 12f;
        /// <summary>
        /// k を測るときのタンジェントの縮みの最小値: 頂点が 1 本のボーンに従うところ（足に乗るかかと）では
        /// タンジェントはほとんど縮まず、その 0 に近い縮みに対する面積の変化は大きくなりすぎる（形の鋭いかかとでは、
        /// 足を伸ばしたときにニットがほぼ不透明に描かれる）。
        /// </summary>
        const float MinShrink = 0.05f;

        /// <summary>
        /// 伸びを測る唯一のポーズ（度）: 股関節・膝・足首を、座ったときや大股のときくらいまで曲げる。
        /// 残すのはタンジェントの縮みの単位あたりの伸びなので、正確な角度はあまり関係しない。
        /// ここまで曲げれば伸びは丸め誤差より十分大きく、まだ関節で折り重なってもいない。
        /// </summary>
        const float HipBend = 70f, KneeBend = 100f, AnkleBend = 30f;

        public static void Store(SkinnedMeshRenderer r, Animator animator)
        {
            var mesh = r.sharedMesh;
            var n = mesh.vertexCount;
            var sk = new Skinning(r);
            // 脚に沿う向き（膝と股関節の曲げの軸に垂直）。メッシュ空間で
            var up = r.transform.InverseTransformDirection(animator.transform.up).normalized;
            var rest = sk.Matrices();
            var tangents = new Vector4[n];
            for (var v = 0; v < n; v++)
            {
                var len = sk.Skin(rest, v, up, false).magnitude;
                var t = len > 1e-6f ? up / len : up;
                tangents[v] = new Vector4(t.x, t.y, t.z, 1f);
            }

            // 衣装のアーマチュアで脚を曲げる（そのボーンはアバターの名前を持つ）
            var forward = animator.transform.forward;
            var bent = sk.Posed(animator, bone =>
            {
                var axis = Vector3.Cross(animator.transform.up, forward);
                foreach (var (u, l, f) in Legs(bone))
                {
                    Bend(u, l.position, axis, HipBend, forward);
                    Bend(l, f.position, axis, KneeBend, -forward);
                    Bend(f, f.position + forward * 0.1f, axis, AnkleBend, Vector3.down);
                }
            });

            // 頂点ごと: 今のまわりの面積 / 立ちポーズでの面積と、タンジェントの縮んだ量
            var a0 = sk.Areas(rest);
            var a1 = sk.Areas(bent);
            var k = new float[n];
            for (var v = 0; v < n; v++)
            {
                var c = 1f - sk.Skin(bent, v, tangents[v], false).magnitude;
                if (c < 0.01f || a0[v] <= 0) continue;
                k[v] = Mathf.Clamp((a1[v] / a0[v] - 1f) / Mathf.Max(c, MinShrink), -MaxK, MaxK);
            }
            // 隣どうしでならす（面積は三角形ごと）
            var nb = MeshMath.Neighbours(mesh.triangles, n);
            // 3 回: 変化を隣の三角形へ広げ、粗いメッシュで関節に三角形の形の斑が出ないようにする
            var smooth = k;
            for (var round = 0; round < 3; round++)
            {
                var prev = smooth;
                smooth = new float[n];
                for (var v = 0; v < n; v++)
                    smooth[v] = nb[v].Count == 0 ? prev[v] : (prev[v] + nb[v].Average(j => prev[j])) * 0.5f;
            }

            mesh.tangents = tangents;
            mesh.SetUVs(3, smooth.Select(x => new Vector2(x, 0f)).ToList());
            EditorUtility.SetDirty(mesh);
        }

        /// <summary>アーマチュアの左右の（上腿・下腿・足）。すべてそろっている場合。</summary>
        public static IEnumerable<(Transform upper, Transform lower, Transform foot)> Legs(System.Func<HumanBodyBones, Transform> bone)
        {
            foreach (var (upper, lower, foot) in new[]
                     {
                         (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
                         (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
                     })
            {
                var (u, l, f) = (bone(upper), bone(lower), bone(foot));
                if (u != null && l != null && f != null) yield return (u, l, f);
            }
        }

        /// <summary>`bone` を `axis` まわりに `degrees` だけ、`point` を `toward` へ近づける向きに回す。</summary>
        public static void Bend(Transform bone, Vector3 point, Vector3 axis, float degrees, Vector3 toward)
        {
            var local = bone.InverseTransformPoint(point);
            var rest = bone.rotation;
            bone.rotation = Quaternion.AngleAxis(degrees, axis.normalized) * rest;
            if (Vector3.Dot(bone.TransformPoint(local) - point, toward) < 0)
                bone.rotation = Quaternion.AngleAxis(-degrees, axis.normalized) * rest;
        }

        /// <summary>衣装の CPU 上での線形ブレンドスキニング。スキニング後の頂点がシェーダーに届く空間で行う。</summary>
        class Skinning
        {
            readonly SkinnedMeshRenderer _r;
            readonly Matrix4x4[] _bind;
            readonly byte[] _bpv;
            readonly BoneWeight1[] _all;
            readonly int[] _start;
            readonly Vector3[] _verts;
            readonly int[] _tris;

            public Skinning(SkinnedMeshRenderer r)
            {
                _r = r;
                var mesh = r.sharedMesh;
                _bind = mesh.bindposes;
                _bpv = mesh.GetBonesPerVertex().ToArray();
                _all = mesh.GetAllBoneWeights().ToArray();
                _start = new int[_bpv.Length];
                for (int v = 0, s = 0; v < _bpv.Length; s += _bpv[v], v++) _start[v] = s;
                _verts = mesh.vertices;
                _tris = mesh.triangles;
            }

            /// <summary>今のスキニング行列。ルートボーンの空間でその拡大縮小を除いたもの（シェーダーから見たとおり）。
            /// </summary>
            public Matrix4x4[] Matrices()
            {
                var space = _r.rootBone != null ? _r.rootBone : _r.transform;
                var toSpace = Matrix4x4.TRS(space.position, space.rotation, Vector3.one).inverse;
                return _r.bones.Select((b, i) => b == null ? Matrix4x4.identity : toSpace * b.localToWorldMatrix * _bind[i]).ToArray();
            }

            /// <summary>衣装のアーマチュアを `pose` にしたときの行列（そのボーンはアバターの名前を持つ。
            /// 後で元に戻す）。</summary>
            public Matrix4x4[] Posed(Animator animator, System.Action<System.Func<HumanBodyBones, Transform>> pose)
            {
                var bones = _r.bones;
                var saved = bones.Where(b => b != null).Distinct().ToDictionary(b => b, b => b.localRotation);
                Transform Copy(HumanBodyBones hb)
                {
                    var original = animator.GetBoneTransform(hb);
                    return original == null ? null : bones.FirstOrDefault(b => b != null && b.name == original.name);
                }
                pose(Copy);
                var m = Matrices();
                foreach (var kv in saved) kv.Key.localRotation = kv.Value;
                return m;
            }

            public Vector3 Skin(Matrix4x4[] m, int v, Vector3 x, bool point)
            {
                var acc = Vector3.zero;
                for (var k = 0; k < _bpv[v]; k++)
                {
                    var w = _all[_start[v] + k];
                    acc += w.weight * (point ? m[w.boneIndex].MultiplyPoint3x4(x) : m[w.boneIndex].MultiplyVector(x));
                }
                return acc;
            }

            /// <summary>頂点ごと: `m` でスキニングした、まわりの三角形の面積。</summary>
            public float[] Areas(Matrix4x4[] m)
            {
                var p = new Vector3[_verts.Length];
                for (var v = 0; v < p.Length; v++) p[v] = Skin(m, v, _verts[v], true);
                var a = new float[p.Length];
                for (var i = 0; i < _tris.Length; i += 3)
                {
                    var (x, y, z) = (_tris[i], _tris[i + 1], _tris[i + 2]);
                    var r = Vector3.Cross(p[y] - p[x], p[z] - p[x]).magnitude;
                    a[x] += r;
                    a[y] += r;
                    a[z] += r;
                }
                return a;
            }
        }
    }
}
