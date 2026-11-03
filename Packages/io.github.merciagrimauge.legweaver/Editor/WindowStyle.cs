// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UnityEditor;
using UnityEngine;

namespace LegWeaver
{
    /// <summary>
    /// ウィンドウの見た目の部品: 見出しの帯、区画のカード、種類のボタンの段、色の見本、生成のボタン。
    /// エディターの明るい・暗い見た目の両方で読める色にする。
    /// </summary>
    static class WindowStyle
    {
        static bool Dark => EditorGUIUtility.isProSkin;
        /// <summary>強調色（選んでいる種類・生成のボタン）。</summary>
        public static Color Accent => Dark ? new Color(0.55f, 0.47f, 0.95f) : new Color(0.42f, 0.33f, 0.85f);
        static Color HeaderBack => Dark ? new Color(0.13f, 0.13f, 0.17f) : new Color(0.86f, 0.85f, 0.92f);
        static Color CardBack => Dark ? new Color(1f, 1f, 1f, 0.035f) : new Color(1f, 1f, 1f, 0.45f);
        static Color CardLine => Dark ? new Color(1f, 1f, 1f, 0.08f) : new Color(0f, 0f, 0f, 0.12f);
        static Color Faint => Dark ? new Color(0.62f, 0.62f, 0.68f) : new Color(0.38f, 0.38f, 0.44f);

        static GUIStyle _title, _sub, _cardTitle, _card, _primary, _typeButton, _rowLabel, _caption;
        static GUIStyle Title => _title ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 18 };
        static GUIStyle Sub => _sub ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Faint } };
        static GUIStyle CardTitle => _cardTitle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 12 };
        static GUIStyle CardStyle => _card ??= new GUIStyle { padding = new RectOffset(10, 10, 6, 8), margin = new RectOffset(4, 4, 3, 3) };
        static GUIStyle Primary => _primary ??= new GUIStyle(GUI.skin.button)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            fixedHeight = 36,
            normal = { textColor = Color.white },
            hover = { textColor = Color.white },
        };
        static GUIStyle TypeButton => _typeButton ??= new GUIStyle(GUI.skin.button) { fixedHeight = 24 };
        static GUIStyle RowLabel => _rowLabel ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            fixedHeight = 24,
            normal = { textColor = Faint },
        };
        /// <summary>カードの中の補足の文（数値の要約など）。</summary>
        public static GUIStyle Caption => _caption ??= new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, normal = { textColor = Faint } };

        /// <summary>見出しの帯: 名前、説明、版（分かれば）。</summary>
        public static void Header(string version)
        {
            var r = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, HeaderBack);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 2, r.width, 2), Accent);
            GUI.Label(new Rect(r.x + 12, r.y + 6, r.width - 24, 24), "LegWeaver", Title);
            GUI.Label(new Rect(r.x + 12, r.y + 28, r.width - 24, 16), "体に沿ったタイツ・ソックス・手袋を Blender で作ります", Sub);
            if (!string.IsNullOrEmpty(version))
                GUI.Label(new Rect(r.x, r.y + 10, r.width - 12, 16), "v" + version, new GUIStyle(Sub) { alignment = TextAnchor.UpperRight });
            GUILayout.Space(4);
        }

        /// <summary>区画のカード（using で閉じる）。見出しの左に Unity の組み込みのアイコンを付けられる。</summary>
        public static IDisposable Card(string title, string icon = null)
        {
            var scope = new EditorGUILayout.VerticalScope(CardStyle);
            EditorGUI.DrawRect(scope.rect, CardBack);
            DrawFrame(scope.rect);
            CardHeading(title, icon);
            return scope;
        }

        /// <summary>たためるカード。開いていれば中身を描くための scope を、閉じていれば null を返す（見出しだけ描く）。</summary>
        public static IDisposable FoldCard(ref bool open, string title, string icon = null)
        {
            var scope = new EditorGUILayout.VerticalScope(CardStyle);
            EditorGUI.DrawRect(scope.rect, CardBack);
            DrawFrame(scope.rect);
            using (new EditorGUILayout.HorizontalScope())
            {
                var arrow = open ? "▼" : "▶";
                if (GUILayout.Button($"{arrow}  {title}", CardTitle)) open = !open;
                if (icon != null) GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(18), GUILayout.Height(18));
            }
            if (open)
            {
                GUILayout.Space(2);
                return scope;
            }
            scope.Dispose();
            return null;
        }

        static void CardHeading(string title, string icon)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (icon != null) GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(18), GUILayout.Height(18));
                GUILayout.Label(title, CardTitle);
            }
            GUILayout.Space(2);
        }

        static void DrawFrame(Rect r)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), CardLine);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), CardLine);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1, r.height), CardLine);
            EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), CardLine);
        }

        /// <summary>
        /// 種類を選ぶボタンの段: 段ごとに（名前, その段の種類と短い名前と説明）。選んでいる物を強調色で示す。選んだ種類を返す。
        /// </summary>
        public static GarmentType TypeRows(GarmentType current, (string row, (GarmentType type, string label, string tip)[] items)[] rows)
        {
            foreach (var (row, items) in rows)
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(row, RowLabel, GUILayout.Width(34));
                    foreach (var (type, label, tip) in items)
                    {
                        var on = type == current;
                        var back = GUI.backgroundColor;
                        if (on) GUI.backgroundColor = Accent;
                        var style = on ? new GUIStyle(TypeButton) { fontStyle = FontStyle.Bold, normal = { textColor = Color.white } } : TypeButton;
                        if (GUILayout.Button(new GUIContent(label, tip), style, GUILayout.MinWidth(40))) current = type;
                        GUI.backgroundColor = back;
                    }
                }
            return current;
        }

        /// <summary>色の見本付きのボタン。押されたかを返す。</summary>
        public static bool Swatch(string label, Color color)
        {
            var r = GUILayoutUtility.GetRect(new GUIContent("  " + label), EditorStyles.miniButton, GUILayout.MinWidth(46));
            var pressed = GUI.Button(r, new GUIContent("     " + label), EditorStyles.miniButton);
            var chip = new Rect(r.x + 5, r.y + (r.height - 10) * 0.5f, 10, 10);
            EditorGUI.DrawRect(new Rect(chip.x - 1, chip.y - 1, chip.width + 2, chip.height + 2), CardLine);
            EditorGUI.DrawRect(chip, new Color(color.r, color.g, color.b, 1f));
            return pressed;
        }

        /// <summary>大きな強調色のボタン。押されたかを返す。</summary>
        public static bool PrimaryButton(string text)
        {
            var back = GUI.backgroundColor;
            GUI.backgroundColor = Accent;
            var pressed = GUILayout.Button(text, Primary);
            GUI.backgroundColor = back;
            return pressed;
        }

        /// <summary>進み具合の棒（0..1）と、その上の文。</summary>
        public static void Progress(float t, string label)
        {
            var r = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(r, Mathf.Clamp01(t), label);
        }
    }
}
