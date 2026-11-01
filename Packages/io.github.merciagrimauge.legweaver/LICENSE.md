# License / ライセンス

Copyright (c) 2026 MerciaGrimauge (https://github.com/MerciaGrimauge)

LegWeaver is made of parts under **different licenses**. Each file says which one in its first lines
(`SPDX-License-Identifier`); files that cannot carry a comment are listed in `REUSE.toml`. The English text below is
the authoritative one; the Japanese text is a translation.

| Path (inside this folder) | License | Full text |
|---|---|---|
| `Shaders/` (everything in it) | **MIT** | `Shaders/LICENSE.txt`, `LICENSES/MIT.txt` |
| `Runtime/` (everything in it) | **MIT** | `Runtime/LICENSE.txt`, `LICENSES/MIT.txt` |
| `ALGORITHM.md` (the external design document) | **0BSD OR MIT-0** (your choice) | `LICENSES/0BSD.txt`, `LICENSES/MIT-0.txt` |
| everything else (`Editor/` including `Editor/Blender/`, `README.md`) | **GPL-3.0-or-later** | `LICENSES/GPL-3.0-or-later.txt` |

## What this means

1. **The MIT, 0BSD and MIT-0 parts are not GPL.** `Shaders/` and `Runtime/` are licensed under the MIT License
   only, and `ALGORITHM.md` under the 0BSD or MIT-0 License at your choice; none of them is licensed under the GPL. Being
   distributed in the same package or repository as the GPL parts does not change that: they are separate works
   placed side by side (an "aggregate", GPLv3 section 5), not a work based on the GPL parts. Anyone may use them under
   their own license alone, including in closed-source or commercial works. `ALGORITHM.md` may be used without any
   copyright notice or attribution.
2. **What the tool makes is yours, not GPL.** The meshes, textures, materials, prefabs and component settings that
   LegWeaver generates from your avatar are its output. They are not covered by this tool's licenses (in
   particular not by the GPL); they remain subject only to the terms of the avatar and assets you made them from.
3. **Avatars are not made GPL.** Uploading, distributing or selling an avatar that contains generated garments, uses
   the shaders in `Shaders/`, or carries the `Runtime/` component does not require that avatar, or anything else in
   it, to be licensed under the GPL. The only parts of this tool that end up in an avatar are the MIT parts.
4. **The GPL applies to the tool itself.** If you distribute the editor tool (`Editor/`) or a modified version of it,
   do so under GPL-3.0-or-later with its source code.
5. **No additional permissions or exceptions are granted**, and none are needed for the points above. Statements 1-3
   describe how the licenses in the table apply; they do not add to or take away from them.
6. **Third-party code.** The files in `Shaders/` that follow lilToon's custom shader template keep lilToon's notice
   (MIT, Copyright (c) 2020-present lilxyzw), included in `Shaders/LICENSE.txt`. lilToon, Modular Avatar, Avatar
   Optimizer, NDMF, the VRChat SDK, Unity and Blender are not included in this tool; each has its own license.
   `Editor/BodyFinder.cs` follows a bone-matching rule of Modular Avatar (MIT, Copyright (c) 2022 bd_); its notice is
   in `THIRD_PARTY_NOTICES.md`.

---

## 日本語訳

LegWeaver は、**異なるライセンス**の部分からできています。各ファイルの先頭の行
（`SPDX-License-Identifier`）にどちらかを書いています。コメントを書けないファイルは `REUSE.toml` に載せています。
上の英語の文が正文で、以下はその訳です。

| 場所（このフォルダの中） | ライセンス | 全文 |
|---|---|---|
| `Shaders/`（中のすべて） | **MIT** | `Shaders/LICENSE.txt`、`LICENSES/MIT.txt` |
| `Runtime/`（中のすべて） | **MIT** | `Runtime/LICENSE.txt`、`LICENSES/MIT.txt` |
| `ALGORITHM.md`（外部設計書） | **0BSD または MIT-0**（どちらかを選べる） | `LICENSES/0BSD.txt`、`LICENSES/MIT-0.txt` |
| それ以外のすべて（`Editor/`。`Editor/Blender/` を含む。`README.md`） | **GPL-3.0-or-later** | `LICENSES/GPL-3.0-or-later.txt` |

### 意味するところ

1. **MIT・0BSD・MIT-0 の部分は GPL ではありません。** `Shaders/`・`Runtime/` は MIT だけ、`ALGORITHM.md` は
   0BSD と MIT-0 のどちらかを選べるライセンスで、どれも GPL ではありません。GPL の部分と
   同じパッケージやリポジトリで配布されていても変わりません。並べて置かれた別々の著作物（GPLv3 第 5 条の「集積物」）で
   あって、GPL の部分に基づく著作物ではありません。誰でもそれぞれのライセンスだけに従って使えます（非公開・商用の作品を含む）。`ALGORITHM.md` は
   著作権の表示や出典の記載なしで使えます。
2. **このツールが作った物はあなたの物で、GPL ではありません。** LegWeaver がアバターから生成するメッシュ・
   テクスチャ・マテリアル・プレハブ・コンポーネントの設定は、このツールの出力です。このツールのライセンス（特に GPL）の
   対象ではなく、元にしたアバターや素材の規約だけに従います。
3. **アバターは GPL になりません。** 生成した衣類を含む、`Shaders/` のシェーダーを使う、または `Runtime/` の
   コンポーネントを持つアバターをアップロード・配布・販売しても、そのアバターやその中の物を GPL にする必要はありません。
   アバターに入るこのツールの部分は、MIT の部分だけです。
4. **GPL が適用されるのはツール自体です。** エディタのツール（`Editor/`）やその改変版を配布するときは、
   GPL-3.0-or-later に従い、ソースコードと一緒に配布してください。
5. **追加の許可や例外は与えていません。** 上の 1〜3 に例外は要りません。1〜3 は表のライセンスがどう適用されるかの
   説明で、ライセンスに何かを足したり引いたりするものではありません。
6. **第三者のコード。** `Shaders/` のうち lilToon のカスタムシェーダーのテンプレートの形に沿ったファイルは、
   lilToon の表示（MIT、Copyright (c) 2020-present lilxyzw）を `Shaders/LICENSE.txt` に含めています。lilToon・
   Modular Avatar・Avatar Optimizer・NDMF・VRChat SDK・Unity・Blender はこのツールに含まれておらず、それぞれ独自の
   ライセンスに従います。`Editor/BodyFinder.cs` は Modular Avatar（MIT、Copyright (c) 2022 bd_）のボーンの対応の決まりに
   沿っており、その表示を `THIRD_PARTY_NOTICES.md` に含めています。
