// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UnityEngine;

namespace LegWeaver
{
    [Serializable]
    public class GarmentSettings
    {
        public GarmentType type = GarmentType.Tights;
        public Color color = GarmentColors.Black;
        public float denier = 30f;

        /// <summary>
        /// 2 層の「見せかけの透け」タイツ: 厚い編み地（上のデニール。実際は不透明）の肌色の裏地が透ける外側の編み地
        /// 越しに見え、`apparentDenier` のように見える。
        /// </summary>
        public bool twoLayer;
        public float apparentDenier = 60f;
        /// <summary>裏地の色をアバター自身の脚の肌から取る（でなければ `liningColor`）。</summary>
        public bool liningFromSkin = true;
        public Color liningColor = new Color(0.93f, 0.78f, 0.68f, 1f);

        /// <summary>ウエストの線: 0 = 股関節、1 = 胸。</summary>
        public float waistHeight = 0.45f;
        /// <summary>脚に沿ったスパッツの裾: 0 = 股関節、1 = 膝、2 = 足首。</summary>
        public float legCut = 0.55f;
        /// <summary>脚に沿ったサイハイの上端: 0 = 股関節、1 = 膝。</summary>
        public float thighTop = 0.25f;
        /// <summary>脚に沿ったニーハイの上端: 1 = 膝（関節を避ける: 0.9 未満か 1.1 より先）。</summary>
        public float kneeTop = 1.25f;
        /// <summary>サイハイとニーハイ: 上端の帯の見た目。</summary>
        public SockTop sockTop = SockTop.Plain;
        /// <summary>ガーター: 帯の中心の脚の座標（0 = 股関節、1 = 膝、2 = 足首）。</summary>
        public float garterAt = 0.5f;
        /// <summary>ガーター: 帯の幅（mm）。</summary>
        public float garterWidthMm = 25f;
        /// <summary>ガーター・レッグラップ: 前に作ったタイツ・靴下があれば、その上に付ける（でなければ素肌の上）。</summary>
        public bool overLegwear = true;
        /// <summary>
        /// ガーター・レッグラップ: アバター自身の脚の服（靴下・レッグウォーマーなど）の上にも巻く。オフなら、その服は無い物と
        /// して肌（またはタイツ）の上に巻く（服を脱がせた状態用。服を着たままでは服を突き抜ける）。
        /// </summary>
        public bool overAvatarLegwear = true;
        /// <summary>ガーター: 帯の前の外寄りにリボンの結び目を付ける（垂れは揺れ物で揺れる）。</summary>
        public bool garterRibbon = true;
        /// <summary>レッグラップ: 巻く範囲の両端の脚の座標（0 = 股関節、1 = 膝、2 = 足首）。</summary>
        public float wrapFrom = 1.9f, wrapTo = 1.15f;
        /// <summary>レッグラップ: 範囲の中で脚の周りを巻く回数。</summary>
        public float wrapTurns = 3f;
        /// <summary>レッグラップ: リボンの幅（mm）。</summary>
        public float wrapWidthMm = 12f;
        /// <summary>レッグラップ: 逆向きのらせんも巻いて交差させる。</summary>
        public bool wrapCross = true;
        /// <summary>ガーター・レッグラップ（脚の衣類の上に付ける小物）。</summary>
        public bool IsAccessory => type is GarmentType.Garter or GarmentType.LegWrap;

        public bool IsSock => type is GarmentType.ThighHigh or GarmentType.KneeHigh;

        public GloveLength gloveLength = GloveLength.Wrist;
        public GloveFingers gloveFingers = GloveFingers.Five;
        /// <summary>アームカバー: 腕に沿った上端（0 肩関節、1 肘、2 手首）。</summary>
        public float armTop = 0.6f;
        /// <summary>アームカバー: 手の甲を指の付け根まで覆い、親指の穴を開ける（でなければ手首まで）。</summary>
        public bool armToHand;

        /// <summary>重い処理（高品質）には長い方の打ち切り時間を使う。</summary>
        public Quality TimeClass => quality;

        public bool IsArm => type is GarmentType.Gloves or GarmentType.ArmCovers;
        /// <summary>
        /// 親指のサックは肌にぴったり沿わせ、上に着るタイツの内側に収める。ガーターの帯は締まる: 肌（またはその下の
        /// タイツ）との隙間はこれ以下にする。
        /// </summary>
        public const float ToeCapClearanceMm = 0.3f;
        /// <summary>Blender に渡す、肌との隙間。</summary>
        public float ClearanceForJob => type is GarmentType.ToeCap or GarmentType.Garter or GarmentType.LegWrap
            ? Mathf.Min(clearanceMm, ToeCapClearanceMm) : clearanceMm;
        /// <summary>腕に沿った手袋の上端。</summary>
        public float GloveTopAt => gloveLength switch { GloveLength.Opera => 0.55f, GloveLength.Forearm => 1.5f, _ => 1.92f };
        /// <summary>脚の座標での靴下の上端。</summary>
        public float SockTopAt => type == GarmentType.KneeHigh ? kneeTop : thighTop;
        /// <summary>膝にこれほど近いニーハイの上端は、関節の上に乗って曲げると滑る。</summary>
        public bool KneeTopOnJoint => type == GarmentType.KneeHigh && kneeTop > 0.9f && kneeTop < 1.1f;

