// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using UnityEngine;

namespace LegWeaver
{
    public enum GarmentType
    {
        Tights,
        Spats,
        ThighHigh,
        /// <summary>ニーハイ（上端は膝のすぐ下か上）。</summary>
        KneeHigh,
        Gloves,
        ArmCovers,
        /// <summary>
        /// 体以外のメッシュをニットで包む（一部のアバターが脚の代わりに持つ下半身、幽霊の尾）: WrapGarment を使い、
        /// Blender は使わない。
        /// </summary>
        Wrap,
        /// <summary>足の親指だけを覆うサック（タイツの下に着る、爪による伝線を防ぐ補強）。</summary>
        ToeCap,
        /// <summary>ガーター（脚に巻く帯）。素肌の上か、前に作ったタイツ・靴下の上に付ける。</summary>
        Garter,
        /// <summary>レッグラップ（脚にらせんに巻くリボン。交差もできる）。素肌の上か、前に作ったタイツ・靴下の上に付ける。</summary>
        LegWrap,
    }

    public enum GloveLength
    {
        /// <summary>手首まで。</summary>
        Wrist,
        /// <summary>前腕の半ばまで。</summary>
        Forearm,
        /// <summary>肘の上まで（オペラグローブ）。</summary>
        Opera,
    }

    public enum GloveFingers
    {
        /// <summary>指を 1 本ずつ覆う（体の指に沿う）。</summary>
        Five,
        /// <summary>指の付け根で終わる。</summary>
        Fingerless,
        /// <summary>絵描き用: 薬指と小指だけを包み、親指は穴から出し、人差し指と中指は付け根で終わる。</summary>
        Drawing,
    }

    /// <summary>ソックスの上端のバンド。</summary>
    public enum SockTop
    {
        Plain,
        /// <summary>レースのバンド（小さな穴の網目）。</summary>
        Lace,
        /// <summary>シリコンの滑り止めの波線。</summary>
        Silicone,
    }

    public enum ToeStyle
    {
        /// <summary>全部の指を 1 つの袋に入れる（指の間はつなぐ）。</summary>
        Bundled,
        /// <summary>指を 1 本ずつ別の筒に入れ、体の指（アバターにあれば指のボーンにも）に沿わせる。</summary>
        FiveToe,
        /// <summary>足袋型: 親指だけを別の筒に入れ、ほかの 4 本はまとめて包む。</summary>
        Tabi,
    }

    public enum OutfitMode
    {
        /// <summary>アバターが腰と太ももに着けているもの（下着）の上から衣装を着せる。</summary>
        OverUnderLayers,
        /// <summary>肌だけを数え、ほかのメッシュは無視する。</summary>
        SkinOnly,
    }

    /// <summary>衣装のつま先: 密に編む（補強）、わずかに密にする（サンダルトウ）、何もしない（ヌードトウ）。</summary>
    public enum ToeReinforcement
    {
        Nude,
        Sandal,
        Reinforced,
    }

    /// <summary>
    /// 本物のタイツで編み方の違う部分。それぞれ専用のマテリアルで描く。Blender は三角形ごとにこれらの
    /// 番号（Blender/lw_regions.py の REGION_*）を書き出す: 両者を同じ順に保つこと。
    /// </summary>
    public enum GarmentRegion
    {
        Leg,
        Panty,
        Band,
        Reinforce,
        /// <summary>フェイクのサイハイ: 境目より上のストッキング部分。</summary>
        Upper,
        /// <summary>フェイクのサイハイ: 境目より下のソックスの上端のバンド。</summary>
        SockBand,
        /// <summary>中央の縫い目（前と後ろの一方または両方）。</summary>
        Seam,
        /// <summary>股のひし形のマチ。</summary>
        Gusset,
    }

    /// <summary>脚に編み込む柄（密な編み目）。</summary>
    public enum PatternKind
    {
        None,
        /// <summary>脚の後ろを縦に通る縫い目。</summary>
        BackSeam,
        Dots,
        Cats,
    }

    /// <summary>フェイクのサイハイの境目。</summary>
    public enum BorderKind
    {
        Plain,
        /// <summary>境目より上はレースのスカラップ、ソックスの上端のバンドは網目。</summary>
        Lace,
        /// <summary>ソックスが波形に上へ伸びる。</summary>
        Wave,
        /// <summary>各脚の前に猫の顔: 耳は境目より上に出て、顔は境目より下にある。</summary>
        Cat,
    }

    /// <summary>下着の上に着る透ける衣装: 両方を満たせないところでどちらを優先するか。</summary>
    public enum UnderwearFit
    {
        /// <summary>下着はどこでも衣装の内側に収まる（ゆるいフリルが太ももの間で衣装をくしゃくしゃにすることがある）。
        /// </summary>
        Inside,
        /// <summary>衣装は太ももの間でなめらかに沿う（下着のゆるい部分が突き抜けることがある）。</summary>
        SmoothCrotch,
    }

    /// <summary>衣装をどれだけ細かく作るか。</summary>
    public enum Quality
    {
        /// <summary>体と同じ細かさ（アバターに軽い）。</summary>
        Normal,
        /// <summary>曲がるところをより細かく丸くする。三角形は HighQualityMaxTriangles 個まで。</summary>
        High,
    }

    public enum CenterSeam
    {
        None,
        Front,
        Back,
        Both,
    }

    public enum HeelShape
    {
        /// <summary>かかとを囲む U 字のカップ。</summary>
        Cup,
        /// <summary>Y ヒールの斜めの線。</summary>
        Y,
    }

    public enum ToeShape
    {
        /// <summary>指の上を横切るまっすぐな線。</summary>
        Straight,
        /// <summary>トウキャップのように、指の両脇へ回り込んで戻る。</summary>
        Rounded,
    }

    /// <summary>柄をどちらの脚に付けるか。</summary>
    public enum PatternSide
    {
        Both,
        Left,
        Right,
    }

    public enum SkinRemoval
    {
        /// <summary>衣装が不透明なとき、または肌の写しが肌の代わりになるとき、覆われた肌を取り除く。</summary>
        Auto,
        Always,
        Never,
    }

    /// <summary>プリセットとして出す色: 黒と、持つ価値のある色付きタイツ（水色・紫）。</summary>
    public static class GarmentColors
    {
        public static readonly Color Black = new(0.05f, 0.05f, 0.06f, 1f);
        public static readonly Color LightBlue = new(0.5f, 0.76f, 0.95f, 1f);
        public static readonly Color Purple = new(0.42f, 0.27f, 0.6f, 1f);
        public static readonly (string label, Color color)[] Presets = { ("黒", Black), ("水色", LightBlue), ("紫", Purple) };
    }
}
