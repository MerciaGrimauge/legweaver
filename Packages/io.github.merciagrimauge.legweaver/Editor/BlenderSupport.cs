// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;

namespace LegWeaver
{
    /// <summary>
    /// どの Blender（と、その中で生成を実行する Python）を使ってよいか: Blender 5.x 以降で、
    /// 両方ともまだサポート中であり、サポート終了まで <see cref="BlockDays"/> 日以内でないこと。それ以外は、
    /// Blender を自分で見つけたかパスを指定されたかにかかわらず、生成を止める。
    /// 終了日は下の表から取る（ネットワークは使わない）。表はツールと一緒に更新する。
    /// </summary>
    public static class BlenderSupport
    {
        /// <summary>サポート終了までこの日数以内（または過ぎている）: 使わない。</summary>
        public const int BlockDays = 90;
        /// <summary>この日数以内: 警告を出して使う。</summary>
        public const int WarnDays = 180;
        /// <summary>使う Blender の最も古いメジャーバージョン。</summary>
        public const int MinMajor = 5;

        /// <summary>
        /// Blender: 各リリースは次のリリースまで、LTS は 2 年間サポートされる
        /// （developer.blender.org/docs/release_notes, blender.org/download/lts, 2026-10-27 に確認）。
        /// </summary>
        static readonly Dictionary<Version, (DateTime end, string note)> BlenderEnds = new()
        {
            [new Version(5, 0)] = (new DateTime(2026, 3, 17), "5.1 の公開で終了"),
            [new Version(5, 1)] = (new DateTime(2026, 7, 14), "5.2 の公開で終了"),
            [new Version(5, 2)] = (new DateTime(2028, 7, 14), "LTS（2026-07-14 公開から 2 年の見込み）"),
        };

        /// <summary>Python: 各バージョンのセキュリティサポートの終了日（peps.python.org。リリースから 5 年）。
        /// </summary>
        static readonly Dictionary<Version, DateTime> PythonEnds = new()
        {
            [new Version(3, 9)] = new DateTime(2025, 10, 31),
            [new Version(3, 10)] = new DateTime(2026, 10, 31),
            [new Version(3, 11)] = new DateTime(2027, 10, 31),
            [new Version(3, 12)] = new DateTime(2028, 10, 31),
            [new Version(3, 13)] = new DateTime(2029, 10, 31),
            [new Version(3, 14)] = new DateTime(2030, 10, 31),
        };

        const string PythonPrefKey = "LegWeaver.PythonPath";

        /// <summary>Blender の隣に見つからない場合の、Blender 自身の python.exe の場所（バージョンを読むためだけに
        /// 使う）。</summary>
        public static string PythonPath
        {
            get => EditorPrefs.GetString(PythonPrefKey, "");
            set => EditorPrefs.SetString(PythonPrefKey, value ?? "");
        }

        public enum Verdict
        {
            Ok,
            Warn,
            Blocked,
        }

        /// <summary>今日 `blender` を使ってよいかと、その理由（ユーザー向け）。</summary>
        public static (Verdict verdict, string message) Check(string blender) => Check(blender, PythonPath, DateTime.Today);

        public static (Verdict verdict, string message) Check(string blender, string pythonPath, DateTime today)
        {
            if (string.IsNullOrEmpty(blender) || !File.Exists(blender))
                return (Verdict.Blocked, "Blender が見つかりません。");
            var dir = Path.GetDirectoryName(blender) ?? "";
            var bv = BlenderVersion(dir);
            if (bv == null)
                return (Verdict.Blocked, "Blender の版が分かりません（blender.exe と同じフォルダに「5.2」のような版のフォルダがありません）。");
            if (bv.Major < MinMajor)
                return (Verdict.Blocked, $"Blender {bv} は使えません。Blender {MinMajor}.x 以降を使ってください。");
            var (bVerdict, bMessage) = Judge($"Blender {bv}", bv, BlenderEnds.ToDictionary(kv => kv.Key, kv => kv.Value.end), today);

            var pv = PythonVersion(dir, bv, pythonPath);
            if (pv == null)
                return (Verdict.Blocked, $"Blender {bv} に同梱の Python の版が分かりません。Blender の python.exe の場所を指定してください。");
            var (pVerdict, pMessage) = Judge($"同梱の Python {pv}", pv, PythonEnds, today);

            var verdict = (Verdict)Math.Max((int)bVerdict, (int)pVerdict);
            var lines = new[] { bMessage, pMessage }.Where(m => m != null).ToList();
            if (verdict == Verdict.Ok) lines.Insert(0, $"Blender {bv}（同梱の Python {pv}）を使います。");
            return (verdict, string.Join("\n", lines));
        }

        /// <summary>サポート終了の表と照らす: 終了まで BlockDays 以内なら止め、WarnDays 以内なら警告する。</summary>
        static (Verdict, string) Judge(string what, Version v, Dictionary<Version, DateTime> ends, DateTime today)
        {
            if (!ends.TryGetValue(v, out var end))
            {
                // 表のどれよりも新しい: ツールより後に出たリリース（その終了日はまだわからない）
                if (ends.Keys.All(k => k < v))
                    return (Verdict.Warn, $"{what} のサポート期間はこのツールの表にありません（新しい版）。ツールを更新すると確かめられます。");
                return (Verdict.Blocked, $"{what} はサポートが終わっている版です。");
            }
            var left = (end - today).TotalDays;
            if (left < 0) return (Verdict.Blocked, $"{what} はサポートが終わっています（{end:yyyy-MM-dd}）。新しい版を使ってください。");
            if (left <= BlockDays)
                return (Verdict.Blocked, $"{what} はまもなくサポートが終わります（{end:yyyy-MM-dd}、残り {left:0} 日）。新しい版を使ってください。");
            if (left <= WarnDays) return (Verdict.Warn, $"{what} のサポートは {end:yyyy-MM-dd} に終わります（残り {left:0} 日）。");
            return (Verdict.Ok, null);
        }

        /// <summary>blender.exe の隣のバージョンのフォルダ（"5.2"）から取る。複数あれば最新のもの。</summary>
        public static Version BlenderVersion(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            return Directory.GetDirectories(dir)
                .Select(d => Regex.Match(Path.GetFileName(d), @"^(\d+)\.(\d+)$"))
                .Where(m => m.Success)
                .Select(m => new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)))
                .OrderByDescending(v => v)
                .FirstOrDefault();
        }

        /// <summary>
        /// Blender の中の Python: バージョンのフォルダの python\bin、または指定された python.exe の隣にある DLL の
        /// 名前（python313.dll = 3.13）から取る。
        /// </summary>
        public static Version PythonVersion(string blenderDir, Version blenderVersion, string pythonPath)
        {
            var dirs = new List<string> { Path.Combine(blenderDir, $"{blenderVersion.Major}.{blenderVersion.Minor}", "python", "bin") };
            if (!string.IsNullOrEmpty(pythonPath)) dirs.Insert(0, File.Exists(pythonPath) ? Path.GetDirectoryName(pythonPath) : pythonPath);
            foreach (var d in dirs.Where(Directory.Exists))
                foreach (var f in Directory.GetFiles(d, "python3*.dll"))
                {
                    var m = Regex.Match(Path.GetFileName(f), @"^python3(\d+)\.dll$", RegexOptions.IgnoreCase);
                    if (m.Success) return new Version(3, int.Parse(m.Groups[1].Value));
                }
            return null;
        }
    }
}