        /// <summary>肌と衣装の内面の間のすき間。</summary>
        public float clearanceMm = 1.0f;
        /// <summary>ボーンが混ざる所（膝、腰）の追加のゆとり: 関節を曲げるとスキニングでそこのすき間が縮む。</summary>
        public float jointExtraMm = 2.0f;
        public bool autoThickness = true;
        public float thicknessMm = 0.3f;
        public ToeStyle toes = ToeStyle.Bundled;
        /// <summary>実際に使うつま先: 足先を覆う種類だけ設定に従い、ほかの種類（隠れた設定が残っていても）はまとめと同じ。</summary>
        public ToeStyle Toes => type is GarmentType.Tights or GarmentType.ThighHigh or GarmentType.KneeHigh ? toes : ToeStyle.Bundled;
        /// <summary>まとめた指: この約 2 倍より狭い指の間のすき間はつなぐ。</summary>
        public float toeBridgeMm = 4f;
        /// <summary>そこでの体に対する、足の指（SDF の部分）の三角形の割り当て。残りは体自身のものに従う。</summary>
        public float density = 1.0f;
        public float voxelMm = 0.8f;
        /// <summary>伸びる部分（太もも、ふくらはぎ、膝の前）がどれだけ強く透けるか。透けるデニールだけ。</summary>
        public float stretchStrength = 1f;
        /// <summary>
        /// LegWeaver の lilToon カスタムシェーダーで、ポーズにも従う（膝を曲げる = より透ける）。PC のみ。
        /// </summary>
        public bool dynamicStretch = true;
        /// <summary>
        /// 不透明なデニール: 張った部分（足を曲げたときのかかとやつま先、曲げた膝の前、お尻）が、それでも
        /// どれだけ肌を透かすか。0 = 透かさない。
        /// </summary>
        public float opaqueStretchSheer;

        /// <summary>編み地の表面（光沢、陰影）。</summary>
        public Finish finish = Finish.Standard;

        public Quality quality = Quality.Normal;
        public const int HighQualityMaxTriangles = 30000;

        public OutfitMode outfitMode = OutfitMode.OverUnderLayers;
        /// <summary>下着の上の透ける衣装（不透明なものは常に内側に収める）。</summary>
        public UnderwearFit underwearFit = UnderwearFit.Inside;

        public bool KeepsWornInside => IsOpaque && !IsFakeThighHigh || underwearFit == UnderwearFit.Inside;

        // 本物のタイツの作り
        /// <summary>ウエストバンド（とサイハイの上端の帯）: 上端の縁の下の、密な帯。</summary>
        public bool waistband = true;
        /// <summary>タイツ: ウエストバンドから伝線止めまでの部分を密に編む。</summary>
        public bool pantyPart = true;
        /// <summary>タイツ: パンティ部の終わる太ももの上部の、密な輪。</summary>
        public bool runGuard = true;
        public ToeReinforcement toeReinforcement = ToeReinforcement.Reinforced;
        public bool heelReinforcement = true;
        public HeelShape heelShape = HeelShape.Cup;
        public ToeShape toeShape = ToeShape.Straight;
        /// <summary>タイツ: 中央の縫い目（細い、密な線）。</summary>
        public CenterSeam centerSeam = CenterSeam.None;
        /// <summary>タイツ: 股のひし形のマチ（密な編み地。綿ではない）。</summary>
        public bool gusset;
        /// <summary>腰に対するマチの幅（1 = 左右それぞれ腰の幅の 5 分の 1）。</summary>
        public float gussetSize = 1f;
        /// <summary>マチの幅に対する長さ（前から後ろ）。</summary>
        public float gussetAspect = 1.6f;

        /// <summary>
        /// フェイクサイハイのタイツ: 太ももの境目までの靴下の部分（上のデニールと色）と、その上の、肌が透ける
        /// ストッキングの部分。
        /// </summary>
        public bool fakeThighHigh;
        /// <summary>脚に沿った境目: 0 = 股関節、1 = 膝。</summary>
        public float fakeTop = 0.55f;
        public float upperDenier = 15f;
        public Color upperColor = new Color(0.87f, 0.74f, 0.62f, 1f);
        /// <summary>本物のサイハイの上端のような、靴下の部分の上端の密な帯。</summary>
        public bool fakeBand = true;

        public BorderKind fakeBorder = BorderKind.Plain;

