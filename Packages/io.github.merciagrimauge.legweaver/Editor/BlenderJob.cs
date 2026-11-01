// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>衣装 1 つ分として Unity が Blender に渡すもの（legweaver.py が読む）。</summary>
    public static class BlenderJob
    {
        /// <summary>JSON の文字列リテラル: 引用符・バックスラッシュ・制御文字をエスケープする。</summary>
        static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
                sb.Append(c switch
                {
                    '"' => "\\\"",
                    '\\' => "\\\\",
                    < ' ' => $"\\u{(int)c:x4}",
                    _ => c.ToString(),
                });
            return sb.Append('"').ToString();
        }

        /// <summary>
        /// Blender 用のジョブを `dir` に書く: input.bin（体）、under.bin（その下に着ているもの）と、
        /// ジョブのファイル `jobPath`（個数・目印の位置・ファイルのパス・衣装のパラメーター）。
        /// 形式は legweaver.py の先頭の説明にあるもの。
        /// </summary>
        public static void Write(AvatarBody body, string dir, GarmentSettings g, string jobPath)
        {
            var input = Path.Combine(dir, "input.bin");
            using (var bw = new BinaryWriter(File.Create(input)))
            {
                foreach (var v in body.Vertices) { bw.Write(v.x); bw.Write(v.y); bw.Write(v.z); }
                foreach (var n in body.Normals) { bw.Write(n.x); bw.Write(n.y); bw.Write(n.z); }
                foreach (var u in body.UV) { bw.Write(u.x); bw.Write(u.y); }
                foreach (var l in body.LegWeight) bw.Write(l);
                foreach (var x in body.JointMix) bw.Write(x);
                foreach (var x in body.UnderHeight ?? new float[body.Vertices.Length]) bw.Write(x);
                foreach (var x in body.OverHeight ?? new float[body.Vertices.Length]) bw.Write(x);
                foreach (var i in body.Triangles) bw.Write(i);
            }
            // 下に着ているもの: 頂点数・三角形数・位置・三角形
            using (var bw = new BinaryWriter(File.Create(Path.Combine(dir, "under.bin"))))
            {
                bw.Write(body.UnderVerts.Length);
                bw.Write(body.UnderTris.Length / 3);
                foreach (var v in body.UnderVerts) { bw.Write(v.x); bw.Write(v.y); bw.Write(v.z); }
                foreach (var i in body.UnderTris) bw.Write(i);
            }

            var ci = CultureInfo.InvariantCulture;
            string F(float f) => f.ToString("R", ci);
            string B(bool b) => b ? "true" : "false";
            string Vec(Vector3? v) => v == null ? "null" : $"[{F(v.Value.x)},{F(v.Value.y)},{F(v.Value.z)}]";
            string P(string p) => Json(p.Replace("\\", "/"));
            var garment = new List<(string key, string value)>
            {
                ("type", Json(g.TypeId)), ("waistHeight", F(g.waistHeight)), ("legCut", F(g.legCut)),
                ("thighTop", F(g.SockTopAt)), ("gloveTop", F(g.GloveTopAt)),
                ("fingerless", B(g.gloveFingers == GloveFingers.Fingerless)),
                ("drawing", B(g.gloveFingers == GloveFingers.Drawing)), ("armTop", F(g.armTop)),
                ("armToHand", B(g.armToHand)), ("clearanceMm", F(g.ClearanceForJob)), ("jointExtraMm", F(g.jointExtraMm)),
                ("thicknessMm", F(g.Thickness)), ("toeBridgeMm", F(g.Toes == ToeStyle.FiveToe || g.type is GarmentType.ToeCap or GarmentType.Garter or GarmentType.LegWrap ? 0f : g.toeBridgeMm)),
                ("density", F(g.density)), ("voxelMm", F(g.voxelMm)),
                ("stretchStrength", F((g.LooksSheer || g.IsFakeThighHigh) && !g.StretchSheers ? g.EffectiveStretch : 0f)),
                ("poseStretch", B(g.UsesPoseStretch)),
                ("waistband", B(g.waistband)), ("pantyPart", B(g.pantyPart)), ("runGuard", B(g.runGuard)),
                ("toeReinforce", ((int)g.toeReinforcement).ToString(ci)), ("heelReinforce", B(g.heelReinforcement)),
                ("heelShape", Json(g.heelShape == HeelShape.Y ? "y" : "cup")),
                ("toeShape", Json(g.toeShape == ToeShape.Rounded ? "rounded" : "straight")),
                ("centerSeam", ((int)g.centerSeam).ToString(ci)), ("gusset", B(g.gusset)),
                ("wrinkles", F(g.wrinkles)),
                ("gussetSize", F(g.gussetSize)), ("gussetAspect", F(g.gussetAspect)),
                ("fakeThighHigh", B(g.IsFakeThighHigh)), ("fakeTop", F(g.fakeTop)), ("fakeBand", B(g.fakeBand)),
                ("skinLining", B(g.UsesSkinLining)), ("opaque", B(g.IsOpaque && !g.IsFakeThighHigh)),
                ("wornInside", B(g.KeepsWornInside)), ("twoLayer", B(g.twoLayer)),
                ("quality", Json(g.quality == Quality.High ? "high" : "normal")),
                ("maxTriangles", GarmentSettings.HighQualityMaxTriangles.ToString(ci)),
            };
            // 足袋型だけ書く（ほかの種類の job.json は変えない）
            if (g.Toes == ToeStyle.Tabi) garment.Add(("tabi", "true"));
            // ガーター・レッグラップだけ書く（ほかの種類の job.json は変えない）
            if (g.type == GarmentType.Garter)
            {
                garment.Add(("garterAt", F(g.garterAt)));
                garment.Add(("garterWidthMm", F(g.garterWidthMm)));
                garment.Add(("ribbon", B(g.garterRibbon)));
            }
            if (g.type == GarmentType.LegWrap)
            {
                garment.Add(("wrapFrom", F(g.wrapFrom)));
                garment.Add(("wrapTo", F(g.wrapTo)));
                garment.Add(("wrapTurns", F(g.wrapTurns)));
                garment.Add(("wrapWidthMm", F(g.wrapWidthMm)));
                garment.Add(("wrapCross", B(g.wrapCross)));
            }
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($" \"vertexCount\": {body.Vertices.Length},\n");
            sb.Append($" \"triangleCount\": {body.Triangles.Length / 3},\n");
            sb.Append($" \"metersPerUnit\": {F(body.MetersPerUnit)},\n");
            sb.Append($" \"inputBin\": {P(input)},\n");
            sb.Append($" \"underBin\": {P(Path.Combine(dir, "under.bin"))},\n");
            sb.Append($" \"outputBin\": {P(Path.Combine(dir, "output.bin"))},\n");
            sb.Append($" \"outputJson\": {P(Path.Combine(dir, "output.json"))},\n");
            sb.Append(" \"landmarks\": {");
            sb.Append(string.Join(",", body.Landmarks.Select(kv => $"{Json(kv.Key)}: {Vec(kv.Value)}")));
            sb.Append("},\n");
            // 足の指ごとに骨を持つアバターだけ: 指の番号を持つ頂点（頂点, 番号）の並び（無ければ書かない）
            var digits = body.ToeDigit?.Select((d, v) => (d, v)).Where(x => x.d > 0).ToList();
            if (digits != null && digits.Count > 0)
                sb.Append($" \"toeDigits\": [{string.Join(",", digits.Select(x => $"{x.v},{x.d}"))}],\n");
            sb.Append(" \"garment\": {\n");
            sb.Append(string.Join(",\n", garment.Select(f => $"  {Json(f.key)}: {f.value}")));
            sb.Append("\n }\n}\n");
            File.WriteAllText(jobPath, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
