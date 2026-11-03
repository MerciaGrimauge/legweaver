// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// 衣装の lilToon マテリアル。不透明なデニールは不透明のシェーダーを使う。透けるデニールは、見る角度による
    /// アルファを持つ半透明のシェーダーを使う: MatCap の UV（カメラから見た法線）でサンプルした lilToon の 2nd
    /// テクスチャがアルファを置き換え、テクスチャは Denier.OpacityAtAngle を持つので、本物のタイツのように縁が
    /// 濃くなる。2 層のタイツは同じ面に 2 つのマテリアルを持つ: 不透明な肌色の裏地と、その上に描く透ける
    /// 外側の編み地（半透明のキュー、同じ深度）。
    /// </summary>
    public static class GarmentMaterial
    {
        const int TexSize = 128;
        /// <summary>Shaders/*.lilcontainer から作る lilToon のカスタムシェーダー。</summary>
        public const string DynamicShaderName = "LegWeaver/Transparent";
        /// <summary>コンテナを GUID で: シェーダーが使うパス、次にシェーダー自体。</summary>
        static readonly string[] ShaderContainerGuids = { "97180c6d8e604e04db56d5f1ed865490", "14abf07e0648f084b817646e5a607423" };

        /// <summary>
        /// カスタムシェーダー。使えなければ null。使うパスより先に取り込まれると（新規インストールのように名前順で）、
        /// サブシェーダーをすべて失っている: パス、次にシェーダーを取り込み直す。
        /// </summary>
        static Shader CustomShader()
        {
            var shader = Shader.Find(DynamicShaderName);
            if (shader != null && shader.isSupported) return shader;
            foreach (var guid in ShaderContainerGuids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            shader = Shader.Find(DynamicShaderName);
            return shader != null && shader.isSupported ? shader : null;
        }

        public static bool UsesDynamicStretch(Material[] ms) => System.Array.Exists(ms, UsesDynamicStretch);

        public static bool UsesDynamicStretch(Material m) => m != null && m.shader != null && m.shader.name == DynamicShaderName &&
                                                             m.GetFloat("_LWDynamicStretch") > 0;

        /// <summary>
        /// 部位を脚に対してどう編むか: 覆う度合いは 1 - (1 - c)^Density（密な編み地ほど透かさない）、色は脚の色の
        /// Shade 倍。補強はつま先の様式に従う。
        /// </summary>
        static (float density, float shade) Style(GarmentRegion r, GarmentSettings g) => r switch
        {
            GarmentRegion.Panty => (1.6f, 0.97f),
            GarmentRegion.Band or GarmentRegion.SockBand => (3f, 0.85f),
            GarmentRegion.Seam => (3f, 0.75f),
            GarmentRegion.Gusset => (2f, 0.82f),
            GarmentRegion.Reinforce => g.toeReinforcement == ToeReinforcement.Sandal ? (1.3f, 0.97f) : (2.2f, 0.9f),
            _ => (1f, 1f),
        };

        /// <summary>
        /// 肌の写しに使う体のマテリアル。輪郭線は外す（裏地の縁の周りと衣装のシルエットの上に線を描いてしまう）。
        /// </summary>
        /// <param name="plainColor">指定すると、メインテクスチャを外してこの色の単色で描く（足の指先: 爪の模様を写さない）。</param>
        public static Material SkinCopy(Material body, string folder, string name, Color? plainColor = null)
        {
            if (body == null) return null;
            var mat = new Material(body) { name = name };
            if (plainColor != null)
            {
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", null);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", plainColor.Value);
            }
            var shader = mat.shader != null ? mat.shader.name : "";
            if (shader.Contains("Outline"))
            {
                var plain = Shader.Find(shader.Replace("Outline", ""));
                if (plain == null && shader.Replace("Outline", "") == "Hidden/lilToon") plain = Shader.Find("lilToon");
                if (plain != null)
                {
                    var queue = mat.renderQueue;
                    mat.shader = plain;
                    mat.renderQueue = queue;
                }
            }
            // ほかのトゥーンシェーダー（MToon など）は幅から輪郭線を描く
            foreach (var p in new[] { "_OutlineWidth", "_Outline_Width" })
                if (mat.HasProperty(p)) mat.SetFloat(p, 0f);
            // 裏地が見せるのは肌: 体のマテリアルのアルファマスク（たとえば衣装自身のストッキングの下の脚を隠すもの）
            // は、そこに穴を残す
            if (mat.HasProperty("_AlphaMaskMode")) mat.SetFloat("_AlphaMaskMode", 0f);
            PushBack(mat);
            AssetDatabase.CreateAsset(mat, $"{folder}/{name}.mat");
            return mat;
        }

        /// <summary>
        /// 裏地はその上の透ける編み地と同じ深度に描く: カメラからほんの少し遠ざける。でないと丸め誤差で編み地が
        /// ところどころ深度テストに負け、裏地が色付けされずに見える。（編み地を手前に出さずに裏地を奥へ押すので、
        /// 編み地は上に履いた靴下の後ろに収まる。）
        /// </summary>
        static void PushBack(Material mat)
        {
            if (mat.HasProperty("_OffsetFactor")) mat.SetFloat("_OffsetFactor", 1f);
            if (mat.HasProperty("_OffsetUnits")) mat.SetFloat("_OffsetUnits", 1f);
        }

        /// <summary>部位を作る編み地: 透けるかどうか、覆う度合いと色。</summary>
        static (bool sheer, float coverage, Color color) KnitOf(GarmentRegion r, GarmentSettings g)
        {
            // フェイクサイハイ: 靴下の部分より上はすべて、透けるストッキングの編み地
            var upper = g.IsFakeThighHigh && r is GarmentRegion.Upper or GarmentRegion.Panty or GarmentRegion.Band
                or GarmentRegion.Seam or GarmentRegion.Gusset;
            return upper
                ? (g.UpperIsSheer, Denier.Coverage(g.upperDenier), g.upperColor)
                : (g.LooksSheer, g.LookCoverage, g.color);
        }

        /// <param name="regions">各サブメッシュの部位（2 層のタイツが先に足す裏地の後）</param>
        public static Material[] Create(GarmentSettings g, AvatarBody body, string folder, string name,
            IReadOnlyList<GarmentRegion> regions)
        {
            var materials = new List<Material>();
#if LW_LILTOON
            var shader = Shader.Find("lilToon");
            Color? skin = null; // 編み地の伝線用。必要になったときに一度だけ推定する
            if (shader != null)
            {
                if (g.twoLayer)
                    materials.Add(CreateLining(g.liningFromSkin ? SkinColor.Estimate(body, g.liningColor) : g.liningColor,
                        folder, name + "_Lining", shader));
                foreach (var r in regions)
                {
                    var (density, shade) = Style(r, g);
                    var knit = KnitOf(r, g);
                    var matName = regions.Count > 1 ? $"{name}_{r}" : name;
                    var mat = CreateLilToon(g, knit, folder, matName, shader, density, shade);
                    if (LegPattern.Has(g, r))
                        AddPattern(mat, knit.sheer, LegPattern.Create(g, r, knit.color * shade, knit.sheer,
                            skin ??= SkinColor.Estimate(body, g.liningColor), $"{folder}/{matName}_Pattern.png"));
                    // キャットタイツ: 猫の耳のあるストッキングは、張った所でもより透けない（上へ伸びた靴下である
                    // 耳も一緒に薄れてしまう）
                    if (g.IsFakeThighHigh && g.fakeBorder == BorderKind.Cat && r == GarmentRegion.Upper)
                    {
                        mat.SetTexture("_AlphaMask", null);
                        mat.SetFloat("_AlphaMaskMode", 0);
                        mat.SetFloat("_LWDynamicStretch", 0);
                    }
                    materials.Add(mat);
                }
                return materials.ToArray();
            }
#endif
            foreach (var r in regions)
            {
                var mat = new Material(Shader.Find("Standard")) { name = $"{name}_{r}", color = g.color * Style(r, g).shade };
                AssetDatabase.CreateAsset(mat, $"{folder}/{mat.name}.mat");
                materials.Add(mat);
            }
            return materials.ToArray();
        }

#if LW_LILTOON
        static Material CreateLining(Color color, string folder, string name, Shader shader)
        {
            var mat = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(mat, $"{folder}/{name}.mat");
            lilToon.lilToonInspector.SetupMaterialWithRenderingMode(mat, lilToon.RenderingMode.Opaque,
                lilToon.TransparentMode.Normal);
            color.a = 1f;
            mat.SetColor("_Color", color);
            mat.SetFloat("_Cull", 2);
            PushBack(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static Material CreateLilToon(GarmentSettings g, (bool sheer, float coverage, Color color) knit, string folder,
            string name, Shader shader, float density, float shade)
        {
            var mat = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(mat, $"{folder}/{name}.mat");
            var color = knit.color * shade;
            color.a = 1f;
            if (!knit.sheer)
            {
                lilToon.lilToonInspector.SetupMaterialWithRenderingMode(mat, lilToon.RenderingMode.Opaque,
                    lilToon.TransparentMode.Normal);
                mat.SetColor("_Color", color);
            }
            else
            {
                lilToon.lilToonInspector.SetupMaterialWithRenderingMode(mat, lilToon.RenderingMode.Transparent,
                    lilToon.TransparentMode.Normal);
                mat.SetColor("_Color", color);
                var tex = CreateAngleAlphaTexture(1f - Mathf.Pow(1f - knit.coverage, density), $"{folder}/{name}_AngleAlpha.png");
                mat.SetFloat("_UseMain2ndTex", 1);
                mat.SetTexture("_Main2ndTex", tex);
                mat.SetColor("_Color2nd", color);
                mat.SetFloat("_Main2ndTex_UVMode", 4); // MatCap
                mat.SetFloat("_Main2ndTexAlphaMode", 1); // 置き換え
                mat.SetFloat("_Main2ndTexBlendMode", 0);
                mat.SetFloat("_Main2ndEnableLighting", 1);
                // lilToon の半透明モードでも _Cutoff（既定 0.5）未満は切り捨てるので、透けるデニールが消えてしまう
                mat.SetFloat("_Cutoff", 0.001f);
                // 伸び: alpha *= ramp(UV0.x)。メッシュは UV0.x に 1/stretch を入れる
                // （張った所で透ける不透明な衣装には付けない: 立った姿勢ではどこも不透明）
                if (g.EffectiveStretch > 0 && !g.StretchSheers)
                {
                    mat.SetTexture("_AlphaMask", CreateRampTexture($"{folder}/{name}_StretchRamp.png"));
                    mat.SetFloat("_AlphaMaskMode", 2); // 乗算
                    mat.SetFloat("_AlphaMaskScale", 1);
                    mat.SetFloat("_AlphaMaskValue", 0);
                }
                // lilToon の半透明のカスタムシェーダー: 先に深度を描き（衣装が自身と重なる所では最も手前の層だけ）、
                // 望むならポーズに応じた伸びも描く
                var customShader = CustomShader();
                if (customShader != null)
                {
                    mat.shader = customShader;
                    mat.SetFloat("_LWDynamicStretch", !g.UsesPoseStretch ? 0 : g.StretchSheers ? g.EffectiveStretch : 1);
                }
            }
            mat.SetFloat("_Cull", 2);
            GarmentFinish.Apply(mat, g.finish);
            EditorUtility.SetDirty(mat);
            return mat;
        }
#endif

        /// <summary>
        /// 柄を UV1 の lilToon の 3rd テクスチャとして付ける。透ける: アルファは「加算」（密な編み地。lilToon は
        /// そのときテクスチャの色を使い、それは柄の無い所では編み地自身の色）。不透明: 色だけ。
        /// </summary>
        static void AddPattern(Material mat, bool sheer, Texture2D tex)
        {
            mat.SetFloat("_UseMain3rdTex", 1);
            mat.SetTexture("_Main3rdTex", tex);
            mat.SetColor("_Color3rd", Color.white);
            mat.SetFloat("_Main3rdTex_UVMode", 1);
            mat.SetFloat("_Main3rdTexBlendMode", 0);
            mat.SetFloat("_Main3rdTexAlphaMode", sheer ? 3 : 0);
            mat.SetFloat("_Main3rdEnableLighting", 1);
            EditorUtility.SetDirty(mat);
        }

        // lilToon はアルファマスクをメインテクスチャのサンプラー（繰り返す）でサンプルするので、端から離す
        const float RampMargin = 0.02f;

        /// <summary>ランプが `value` を返す UV0.x。</summary>
        public static float RampU(float value) => RampMargin + (1f - 2f * RampMargin) * Mathf.Clamp01(value);

        /// <summary>赤チャンネルが RampU の逆になるランプ。アルファマスクが UV0.x に入れた値を返す。</summary>
        static Texture2D CreateRampTexture(string path)
        {
            const int w = 256;
            var tex = new Texture2D(w, 4, TextureFormat.RGBA32, false, true);
            var px = new Color32[w * 4];
            for (var x = 0; x < w; x++)
            {
                var u = (x + 0.5f) / w;
                var v = (byte)Mathf.RoundToInt(Mathf.Clamp01((u - RampMargin) / (1f - 2f * RampMargin)) * 255f);
                for (var y = 0; y < 4; y++) px[y * w + x] = new Color32(v, v, v, 255);
            }
            tex.SetPixels32(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return ImportLinear(path);
        }

        static Texture2D ImportLinear(string path)
        {
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>放射状のテクスチャ: MatCap の半径 r では、面を sinθ = r の角度で見ている。</summary>
        static Texture2D CreateAngleAlphaTexture(float coverage, string path)
        {
            var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false, true);
            var px = new Color32[TexSize * TexSize];
            for (var y = 0; y < TexSize; y++)
                for (var x = 0; x < TexSize; x++)
                {
                    var u = (x + 0.5f) / TexSize * 2f - 1f;
                    var v = (y + 0.5f) / TexSize * 2f - 1f;
                    var r2 = Mathf.Min(u * u + v * v, 0.9999f);
                    var a = Denier.OpacityAtAngle(coverage, Mathf.Sqrt(1f - r2));
                    px[y * TexSize + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            tex.SetPixels32(px);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            return ImportLinear(path);
        }
    }
}
