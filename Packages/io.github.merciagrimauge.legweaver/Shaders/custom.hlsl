// SPDX-FileCopyrightText: 2026 MerciaGrimauge
// SPDX-License-Identifier: MIT

// LegWeaver - lilToon 用の、ポーズに応じた伸び。
//
// 頂点ごとに、なめらかに: メッシュのタンジェントは脚に沿う単位ベクトルで、立ちポーズでスキニングしたとき
// 長さ 1 になるよう拡大縮小してある。ブレンドスキニングは、ボーンどうしが曲がり合うところ（関節）でこれを短くし、
// 曲がるほど短くなる: c = 1 - length(スキニング後のタンジェント) が各頂点の曲がりを表す。UV3.x は c の単位あたりに
// そこのニットがどれだけ伸びるか（k。生成時に脚を曲げて求める: 膝の前では正、膝の裏では負）を持つ。
// 伸び s = 1 + k c は各三角形の中で補間されるので、なめらかに変わる。ニットの被覆は c / s になるので、
// アルファを s で割る。縮むところ（s が 1 未満）では本物のニットは詰まらずにたわむので、s は 0.92 で止める:
// それより低いと、もともと密なかかとが、足を伸ばしたときにほぼ不透明になる。

#define LIL_REQUIRE_APP_TANGENT

#define LIL_CUSTOM_PROPERTIES \
    float _LWDynamicStretch;

#define LIL_CUSTOM_TEXTURES

#define LIL_CUSTOM_V2F_MEMBER(id0,id1,id2,id3,id4,id5,id6,id7) \
    float lwStretch : TEXCOORD##id0;

#define LIL_CUSTOM_VERT_COPY \
    LIL_V2F_OUT_BASE.lwStretch = 1.0 + input.uv3.x * (1.0 - length(input.tangentOS.xyz));

#define BEFORE_ALPHAMASK \
    if(_LWDynamicStretch > 0) \
    { \
        float lwS = pow(clamp(input.lwStretch, 0.92, 3.0), _LWDynamicStretch); \
        fd.col.a = saturate(fd.col.a / lwS); \
    }
