// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: MIT

using UnityEngine;
using VRC.SDKBase;

namespace LegWeaver
{
    /// <summary>
    /// 作った衣装のオブジェクトに付ける覚え書き: その衣装のために体などへ加えた変更（肌の削除の部品、0 にした
    /// 縮めるシェイプキーとその元の値）。ウィンドウの「外す」が、衣装と一緒にこれらを元に戻すのに使う。
    /// アバターのビルドには入らない。
    /// </summary>
    [AddComponentMenu("")]
    public class GarmentParts : MonoBehaviour, IEditorOnly
    {
        /// <summary>この衣装のために加えた肌の削除の部品（体や下に着ている物のオブジェクトに付いている）。</summary>
        public Component[] removals = new Component[0];
        /// <summary>縮めるシェイプキーを 0 にした体（無ければ null）。</summary>
        public SkinnedMeshRenderer shrinkBody;
        public string[] shrinkShapes = new string[0];
        /// <summary>`shrinkShapes` の元の値。</summary>
        public float[] shrinkWeights = new float[0];
    }
}
