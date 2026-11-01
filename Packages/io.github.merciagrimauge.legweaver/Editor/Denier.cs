// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// 実物のデニールの参考値。被覆率は、正面から見たときにニットがさえぎる光の割合（メーカーの表: 20D はシアー、
    /// 30-40D は半透明、60D はほぼ不透明、80D+ は不透明）。厚みの値は推定値（mm の公表データは見つからなかった）。
    /// </summary>
    public static class Denier
    {
        public const float OpaqueCoverage = 0.95f;

        static readonly Vector2[] CoverageTable =
        {
            new(0, 0f), new(5, 0.08f), new(10, 0.15f), new(15, 0.22f), new(20, 0.30f), new(30, 0.45f),
            new(40, 0.60f), new(60, 0.82f), new(80, 0.95f), new(110, 0.99f), new(200, 1f), new(1000, 1f),
        };

        /// <summary>
        /// ~200D を超えると、裏起毛タイツの「デニール」（1000D 相当などとして売られる）は暖かさの目安である。
        /// 起毛した裏地により厚みは数ミリになる。
        /// </summary>
        static readonly Vector2[] ThicknessTable =
        {
            new(0, 0.08f), new(10, 0.10f), new(20, 0.15f), new(40, 0.25f), new(80, 0.40f), new(110, 0.50f),
            new(200, 0.90f), new(400, 1.50f), new(700, 2.20f), new(1000, 3.00f),
        };

        public const float MinDenier = 5f;
        public const float MaxDenier = 1000f;

        public static float Coverage(float denier) => Lerp(CoverageTable, denier);
        public static float ThicknessMm(float denier) => Lerp(ThicknessTable, denier);

        public static string Describe(float denier)
        {
            if (denier < 15) return "極薄（ほぼ素肌）";
            if (denier < 25) return "薄手（肌がうっすら色づく）";
            if (denier < 50) return "半透明";
            if (denier < 80) return "ほぼ不透明";
            if (denier < 200) return "不透明";
            return "厚手（裏起毛相当）";
        }

        static float Lerp(Vector2[] table, float x)
        {
            if (x <= table[0].x) return table[0].y;
            for (var i = 1; i < table.Length; i++)
            {
                if (x <= table[i].x)
                {
                    var a = table[i - 1];
                    var b = table[i];
                    return Mathf.Lerp(a.y, b.y, (x - a.x) / (b.x - a.x));
                }
            }
            return table[^1].y;
        }

        /// <summary>
        /// 法線から角度 θ で見たときの不透明度: ニットを通る光の道のりは 1/cosθ で伸びるので、
        /// 透過率 (1 - c) は (1 - c)^(1/cosθ) になる。透けるタイツが縁で暗く見えるのはこのため。
        /// </summary>
        public static float OpacityAtAngle(float coverage, float cosTheta)
        {
            cosTheta = Mathf.Max(cosTheta, 0.02f);
            return 1f - Mathf.Pow(1f - Mathf.Clamp01(coverage), 1f / cosTheta);
        }
    }
}
