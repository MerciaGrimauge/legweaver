<!--
SPDX-FileCopyrightText: 2026 MerciaGrimauge
SPDX-License-Identifier: 0BSD OR MIT-0
-->

# LegWeaver の設計書

LegWeaver と同等のツールを作る人、LegWeaver を保守・改造する人のための設計書です。ツールを使うだけなら読む必要はありません
（使い方はパッケージの README を見てください）。このフォルダはパッケージに含まれず、利用者のプロジェクトには入りません。

## 中身

| ファイル | 内容 |
|---|---|
| [ALGORITHM.md](ALGORITHM.md) | 処理の仕様。何を入力に、何を、どの順で、どんな基準と数値で行い、何を出力するか |
| [basic_design.md](basic_design.md) | 基本設計書。全体の構成・機能・取り決め |
| [algorithm_editor.md](algorithm_editor.md) | 詳細設計: エディター拡張（C#）と Blender 内の処理（Python） |
| [algorithm_shader_runtime.md](algorithm_shader_runtime.md) | 詳細設計: シェーダーと実行時の部品 |
| [uml/](uml/design_uml.md) | UML の図（PlantUML の元の文 `.puml` と、書き出した `.png`） |

どの文書も、コードを写さず、働き・取り決め・数値の仕様を書いています。コードの文言は含みません。

## ライセンス

このフォルダの文書と図は、**0BSD と MIT-0 のどちらか**を選んで使えます（SPDX: `0BSD OR MIT-0`）。著作権の表示や出典の
記載は要りません。全文は [LICENSES/0BSD.txt](LICENSES/0BSD.txt) と [LICENSES/MIT-0.txt](LICENSES/MIT-0.txt) です。

ツールのコードは別のライセンスです（エディター拡張は GPL-3.0-or-later、シェーダーと実行時の部品は MIT）。詳しくはパッケージの
`LICENSE.md` を見てください。

## 図の書き出し

`.png` は、`uml/` の `.puml` を PlantUML（レイアウトは内蔵の smetana）で書き出した物です。
