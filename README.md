# LegWeaver

VRChat アバターの体に合わせて、タイツ・ソックス・手袋・アームカバーのメッシュを Blender で生成する Unity エディタ拡張です。

パッケージ ID は `io.github.merciagrimauge.legweaver` です。使い方・必要なもの・仕組みは[パッケージの README](Packages/io.github.merciagrimauge.legweaver/README.md) を参照してください。処理の詳しい説明（[ALGORITHM.md](Design/ALGORITHM.md)）と、同等のツールを作る人・保守する人のための設計書・UML の図は、パッケージとは別に [Design/](Design/README.md) にあります。

**ライセンスは部分ごとに違います。全部が GPL ではありません。**

| 部分 | ライセンス | 全文 |
|---|---|---|
| エディタ拡張（`Editor/`。Blender の中で動く処理を含む） | GPL-3.0-or-later | [LICENSE-GPL](LICENSE-GPL) |
| アバターに入るシェーダー（`Shaders/`）と実行時の部品（`Runtime/`） | MIT | [LICENSE-MIT](LICENSE-MIT) |
| 設計書（`Design/`） | 0BSD と MIT-0 のどちらかを選べる | [LICENSE-0BSD](LICENSE-0BSD)、[LICENSE-MIT-0](LICENSE-MIT-0) |

生成した衣類と、それを含むアバターは GPL の対象になりません。詳しくは下の「ライセンス」を見てください。

## 導入

ALCOM に VPM リポジトリ `https://merciagrimauge.github.io/vpm-repository/index.json` を追加し、プロジェクトに LegWeaver を追加します。依存する VRChat SDK（Avatars）・NDMF・lilToon・Modular Avatar は一緒に入ります。Avatar Optimizer は任意で、入っていれば不透明な衣類の下の肌の削除に使います。

別に Blender 5.x 以降（インストール版、またはポータブル版の場所を指定）が必要です。サポートの終わった版は使えないので、2026 年 11 月の時点では 5.2 以降です（詳しくはパッケージの README）。

Unity 2022.3 向けです。Unity 6 では確認していません。対応 OS は Windows だけです（macOS・Linux では動作を確認していません）。

版番号の決まり（何を互換性として守るか）は、パッケージの README の「バージョン規則」を参照してください。生成される形が変わる変更（不具合の修正を含む）はマイナーの版で出すので、形を変えたくない場合はマイナーの範囲を固定してください。

## ライセンス

エディタ拡張（`Editor/`）は GPL-3.0-or-later、アバターに入るシェーダー（`Shaders/`）と実行時の部品（`Runtime/`）は MIT、`Design/` の設計書は 0BSD と MIT-0 のどちらかを選べます。直下の `LICENSE-GPL`・`LICENSE-MIT`・`LICENSE-0BSD`・`LICENSE-MIT-0` は、それぞれのライセンスの全文です（どれか 1 つが全体のライセンスという意味ではありません）。生成した衣類と、それを含むアバターは GPL の対象になりません。詳しくは [LICENSE.md](Packages/io.github.merciagrimauge.legweaver/LICENSE.md) を参照してください。本ソフトウェアは現状のまま提供します。VRChat SDK・NDMF・lilToon・Modular Avatar・Avatar Optimizer・Blender は同梱していません。

## Q.A
Q.XXに対応して欲しい
A.OSSなので、そっちで変更して

Q.XXが欲しい
A.**私は要らない**

Q.サポートをして欲しい
A.それをする金は貰って無い

Q.コードを間違って取り込んでしまった。該当の部分はGPLだった。どうしたら良い？
A.This code is licensed under the GPL. Nothing more, nothing less. There is no remedy. You'll have to rewind time and undo past mistakes.

Q.派生物を非GPLにしたい
A.GPLコード部分に対する質問と解釈する。GPLはこれを認めない

Q.派生物をAGPLにしたい
A.GPLコード部分に対する質問と解釈する。GPLはこれを認めない

Q.過失は保護される？
A.GPLコード部分に対する質問と解釈する。GPLはこれを認めない

Q.AIが勝手にやった！
A.GPLコード部分に対する質問と解釈する。GPLはこれを認めない

Q.Blender由来でない箇所が明らかにある。そこはGPLにする必要はない
A.だからなに？

Q.Blender由来でない箇所のGPLはリポジトリ開発者は解除出来るの？
A.そこまで見ているなら目測は正しいし、通常は交渉の余地がある。私は将来に渡ってその部分に限っては勝手に自分の分だけ解除できる権利を持つ
一方で既にGPLとして入手したユーザーの自由は以降も保証される。ただ、聞きたいことは多分違うだろう。交渉の余地はない。

Q.設計書を間違えて取り込んでしまった。どうしたら良い？
A.設計書は問題になるライセンスは入ってないと思うが…

考えうる殆どの問い合わせは上記しか返ってこないので、問い合わせはするだけ時間の無駄だと思います。
実際問題、Opusが親切心でタイツの足先だけ出すパターン（一般的にはオープントゥタイツと呼ばれる）も実装してくれてましたが、私は要らんので消させました。
100万ドルをポンとくれればフルサポートします。そんな事するなら1%の単価でSE、PGに依頼したほうが良いっすよ
