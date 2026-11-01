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
    public class LegWeaverWindow : EditorWindow
    {
        [SerializeField] GameObject avatar;
        [SerializeField] SkinnedMeshRenderer bodyOverride;
        /// <summary>Wrap の衣装が包むメッシュ（null: 見つかった中で最も見込みの高いもの）。</summary>
        [SerializeField] SkinnedMeshRenderer wrapOverride;
        [SerializeField] GarmentSettings settings = new();
        [SerializeField] bool showAdvanced;
        [SerializeField] bool showBuild = true;
        [SerializeField] bool showLog;

        BlenderRunner _runner;
        GarmentGenerator _gen;
        GameObject _candidatesFor;
        SkinnedMeshRenderer _autoBody;
        SkinnedMeshRenderer _autoWrap;
        SkinnedMeshRenderer _spanFor;
        (float top, float bottom) _span;
        float _legContrast;
        /// <summary>
        /// ほかの肌とこれだけ違う色（L*a*b*）の脚には何かが描かれている（描かれたタイツはこれを大きく上回り、
        /// 肌色の脚は十分下回る）。
        /// </summary>
        const float PaintedLegs = 20f;
        /// <summary>
        /// 脚の肌が股関節からこれだけ下（膝までの道のりに対する割合）で始まる、または足首からこれだけ上で終わるなら、
        /// 肌が欠けている（脚全体があれば両方とも十分内側に収まる）。
        /// </summary>
        const float MissingTop = 0.1f, MissingBottom = 0.15f;
        /// <summary>ウィンドウに出す Blender のログの最大量（その末尾）。</summary>
        const int MaxLogShown = 15000;
        (GameObject, SkinnedMeshRenderer) _wrapFor;
        // 着衣モード: 検出したメッシュとユーザーの選択（アバターの下でのパスで持つので、プレハブを配置しても残る）
        List<UnderLayers.Candidate> _layers;
        (GameObject, SkinnedMeshRenderer) _layersFor;
        readonly Dictionary<string, bool> _wearOver = new();
        double _startTime;
        Vector2 _scroll, _logScroll;
        string _status = "";

        [MenuItem("Tools/LegWeaver")]
        static void Open() => GetWindow<LegWeaverWindow>("LegWeaver");

        void OnDisable()
        {
            _runner?.Kill();
            EditorApplication.update -= Poll;
            _gen?.Discard();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var busy = _runner != null && _runner.IsRunning;
            using (new EditorGUI.DisabledScope(busy))
            {
                DrawTarget();
                EditorGUILayout.Space();
                DrawGarment();
                EditorGUILayout.Space();
                DrawBlender();
            }
            EditorGUILayout.Space();
            DrawRun(busy);
            EditorGUILayout.EndScrollView();
        }

        void DrawTarget()
        {
            EditorGUILayout.LabelField("対象", EditorStyles.boldLabel);
            avatar = (GameObject)EditorGUILayout.ObjectField("アバター", avatar, typeof(GameObject), true);
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("アバターのルート（シーン上のオブジェクト、または .prefab）を指定してください。", MessageType.Info);
                return;
            }
            if (EditorUtility.IsPersistent(avatar))
                EditorGUILayout.HelpBox("プレハブアセットが指定されています。生成時にシーンへ配置してから適用します（アセットは変更しません）。", MessageType.None);
            var animator = AvatarBody.FindHumanoid(avatar, out var error);
            if (animator == null)
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
                return;
            }
            if (_candidatesFor != avatar)
            {
                _autoBody = AvatarBody.FindBodyCandidates(avatar, animator).FirstOrDefault();
                _candidatesFor = avatar;
            }
            var auto = _autoBody;
            bodyOverride = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                new GUIContent("ボディメッシュ", "空欄なら自動選択（脚を覆うメッシュのうち、いちばん内側の物）"),
                bodyOverride, typeof(SkinnedMeshRenderer), true);
            if (bodyOverride == null)
                EditorGUILayout.LabelField(" ", auto != null ? $"自動: {auto.name}" : "自動: 見つかりません");
            var body = bodyOverride != null ? bodyOverride : auto;
            if (body != null && body.sharedMesh != null)
            {
                // 1 つの衣装向けに作られたアバターは、その衣装が覆う肌を省いていることがある（ソックスが下腿そのもの、
                // スカートが腰を隠す）: そのとき衣装は肌と同じところで切りっぱなしの断面で終わる
                if (_spanFor != body)
                {
                    _span = BodyCoverage.LegSpan(body, animator);
                    _legContrast = SkinColor.LegContrast(body, animator);
                    _spanFor = body;
                }
                if (_legContrast > PaintedLegs)
                    EditorGUILayout.HelpBox("このボディの脚は、ほかの肌と色が大きく違います（テクスチャにタイツや靴下が描かれている可能性があります）。" +
                                            "透ける生地では描かれた物が透けて見え、着せても見た目がほとんど変わりません。不透明な生地か、脚が肌の色のテクスチャをおすすめします。", MessageType.Warning);
                if (_span.top > MissingTop)
                    EditorGUILayout.HelpBox("このボディメッシュには腰から太ももの上の肌がありません（スカートなどの衣装が隠す所を省いた作りの可能性があります）。" +
                                            "肌のない所にはタイツを作れないので、上端が太ももの途中で切れた筒になります。", MessageType.Warning);
                if (_span.bottom > MissingBottom)
                    EditorGUILayout.HelpBox("このボディメッシュには膝から下の肌が途中までしかありません（靴下などの衣装が脚を兼ねている作りの可能性があります）。" +
                                            "肌のない所にはタイツを作れないので、すねの途中で切れた筒になります。", MessageType.Warning);
            }
        }

        void DrawGarment()
        {
            EditorGUILayout.LabelField("衣装", EditorStyles.boldLabel);
            var g = settings;
            g.type = (GarmentType)EditorGUILayout.Popup("種類", (int)g.type, new[] { "タイツ", "スパッツ", "サイハイソックス", "ニーハイソックス", "手袋", "アームカバー", "包む（おばけのしっぽなど）", "親指サック（タイツの下）", "ガーター", "レッグラップ" });
            if (g.IsSock)
                using (new EditorGUI.IndentLevelScope())
                    g.sockTop = (SockTop)EditorGUILayout.Popup(new GUIContent("口ゴム", "上端の帯の見た目（無地／レースの網目／滑り止めの波線）"),
                        (int)g.sockTop, new[] { "無地", "レース", "滑り止め" });
            g.color = EditorGUILayout.ColorField("色", g.color);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(" ");
                foreach (var (label, color) in GarmentColors.Presets)
                    if (GUILayout.Button(label, EditorStyles.miniButton)) g.color = color;
            }
            g.finish = (Finish)EditorGUILayout.Popup(
                new GUIContent("質感", "標準: lilToon の既定のまま。マット〜サテン: 現実寄り（つやの強さが順に増す）。トゥーン: 段のある影と硬いハイライト（アニメ調）"),
                (int)g.finish, g.type == GarmentType.Gloves ? GarmentFinish.GloveLabels : GarmentFinish.Labels);
            // 綿は手袋だけ
            if (g.finish == Finish.Cotton && g.type != GarmentType.Gloves) g.finish = Finish.Matte;
            using (new EditorGUILayout.HorizontalScope())
            {
                // 対数: 5D..1000D を 1 本のスライダーに収め、透ける範囲に余裕を持たせる
                EditorGUILayout.PrefixLabel("デニール");
                var t = GUILayout.HorizontalSlider(Mathf.Log(g.denier), Mathf.Log(Denier.MinDenier), Mathf.Log(Denier.MaxDenier));
                var d = EditorGUILayout.FloatField(Mathf.Round(Mathf.Exp(t)), GUILayout.Width(60));
                g.denier = Mathf.Clamp(d, Denier.MinDenier, Denier.MaxDenier);
            }
            if (g.type == GarmentType.Wrap)
            {
                DrawWrap(g);
                return;
            }
            DrawOutfit(g);
            using (new EditorGUI.DisabledScope(g.type is GarmentType.Spats or GarmentType.ToeCap or GarmentType.Garter or GarmentType.LegWrap))
                g.toes = (ToeStyle)EditorGUILayout.Popup(
                    new GUIContent("つま先", "まとめ: 指をひとまとめに包み、指の間を埋めます。5 本指: 体の指に沿って 1 本ずつ包みます（指のボーンがあれば指ごとに曲がります。体の指が分かれていなければ体と同じ形になります）。足袋型: 親指だけを分けて包み、ほかの 4 本はまとめます（体の親指と人差し指が離れていなければ、まとめと同じ形になります）"),
                    (int)g.toes, new[] { "まとめ", "5 本指", "足袋型" });
            g.quality = (Quality)EditorGUILayout.Popup(
                new GUIContent("仕上がり", $"通常: 体のメッシュと同じ細かさ（軽い）。高品質: 曲がる所を細かく丸くします（最大 {GarmentSettings.HighQualityMaxTriangles:N0} 三角形。2 層式は裏地を含めて）"),
                (int)g.quality, new[] { "通常", "高品質" });
            DrawFakeThighHigh(g);
            DrawPattern(g);
            using (new EditorGUI.DisabledScope(g.IsFakeThighHigh))
                g.twoLayer = EditorGUILayout.Toggle(
                    new GUIContent("2 層式（フェイクタイツ）", "厚手の生地の裏に肌色の裏地があり、表から素肌が透けて見えるタイツ。厚みと肌の削除は上のデニール、見た目は「見かけのデニール」に従います"),
                    g.twoLayer);
            if (g.twoLayer)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    g.apparentDenier = EditorGUILayout.Slider(new GUIContent("見かけのデニール", "表から見た透け感（裏地の肌色がどれだけ見えるか）"),
                        g.apparentDenier, Denier.MinDenier, 200f);
                    g.liningFromSkin = EditorGUILayout.Toggle(new GUIContent("裏地の色を肌から取る", "脚の肌テクスチャの平均色を裏地に使います"),
                        g.liningFromSkin);
                    using (new EditorGUI.DisabledScope(g.liningFromSkin))
                        g.liningColor = EditorGUILayout.ColorField("裏地の色", g.liningColor);
                }
            }
            EditorGUILayout.LabelField(" ",
                $"{Denier.Describe(g.denier)} / 厚み {g.Thickness:0.00} mm / " +
                (g.twoLayer
                    ? $"見た目 {g.apparentDenier:0}D 相当（正面の不透明度 {g.LookCoverage:P0}）/ 肌は削除"
                    : $"正面の不透明度 {g.Coverage:P0}" + (g.IsOpaque ? " / 不透明として扱う" : "")));

            using (new EditorGUI.DisabledScope(!g.IsOpaque || g.twoLayer || g.IsFakeThighHigh || g.denier >= 200f))
                g.opaqueStretchSheer = EditorGUILayout.Slider(
                    new GUIContent("引っ張られた所の透け（不透明）",
                        "不透明のデニールでも、かかと・つま先・曲げた膝の前・お尻など引っ張られる所だけ肌が透けるようにします。0 で透けません。" +
                        "デニールが高いほど弱く、200D 以上では透けません。肌の写しを使います"),
                    g.opaqueStretchSheer, 0f, 1f);
            using (new EditorGUI.DisabledScope(!g.LooksSheer))
                g.stretchStrength = EditorGUILayout.Slider(
                    new GUIContent("伸びによる透け", "太もも・ふくらはぎ・膝の前など、布が伸びる部分を透けやすくします（半透明のときだけ）"),
                    g.stretchStrength, 0f, 2f);
            // （腕の衣装には使わない: 伸びはボーンの接するところに出る。PoseStretch を参照）
            using (new EditorGUI.DisabledScope(!g.LooksSheer || g.stretchStrength <= 0 || g.IsArm))
                g.dynamicStretch = EditorGUILayout.Toggle(
                    new GUIContent("姿勢に応じて透けを変える", "膝を曲げると膝の前が透け、裏が濃くなります。専用の lilToon カスタムシェーダーを使います（PC 版のみ）"),
                    g.dynamicStretch);

            DrawBuild(g);

            switch (g.type)
            {
                case GarmentType.Tights:
                    g.waistHeight = EditorGUILayout.Slider(new GUIContent("ウエストの高さ", "0 = 股関節、1 = 胸"), g.waistHeight, 0f, 1f);
                    break;
                case GarmentType.Spats:
                    g.waistHeight = EditorGUILayout.Slider(new GUIContent("ウエストの高さ", "0 = 股関節、1 = 胸"), g.waistHeight, 0f, 1f);
                    g.legCut = EditorGUILayout.Slider(new GUIContent("裾の位置", "0 = 股関節、1 = 膝、2 = 足首"), g.legCut, 0.1f, 2f);
                    break;
                case GarmentType.ThighHigh:
                    g.thighTop = EditorGUILayout.Slider(new GUIContent("上端の位置", "0 = 股関節、1 = 膝、2 = 足首"), g.thighTop, 0f, 0.9f);
                    break;
                case GarmentType.Gloves:
                    g.gloveLength = (GloveLength)EditorGUILayout.Popup(new GUIContent("長さ", "手首まで／前腕の中ほど／肘の上（オペラ）"),
                        (int)g.gloveLength, new[] { "手首", "前腕", "肘上（オペラ）" });
                    g.gloveFingers = (GloveFingers)EditorGUILayout.Popup(new GUIContent("指", "5 本指: 体の指に沿って包みます。指なし: 指の付け根で切ります。" +
                        "絵描き用: 薬指と小指だけを包み、親指は穴から出し、人差し指と中指は付け根で切ります"),
                        (int)g.gloveFingers, new[] { "5 本指", "指なし", "絵描き用" });
                    break;
                case GarmentType.ArmCovers:
                    g.armTop = EditorGUILayout.Slider(new GUIContent("上端の位置", "0 = 肩、1 = 肘、2 = 手首"), g.armTop, 0f, 1.6f);
                    g.armToHand = EditorGUILayout.Toggle(new GUIContent("手の甲まで", "指の付け根まで覆い、親指は穴から出します（オフなら手首まで）"), g.armToHand);
                    break;
                case GarmentType.Garter:
                    g.garterAt = EditorGUILayout.Slider(new GUIContent("帯の位置", "0 = 股関節、1 = 膝、2 = 足首（太ももは 0.1〜0.8、ふくらはぎは 1.2〜1.8）。付け根の近くでは、股をまたがない所まで自動で下げます"), g.garterAt, 0.1f, 1.9f);
                    g.garterWidthMm = EditorGUILayout.Slider(new GUIContent("帯の幅 (mm)", "脚に沿った帯の幅"), g.garterWidthMm, 8f, 80f);
                    g.garterRibbon = EditorGUILayout.Toggle(new GUIContent("リボン", "帯の前の外寄りにリボンの結び目を付けます。垂れは VRChat の揺れ物（PhysBone）で揺れます"), g.garterRibbon);
                    g.overLegwear = EditorGUILayout.Toggle(new GUIContent("タイツ・靴下の上に付ける", "前に作ったタイツ・靴下があれば、その上に付けます（無ければ素肌の上）"), g.overLegwear);
                    break;
                case GarmentType.LegWrap:
                    g.wrapFrom = EditorGUILayout.Slider(new GUIContent("巻き始め", "脚の座標（0 = 股関節、1 = 膝、2 = 足首）"), g.wrapFrom, 0.1f, 2f);
                    g.wrapTo = EditorGUILayout.Slider(new GUIContent("巻き終わり", "脚の座標（0 = 股関節、1 = 膝、2 = 足首）"), g.wrapTo, 0.1f, 2f);
                    g.wrapTurns = EditorGUILayout.Slider(new GUIContent("巻く回数", "範囲の中で脚の周りを回る回数"), g.wrapTurns, 0.5f, 8f);
                    g.wrapWidthMm = EditorGUILayout.Slider(new GUIContent("リボンの幅 (mm)", "脚に巻くリボンの幅"), g.wrapWidthMm, 4f, 40f);
                    g.wrapCross = EditorGUILayout.Toggle(new GUIContent("交差させる", "逆向きのらせんも巻いて、編み上げのように交差させます"), g.wrapCross);
                    g.overLegwear = EditorGUILayout.Toggle(new GUIContent("タイツ・靴下の上に付ける", "前に作ったタイツ・靴下があれば、その上に付けます（無ければ素肌の上）"), g.overLegwear);
                    using (new EditorGUI.DisabledScope(!g.overLegwear))
                        g.overAvatarLegwear = EditorGUILayout.Toggle(new GUIContent("アバターの靴下・レッグウォーマーの上にも巻く", "オンなら、アバター自身の脚の服の上からも巻きます。オフなら、その服は無い物として肌（またはタイツ）の上に巻きます（服を脱がせた状態用。服を着たままでは服を突き抜けます）"), g.overAvatarLegwear);
                    break;
                case GarmentType.ToeCap:
                    EditorGUILayout.HelpBox($"足の親指だけを覆うサックです。タイツの下に着る補強として、肌との隙間は {GarmentSettings.ToeCapClearanceMm} mm 以下で作ります。" +
                                            "体の足の指が分かれていなければ、指先の内側を親指として切ります。", MessageType.Info);
                    break;
                case GarmentType.KneeHigh:
                    g.kneeTop = EditorGUILayout.Slider(new GUIContent("上端の位置", "1 = 膝。0.7〜0.9 で膝上、1.1〜1.5 で膝下（膝の関節の上は避けます）"), g.kneeTop, 0.7f, 1.5f);
                    if (g.KneeTopOnJoint)
                        EditorGUILayout.HelpBox("上端が膝の関節にかかっています。膝を曲げると口ゴムが肌から浮いたり沈んだりするので、0.9 以下か 1.1 以上をおすすめします。", MessageType.Warning);
                    break;
            }

            g.turnOffShrinkShapes = EditorGUILayout.Toggle(
                new GUIContent("シュリンク系シェイプキーを 0 にする", "衣装用に肌を縮めて隠すシェイプキー（形から自動で判定）を、生成前に 0 にします。衣装を脱いだ状態に合わせます（元に戻す操作で戻せます）"),
                g.turnOffShrinkShapes);

            using (new EditorGUI.DisabledScope(g.twoLayer))
                g.skinLining = EditorGUILayout.Toggle(
                    new GUIContent("肌の写し（半透明）", "半透明でも覆われる肌を削除し、布の内側に同じ見た目の肌（体の UV とマテリアル）を描きます。布と一緒に動くので、どんな姿勢でも肌が布から出ません"),
                    g.skinLining);

            g.skinRemoval = (SkinRemoval)EditorGUILayout.Popup(
                new GUIContent("隠れる肌の削除", "AAO Remove Mesh By Box でタイツに完全に覆われる肌のポリゴンを削除します"),
                (int)g.skinRemoval, new[] { "自動（不透明か、肌の写しを使うとき）", "常に", "しない" });
            if (g.skinRemoval != SkinRemoval.Never)
            {
                g.removalMarginCm = EditorGUILayout.Slider(new GUIContent("端からの余裕 (cm)", "布の端からこの距離までの肌は残します"),
                    g.removalMarginCm, 0f, 5f);
                if (!GarmentInstaller.CanRemoveSkin)
                    EditorGUILayout.HelpBox("Avatar Optimizer (AAO) が見つからないため、肌の削除は行いません。", MessageType.Warning);
            }

            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "詳細設定", true);
            if (!showAdvanced) return;
            using (new EditorGUI.IndentLevelScope())
            {
                g.clearanceMm = EditorGUILayout.Slider(new GUIContent("肌との隙間 (mm)", "肌と布の内側の距離。小さいと肌が突き抜けやすくなります"), g.clearanceMm, 0.2f, 5f);
                g.autoThickness = EditorGUILayout.Toggle("厚みをデニールから決める", g.autoThickness);
                using (new EditorGUI.DisabledScope(g.autoThickness))
                    g.thicknessMm = EditorGUILayout.Slider("布の厚み (mm)", g.autoThickness ? g.Thickness : g.thicknessMm, 0.05f, 5f);
                using (new EditorGUI.DisabledScope(g.toes == ToeStyle.FiveToe))
                    g.toeBridgeMm = EditorGUILayout.Slider(new GUIContent("足指の間を埋める (mm)", "つま先が「まとめ」「足袋型」のとき、この約 2 倍より狭い指の隙間を布でつなぎます"), g.toeBridgeMm, 0.5f, 8f);
                g.jointExtraMm = EditorGUILayout.Slider(new GUIContent("関節の余裕 (mm)", "膝・脚の付け根など、ボーンのウェイトが混ざる所で布をさらに離す距離。曲げたときの突き抜けを防ぎます"), g.jointExtraMm, 0f, 5f);
                g.density = EditorGUILayout.Slider(new GUIContent("足先のポリゴン密度", "足の指の付け根より前（SDF で作る部分）の、元のポリゴン数に対する倍率。それ以外はボディと同じ構成です"), g.density, 0.5f, 4f);
                g.voxelMm = EditorGUILayout.Slider(new GUIContent("計算の細かさ (mm)", "小さいほど精密で遅くなります"), g.voxelMm, 0.4f, 2f);
            }
        }

        /// <summary>
        /// Wrap の衣装に要るのは、包むメッシュとそこからどれだけ離すかだけ（ニット自体は上で設定する）:
        /// 脚のオプション（着衣・指・2 層・肌の写しとその除去・柄）は当てはまらない。
        /// </summary>
        void DrawWrap(GarmentSettings g)
        {
            var body = bodyOverride != null ? bodyOverride : _autoBody;
            if (avatar != null && _wrapFor != (avatar, body))
            {
                var animator = AvatarBody.FindHumanoid(avatar, out _);
                _autoWrap = animator != null ? WrapGarment.Candidates(avatar, animator, body).FirstOrDefault() : null;
                _wrapFor = (avatar, body);
            }
            wrapOverride = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                new GUIContent("包むメッシュ", "脚の代わりの下半身（おばけのしっぽなど）。空欄なら自動で探します"),
                wrapOverride, typeof(SkinnedMeshRenderer), true);
            if (wrapOverride == null)
                EditorGUILayout.LabelField(" ", _autoWrap != null ? $"自動: {_autoWrap.name}" : "自動: 見つかりません");
            EditorGUILayout.HelpBox("選んだメッシュを、そのままの形でタイツの生地で包みます（Blender は使いません）。" +
                                    "包む物はそのメッシュのオブジェクトの子に置くので、そのオブジェクトと一緒に表示・非表示が切り替わります。",
                MessageType.Info);
            g.clearanceMm = EditorGUILayout.Slider(new GUIContent("メッシュとの隙間 (mm)", "包むメッシュと布の内側の距離"), g.clearanceMm, 0.2f, 5f);
            g.autoThickness = EditorGUILayout.Toggle("厚みをデニールから決める", g.autoThickness);
            using (new EditorGUI.DisabledScope(g.autoThickness))
                g.thicknessMm = EditorGUILayout.Slider("布の厚み (mm)", g.autoThickness ? g.Thickness : g.thicknessMm, 0.05f, 5f);
        }

        /// <summary>フェイクのサイハイタイツ: 太ももの境目より下はソックス部分、上は透けるストッキング部分。</summary>
        void DrawFakeThighHigh(GarmentSettings g)
        {
            using (new EditorGUI.DisabledScope(g.type != GarmentType.Tights))
                g.fakeThighHigh = EditorGUILayout.Toggle(
                    new GUIContent("フェイクサイハイ", "太ももの境目より下を上のデニール・色のソックス部、上を素肌が透ける薄いストッキング部にします（タイツのみ。2 層式とは同時に使えません）"),
                    g.fakeThighHigh);
            if (!g.IsFakeThighHigh) return;
            g.twoLayer = false;
            using (new EditorGUI.IndentLevelScope())
            {
                g.fakeTop = EditorGUILayout.Slider(new GUIContent("境目の高さ", "0 = 股関節、1 = 膝"), g.fakeTop, 0.15f, 0.95f);
                g.upperDenier = EditorGUILayout.Slider(new GUIContent("上部のデニール", "境目より上のストッキング部"), g.upperDenier, Denier.MinDenier, 60f);
                g.upperColor = EditorGUILayout.ColorField(new GUIContent("上部の色", "ベージュ系にすると素肌に近く見えます"), g.upperColor);
                g.fakeBand = EditorGUILayout.Toggle(new GUIContent("境目の帯", "ソックス部の上端に、サイハイの口ゴムのような濃い帯を付けます"), g.fakeBand);
                g.fakeBorder = (BorderKind)EditorGUILayout.Popup(
                    new GUIContent("境目の飾り", "レース: 境目の上にスカラップ（半円の縁）、帯に網目。波: ソックス部が波形に伸び上がります。猫: 脚の前に猫の顔（耳が境目の上に出て、目・鼻・ひげを上部の色で描きます）"),
                    (int)g.fakeBorder, new[] { "なし", "レース", "波", "猫" });
            }
        }

        /// <summary>脚に編み込む柄。</summary>
        void DrawPattern(GarmentSettings g)
        {
            g.gradient = EditorGUILayout.Toggle(new GUIContent("グラデーション", "脚の上から下へ色を変えます"), g.gradient);
            if (g.gradient)
                using (new EditorGUI.IndentLevelScope())
                {
                    g.gradientColor = EditorGUILayout.ColorField(new GUIContent("下の色", "上は「色」、下へ向かってこの色になります"), g.gradientColor);
                    EditorGUILayout.MinMaxSlider(new GUIContent("変わる範囲", "0 = 股関節、1 = 膝、2 = 足首"), ref g.gradientTop, ref g.gradientBottom, -0.5f, 2.5f);
                }
            g.runCount = EditorGUILayout.IntSlider(new GUIContent("伝線", "編み目がほつれて肌が見える縦の線の本数"), g.runCount, 0, 10);
            if (g.runCount > 0)
                using (new EditorGUI.IndentLevelScope())
                    g.runSeed = EditorGUILayout.IntField(new GUIContent("配置の種", "同じ数なら同じ配置になります"), g.runSeed);
            g.wrinkles = EditorGUILayout.Slider(new GUIContent("たるみ", "足首・膝裏のしわ（形状。高品質での使用がおすすめ）"), g.wrinkles, 0f, 1f);
            g.pattern = (PatternKind)EditorGUILayout.Popup(
                new GUIContent("柄", "脚に編み込む柄（濃く編んだ部分）。フェイクサイハイではソックス部に入ります。バックシームは脚の後ろの縫い目です"),
                (int)g.pattern, new[] { "なし", "バックシーム", "ドット", "猫" });
            if (g.pattern == PatternKind.None && !(g.IsFakeThighHigh && g.fakeBorder != BorderKind.Plain)) return;
            using (new EditorGUI.IndentLevelScope())
            {
                g.patternColor = EditorGUILayout.ColorField(new GUIContent("柄の色", "境目の飾りはソックス部の色になります"), g.patternColor);
                g.patternOpacity = EditorGUILayout.Slider(new GUIContent("柄の濃さ", "透ける部分で柄をどれだけ濃く（透けにくく）するか"), g.patternOpacity, 0f, 1f);
                g.patternScale = EditorGUILayout.Slider(new GUIContent("柄の大きさ", "模様の大きさ（バックシームは線の太さ）"), g.patternScale, 0.5f, 2f);
                g.patternSide = (PatternSide)EditorGUILayout.Popup(new GUIContent("柄を入れる脚", "片脚だけに柄を入れられます"),
                    (int)g.patternSide, new[] { "両脚", "左脚", "右脚" });
            }
        }

        /// <summary>本物のタイツで編み方の違う部分（それぞれ専用のマテリアルで描く）。</summary>
        void DrawBuild(GarmentSettings g)
        {
            showBuild = EditorGUILayout.Foldout(showBuild, "実物の構造", true);
            if (!showBuild) return;
            using (new EditorGUI.IndentLevelScope())
            {
                g.waistband = EditorGUILayout.Toggle(new GUIContent("ウエストバンド", "上端（サイハイは上端の帯）を濃く編んだ帯にします"), g.waistband);
                using (new EditorGUI.DisabledScope(g.type != GarmentType.Tights))
                {
                    g.pantyPart = EditorGUILayout.Toggle(new GUIContent("パンティ部", "ウエストから太ももの付け根までを少し濃くします（タイツのみ）"), g.pantyPart);
                    g.runGuard = EditorGUILayout.Toggle(new GUIContent("ランガード", "パンティ部の下端に濃い帯を付けます（タイツのみ）"), g.runGuard);
                }
                using (new EditorGUI.DisabledScope(g.type == GarmentType.Spats))
                {
                    g.toeReinforcement = (ToeReinforcement)EditorGUILayout.Popup(
                        new GUIContent("つま先の補強", "補強: つま先を濃く編む。サンダル: ほとんど目立たない補強。ヌード: 補強なし"),
                        (int)g.toeReinforcement, new[] { "ヌード（なし）", "サンダル", "補強" });
                    using (new EditorGUI.DisabledScope(g.toeReinforcement == ToeReinforcement.Nude))
                    using (new EditorGUI.IndentLevelScope())
                        g.toeShape = (ToeShape)EditorGUILayout.Popup(new GUIContent("つま先の形", "まっすぐ: 指の上を横切る線。包む形: 指の両脇まで回り込むトウキャップ形"),
                            (int)g.toeShape, new[] { "まっすぐ", "包む形" });
                    g.heelReinforcement = EditorGUILayout.Toggle(new GUIContent("かかとの補強", "かかとを濃く編みます（つま先と同じ見た目）"), g.heelReinforcement);
                    using (new EditorGUI.DisabledScope(!g.heelReinforcement))
                    using (new EditorGUI.IndentLevelScope())
                        g.heelShape = (HeelShape)EditorGUILayout.Popup(new GUIContent("かかとの形", "カップ: かかとを U 字に包む形（一般的）。Y ヒール: 足首の後ろから足裏へ斜めの線"),
                            (int)g.heelShape, new[] { "カップ（U 字）", "Y ヒール" });
                }
                using (new EditorGUI.DisabledScope(g.type != GarmentType.Tights))
                {
                    g.centerSeam = (CenterSeam)EditorGUILayout.Popup(new GUIContent("マチ（中心の縫い目）", "股を通って前後の中心に走る縦の縫い目（タイツのみ）"),
                        (int)g.centerSeam, new[] { "なし", "前", "後ろ", "前後" });
                    g.gusset = EditorGUILayout.Toggle(new GUIContent("ひしマチ", "股の下の菱形の当て布（同じ編み地を濃くしたもの。タイツのみ）"), g.gusset);
                    if (g.gusset)
                        using (new EditorGUI.IndentLevelScope())
                        {
                            g.gussetSize = EditorGUILayout.Slider(new GUIContent("大きさ", "腰の幅に対する大きさ"), g.gussetSize, 0.5f, 1.8f);
                            g.gussetAspect = EditorGUILayout.Slider(new GUIContent("縦横比", "前後の長さ ÷ 幅"), g.gussetAspect, 1f, 3f);
                        }
                }
            }
        }

        void DrawOutfit(GarmentSettings g)
        {
            g.outfitMode = (OutfitMode)EditorGUILayout.Popup(
                new GUIContent("衣装の扱い", "衣装を考慮: 肌にぴったり沿って着ている物（下着・パンストなど）の上に重ねて履きます。スカートのように肌から離れている衣類は覆いません。肌のみ: 他のメッシュは考えず肌に沿わせます"),
                (int)g.outfitMode, new[] { "衣装を考慮", "肌のみ" });
            if (g.outfitMode != OutfitMode.OverUnderLayers || avatar == null) return;
            using (new EditorGUI.IndentLevelScope())
            using (new EditorGUI.DisabledScope(!g.LooksSheer && !g.IsFakeThighHigh))
                g.underwearFit = (UnderwearFit)EditorGUILayout.Popup(
                    new GUIContent("下着の包み方（半透明）",
                        "下着を内側に収める（おすすめ）: どの姿勢でも下着がタイツから出ないようにします。ぴったりした普通の下着ならこちら。\n" +
                        "股の見た目を優先: フリルやリボンなど肌から大きく浮いた下着で、股を下から見たときに布が重なって崩れる場合に使います。" +
                        "浮いた部分は少しタイツから出ることがあります。\n不透明のタイツでは常に内側に収めます"),
                    (int)g.underwearFit, new[] { "下着を内側に収める（おすすめ）", "股の見た目を優先" });
            var body = bodyOverride != null ? bodyOverride : _autoBody;
            if (body == null) return;
            if (_layers == null || _layersFor != (avatar, body))
            {
                _layersFor = (avatar, body);
                try
                {
                    var animator = AvatarBody.FindHumanoid(avatar, out _);
                    _layers = UnderLayers.Detect(AvatarBody.Read(avatar, animator, body));
                }
                catch (Exception e)
                {
                    _layers = new List<UnderLayers.Candidate>();
                    Debug.LogWarning("[LegWeaver] 衣装の検出に失敗しました: " + e.Message);
                }
            }
            using (new EditorGUI.IndentLevelScope())
            {
                if (_layers.Count == 0)
                    EditorGUILayout.LabelField(" ", "肌に沿って着ている物は見つかりません");
                foreach (var c in _layers)
                {
                    var path = LayerPath(avatar, c.Renderer);
                    var on = _wearOver.TryGetValue(path, out var v) ? v : c.Under;
                    var next = EditorGUILayout.ToggleLeft(new GUIContent($"{c.Renderer.name} の上に重ねる",
                        c.Under ? "腰を覆い、肌にぴったり沿う薄い物（下着・パンストなど）として検出しました" :
                        c.Spats ? "スパッツ（太ももまで肌にぴったり沿う物）として検出しました。タイツと一緒に穿く物ではないので、無いものとして生成します（見た目が崩れることがあります）" : "肌に触れていますが、腰を覆わない（靴下など）、肌から離れている（スカート・ズボンなど）、太ももまで覆う（ドロワーズ・ブルマーなど、タイツの上に穿く物）ため、既定では覆いません"), on);
                    if (next != on) _wearOver[path] = next;
                }
            }
        }

        static string LayerPath(GameObject root, SkinnedMeshRenderer r) => AnimationUtility.CalculateTransformPath(r.transform, root.transform);

        void DrawBlender()
        {
            EditorGUILayout.LabelField("Blender", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                var path = EditorGUILayout.TextField("blender.exe", BlenderRunner.BlenderPath);
                if (path != BlenderRunner.BlenderPath) BlenderRunner.BlenderPath = path;
                if (GUILayout.Button("参照", GUILayout.Width(50)))
                {
                    var p = EditorUtility.OpenFilePanel("blender.exe", "", "exe");
                    if (!string.IsNullOrEmpty(p)) BlenderRunner.BlenderPath = p;
                }
            }
            var q = settings.TimeClass;
            var current = (int)BlenderRunner.GetTimeout(q);
            var limit = EditorGUILayout.IntSlider(
                new GUIContent(q == Quality.High ? "打ち切り時間（高品質・秒）" : "打ち切り時間（秒）",
                    $"Blender の処理がこれを超えたら止めます。既定は通常 {BlenderRunner.DefaultTimeoutSeconds:0} 秒・高品質 {BlenderRunner.DefaultHighTimeoutSeconds:0} 秒（品質ごとに保存）"),
                current, (int)BlenderRunner.MinTimeoutSeconds, (int)BlenderRunner.MaxTimeoutSeconds);
            if (limit != current) BlenderRunner.SetTimeout(q, limit);
            using (new EditorGUILayout.HorizontalScope())
            {
                var py = EditorGUILayout.TextField(new GUIContent("同梱 Python（任意）",
                    "Blender に同梱の python.exe。自動で版が分からないときだけ指定します（版の確認にだけ使い、生成は Blender の Python で動きます）"),
                    BlenderSupport.PythonPath);
                if (py != BlenderSupport.PythonPath) BlenderSupport.PythonPath = py;
                if (GUILayout.Button("参照", GUILayout.Width(50)))
                {
                    var p = EditorUtility.OpenFilePanel("python.exe", "", "exe");
                    if (!string.IsNullOrEmpty(p)) BlenderSupport.PythonPath = p;
                }
            }
            if (File.Exists(BlenderRunner.BlenderPath))
            {
                var (verdict, why) = BlenderSupport.Check(BlenderRunner.BlenderPath);
                EditorGUILayout.HelpBox(why + (verdict == BlenderSupport.Verdict.Blocked
                        ? $"\nサポートが終わった版や終了 {BlenderSupport.BlockDays} 日前を過ぎた版は、安全のため使えません。"
                        : ""),
                    verdict == BlenderSupport.Verdict.Blocked ? MessageType.Error : verdict == BlenderSupport.Verdict.Warn ? MessageType.Warning : MessageType.Info);
            }
            if (!File.Exists(BlenderRunner.BlenderPath))
            {
                EditorGUILayout.HelpBox(
                    $"Blender が見つかりません。Blender {BlenderSupport.MinMajor}.x 以降をインストールするか（blender.org）、" +
                    "ポータブル版（zip を展開したもの）の blender.exe を上の欄で指定してください。", MessageType.Warning);
            }
        }

        void DrawRun(bool busy)
        {
            if (busy)
            {
                var elapsed = EditorApplication.timeSinceStartup - _startTime;
                EditorGUILayout.HelpBox($"Blender で生成中… {elapsed:0} 秒（{_runner.Timeout:0} 秒で打ち切り）\n{_runner.LastLine}", MessageType.None);
                if (GUILayout.Button("キャンセル")) Cancel();
            }
            else
            {
                var needsBlender = settings.type != GarmentType.Wrap;
                using (new EditorGUI.DisabledScope(avatar == null || needsBlender && (!File.Exists(BlenderRunner.BlenderPath) ||
                                                   BlenderSupport.Check(BlenderRunner.BlenderPath).verdict == BlenderSupport.Verdict.Blocked)))
                    if (GUILayout.Button("生成", GUILayout.Height(32)))
                        StartGenerate();
            }
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.None);
            if (_runner == null) return;
            showLog = EditorGUILayout.Foldout(showLog, "Blender のログ", true);
            if (showLog)
            {
                _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(160));
                // （その末尾: テキストエリアに出せる量には限りがある）
                var log = _runner.Log.ToString();
                EditorGUILayout.TextArea(log.Length > MaxLogShown ? log.Substring(log.Length - MaxLogShown) : log);
                EditorGUILayout.EndScrollView();
            }
        }

        void StartGenerate()
        {
            try
            {
                var root = avatar;
                var choices = new Dictionary<string, bool>(_wearOver);
                if (EditorUtility.IsPersistent(root))
                {
                    root = (GameObject)PrefabUtility.InstantiatePrefab(root);
                    Undo.RegisterCreatedObjectUndo(root, "Place Avatar");
                    avatar = root;
                    // アセットで選んだもの。配置したアバターがこれ以降に持つ形で
                    bodyOverride = InAvatar(bodyOverride, root, "ボディメッシュ");
                    wrapOverride = InAvatar(wrapOverride, root, "包むメッシュ");
                    ResetFound();
                }
                _gen = GarmentGenerator.Start(root, InAvatar(bodyOverride, root, "ボディメッシュ"), settings,
                    c => choices.TryGetValue(LayerPath(root, c.Renderer), out var on) ? on : c.Under,
                    settings.type == GarmentType.Wrap ? InAvatar(wrapOverride, root, "包むメッシュ") : null);
                _runner = _gen.Runner;
                _startTime = EditorApplication.timeSinceStartup;
                _status = "";
                EditorApplication.update += Poll;
            }
            catch (Exception e)
            {
                _status = "開始できませんでした: " + e.Message;
                Debug.LogException(e);
            }
        }

        /// <summary>
        /// ウィンドウで選んだレンダラーを、アバター `root` が持つ形で返す: シーンで `root` の中にあればそれ自身、
        /// なければ同じパスにあるもの（アバターはプレハブアセットで、生成のためにシーンに配置した）。
        /// `root` のそこに何もない（ほかのアバターのものである）ときは、それやアセットを変えずに例外を投げる。
        /// </summary>
        static SkinnedMeshRenderer InAvatar(SkinnedMeshRenderer r, GameObject root, string what)
        {
            if (r == null) return null;
            if (!EditorUtility.IsPersistent(r) && r.transform.IsChildOf(root.transform)) return r;
            var path = AnimationUtility.CalculateTransformPath(r.transform, r.transform.root);
            var found = root.transform.Find(path)?.GetComponent<SkinnedMeshRenderer>();
            if (found == null) throw new InvalidOperationException($"{what}（{r.name}）がアバター {root.name} の中に見つかりません。指定し直してください。");
            return found;
        }

        /// <summary>アバターで見つけたものを探し直す（何かを作った、配置した、または途中で失敗した後）。</summary>
        void ResetFound() => (_candidatesFor, _wrapFor, _layers, _spanFor) = (null, (null, null), null, null);

        void Cancel()
        {
            _runner?.Kill();
            EditorApplication.update -= Poll;
            _gen?.Discard();
            _status = "キャンセルしました。";
        }

        void Poll()
        {
            _runner.Pump();
            Repaint();
            if (_runner.IsRunning) return;
            EditorApplication.update -= Poll;
            _runner.Pump();
            try
            {
                _gen.Finish();
                _status = _gen.Status;
                ResetFound();
                Selection.activeGameObject = _gen.Result.gameObject;
                EditorGUIUtility.PingObject(_gen.Result.gameObject);
            }
            catch (Exception e)
            {
                _gen.Discard();
                ResetFound();
                _status = "生成に失敗しました: " + e.Message;
                showLog = true;
                Debug.LogException(e);
            }
        }
    }
}
