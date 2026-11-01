// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if LW_MA
using nadena.dev.modular_avatar.core;
#endif
#if LW_AAO
using Anatawa12.AvatarOptimizer;
#endif

namespace LegWeaver
{
    /// <summary>
    /// 生成した衣装を衣装としてアバターに着せる（元に戻せる）: 衣装が使うボーンの複製（アバターのものと同じ名前と
    /// トランスフォーム）とレンダラーを持つオブジェクトを、体のオブジェクトの下に置き（体と一緒に表示・非表示が
    /// 切り替わる。体のオブジェクトがアバター自体の場合、不均一に拡大縮小されている場合、アーマチュアの中にある
    /// 場合はアバターのルートの下）、Modular Avatar の Setup Outfit でつなぐ。MA がビルド時にアバターの
    /// アーマチュアへ統合する。
    /// </summary>
    public static class GarmentInstaller
    {
        /// <param name="skin">任意の肌の写し（メッシュとマテリアル）。2 つ目のレンダラーで描く</param>
        /// <returns>衣装のレンダラーと裏地のレンダラー（裏地が無ければ null）</returns>
        public static (SkinnedMeshRenderer garment, SkinnedMeshRenderer skin) Install(AvatarBody body, Mesh mesh,
            Material[] materials, string objectName, IEnumerable<string> syncedShapes,
            (Mesh mesh, Material[] materials)? skin = null)
        {
            var src = body.Renderer;
            var avatarRoot = body.Root.transform;
            var outfit = new GameObject(objectName);
            Undo.RegisterCreatedObjectUndo(outfit, "LegWeaver: Generate");
            outfit.transform.SetParent(avatarRoot, false);

            var map = CopyArmature(body, outfit.transform);

            var shapes = syncedShapes.ToList();
            SkinnedMeshRenderer Add(string goName, Mesh m, Material[] mats)
            {
                // メッシュは体のレンダラーのメッシュ空間にあるので、レンダラーに同じワールドのトランスフォームを与える
                var go = new GameObject(goName);
                go.transform.SetParent(outfit.transform, false);
                go.transform.SetPositionAndRotation(src.transform.position, src.transform.rotation);
                go.transform.localScale = DivideScale(src.transform.lossyScale, outfit.transform.lossyScale);

                var r = go.AddComponent<SkinnedMeshRenderer>();
                r.sharedMesh = m;
                r.bones = src.bones.Select(b => b != null && map.TryGetValue(b, out var c) ? c : b).ToArray();
                r.rootBone = src.rootBone != null && map.TryGetValue(src.rootBone, out var rb) ? rb : src.rootBone;
                // サブメッシュより多いマテリアルは最後のサブメッシュをもう一度描く: 裏地の上の外側の編み地
                r.sharedMaterials = mats;
                CopyRendering(src, r);
                foreach (var name in shapes)
                {
                    var si = src.sharedMesh.GetBlendShapeIndex(name);
                    var di = m.GetBlendShapeIndex(name);
                    if (si >= 0 && di >= 0) r.SetBlendShapeWeight(di, src.GetBlendShapeWeight(si));
                }
                AddBlendshapeSync(go, src, shapes);
                return r;
            }

            var garment = Add(objectName + "_Mesh", mesh, materials);
            var lining = skin is { } s && s.mesh != null ? Add(objectName + "_Skin", s.mesh, s.materials) : null;
            // 体のオブジェクトがアバター自体でなければ、その下に置く: 元にした体と一緒に表示・非表示が切り替わる
            // （脚を別の下半身に切り替えるアバターは体を隠し、衣装も一緒に隠れる）。
            // ワールドでの姿勢はそのまま保つ。
            var armature = BodyFinder.Armature(body.Animator);
            if (src.transform != avatarRoot && src.transform.IsChildOf(avatarRoot) && EvenlyScaled(src.transform) &&
                (armature == null || !src.transform.IsChildOf(armature)))
                outfit.transform.SetParent(src.transform, true);
#if LW_MA
            nadena.dev.modular_avatar.core.editor.SetupOutfit.SetupOutfitUI(outfit);
#endif
            return (garment, lining);
        }

