// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: MIT

using UnityEngine;
using VRC.SDKBase;

namespace LegWeaver
{
    /// <summary>
    /// 肌の削除の部品と同じオブジェクトに付ける印: その削除がどの衣装のためのものか。衣装が消されたら
    /// （または EditorOnly にされたら）、ビルドの最初でその削除を外す（衣装を消しても肌が消えたままにならない）。
    /// アバターのビルドには入らない。
    /// </summary>
    [AddComponentMenu("")]
    public class RemovalOwner : MonoBehaviour, IEditorOnly
    {
        /// <summary>持ち主の衣装のオブジェクト。</summary>
        public GameObject garment;
        /// <summary>持ち主のために加えた肌の削除の部品。</summary>
        public Component removal;
    }
}
