# SPDX-FileCopyrightText: 2026 MerciaGrimauge
# SPDX-License-Identifier: GPL-3.0-or-later

# LegWeaver の Blender 側: 衣装の下に着るもの（下着）。その上の衣装は下着の表面から作るので、下着とまったく同じに
# 動き、下着に突き抜けられない。

from typing import Any, Callable
import bmesh
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from lw_common import BodySurface, barycentric


def load_under(job: dict[str, Any]) -> tuple[BVHTree, np.ndarray, np.ndarray] | None:
    """下に着るものの（BVH, verts, tris）（体のメッシュ空間）。無ければ None。"""
    path = job.get("underBin")
    if not path:
        return None
    with open(path, "rb") as fp:
        data = fp.read()
    if len(data) < 8:
        raise ValueError("under.bin is too short")
    nv, nt = (int(x) for x in np.frombuffer(data[:8], dtype=np.int32))
    # 個数どおりちょうど: 2 つの個数と、頂点・三角形それぞれ 12 バイト
    if nv < 0 or nt < 0 or len(data) != 8 + 12 * nv + 12 * nt:
        raise ValueError(f"under.bin does not match its counts ({nv} vertices, {nt} triangles, {len(data)} bytes)")
    if nv == 0 or nt == 0:
        return None
    verts = np.frombuffer(data[8 : 8 + nv * 12], dtype=np.float32).reshape(-1, 3).astype(np.float64)
    tris = np.frombuffer(data[8 + nv * 12 : 8 + nv * 12 + nt * 12], dtype=np.int32).reshape(-1, 3)
    if tris.min() < 0 or tris.max() >= nv:
        raise ValueError("under.bin has triangle corners out of range")
    bvh = BVHTree.FromPolygons([Vector(p) for p in verts], tris.tolist(), all_triangles=True)
    return bvh, verts, tris


def snap_to_under(
    bm: bmesh.types.BMesh,
    under: tuple[BVHTree, np.ndarray, np.ndarray],
    body: BodySurface,
    src: bmesh.types.BMLayerItem,
    wunder: np.ndarray,
    wtris: np.ndarray,
    offset: float,
    extra: float,
    unit: Callable[[float], float],
) -> tuple[dict, dict[bmesh.types.BMVert, tuple]]:
    """
    体の点の真上に下着がある（同じ向きで、そこで測った高さにある）衣装の頂点を、下着の表面から `offset` + `extra`
    だけ離れて立つよう持ち上げる。そうした頂点ごとに、その下の下着の三角形と重心座標（Unity でのウェイト用）を返し、
    下着の上に立たなければならない頂点（頂点 -> (肌の点, 法線, 高さ)）も返す。
    """
    bvh, verts, tris = under
    bound = {}
    need = {}  # 頂点 -> (肌の点, 法線, 立つべき高さ)
    for f in bm.faces:
        f.normal_update()
    turned = {f: f.normal.copy() for f in bm.faces}
    for v in bm.verts:
        loc = np.array(v[src])
        t, bary, _ = body.bind(loc)
        worn = float(bary @ wunder[wtris[t]])
        if worn <= 0:
            continue
        n = body.normal(t, bary)
        # すでに下着よりずっと上に持ち上がっている（お尻をまたいで橋渡しされた所、太ももの間）: その場に残し、
        # 下着と一緒に動かすだけにする
        lifted = (np.array(v.co) - loc) @ n > worn + offset + extra + unit(2.0)
        guess = loc + n * worn
        hit, hn, fi, d = bvh.find_nearest(Vector(guess), float(worn + unit(3.0)))
        if hit is None:
            continue
        hn = np.array(hn)
        if hn @ n < 0:
            hn = -hn
        hit = np.array(hit)
        # 肌の点の真上: 同じ向きで、横にずれていない
        if hn @ n < 0.6 or np.linalg.norm((hit - loc) - n * ((hit - loc) @ n)) > worn + unit(2.0):
            continue
        # 肌の法線に沿ってまっすぐ外へ持ち上げて下着の上に立たせる（横に動かして下着に載せると、太ももの間の
        # 肌の法線が向きを変える所で面が折れる）。下げることはしない（関節や縁のために、肌側の規則で持ち上げた
        # ものである）
        if not lifted:
            rise = (hit - loc) @ n + offset + extra - (np.array(v.co) - loc) @ n
            need[v] = (loc, n, (hit - loc) @ n + offset + extra)
            if rise > 0:
                v.co = Vector(np.array(v.co) + n * rise)
        a, b, c = verts[tris[fi]]
        bound[v] = (int(fi), barycentric(hit, a, b, c))
    # 狭いくぼみ（太ももの間の溝）では、それぞれの法線に沿って持ち上げた点が互いに寄って面が折り重なる:
    # その折れを均して取る（くぼみでは均すと点が外へ動くので、下着から離れたままになる）
    for _ in range(10):
        folded = set()
        for f in bm.faces:
            f.normal_update()
            if f.normal.dot(turned[f]) < 0.5:
                folded.update(f.verts)
        if not folded:
            break
        ring = set(folded)
        for v in folded:
            ring.update(e.other_vert(v) for e in v.link_edges)
        moved = {}
        for v in ring:
            if v.is_boundary or not v.link_edges:
                continue
            mean = sum((e.other_vert(v).co for e in v.link_edges), Vector()) / len(v.link_edges)
            moved[v] = v.co * 0.5 + mean * 0.5
        for v, co in moved.items():
            v.co = co
            # 下着の中へは決して入れない（膨らみを均すと点が内へ引かれる）。下着に載せない点も、肌から offset より
            # 内へは入れない（股の付け根の溝で、均すと肌の下へ引かれ、肌が布の外に出ていた）
            if v in need:
                loc, n, h = need[v]
            else:
                t, bary, loc = body.bind(np.array(v[src]))
                n, h = body.normal(t, bary), offset
            short = h - (np.array(v.co) - loc) @ n
            if short > 0:
                v.co = Vector(np.array(v.co) + n * short)
    return bound, need
