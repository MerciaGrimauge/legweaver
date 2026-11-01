// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// エディタでの SkinFollow（NDMF のプレビュー）。プレイモードに入らずに使える:
    /// 体をビルド後に持つウェイトで描くので、アバターにポーズを取らせると、
    /// 肌がその上に着ているものと一緒に動くのが見える。
    /// </summary>
    internal class SkinFollowPreview : IRenderFilter
    {
        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var groups = new Dictionary<Renderer, List<SkinFollow>>();
            foreach (var root in context.GetAvatarRoots().Where(r => context.ActiveInHierarchy(r)))
                foreach (var follow in context.GetComponentsInChildren<SkinFollow>(root, true))
                {
                    var target = context.Observe(follow, f => f.body);
                    if (target == null) continue;
                    if (!groups.TryGetValue(target, out var list)) groups[target] = list = new List<SkinFollow>();
                    list.Add(follow);
                }
            return groups.Select(kv => RenderGroup.For(kv.Key).WithData(kv.Value.ToImmutableList(),
                (a, b) => a.SequenceEqual(b))).ToImmutableList();
        }

        public Task<IRenderFilterNode> Instantiate(RenderGroup group, IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            var follows = group.GetData<ImmutableList<SkinFollow>>();
            Mesh mesh = null;
            var made = false;
            foreach (var (_, proxy) in proxyPairs)
            {
                if (proxy is not SkinnedMeshRenderer smr || smr.sharedMesh == null) continue;
                var current = smr.sharedMesh;
                foreach (var follow in follows)
                {
                    // （保存されたウェイトが変わるたびに作り直す）
                    context.Observe(follow, f => (f.vertices, f.counts, f.bones, f.weights));
                    var next = SkinFollowPlugin.Apply(current, follow);
                    // （前のもの用に作った複製は、新しい複製に置き換わった時点で消す。
                    // Apply は入力をそのまま返すことがある）
                    if (next != current && current != smr.sharedMesh) Object.DestroyImmediate(current);
                    current = next;
                }
                mesh = current;
                made = current != smr.sharedMesh;
            }
            // 何も適用しなかった（ウェイトがメッシュに合わない）: プロキシは自身のメッシュを持ち続け、
            // それはこちらで破棄してよいものではない
            return Task.FromResult<IRenderFilterNode>(new Node(made ? mesh : null));
        }

        class Node : IRenderFilterNode
        {
            readonly Mesh _mesh;

            public Node(Mesh mesh) => _mesh = mesh;

            public RenderAspects WhatChanged => RenderAspects.Mesh;

            public Task<IRenderFilterNode> Refresh(IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context,
                RenderAspects updatedAspects) =>
                Task.FromResult<IRenderFilterNode>(updatedAspects.HasFlag(RenderAspects.Mesh) ? null : this);

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (_mesh != null && proxy is SkinnedMeshRenderer smr) smr.sharedMesh = _mesh;
            }

            public void Dispose()
            {
                if (_mesh != null) Object.DestroyImmediate(_mesh);
            }
        }
    }
}
