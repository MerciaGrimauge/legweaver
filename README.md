# LegWeaver

VRChat アバターの体に合わせて、タイツ・ソックス・手袋・アームカバーのメッシュを Blender で生成する Unity エディタ拡張です。

パッケージ ID は `io.github.merciagrimauge.legweaver` です。使い方・必要なもの・仕組みは[パッケージの README](Packages/io.github.merciagrimauge.legweaver/README.md) を、処理の詳しい説明は [ALGORITHM.md](Packages/io.github.merciagrimauge.legweaver/ALGORITHM.md) を参照してください。

## 導入

ALCOM に VPM リポジトリ `https://merciagrimauge.github.io/vpm-repository/index.json` を追加し、プロジェクトに LegWeaver を追加します。依存する VRChat SDK（Avatars）・NDMF・lilToon・Modular Avatar は一緒に入ります。Avatar Optimizer は任意で、入っていれば不透明な衣類の下の肌の削除に使います。

別に Blender 5.x 以降（インストール版、またはポータブル版の場所を指定）が必要です。サポートの終わった版は使えないので、2026 年 11 月の時点では 5.2 以降です（詳しくはパッケージの README）。

Unity 2022.3 向けです。Unity 6 では確認していません。

版番号の決まり（何を互換性として守るか）は、パッケージの README の「バージョン規則」を参照してください。生成される形が変わる変更（不具合の修正を含む）はマイナーの版で出すので、形を変えたくない場合はマイナーの範囲を固定してください。

## ライセンス

エディタ拡張（`Editor/`）は GPL-3.0-or-later、アバターに入るシェーダー（`Shaders/`）と実行時の部品（`Runtime/`）は MIT、外部設計書の `ALGORITHM.md` は 0BSD と MIT-0 のどちらかを選べます。生成した衣類と、それを含むアバターは GPL の対象になりません。詳しくは [LICENSE.md](Packages/io.github.merciagrimauge.legweaver/LICENSE.md) を参照してください。本ソフトウェアは現状のまま提供します。VRChat SDK・NDMF・lilToon・Modular Avatar・Avatar Optimizer・Blender は同梱していません。
