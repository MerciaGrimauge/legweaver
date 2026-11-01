// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: MIT

using UnityEngine;
using VRC.SDKBase;

namespace LegWeaver
{
    /// <summary>
    /// 衣装の下に着ているものの下に残す肌を、それと一緒に動かす（生成処理が設定し、
    /// アバターのビルド時に SkinFollowPlugin が適用してから取り除く）: 体の一部の頂点に与える新しいボーンウェイト。
    /// アバター自身のメッシュは変えない。
    /// </summary>
    [AddComponentMenu("")]
    public class SkinFollow : MonoBehaviour, IEditorOnly
    {
        public SkinnedMeshRenderer body;
        /// <summary>新しいウェイトを与える体の頂点。</summary>
        public int[] vertices = new int[0];
        /// <summary>`vertices` の頂点ごと: `bones` / `weights` のうちいくつがその頂点のものか（順に並ぶ）。</summary>
        public int[] counts = new int[0];
        /// <summary>体のレンダラーのボーンを指すボーンの番号。</summary>
        public int[] bones = new int[0];
        public float[] weights = new float[0];
    }
}
