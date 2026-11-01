// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using UnityEditor;

namespace LegWeaver
{
    /// <summary>Blender を探し、生成のスクリプトをバックグラウンドで実行する。</summary>
    public class BlenderRunner
    {
        const string PrefKey = "LegWeaver.BlenderPath";

        public static string BlenderPath
        {
            get
            {
                var p = EditorPrefs.GetString(PrefKey, "");
                // 指定された場所も、自動で探した場所と同じく Blender の配置かを確かめる（違えば自動で探した場所）。
                // （エディタのセッションにつき 1 回だけ探す: ウィンドウは再描画のたびに問い合わせる。
                // 見つからなかったときは再び問い合わせる）
                if (IsBlender(p)) return p;
                if (string.IsNullOrEmpty(_detected)) _detected = Detect();
                return _detected;
            }
            set => EditorPrefs.SetString(PrefKey, value ?? "");
        }

        static string _detected;

        /// <summary>
        /// `exe` が Blender と同じ配置か: Blender のスクリプトを収めたバージョンのフォルダ（"5.2"）の隣にあること
        /// （アンインストールの登録情報は何でも書き込めるし、ファイルのバージョン情報はエディタから読めない）。
        /// </summary>
        public static bool IsBlender(string exe)
        {
            if (!File.Exists(exe)) return false;
            var dir = Path.GetDirectoryName(exe) ?? "";
            var v = BlenderSupport.BlenderVersion(dir);
            return v != null && Directory.Exists(Path.Combine(dir, $"{v.Major}.{v.Minor}", "scripts"));
        }

        /// <summary>
        /// インストール済みで最新の Blender: Windows がインストール済みのプログラムについて記録しているもの
        /// （アンインストールの登録情報。winget もこれを読み、何も尋ねない）から探し、
        /// なければ Program Files の下から探す。ポータブル版はパスを指定したときだけ使う。いずれにしても、
        /// 使ってよいかは BlenderSupport が決める。
        /// </summary>
        public static string Detect()
        {
            var installed = FromInstalledPrograms();
            if (installed != null) return installed;
            return FromProgramFiles();
        }

        /// <summary>インストール済みのプログラム（マシンとユーザー）のうち、最新の "Blender" の登録の blender.exe。
        /// </summary>
        static string FromInstalledPrograms()
        {
            var found = new List<(Version version, string exe)>();
            try
            {
                foreach (var (hive, view) in new[]
                         {
                             (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32),
                             (RegistryHive.CurrentUser, RegistryView.Registry64),
                         })
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall == null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(name);
                        if (!(app?.GetValue("DisplayName") is string display) || !display.StartsWith("Blender")) continue;
                        if (!(app.GetValue("InstallLocation") is string location) || string.IsNullOrEmpty(location)) continue;
                        var exe = Path.Combine(location, "blender.exe");
                        if (!IsBlender(exe)) continue;
                        Version.TryParse(app.GetValue("DisplayVersion") as string ?? "", out var v);
                        found.Add((v ?? new Version(0, 0), exe));
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
            }
            return found.OrderByDescending(f => f.version).Select(f => f.exe).FirstOrDefault();
        }

        static string FromProgramFiles()
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                var dir = Path.Combine(root, "Blender Foundation");
                if (!Directory.Exists(dir)) continue;
                var exe = Directory.GetDirectories(dir, "Blender*")
                    .Select(d => Path.Combine(d, "blender.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(p => ParseVersion(Path.GetFileName(Path.GetDirectoryName(p))))
                    .FirstOrDefault();
                if (exe != null) return exe;
            }
            return "";
        }

        static Version ParseVersion(string dirName)
        {
            var digits = new string(dirName.SkipWhile(c => !char.IsDigit(c)).ToArray());
            return Version.TryParse(digits.Contains('.') ? digits : digits + ".0", out var v) ? v : new Version(0, 0);
        }

        Process _process;
        readonly ConcurrentQueue<string> _lines = new();

        const string TimeoutKey = "LegWeaver.BlenderTimeout";
        /// <summary>通常品質: 普通のアバターはこれを大きく下回る時間で終わる。</summary>
        public const double DefaultTimeoutSeconds = 60;
        /// <summary>高品質: 細分化そのものは速い。重いアバターのための余裕。</summary>
        public const double DefaultHighTimeoutSeconds = 90;
        /// <summary>許す範囲: これより短いと小さなアバターでも終わらず、長くしても固まった Blender を隠すだけ。
        /// </summary>
        public const double MinTimeoutSeconds = 10, MaxTimeoutSeconds = 600;

        /// <summary>
        /// アバターごとに Blender に与える時間（ユーザーが品質ごとに設定する）。これを過ぎた実行は止め、
        /// 時間切れとして報告する。
        /// </summary>
        public static double GetTimeout(Quality q) =>
            Clamp(EditorPrefs.GetFloat(Key(q), (float)(q == Quality.High ? DefaultHighTimeoutSeconds : DefaultTimeoutSeconds)));

        public static void SetTimeout(Quality q, double seconds) => EditorPrefs.SetFloat(Key(q), (float)Clamp(seconds));

        static string Key(Quality q) => q == Quality.High ? TimeoutKey + ".High" : TimeoutKey;

        static double Clamp(double s) => double.IsNaN(s) ? DefaultTimeoutSeconds : Math.Min(MaxTimeoutSeconds, Math.Max(MinTimeoutSeconds, s));

        /// <summary>この実行の上限（開始時に取る）。</summary>
        public double Timeout { get; private set; } = DefaultTimeoutSeconds;

        DateTime _started;

        public bool TimedOut { get; private set; }

        public double Elapsed => _started == default ? 0 : (DateTime.Now - _started).TotalSeconds;

        public bool IsRunning
        {
            get
            {
                if (_process == null) return false;
                if (_process.HasExited)
                {
                    // 出力をすべて読んでから（長くても数秒: ここはエディタのメインスレッド）、手放す。時間を指定した
                    // WaitForExit は非同期の出力の読み終わりを待たないので、終わっていれば引数なしでもう一度待つ
                    // （最後の行: 品質の報告や、失敗の内容が欠けないように）
                    if (_process.WaitForExit(5000)) _process.WaitForExit();
                    _exitCode = _process.ExitCode;
                    _process.Dispose();
                    _process = null;
                    return false;
                }
                if (Elapsed <= Timeout) return true;
                TimedOut = true;
                Kill();
                return false;
            }
        }

        public int ExitCode => _process != null && _process.HasExited ? _process.ExitCode : _exitCode;
        int _exitCode = -1;

        /// <summary>Log に残す Blender の出力の最大量（超えた分は先頭から捨てる）。</summary>
        const int MaxLog = 200_000;
        public string LastLine { get; private set; } = "";
        public System.Text.StringBuilder Log { get; } = new();

        public void Start(string blender, string script, string job, double timeout)
        {
            var psi = new ProcessStartInfo
            {
                FileName = blender,
                Arguments = $"-b --factory-startup --python \"{script}\" -- \"{job}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => { if (e.Data != null) _lines.Enqueue(e.Data); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data != null) _lines.Enqueue(e.Data); };
            _started = DateTime.Now;
            Timeout = timeout;
            TimedOut = false;
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        /// <summary>受け取った出力を Log へ移す。メインスレッドから呼ぶ。</summary>
        public void Pump()
        {
            while (_lines.TryDequeue(out var line))
            {
                Log.AppendLine(line);
                if (line.StartsWith("[LW] ")) LastLine = line.Substring(5);
            }
            if (Log.Length > MaxLog) Log.Remove(0, Log.Length - MaxLog);
        }

        public void Kill()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
