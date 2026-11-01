// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using UnityEditor;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// 衣装に編み込む柄。メッシュが UV1 に持つ脚の座標で描く: x = 脚の周り（周回数。前が 0、両脚がそろうよう
    /// 左右反転。右脚は 2 周ずらすので、柄を片脚だけに付けられる）/ 4、y = 脚に沿った s を [S0, S1] から
    /// 写したもの（s: 股関節 0、膝 1、足首 2、つま先 3、股関節より上は負）。
    /// 下の模様はこの単位で描く: 脚に沿った長さは s（0.1 は股関節から膝までの 10 分の 1）、周りの幅は周回数
    /// （0.1 は脚の周囲の 10 分の 1）、Smooth の幅はそれぞれの縁のぼかし。
    /// テクスチャは lilToon の 3rd テクスチャ。透ける編み地: アルファは「加算」（柄は密な編み）で、テクスチャの色が
    /// 編み地の色に置き換わる（テクスチャが編み地自身の色、そのグラデーション、伝線を持つ）。
    /// 不透明な編み地: 色だけ。
    /// </summary>
    public static class LegPattern
    {
        public const float S0 = -1.5f, S1 = 3.5f;
        /// <summary>テクスチャが覆う u の周回数: 左脚が [0, 2)、右脚が [2, 4)。</summary>
        const float Turns = 4f;
        const int Width = 2048, Height = 2048;

        /// <summary>脚の座標 (s, u) の点の UV1。</summary>
        public static Vector2 UV(Vector2 leg) => new(leg.y / Turns, (leg.x - S0) / (S1 - S0));

        /// <summary>`region` の (s, u) での柄の濃さ（0..1）。その部位に柄が無ければ 0。</summary>
        static float Density(GarmentSettings g, GarmentRegion r, float s, float u, bool right)
        {
            var p = 0f;
            var onThisLeg = g.patternSide == PatternSide.Both || (g.patternSide == PatternSide.Right) == right;
            // フェイクサイハイでは靴下の部分、ほかでは脚（足には付けない: 補強されているか、曲がって向きが変わる）
            var legPart = g.IsFakeThighHigh
                ? r is GarmentRegion.Leg && s > g.fakeTop
                : r is GarmentRegion.Leg;
            var feet = Smooth(1.9f - s, 0.06f); // 脚が足へ曲がる足首の上で消えていく
            if (onThisLeg)
                switch (g.pattern)
                {
                    case PatternKind.BackSeam:
                        // 後ろを足首の少し上から腰まで、足より上のすべての部位を通って上る
                        if (r != GarmentRegion.Reinforce)
                            p = Smooth(0.005f * g.patternScale - Mathf.Abs(u - 0.5f), 0.0015f) * feet;
                        break;
                    case PatternKind.Dots when legPart:
                        p = Cells(s, u, 14, 0.07f, g.patternScale, (x, y) => Smooth(0.27f - Mathf.Sqrt(x * x + y * y), 0.03f)) * feet;
                        break;
                    case PatternKind.Cats when legPart:
                        p = Cells(s, u, 8, 0.12f, g.patternScale, Cat) * feet;
                        break;
                }
            if (g.IsFakeThighHigh && g.fakeBorder != BorderKind.Plain && r is GarmentRegion.Upper or GarmentRegion.SockBand)
                p = Mathf.Max(p, Border(g, r, s, u));
            return Mathf.Clamp01(p);
        }

        /// <summary>伝線: 編み地がほつれた所の、脚に沿った `runCount` 本の細い線（`runSeed` から決める）。</summary>
        static (bool right, float u0, float s0, float len)[] RunList(GarmentSettings g)
        {
            var rng = new SplitMix64((ulong)(uint)g.runSeed);
            var list = new (bool, float, float, float)[Mathf.Max(0, g.runCount)];
            for (var i = 0; i < list.Length; i++)
                list[i] = (rng.NextDouble() < 0.5, (float)rng.NextDouble(), 0.2f + (float)rng.NextDouble() * 1.4f,
                    0.15f + (float)rng.NextDouble() * 0.5f);
            return list;
        }

        /// <summary>
        /// SplitMix64: 数行で完全に定まる小さな乱数生成器。同じシードならどこでも伝線が同じ所に付く
        /// （System.Random の列は仕様で決まっていない）。NextDouble = 上位 53 ビット / 2^53。
        /// </summary>
        struct SplitMix64
        {
            ulong _state;

            public SplitMix64(ulong seed) => _state = seed;

            public ulong Next()
            {
                var z = _state += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }

            public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));
        }

        static float Runs((bool right, float u0, float s0, float len)[] runs, float s, float u, bool right)
        {
            var hit = 0f;
            foreach (var (onRight, u0, s0, len) in runs)
            {
                if (onRight != right || s < s0 || s > s0 + len) continue;
                // わずかなジグザグで、両端へ向かって細くなる
                var du = Mathf.Abs(Mathf.Repeat(u - u0 + 0.5f, 1f) - 0.5f - 0.0015f * Mathf.Sin(s * 400f));
                var t = (s - s0) / len;
                var w = 0.004f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(t));
                hit = Mathf.Max(hit, Smooth(w - du, 0.0015f));
            }
            return hit;
        }

        /// <summary>
        /// フェイクサイハイの境目: レース（境目の上にスカラップ、その下の帯に網目）、波（靴下が波の形に上へ伸びる）、
        /// または猫（脚の前に顔、境目の上に耳）。
        /// </summary>
        static float Border(GarmentSettings g, GarmentRegion r, float s, float u)
        {
            var above = g.fakeTop - s; // ストッキング側で > 0
            if (g.fakeBorder == BorderKind.Lace)
            {
                const int n = 16;
                const float h = 0.06f;
                var x = (u * n % 1f) - 0.5f; // スカラップ 1 つの中で -0.5..0.5
                if (r == GarmentRegion.Upper)
                {
                    if (above < 0) return 0f;
                    var e = Mathf.Sqrt(x * x * 4f + (above / h) * (above / h)); // スカラップの縁で 1
                    var holes = Net(s, u, 72f, 0.018f) * Smooth(0.75f - e, 0.05f);
                    return Smooth(1f - e, 0.06f) * (1f - 0.7f * holes);
                }
                // 帯: 穴の開いた網目
                return 1f - 0.5f * Net(s, u, 72f, 0.018f);
            }
            if (g.fakeBorder == BorderKind.Cat)
                return r == GarmentRegion.Upper && above >= 0 ? CatEars(above, Front(u)) : 0f;
            if (r != GarmentRegion.Upper || above < 0) return 0f;
            // 脚の周りに 7 つの波。高さは s で 0.035、境目から 0.004 上までの帯の上に乗る
            const int waves = 7;
            var crest = 0.035f * (0.5f + 0.5f * Mathf.Cos(u * waves * 2f * Mathf.PI));
            return Smooth(crest + 0.004f - above, 0.003f);
        }

        /// <summary>前から測った脚の周りの位置: -0.5..0.5 周（両脚とも同じように左右反転）。</summary>
        static float Front(float u) => u > 0.5f ? u - 1f : u;

        /// <summary>キャットタイツ: 猫の頭の大きさ（顔が脚の周りのおよそ 3 分の 1 にわたる）。</summary>
        const float CatSize = 1.7f;

        /// <summary>
        /// キャットタイツ: 前から `x` 周の所で境目から `above`（s）だけ上に立つ猫の耳。靴下の編み地で描き、
        /// 内側に小さな透ける部分がある。
        /// </summary>
        static float CatEars(float above, float x)
        {
            var ears = 0f;
            above /= CatSize;
            x /= CatSize;
            foreach (var side in new[] { -1f, 1f })
            {
                // 境目の上の、少し外へ傾いた三角形: 高さは s で 0.085、底辺の半幅は 0.042 周、前の左右 0.066 周の所。
                // 0.02 から 0.05 の高さに透ける部分（半幅 0.009）がある
                const float h = 0.085f, half = 0.042f;
                var t = above / h;
                if (t > 1f) continue;
                var cx = side * (0.066f + 0.01f * t);
                var outer = Smooth(half * (1f - t) - Mathf.Abs(x - cx), 0.003f);
                var ti = (above - 0.02f) / 0.03f;
                var inner = ti is > 0f and < 1f ? Smooth(0.009f * (1f - ti) - Mathf.Abs(x - cx), 0.003f) : 0f;
                ears = Mathf.Max(ears, outer * (1f - inner));
            }
            return ears;
        }

        /// <summary>
        /// キャットタイツ: 前から `x` 周の所で境目から `below`（s）だけ下にある猫の顔。靴下の上にストッキングの色で
        /// 描く: 目が 2 つ、鼻、左右それぞれ 3 本のひげ。
        /// </summary>
        static float CatFace(float below, float x)
        {
            below /= CatSize;
            x /= CatSize;
            // （猫の大きさで: 顔は高さが s で 0.16、前の左右 0.13 周まで。目は 0.07 下・0.045 外、鼻は 0.098 下、
            // ひげは 0.112 下から扇形に広がる）
            if (below < 0f || below > 0.16f || Mathf.Abs(x) > 0.13f) return 0f;
            var f = 0f;
            foreach (var side in new[] { -1f, 1f })
            {
                // 目: 縦長の楕円
                var ex = (x - side * 0.045f) / 0.013f;
                var ey = (below - 0.07f) / 0.02f;
                f = Mathf.Max(f, Smooth(1f - Mathf.Sqrt(ex * ex + ey * ey), 0.2f));
                // ひげ: 鼻の横から外へ扇形に広がる
                for (var k = -1; k <= 1; k++)
                {
                    var along = (x - side * 0.03f) * side; // 外へ 0..0.07
                    if (along < 0f || along > 0.07f) continue;
                    var y = 0.112f + k * 0.011f + along * k * 0.25f;
                    var w = 0.0016f * (1f - along / 0.08f);
                    f = Mathf.Max(f, Smooth(w - Mathf.Abs(below - y), 0.0009f));
                }
            }
            // 鼻: 頂点が下を向いた小さな三角形
            var nt = (below - 0.098f) / 0.014f;
            if (nt is > 0f and < 1f) f = Mathf.Max(f, Smooth(0.011f * (1f - nt) - Mathf.Abs(x), 0.0015f));
            return f;
        }

        /// <summary>格子状に並ぶ丸い穴: 穴の中で 1。</summary>
        static float Net(float s, float u, float around, float along)
        {
            var x = u * around % 1f - 0.5f;
            var y = ((s / along) % 1f + 1f) % 1f - 0.5f;
            return Smooth(0.3f - Mathf.Sqrt(x * x + y * y), 0.05f);
        }

        /// <summary>互い違いの列に繰り返す模様: 1 周に `around` 個、s で `along` ごとに 1 列。</summary>
        static float Cells(float s, float u, int around, float along, float scale, System.Func<float, float, float> motif)
        {
            var n = Mathf.Max(2, Mathf.RoundToInt(around / Mathf.Max(scale, 0.1f)));
            var ds = along * scale;
            var row = Mathf.FloorToInt(s / ds);
            var y = s / ds - row - 0.5f;
            var x = u * n + (row % 2 != 0 ? 0.5f : 0f);
            x = x - Mathf.Floor(x) - 0.5f;
            return motif(x, y);
        }

        /// <summary>
        /// 猫の頭（耳は腰の方、つまり -s の方）。目は抜いて残す。セル自身の座標（x と y はどちらも -0.5..0.5）で:
        /// 頭の半径 0.25、耳は高さ 0.25 で左右 0.15 の所、目は差し渡し 0.045 で 0.09 の所。
        /// </summary>
        static float Cat(float x, float y)
        {
            var head = Smooth(0.25f - Mathf.Sqrt(x * x + (y - 0.05f) * (y - 0.05f) * 1.2f), 0.02f);
            var ear = 0f;
            foreach (var side in new[] { -1f, 1f })
            {
                // 三角形: 底辺は頭の上端、先は上と外へ
                var ex = x * side - 0.15f;
                var ey = -(y + 0.08f); // 上 = -s の方
                if (ey > 0 && ey < 0.25f && Mathf.Abs(ex - ey * 0.25f) < 0.11f * (1f - ey / 0.25f)) ear = 1f;
            }
            var eyes = 0f;
            foreach (var side in new[] { -1f, 1f })
                eyes = Mathf.Max(eyes, Smooth(0.045f - Mathf.Sqrt((x - side * 0.09f) * (x - side * 0.09f) + (y - 0.02f) * (y - 0.02f)), 0.01f));
            return Mathf.Max(head, ear) * (1f - eyes);
        }

        static float Smooth(float d, float w) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / w + 0.5f));

        /// <summary>部位のマテリアルにそもそもテクスチャを付けるか。</summary>
        public static bool Has(GarmentSettings g, GarmentRegion r) =>
            g.IsSock && g.sockTop != SockTop.Plain && r == GarmentRegion.Band ||
            g.gradient || g.runCount > 0 ||
            g.pattern switch
            {
                PatternKind.BackSeam => r != GarmentRegion.Reinforce,
                PatternKind.Dots or PatternKind.Cats => r == GarmentRegion.Leg,
                _ => false,
            } || g.IsFakeThighHigh && g.fakeBorder != BorderKind.Plain && r is GarmentRegion.Upper or GarmentRegion.SockBand ||
            g.IsFakeThighHigh && g.fakeBorder == BorderKind.Cat && r == GarmentRegion.Leg;

        /// <summary>
        /// 部位のマテリアルの 3rd テクスチャ。色: 編み地の色（脚を下るにつれ `gradientColor` へ）、柄のある所は柄の色、
        /// 伝線に沿っては `skin` へ。アルファ: 透ける編み地ではどれだけ密か（柄）、不透明な編み地では 1（色だけ）。
        /// </summary>
        public static Texture2D Create(GarmentSettings g, GarmentRegion r, Color knit, bool sheer, Color skin, string path)
        {
            var tex = new Texture2D(Width, Height, TextureFormat.RGBA32, true, false);
            var px = new Color[Width * Height];
            var pattern = g.patternColor;
            var runs = RunList(g);
            for (var y = 0; y < Height; y++)
            {
                var s = S0 + (y + 0.5f) / Height * (S1 - S0);
                var down = g.gradient ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(g.gradientTop, g.gradientBottom, s)) : 0f;
                var baseColor = Color.Lerp(knit, g.gradientColor, down);
                for (var x = 0; x < Width; x++)
                {
                    var turns = (x + 0.5f) / Width * Turns;
                    var right = turns >= 2f;
                    var u = turns - Mathf.Floor(turns);
                    var d = Density(g, r, s, u, right);
                    // 境目は上へ伸びた靴下なので、靴下の色で描く
                    var c = Color.Lerp(baseColor, r == GarmentRegion.Upper ? g.color : pattern, d);
                    // 靴下の上端の帯: レースの穴（肌が見える）、または滑り止めのわずかに光る線
                    if (g.IsSock && r == GarmentRegion.Band && g.sockTop == SockTop.Lace)
                        c = Color.Lerp(c, skin, 0.75f * Net(s, u, 56f, 0.014f));
                    if (g.IsSock && r == GarmentRegion.Band && g.sockTop == SockTop.Silicone)
                    {
                        var wave = s / 0.01f + 0.25f * Mathf.Sin(u * 2f * Mathf.PI * 14f);
                        c = Color.Lerp(c, Color.white, 0.25f * Smooth(0.18f - Mathf.Abs(wave - Mathf.Round(wave)), 0.05f));
                    }
                    // キャットタイツ: 靴下の上の顔を、ストッキングの色で描く
                    if (g.IsFakeThighHigh && g.fakeBorder == BorderKind.Cat && r is GarmentRegion.Leg or GarmentRegion.SockBand)
                        c = Color.Lerp(c, g.upperColor, CatFace(s - g.fakeTop, Front(u)));
                    var run = Runs(runs, s, u, right);
                    c = Color.Lerp(c, skin, run * 0.7f);
                    // （猫の耳は上へ伸びた靴下なので、靴下と同じ密度）
                    var a = d * (g.IsFakeThighHigh && g.fakeBorder == BorderKind.Cat && r == GarmentRegion.Upper ? 1f : g.patternOpacity);
                    c.a = sheer ? Mathf.Clamp01(a) : 1f;
                    px[y * Width + x] = c;
                }
            }
            tex.SetPixels(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.wrapModeU = TextureWrapMode.Repeat;
            importer.wrapModeV = TextureWrapMode.Clamp;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = Width;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
