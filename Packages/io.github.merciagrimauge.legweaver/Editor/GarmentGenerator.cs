// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>1 回の生成: 体を書き出し、Blender を実行し、衣装を組み立てて取り付ける。</summary>
    public class GarmentGenerator
    {
        const string OutputRoot = "Assets/LegWeaver_Output";
        /// <summary>このキュー以降で描くマテリアルは透ける（lilToon の半透明のもの以降）。</summary>
        const int SheerQueue = 2450;

        /// <summary>
        /// レンダラーがこのツールの作ったメッシュ（OutputRoot に保存）を描くか: 体や包むメッシュには決してしない。
        /// </summary>
        public static bool IsMade(SkinnedMeshRenderer r) =>
            r != null && r.sharedMesh != null && AssetDatabase.GetAssetPath(r.sharedMesh).StartsWith(OutputRoot + "/");

        public AvatarBody Body { get; private set; }
        public GarmentSettings Settings { get; private set; }
        public BlenderRunner Runner { get; private set; }
        public SkinnedMeshRenderer Result { get; private set; }
        /// <summary>肌の写しのレンダラー（衣装に裏地があれば）。</summary>
        public SkinnedMeshRenderer SkinResult { get; private set; }
        public string Status { get; private set; } = "";
        string _workDir;
        List<string> _shrinkOff = new();
        List<string> _under = new();
        List<SkinnedMeshRenderer> _underRenderers = new();
        /// <summary>衣装を上に重ねたメッシュの名前（衣装モード）。</summary>
        public IReadOnlyList<string> UnderLayerNames => _under;
        /// <summary>着ている物のすぐ下に残す肌と、そのメッシュのウェイト（体の頂点 -> ウェイト）。</summary>
        public Dictionary<int, BoneWeight1[]> SkinFollow { get; private set; }

        /// <summary>
        /// 着ている物のすぐ下に残す肌（UnderCore）のウェイト: そこでの着ているメッシュ自身のもので、肌がその内側に
        /// 収まる。（その周りに残す周は肌自身のものを保つ: 着ているメッシュに引っ張られると、深いポーズで隣の肌から
        /// 裂ける。）
        /// </summary>
        Dictionary<int, BoneWeight1[]> SkinFollowWeights()
        {
            var result = new Dictionary<int, BoneWeight1[]>();
            var core = Enumerable.Range(0, Body.Vertices.Length).Where(i => Body.UnderCore[i]).ToArray();
            var skin = MeshMath.SkinnedPositions(Body.Renderer);
            var points = core.Select(i => Body.Renderer.transform.TransformPoint(skin[i])).ToArray();
            var normals = core.Select(i => Body.Renderer.transform.TransformDirection(Body.Normals[i]).normalized).ToArray();
            var worn = UnderLayers.WeightsAt(Body, _underRenderers, points, normals);
            for (var k = 0; k < core.Length; k++)
            {
                // （上に着ているメッシュが見つからない: 自身のウェイト）
                if (worn[k] == null) continue;
                var top = worn[k].OrderByDescending(x => x.Value).Take(4).ToList();
                var sum = top.Sum(x => x.Value);
                if (sum <= 0) continue;
                result[core[k]] = top.Select(x => new BoneWeight1 { boneIndex = x.Key, weight = x.Value / sum }).ToArray();
            }
            return result;
        }

        /// <summary>
        /// このツールのエディタのアセンブリ定義の GUID（その .meta）: Blender が実行するスクリプトはその隣にある。
        /// </summary>
        const string EditorAsmdefGuid = "3584b39c695ae76429d9120734df4a06";

        /// <summary>
        /// Blender のスクリプト。エディタのアセンブリ定義の GUID で探す（名前では探さない。ほかのパッケージと
        /// 重なり得るため）。相対パスで見つかる何かを実行するのではなく、無ければ例外を投げる。
        /// </summary>
        public static string ScriptPath
        {
            get
            {
                var asmdef = AssetDatabase.GUIDToAssetPath(EditorAsmdefGuid);
                if (string.IsNullOrEmpty(asmdef))
                    throw new FileNotFoundException("LegWeaver のエディタのアセンブリ定義が見つかりません（インストールが壊れています）。");
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(asmdef) ?? "", "Blender", "legweaver.py"));
                if (!File.Exists(path)) throw new FileNotFoundException("Blender 用のスクリプトが見つかりません（インストールが壊れています）。", path);
                return path;
            }
        }

        /// <summary>体を書き出して Blender を起動する。`root` はシーンのオブジェクトでなければならない。</summary>
        /// <param name="wearOver">見つけたメッシュのうち衣装を上に重ねる物（衣装モード）。null = 検出の既定</param>
        /// <param name="wrapTarget">Wrap の衣装が包むメッシュ（null: 最も可能性の高い物、
        /// WrapGarment.Candidates）</param>
        public static GarmentGenerator Start(GameObject root, SkinnedMeshRenderer bodyOverride, GarmentSettings settings,
            Func<UnderLayers.Candidate, bool> wearOver = null, SkinnedMeshRenderer wrapTarget = null)
        {
            var animator = AvatarBody.FindHumanoid(root, out var error);
            if (animator == null) throw new InvalidOperationException(error);
            // 選んだ物はシーンの中のそのアバター自身の物でなければならない: プレハブのアセットやほかのアバターは不可
            if (settings.type != GarmentType.Wrap) wrapTarget = null;
            foreach (var (r, what) in new[] { (bodyOverride, "ボディメッシュ"), (wrapTarget, "包むメッシュ") })
                if (r != null && (EditorUtility.IsPersistent(r) || !r.transform.IsChildOf(root.transform)))
                    throw new InvalidOperationException($"{what}（{r.name}）がアバター {root.name} の中にありません。");
            if (settings.type == GarmentType.Wrap) return StartWrap(root, animator, bodyOverride, settings, wrapTarget);
            var smr = bodyOverride != null ? bodyOverride : AvatarBody.FindBodyCandidates(root, animator).FirstOrDefault();
            if (smr == null) throw new InvalidOperationException("脚のウェイトを持つボディメッシュが見つかりません。");

            var gen = new GarmentGenerator
            {
                Body = AvatarBody.Read(root, animator, smr),
                Settings = CopyOf(settings),
                _workDir = Path.GetFullPath(Path.Combine("Temp", "LegWeaver",
                    DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6))),
            };
            if (gen.Settings.turnOffShrinkShapes)
            {
                gen._shrinkOff = ShrinkShapes.TurnOff(smr, gen.Body.MetersPerUnit);
                if (gen._shrinkOff.Count > 0)
                    Debug.Log("[LegWeaver] シュリンク系シェイプキーを 0 にしました: " + string.Join(", ", gen._shrinkOff));
            }
            if (gen.Settings.outfitMode == OutfitMode.OverUnderLayers)
            {
                var found = UnderLayers.Detect(gen.Body);
                var layers = found.Where(wearOver ?? (c => c.Under)).Select(c => c.Renderer).ToList();
                // ガーター・レッグラップは、前に作ったタイツ・靴下の上に付ける（それらは下着の検出では除いている）。
                // アバター自身の脚の服（太ももの半ばより下まで届く靴下・レッグウォーマーなど）の上にも付ける（実物も
                // 靴下の上からリボンを巻く。下に収めようとすると、服より外に出て突き抜ける）
                var avatarLegwear = found.Where(c => !c.Spats && c.Down > 0.5f).Select(c => c.Renderer).ToList();
                if (gen.Settings.IsAccessory && gen.Settings.overLegwear)
                {
                    layers.AddRange(MadeLegwear(root));
                    if (gen.Settings.overAvatarLegwear) layers.AddRange(avatarLegwear.Except(layers));
                }
                // 衣装の下に着ない物は衣装の上に着る（タイツの上の靴下）: 衣装をその下に収める
                // （タイツと一緒に着ないスパッツは、選ばれない限り無い物と数える。アバターの脚の服の上に巻かない
                // ガーター・レッグラップは、その服を無い物と数える: 服を脱がせた状態に合わせる）
                var ignored = gen.Settings.IsAccessory && !(gen.Settings.overLegwear && gen.Settings.overAvatarLegwear)
                    ? avatarLegwear : new List<SkinnedMeshRenderer>();
                gen.Body.OverHeight = UnderLayers.Ceiling(gen.Body,
                    found.Where(c => !c.Spats).Select(c => c.Renderer).Except(layers).Except(ignored));
                if (found.Count > 0)
                {
                    string How(UnderLayers.Candidate c) =>
                        layers.Contains(c.Renderer) ? "（上に重ねる）" : c.Spats ? "（スパッツ: 無いものとして扱う）" : "（覆わない）";
                    Debug.Log("[LegWeaver] 肌に触れている衣装: " + string.Join(", ",
                        found.Select(c => $"{c.Renderer.name}{How(c)} 太ももの {c.Down:0.00} 密着 {c.Clinging:0.00}")));
                }
                gen.Body.UnderHeight = UnderLayers.Heights(gen.Body, layers, out gen.Body.UnderCovered, out gen.Body.UnderPresent, out gen.Body.UnderCore);
                gen._under = layers.Select(l => l.name).ToList();
                gen._underRenderers = layers;
                (gen.Body.UnderVerts, gen.Body.UnderTris, gen.Body.UnderWeights) = UnderSurface.InBodySpace(gen.Body, layers);
                if (gen._under.Count > 0)
                    Debug.Log("[LegWeaver] 下に着ている物の上に重ねます: " + string.Join(", ", gen._under));
            }
            Directory.CreateDirectory(gen._workDir);
            try
            {
                var job = Path.Combine(gen._workDir, "job.json");
                BlenderJob.Write(gen.Body, gen._workDir, gen.Settings, job);
                gen.Runner = new BlenderRunner();
                // サポートが終わった、または終わりそうな Blender（またはその中の Python）は使わない
                var (verdict, why) = BlenderSupport.Check(BlenderRunner.BlenderPath);
                if (verdict == BlenderSupport.Verdict.Blocked) throw new InvalidOperationException(why);
                gen.Runner.Start(BlenderRunner.BlenderPath, ScriptPath, job, BlenderRunner.GetTimeout(gen.Settings.TimeClass));
                return gen;
            }
            catch
            {
                // （体の生のメッシュが入っているので、決して残さない）
                gen.Discard();
                throw;
            }
        }

        public void Finish()
        {
            if (Settings.type == GarmentType.Wrap)
            {
                FinishWrap();
                return;
            }
            var jsonPath = Path.Combine(_workDir, "output.json");
            if (Runner.TimedOut)
                throw new TimeoutException($"Blender の処理が {Runner.Timeout:0} 秒を超えたため打ち切りました。Blender の欄の「打ち切り時間」を延ばすと完了する場合があります。");
            if (!File.Exists(jsonPath))
                throw new InvalidOperationException($"Blender が結果を出力しませんでした（終了コード {Runner.ExitCode}）。ログを確認してください。");
            var g = Settings;
            var result = GarmentMeshBuilder.ReadResult(jsonPath);
            if (!result.ok) throw new InvalidOperationException(result.error);

            var objectName = $"{Label(g.type)}_{g.denier:0}D";
            var folder = CreateOutputFolder(Body.Root.name, objectName);

            var builder = new GarmentMeshBuilder();
            // （ガーター・レッグラップは体と同じウェイトで動かす: 下の層の服のウェイトは左右の脚や腰が混ざることがあり、
            // 受け継ぐと帯が左右の脚にまたがって動く）
            builder.Build(Body, result, Path.Combine(_workDir, "output.bin"), objectName, g.IsAccessory ? null : _underRenderers,
                g.twoLayer, joinedToes: g.Toes != ToeStyle.FiveToe);
            SkinFollow = Body.UnderCore != null ? SkinFollowWeights() : null;
            AssetDatabase.CreateAsset(builder.Mesh, $"{folder}/{objectName}.asset");
            var materials = GarmentMaterial.Create(g, Body, folder, objectName, builder.Regions);
            var skin = g.UsesSkinLining ? SkinLining(builder, materials, folder, objectName) : null;
            (Result, SkinResult) = GarmentInstaller.Install(Body, builder.Mesh, materials, objectName, builder.SyncedBlendShapes, skin);
            if (GarmentMaterial.UsesDynamicStretch(materials)) PoseStretch.Store(Result, Body.Animator);
            // ガーター・レッグラップは、すぐ下のタイツが落とす影を受けない（数 mm 下の面の影が、帯の上でまだらになる）
            if (g.IsAccessory) Result.receiveShadows = false;
            // ガーターのリボンの垂れ: 骨を足して揺らす
            var tails = SwingTails.Apply(Result, result.swing);

            // （衣装が透ける、または透けるようになる所では、裏地が肌の代わりになる所でだけ肌を消す）
            var liningMissing = g.UsesSkinLining && skin == null;
            var removed = g.RemovesSkin && GarmentInstaller.CanRemoveSkin && !liningMissing ? RemoveSkin(builder.Covered) : 0;
            AssetDatabase.SaveAssets();

            Status = $"{objectName} を生成しました（{result.vertexCount} 頂点 / {result.triangleCount} ポリゴン、" +
                     $"シェイプキー {builder.SyncedBlendShapes.Count} 個を同期）。" +
                     (tails > 0 ? $"\nリボンの垂れ {tails} 本に揺れ物を付けました。" : "") +
                     (_under.Count > 0 ? $"\n下に着ている物の上に重ねました: {string.Join(", ", _under)}" : "") +
                     (_shrinkOff.Count > 0 ? $"\nシュリンク系シェイプキーを 0 にしました: {string.Join(", ", _shrinkOff)}" : "") +
                     (removed > 0 ? $"\n肌の削除: AAO Remove Mesh By Box（{removed} 個の箱）を {Body.Renderer.name} に追加しました。" : "") +
                     AvatarTriangles(Body.Animator.gameObject) +
                     $"\n保存先: {folder}";
            Debug.Log("[LegWeaver] " + Status);
            Discard();
        }

        /// <summary>
        /// アバター全体で表示中のメッシュの三角形数と、VRChat の PC のポリゴン数のランクの目安（ビルドで肌を削除して
        /// 減る分は含まない）。
        /// </summary>
        static string AvatarTriangles(GameObject root)
        {
            long total = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(false))
            {
                var mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh
                    : r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
                if (mesh == null || !r.enabled) continue;
                for (var s = 0; s < mesh.subMeshCount; s++)
                    if (mesh.GetTopology(s) == MeshTopology.Triangles) total += mesh.GetIndexCount(s) / 3;
            }
            var rank = total <= 32000 ? "Excellent" : total <= 70000 ? "Good〜Poor" : "Very Poor";
            return $"\nアバター全体（表示中のメッシュ）: {total:N0} 三角形（PC のポリゴン数のランク: {rank}。" +
                   "Excellent は 32,000 まで、70,000 を超えると Very Poor。肌の削除でビルド時に減る分は含みません）";
        }

        /// <summary>衣装の種類ごとのオブジェクトとフォルダに付ける名前。</summary>
        static string Label(GarmentType type) => type switch
        {
            GarmentType.Spats => "Spats",
            GarmentType.ThighHigh => "ThighHighs",
            GarmentType.KneeHigh => "KneeHighs",
            GarmentType.Gloves => "Gloves",
            GarmentType.ArmCovers => "ArmCovers",
            GarmentType.ToeCap => "ToeCaps",
            GarmentType.Garter => "Garters",
            GarmentType.LegWrap => "LegWraps",
            _ => "Tights",
        };

        /// <summary>
        /// 肌の写しのメッシュ（保存済み）とそのマテリアル（体のものの写し、保存済み）。無いとき（透ける所が無い、
        /// またはマテリアルの無い体）は null。
        /// </summary>
        (Mesh, Material[])? SkinLining(GarmentMeshBuilder builder, Material[] materials, string folder, string objectName)
        {
            // 衣装が透ける所すべてに裏地を付ける（フェイクサイハイの靴下の部分は透けない）
            var regionMaterial = builder.Regions.Select((r, i) => (r, m: materials[i])).ToDictionary(x => x.r, x => x.m);
            // （引っ張られた所だけ透ける不透明な衣装は、下に着ている物を隠すので、そこにも裏地を付ける）
            var tips = ToeTips();
            builder.BuildSkinLining(Body, r => regionMaterial.TryGetValue(r, out var m) && m.renderQueue >= SheerQueue,
                keepWorn: !Settings.StretchSheers, inner: Settings.type == GarmentType.ToeCap ? null : UnderToeCaps(),
                plain: tips);
            if (builder.SkinMesh == null) return null;
            var bodyMaterials = Body.Renderer.sharedMaterials;
            var copies = builder.SkinMaterialSlots
                .Select((i, k) =>
                {
                    var src = i < bodyMaterials.Length ? bodyMaterials[i] : bodyMaterials.LastOrDefault();
                    if (!builder.SkinPlain[k]) return GarmentMaterial.SkinCopy(src, folder, $"{objectName}_Skin{i}");
                    // 単色は、指先のすぐ後ろ（指先の頂点と三角形を共にする、指先でない頂点）の肌の色にする（境目で色がそろう。
                    // 脚全体の平均では、足の甲の肌と色が違って境目が目立つ）
                    var behind = TipEdge(tips, i);
                    var color = behind.Count > 0 ? SkinColor.At(Body, src, behind, Color.white)
                        : SkinColor.Estimate(Body, new Color(1f, 0.85f, 0.75f));
                    return GarmentMaterial.SkinCopy(src, folder, $"{objectName}_Skin{i}_Toes", color);
                })
                .ToArray();
            // 体のマテリアルが無いスロットはほかのものを使う。一つも無ければ裏地は無し
            var any = copies.FirstOrDefault(m => m != null);
            if (any == null)
            {
                Debug.LogWarning("[LegWeaver] ボディにマテリアルが無いため、肌の写しを作りませんでした。");
                return null;
            }
            AssetDatabase.CreateAsset(builder.SkinMesh, $"{folder}/{objectName}_Skin.asset");
            return (builder.SkinMesh, copies.Select(m => m != null ? m : any).ToArray());
        }

        /// <summary>
        /// 前に作った親指サックの下にある体の頂点（肌の法線の向きに 4 mm 以内でサックの面に当たる）。サックが無ければ null。
        /// 透ける衣装をその上に重ねるとき、そこには肌の写しを付けない（写しは衣装の面にあり、サックを隠してしまう）。
        /// </summary>
        bool[] UnderToeCaps()
        {
            var caps = Body.Root.GetComponentsInChildren<SkinnedMeshRenderer>(false)
                .Where(r => r.enabled && IsMade(r) && r.name.StartsWith(Label(GarmentType.ToeCap) + "_"))
                .ToList();
            if (caps.Count == 0) return null;
            var pos = MeshMath.WorldPositions(Body.Renderer);
            var tris = Body.Triangles;
            // 体の頂点の法線（今の姿勢の位置から、面積で重み付け）
            var nrm = new Vector3[pos.Length];
            for (var t = 0; t < tris.Length; t += 3)
            {
                var n = Vector3.Cross(pos[tris[t + 1]] - pos[tris[t]], pos[tris[t + 2]] - pos[tris[t]]);
                for (var c = 0; c < 3; c++) nrm[tris[t + c]] += n;
            }
            const float reach = 0.004f; // m
            var under = new bool[pos.Length];
            var any = false;
            foreach (var cap in caps)
            {
                var cp = MeshMath.WorldPositions(cap);
                var ct = cap.sharedMesh.triangles;
                var box = new Bounds(cp[0], Vector3.zero);
                foreach (var p in cp) box.Encapsulate(p);
                box.Expand(reach * 2f);
                for (var v = 0; v < pos.Length; v++)
                {
                    if (under[v] || !box.Contains(pos[v]) || nrm[v].sqrMagnitude < 1e-20f) continue;
                    var d = nrm[v].normalized;
                    for (var t = 0; t < ct.Length && !under[v]; t += 3)
                        under[v] = RayHits(pos[v], d, cp[ct[t]], cp[ct[t + 1]], cp[ct[t + 2]], reach);
                    any |= under[v];
                }
            }
            return any ? under : null;
        }

        /// <summary>
        /// 足の指先の体の頂点: 足の指の付け根（Toes の骨）より前（足首から付け根への向きを脚の上下に垂直にした向きで）、
        /// かつ付け根から、足首から付け根までの長さの 1.5 倍以内。各頂点は、付け根が近いほうの足だけで調べる。
        /// 肌の写しはここをテクスチャで描かない。左右とも Toes の骨が無ければ null。
        /// </summary>
        bool[] ToeTips()
        {
            var feet = new List<(Vector3 toes, Vector3 ahead, float reach)>();
            foreach (var s in new[] { "L", "R" })
            {
                var foot = Body.Landmarks["foot" + s];
                var toes = Body.Landmarks["toes" + s];
                var knee = Body.Landmarks["lowerLeg" + s];
                if (foot == null || toes == null || knee == null) continue;
                var up = (knee.Value - foot.Value).normalized;
                var ahead = Vector3.ProjectOnPlane(toes.Value - foot.Value, up);
                if (ahead.sqrMagnitude < 1e-12f) continue;
                feet.Add((toes.Value, ahead.normalized, (toes.Value - foot.Value).magnitude * 1.5f));
            }
            if (feet.Count == 0) return null;
            var v = Body.Vertices;
            var tips = new bool[v.Length];
            for (var i = 0; i < v.Length; i++)
            {
                var f = feet.OrderBy(x => (v[i] - x.toes).sqrMagnitude).First();
                var d = v[i] - f.toes;
                tips[i] = Vector3.Dot(d, f.ahead) > 0f && d.magnitude < f.reach;
            }
            return tips;
        }

        /// <summary>
        /// 体のサブメッシュ submesh の三角形のうち、指先の頂点と指先でない頂点を共に持つ物の、指先でない頂点
        /// （指先のすぐ後ろの肌）。指先が無ければ空。
        /// </summary>
        HashSet<int> TipEdge(bool[] tips, int submesh)
        {
            var edge = new HashSet<int>();
            var mesh = Body.Mesh;
            if (tips == null || submesh >= mesh.subMeshCount || mesh.GetTopology(submesh) != MeshTopology.Triangles) return edge;
            var t = mesh.GetTriangles(submesh);
            for (var i = 0; i < t.Length; i += 3)
            {
                int a = t[i], b = t[i + 1], c = t[i + 2];
                if (!(tips[a] || tips[b] || tips[c]) || (tips[a] && tips[b] && tips[c])) continue;
                foreach (var v in new[] { a, b, c })
                    if (!tips[v]) edge.Add(v);
            }
            return edge;
        }

        /// <summary>前に作った、表示中の脚の衣装（タイツ・スパッツ・サイハイ・ニーハイの布の本体。肌の写しは除く）。</summary>
        static List<SkinnedMeshRenderer> MadeLegwear(GameObject root)
        {
            var labels = new[] { GarmentType.Tights, GarmentType.Spats, GarmentType.ThighHigh, GarmentType.KneeHigh }
                .Select(t => Label(t) + "_").ToArray();
            return root.GetComponentsInChildren<SkinnedMeshRenderer>(false)
                .Where(r => r.enabled && IsMade(r) && r.name.EndsWith("_Mesh") && labels.Any(l => r.name.StartsWith(l)))
                .ToList();
        }

        /// <summary>点 `o` から向き `d` へ `reach` 以内で三角形 abc に当たるか（Möller–Trumbore）。</summary>
        static bool RayHits(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, float reach)
        {
            var e1 = b - a;
            var e2 = c - a;
            var p = Vector3.Cross(d, e2);
            var det = Vector3.Dot(e1, p);
            if (Mathf.Abs(det) < 1e-12f) return false;
            var s = o - a;
            var u = Vector3.Dot(s, p) / det;
            if (u < 0f || u > 1f) return false;
            var q = Vector3.Cross(s, e1);
            var w = Vector3.Dot(d, q) / det;
            if (w < 0f || u + w > 1f) return false;
            var t = Vector3.Dot(e2, q) / det;
            return t >= 0f && t <= reach;
        }

        /// <summary>
        /// 衣装が隠す肌（`covered`: 体の頂点ごとに、縁からどれだけ内側か）を、ビルド時に AAO の箱で消す。
        /// 不透明な衣装では、上に重ねた物も消す。追加した箱の数を返す。
        /// </summary>
        int RemoveSkin(float[] covered)
        {
            var g = Settings;
            // 下に（腰に）着ている物は透ける衣装越しに見える: フェイクサイハイのストッキングの部分越しにも、
            // 靴下の部分がどれだけ厚くても見える
            var sheerOverWorn = !g.IsOpaque || g.IsFakeThighHigh && g.UpperIsSheer;
            // （引っ張られて透ける不透明な衣装は、不透明なものと同じく隠す）
            // 肌の写しがあるとき、着ている物の下の肌は残す（そこを写すものが無い）
            var skinCovered = covered;
            if (sheerOverWorn && g.UsesSkinLining && Body.UnderPresent != null)
            {
                skinCovered = skinCovered.Select((c, i) => Body.UnderPresent[i] ? -1f : c).ToArray();
                // その肌は上にある物と一緒に動く（ビルド時に適用）
                if (SkinFollow != null && SkinFollow.Count > 0)
                    GarmentInstaller.AddSkinFollow(Body, Result.transform.parent.gameObject, SkinFollow);
            }
            var boxes = SkinRemover.FitBoxes(Body, skinCovered, g.removalMarginCm / 100f);
            var removed = GarmentInstaller.AddSkinRemoval(Body, boxes);
            // 不透明な衣装では、上に重ねた物も同じように隠す（そうしないと、自身のウェイトで肌と違う動きをする所で
            // 衣装を突き抜ける）。透ける衣装ではそれが見える
            foreach (var layer in sheerOverWorn ? new List<SkinnedMeshRenderer>() : _underRenderers)
            {
                var worn = UnderLayers.AsBody(Body, layer);
                var wornCovered = UnderLayers.CoveredFromBody(Body, covered, layer);
                removed += GarmentInstaller.AddSkinRemoval(worn, SkinRemover.FitBoxes(worn, wornCovered, g.removalMarginCm / 100f));
            }
            return removed;
        }

        /// <summary>
        /// Blender のファイル（体の生のメッシュを含む）を消す: 衣装ができたとき、および実行が取り消されたか
        /// 失敗したとき。まだ開いているファイル（Blender が止まった直後）は、Unity が Temp と一緒に消すのに任せる。
        /// </summary>
        public void Discard()
        {
            if (_workDir == null || !Directory.Exists(_workDir)) return;
            try
            {
                Directory.Delete(_workDir, true);
            }
            catch (IOException)
            {
                // （どこかでまだ開いている: Unity が Temp と一緒に消すのに任せる）
            }
            catch (UnauthorizedAccessException)
            {
                // （読み取り専用: 同様）
            }
        }

        SkinnedMeshRenderer _wrapTarget;
        GameObject _root;

        /// <summary>
        /// Wrap の衣装は Finish でメッシュ自体から作り、Blender は使わない（起動しないランナー）。体は要らない
        /// （肌の色を使い得るのは編み地のマテリアルだけ）: 見つけられる脚の無いアバターでもよい。
        /// </summary>
        static GarmentGenerator StartWrap(GameObject root, Animator animator, SkinnedMeshRenderer bodyOverride,
            GarmentSettings settings, SkinnedMeshRenderer wrapTarget)
        {
            var smr = bodyOverride != null ? bodyOverride : AvatarBody.FindBodyCandidates(root, animator).FirstOrDefault();
            AvatarBody body = null;
            if (smr != null)
            {
                try
                {
                    body = AvatarBody.Read(root, animator, smr);
                }
                catch (InvalidOperationException)
                {
                    // （脚の衣装に要る脚のボーンが無い体: ここでは要らない）
                }
            }
            var gen = new GarmentGenerator
            {
                Body = body,
                Settings = CopyOf(settings),
                Runner = new BlenderRunner(),
                _root = root,
                _wrapTarget = wrapTarget != null ? wrapTarget : WrapGarment.Candidates(root, animator, smr).FirstOrDefault(),
            };
            if (gen._wrapTarget == null)
                throw new InvalidOperationException("包むメッシュ（脚の代わりの下半身など）が見つかりません。ウィンドウで指定してください。");
            return gen;
        }

        /// <summary>Wrap の衣装: 対象のメッシュを、すき間と生地の厚さの分だけ外へ動かしたもの。</summary>
        void FinishWrap()
        {
            var g = Settings;
            // 編み地をそのまま 1 層で: 脚の形や曲げによる伸びは無し、脚の座標で描く柄も無し（包むメッシュには
            // その座標が無い）、裏地も無し
            g.dynamicStretch = false;
            g.pattern = PatternKind.None;
            g.fakeThighHigh = false;
            g.twoLayer = false;
            g.gradient = false;
            g.runCount = 0;
            g.opaqueStretchSheer = 0f;
            var objectName = $"Wrap_{g.denier:0}D";
            var folder = CreateOutputFolder(_root.name, objectName);
            var mesh = WrapGarment.Build(_wrapTarget, (g.clearanceMm + g.Thickness) / 1000f, objectName);
            AssetDatabase.CreateAsset(mesh, $"{folder}/{objectName}.asset");
            var materials = GarmentMaterial.Create(g, Body, folder, objectName, new[] { GarmentRegion.Leg });
            Result = WrapGarment.Install(_wrapTarget, mesh, materials, objectName);
            AssetDatabase.SaveAssets();
            Status = $"{objectName} を生成しました（{_wrapTarget.name} を包む、{mesh.vertexCount} 頂点）。{_wrapTarget.name} と一緒に表示・非表示が切り替わります。" +
                     $"\n保存先: {folder}";
            Debug.Log("[LegWeaver] " + Status);
        }

        /// <summary>設定の写し: 生成の実行中にウィンドウ側の設定が変わり得るため。</summary>
        static GarmentSettings CopyOf(GarmentSettings settings) => JsonUtility.FromJson<GarmentSettings>(JsonUtility.ToJson(settings));

        /// <summary>ファイル名に使えない文字を '_' に置き換えた名前。</summary>
        static string SafeFileName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        /// <summary>アバターの衣装用に OutputRoot に作る新しいフォルダ: "{avatar}_{object}_{date}_{time}"。</summary>
        static string CreateOutputFolder(string avatarName, string objectName)
        {
            if (!AssetDatabase.IsValidFolder(OutputRoot))
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(OutputRoot));
            var name = $"{SafeFileName(avatarName)}_{objectName}_{DateTime.Now:yyyyMMdd_HHmmss}";
            return AssetDatabase.GUIDToAssetPath(AssetDatabase.CreateFolder(OutputRoot, name));
        }
    }
}