        /// <summary>脚の柄（フェイクサイハイでは靴下の部分）。</summary>
        public PatternKind pattern = PatternKind.None;
        public Color patternColor = new Color(0.02f, 0.02f, 0.025f, 1f);
        /// <summary>柄をどれだけ密に編むか（透ける部分）: 0 = まったく密にしない、1 = 不透明。</summary>
        public float patternOpacity = 0.8f;
        /// <summary>模様の大きさ（と縫い目の幅）。相対値。</summary>
        public float patternScale = 1f;
        public PatternSide patternSide = PatternSide.Both;

        /// <summary>
        /// 脚を下るにつれ色が変わる: `gradientTop` より上は衣装の色、`gradientBottom` より下は `gradientColor`
        /// （脚の座標: 0 股関節、1 膝、2 足首）。
        /// </summary>
        public bool gradient;
        public Color gradientColor = new Color(0.45f, 0.42f, 0.44f, 1f);
        public float gradientTop = 0.2f;
        public float gradientBottom = 1.8f;

        /// <summary>編み地の伝線: 本数と、それを置くシード（同じシードなら同じ伝線）。</summary>
        public int runCount;
        public int runSeed = 1;

        /// <summary>足首と膝の裏のたるみのしわ（形状。高品質が最適）。</summary>
        public float wrinkles;

        public bool IsFakeThighHigh => fakeThighHigh && type == GarmentType.Tights;
        public bool UpperIsSheer => Denier.Coverage(upperDenier) < Denier.OpaqueCoverage;

        /// <summary>先に体のシュリンク系シェイプキーを切る（アバター自身の衣装を脱いだときのように）。</summary>
        public bool turnOffShrinkShapes = true;

        /// <summary>
        /// 透ける衣装: 覆われた肌も消し、その写し（体自身の UV とマテリアル）を衣装の内側に描く。写しは衣装と
        /// 一緒に動くので、決して衣装を突き抜けない。
        /// </summary>
        public bool skinLining = true;

        public SkinRemoval skinRemoval = SkinRemoval.Auto;
        public float removalMarginCm = 1.0f;

        public float Thickness => autoThickness ? Denier.ThicknessMm(denier) : thicknessMm;
        public float Coverage => Denier.Coverage(denier);
        /// <summary>肌を隠す（2 層のタイツでは裏地が隠す）: 厚さと肌の削除はこれに従う。</summary>
        public bool IsOpaque => twoLayer || Coverage >= Denier.OpaqueCoverage;
        /// <summary>外側の編み地が透ける（裏地か肌が見える）: マテリアルと伸びはこれに従う。</summary>
        public bool LooksSheer => twoLayer ? LookCoverage < Denier.OpaqueCoverage : !IsOpaque || StretchSheers;

        /// <summary>
        /// 張った所で透ける不透明な衣装: 透ける衣装のように描く（肌の写しも含む）。立った姿勢では不透明で、
        /// ポーズで伸びる所だけより透ける（ポーズに応じるシェーダー、PC）。厚い編み地ほど弱める（200D 以上では無し）。
        /// </summary>
        public bool StretchSheers => IsOpaque && !twoLayer && !IsFakeThighHigh && opaqueStretchSheer > 0 && denier < 200f;

        /// <summary>実際に使う伸びの強さ（不透明なものは、編み地の薄さで縮める）。</summary>
        public float EffectiveStretch => StretchSheers
            ? stretchStrength * opaqueStretchSheer * Mathf.Clamp01((200f - denier) / 140f)
            : stretchStrength;
        /// <summary>外側の編み地が下の物をどれだけ覆うか。</summary>
        public float LookCoverage => twoLayer ? Denier.Coverage(apparentDenier) : StretchSheers ? 1f : Coverage;

        /// <summary>（脚だけ: PoseStretch を参照。腕の衣装は静的な伸びのマスクだけを持つ。）</summary>
        public bool UsesPoseStretch => dynamicStretch && !IsArm && (LooksSheer || IsFakeThighHigh && UpperIsSheer) && EffectiveStretch > 0;

        public bool UsesSkinLining => skinLining && !twoLayer && (LooksSheer || IsFakeThighHigh && UpperIsSheer);

        public bool RemovesSkin => skinRemoval == SkinRemoval.Always ||
                                   (skinRemoval == SkinRemoval.Auto && (IsOpaque || UsesSkinLining));

        public string TypeId => type switch
        {
            GarmentType.Spats => "spats",
            // ニーハイはサイハイと同じように作り、上端だけ自分のものを使う
            GarmentType.ThighHigh or GarmentType.KneeHigh => "thighhigh",
            GarmentType.Gloves => "gloves",
            GarmentType.ArmCovers => "armcovers",
            GarmentType.ToeCap => "toecap",
            GarmentType.Garter => "garter",
            GarmentType.LegWrap => "legwrap",
            _ => "tights",
        };
    }
}