        /// <summary>
        /// `t` が 3 軸で同じだけ拡大縮小するか（相対 1%）: その下の子は回転にかかわらず形を保つ。
        /// </summary>
        static bool EvenlyScaled(Transform t)
        {
            var s = t.lossyScale;
            var max = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            var min = Mathf.Min(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
            return max > 0 && (max - min) / max < 0.01f;
        }

        /// <summary>
        /// レンダラーの描画の設定（境界、ライトプローブとリフレクションプローブ、影）を `from` から `to` へ写す。
        /// </summary>
        public static void CopyRendering(SkinnedMeshRenderer from, SkinnedMeshRenderer to)
        {
            to.localBounds = from.localBounds;
            to.probeAnchor = from.probeAnchor;
            to.shadowCastingMode = from.shadowCastingMode;
            to.receiveShadows = from.receiveShadows;
            to.lightProbeUsage = from.lightProbeUsage;
            to.reflectionProbeUsage = from.reflectionProbeUsage;
            to.updateWhenOffscreen = from.updateWhenOffscreen;
        }

        /// <summary>
        /// `go` のレンダラーのシェイプキー `names` を、以後 `source` の値に保つ（Modular Avatar の
        /// Blendshape Sync。MA が無ければ何もしない）。
        /// </summary>
        public static void AddBlendshapeSync(GameObject go, SkinnedMeshRenderer source, IReadOnlyCollection<string> names)
        {
#if LW_MA
            if (names.Count == 0) return;
            var sync = go.AddComponent<ModularAvatarBlendshapeSync>();
            foreach (var name in names)
            {
                var reference = new AvatarObjectReference();
                reference.Set(source.gameObject);
                sync.Bindings.Add(new BlendshapeBinding { ReferenceMesh = reference, Blendshape = name, LocalBlendshape = name });
            }
#endif
        }

        /// <summary>
        /// アバターのアーマチュア（BodyFinder.Armature）を `outfitRoot` に複製する。体のレンダラーが使うボーンへの
        /// 経路上のトランスフォームだけを残す。元 -> 複製 を返す（体がアーマチュアの複製を持つ場合は、体自身の
        /// ボーンも含む）。
        /// </summary>
        static Dictionary<Transform, Transform> CopyArmature(AvatarBody body, Transform outfitRoot)
        {
            var armature = BodyFinder.Armature(body.Animator);
            // （体のボーンをアバターのもので表す: ビルド時に統合される体はアーマチュアの複製を持つ）
            var avatarBone = BodyFinder.AvatarBone(body.Animator);
            var needed = new HashSet<Transform>();
            foreach (var b in body.Renderer.bones.Append(body.Renderer.rootBone).Select(avatarBone))
                for (var t = b; t != null && t.IsChildOf(armature); t = t.parent)
                    if (!needed.Add(t)) break;
            needed.Add(armature);

            var map = new Dictionary<Transform, Transform>();
            Transform Copy(Transform original, Transform parent)
            {
                var copy = new GameObject(original.name).transform;
                copy.SetParent(parent, false);
                copy.localPosition = original.localPosition;
                copy.localRotation = original.localRotation;
                copy.localScale = original.localScale;
                map[original] = copy;
                foreach (Transform child in original)
                    if (needed.Contains(child)) Copy(child, copy);
                return copy;
            }
            var top = Copy(armature, outfitRoot);
            // アーマチュアをアバターのものと同じ所に置く（アバターのルートがずれを持つことがある）
            top.SetPositionAndRotation(armature.position, armature.rotation);
            // 体が自身のアーマチュアを持つ場合、体自身のボーンを、それが表すアバターのボーンの複製へ対応させる
            foreach (var b in body.Renderer.bones.Append(body.Renderer.rootBone))
                if (b != null && !map.ContainsKey(b) && avatarBone(b) is { } a && map.TryGetValue(a, out var c))
                    map[b] = c;
            return map;
        }

        static Vector3 DivideScale(Vector3 a, Vector3 b) => new(a.x / b.x, a.y / b.y, a.z / b.z);

        public static bool CanRemoveSkin
        {
            get
            {
#if LW_AAO
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// 衣装のオブジェクトに SkinFollow を付ける: ビルド時に `weights` にある体の頂点がそのウェイトになる
        /// （SkinFollowPlugin）。衣装と一緒に消える。
        /// </summary>
        public static void AddSkinFollow(AvatarBody body, GameObject garment, Dictionary<int, BoneWeight1[]> weights)
        {
            var comp = Undo.AddComponent<SkinFollow>(garment);
            var order = weights.Keys.OrderBy(v => v).ToArray();
            comp.body = body.Renderer;
            comp.vertices = order;
            comp.counts = order.Select(v => weights[v].Length).ToArray();
            comp.bones = order.SelectMany(v => weights[v].Select(w => w.boneIndex)).ToArray();
            comp.weights = order.SelectMany(v => weights[v].Select(w => w.weight)).ToArray();
            EditorUtility.SetDirty(comp);
        }

        /// <summary>不透明な衣装か肌の写しが隠す肌のために、体に AAO の "Remove Mesh By Box" を追加する。</summary>
        public static int AddSkinRemoval(AvatarBody body, List<SkinRemover.Box> boxes)
        {
#if LW_AAO
            if (boxes.Count == 0) return 0;
            var comp = Undo.AddComponent<RemoveMeshInBox>(body.Renderer.gameObject);
            comp.Initialize(1);
            comp.Boxes = boxes.Select(b => new RemoveMeshInBox.BoundingBox
            {
                Center = b.Center,
                Size = b.Size,
                Rotation = b.Rotation,
            }).ToArray();
            EditorUtility.SetDirty(comp);
            return boxes.Count;
#else
            return 0;
#endif
        }
    }
}
