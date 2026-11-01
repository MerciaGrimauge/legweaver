// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using UnityEngine;

namespace LegWeaver
{
    /// <summary>ニットの表面の見え方（ナイロン / ポリウレタンのニット。綿は手袋だけ）。</summary>
    public enum Finish
    {
        /// <summary>lilToon の既定値（独自の陰影なし）。</summary>
        Standard,
        /// <summary>光沢のほとんどないナイロン。柔らかい陰影。</summary>
        Matte,
        /// <summary>縁に向かって柔らかい光沢: 普段使いのストッキング。</summary>
        SemiGloss,
        /// <summary>光沢のあるタイツ。</summary>
        Gloss,
        /// <summary>強く鋭い光沢: サテン、ウェットルック。</summary>
        Satin,
        /// <summary>段のついた陰影と硬いハイライト（アニメ調）。</summary>
        Toon,
        /// <summary>綿（手袋だけ）: 光沢がまったくなく、柔らかい陰影。</summary>
        Cotton,
    }

    /// <summary>
    /// 仕上げを lilToon の設定で表したもの: 影（光の落ち方）、反射（光沢とハイライト）、リムライト。
    /// 見る角度による濃さとニットの伸びは別のもので、そのまま残す。
    /// </summary>
    public static class GarmentFinish
    {
        public static readonly string[] Labels = { "標準", "マット", "セミグロス", "光沢", "サテン（ウェット）", "トゥーン" };
        /// <summary>手袋には綿もある。</summary>
        public static readonly string[] GloveLabels = { "標準", "マット", "セミグロス", "光沢", "サテン（ウェット）", "トゥーン", "綿" };

        public static void Apply(Material mat, Finish finish)
        {
            if (finish == Finish.Standard) return;
            void Set(string name, float v)
            {
                if (mat.HasProperty(name)) mat.SetFloat(name, v);
            }

            // 陰影: 写実的な仕上げは柔らかく、トゥーンは硬い境目
            Set("_UseShadow", 1);
            Set("_ShadowStrength", finish == Finish.Toon ? 0.6f : 0.45f);
            Set("_ShadowBorder", 0.5f);
            Set("_ShadowBlur", finish == Finish.Toon ? 0.03f : 0.5f);

            // 光沢: 滑らかさと反射の量
            if (finish == Finish.Cotton)
            {
                Set("_UseReflection", 0);
                Set("_UseRim", 0);
                return;
            }
            var (smooth, reflect, toonSpec) = finish switch
            {
                Finish.Matte => (0.25f, 0.02f, false),
                Finish.SemiGloss => (0.55f, 0.04f, false),
                Finish.Gloss => (0.75f, 0.08f, false),
                Finish.Satin => (0.9f, 0.16f, false),
                _ => (0.7f, 0.06f, true),
            };
            Set("_UseReflection", 1);
            Set("_Smoothness", smooth);
            Set("_Metallic", 0);
            Set("_Reflectance", reflect);
            Set("_ApplySpecular", 1);
            Set("_SpecularToon", toonSpec ? 1 : 0);

            // 光沢のあるものは縁に沿って光のリムを付ける（ナイロンは浅い角度で光を受ける）
            var rim = finish is Finish.SemiGloss or Finish.Gloss or Finish.Satin or Finish.Toon;
            Set("_UseRim", rim ? 1 : 0);
            if (rim && mat.HasProperty("_RimColor"))
            {
                var strength = finish switch { Finish.SemiGloss => 0.25f, Finish.Gloss => 0.4f, Finish.Satin => 0.6f, _ => 0.35f };
                mat.SetColor("_RimColor", new Color(1f, 1f, 1f, strength));
                Set("_RimBorder", finish == Finish.Toon ? 0.6f : 0.5f);
                Set("_RimBlur", finish == Finish.Toon ? 0.05f : 0.6f);
            }
        }
    }
}
