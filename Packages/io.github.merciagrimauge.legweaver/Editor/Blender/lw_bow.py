# SPDX-FileCopyrightText: 2026 MerciaGrimauge
# SPDX-License-Identifier: GPL-3.0-or-later

# LegWeaver の Blender 側: ガーターの帯に付けるリボンの結び目（2 つの輪・2 本の垂れ・結び目）。
# リボンの断面（幅 × 厚み）を道筋に沿って掃いた閉じた立体を、脚の軸の周りに巻き付けて帯の外に置く。

import bmesh
from typing import Callable
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from lw_common import BodySurface, body_forward, chain_for, chain_point, normalize

BOW_ANGLE = np.radians(35.0)  # 結び目の位置: 前から外側へ回した角度
RIBBON_THICK_MM = 0.6  # リボンの厚み
BOW_GAP_MM = 0.4  # 帯の外面とリボンの間
BOW_LIFT = np.radians(12.0)  # 結び目の輪を帯から外へ起こす角度


def ribbon_profile(w: float, th: float, edge: int = 3) -> list[tuple[float, float]]:
    """
    リボンの断面（幅の向き, 厚みの向き）: 幅 w・厚み th の平たい形で、両端を半径 th / 2 の半円に丸める（角が立たない）。
    下の辺 → 右の半円 → 上の辺 → 左の半円の順。
    """
    r = th * 0.5
    out = [(-w * 0.5 + r, -r), (w * 0.5 - r, -r)]
    out += [(w * 0.5 - r + r * np.sin(a), -r * np.cos(a)) for a in np.linspace(0, np.pi, edge + 2)[1:-1]]
    out += [(w * 0.5 - r, r), (-w * 0.5 + r, r)]
    out += [(-w * 0.5 + r - r * np.sin(a), r * np.cos(a)) for a in np.linspace(0, np.pi, edge + 2)[1:-1]]
    return out


def sweep(path: np.ndarray, widths: np.ndarray, w: np.ndarray, th: float, closed: bool) -> tuple[np.ndarray, list, int]:
    """
    道筋 `path`（点の並び）に沿って、幅の向き `widths`（点ごと）に幅 w（点ごと）、それと道筋に直交する向きに厚み th の
    リボンの断面（ribbon_profile）を掃いた立体の頂点と面。閉じていなければ両端にふたをする。断面の頂点の数も返す。
    """
    n = len(path)
    w = np.broadcast_to(np.asarray(w, dtype=float), (n,))
    m = len(ribbon_profile(float(w[0]), th))
    tang = np.gradient(path, axis=0) if not closed else np.roll(path, -1, axis=0) - np.roll(path, 1, axis=0)
    verts = []
    for i in range(n):
        t = normalize(tang[i])
        wd = normalize(widths[i] - t * float(widths[i] @ t))
        tn = normalize(np.cross(t, wd))
        for a, b in ribbon_profile(float(w[i]), th):
            verts.append(path[i] + wd * a + tn * b)
    faces = []
    for i in range(n if closed else n - 1):
        j = (i + 1) % n
        for k in range(m):
            k2 = (k + 1) % m
            faces.append([i * m + k, i * m + k2, j * m + k2, j * m + k])
    if not closed:
        faces.append(list(range(m - 1, -1, -1)))
        last = (n - 1) * m
        faces.append([last + k for k in range(m)])
    return np.array(verts), faces, m


def in_plane_widths(path: np.ndarray, closed: bool) -> np.ndarray:
    """道筋の接線に直交し、帯の外面に沿う（x・y の面の中の）幅の向き。"""
    tang = np.roll(path, -1, axis=0) - np.roll(path, 1, axis=0) if closed else np.gradient(path, axis=0)
    return np.stack([-tang[:, 1], tang[:, 0], np.zeros(len(path))], axis=1)


def bow_parts(W: float, th: float, gap: float) -> list[tuple[np.ndarray, np.ndarray, bool, np.ndarray, bool, float]]:
    """
    結び目の部品（道筋, 幅の向き, 閉じているか, 幅（点ごと）, 垂れか, 厚み）を、帯の外面に沿った平らな座標で: x は脚の
    周り（外側が正）、y は脚の軸に沿って股関節の向き、z は帯の外面からの高さ。W は帯の幅。
    - 輪（羽根）: リボンが結び目から外へ出て、先で折り返して帯の側へ戻る。道筋は x・z の面の中の、ふくらんだしずく形
      （外の筋は高く、帯の側の筋は帯に沿う。ふくらみは付け根で 0）で、リボンの幅は脚の軸の向き。幅は結び目で 45% に
      くびれ、中ほどで最大、先で 77% に絞る。
      前から見ると、羽根の面がふくらんで見える（道筋を帯の面の中に置くと、輪の穴が見えて線画のようになる）。左右で
      少し上下に開く。
    - 垂れ: 長さを左右で 1 割変え、先へ向けて幅を 15% 広げ、緩い S 字に揺らす。幅は帯の面の中。
    - 結び目: くびれた羽根の付け根を縦に巻く、短く太い輪（道筋は y・z の面の中、幅は脚の周りの向き）。
    """
    parts = []
    Wr = 0.75 * W  # リボンの幅（羽根の高さ）
    L, D = 0.95 * W, 0.32 * W  # 羽根の長さとふくらみ
    phi = np.linspace(0.0, 2.0 * np.pi, 48, endpoint=False)
    out = (1.0 - np.cos(phi)) * 0.5  # 結び目 0 → 先 1
    for sgn, tilt, tail_len in ((1.0, 0.18, 1.7), (-1.0, 0.1, 1.55)):
        x = sgn * L * out
        # 外の筋（0 < φ < π）は高く、帯の側の筋は帯に沿う。先では丸く折り返す
        # （ふくらみは付け根で 0: 外の筋と帯の側の筋が結び目で重なり、横から付け根の穴が見えない）
        z = gap + th + D * np.clip(np.sin(phi), 0.0, None) * out**0.7 + th * 0.6 * out
        y = tilt * L * out**1.5
        path = np.stack([x, y, z], axis=1)
        widths = np.tile(np.array([0.0, 1.0, 0.0]), (len(phi), 1))
        # （幅: 結び目で 45%、中ほどで最大、先で少し絞る。直線の縁の箱に見えない）
        parts.append((path, widths, True, Wr * (0.45 + 0.55 * np.sin(0.8 * np.pi * out)), False, th))
        s = np.linspace(0.0, 1.0, 16)
        x = sgn * (0.06 * W + 0.4 * W * s + 0.07 * W * np.sin(2.0 * np.pi * s) * s)
        y = -(0.1 * W + tail_len * W * s)
        z = np.full(len(s), gap + th * 0.5)
        path = np.stack([x, y, z], axis=1)
        parts.append((path, in_plane_widths(path, False), False, Wr * 0.62 * (1.0 + 0.15 * s), True, th))
    ky = 0.3 * Wr * np.cos(phi)
    kz = gap + th + (0.5 * D + th) * (1.0 + np.sin(phi)) * 0.55
    path = np.stack([np.zeros(len(phi)), ky, kz], axis=1)
    widths = np.tile(np.array([1.0, 0.0, 0.0]), (len(phi), 1))
    parts.append((path, widths, True, np.full(len(phi), 0.32 * Wr), False, th * 1.4))
    return parts


def add_bows(
    bm: bmesh.types.BMesh,
    g: dict,
    lm: dict[str, np.ndarray],
    up: np.ndarray,
    lateral: np.ndarray,
    body: BodySurface,
    src: bmesh.types.BMLayerItem,
    exact: bmesh.types.BMLayerItem,
    utri: bmesh.types.BMLayerItem,
    taut: bmesh.types.BMLayerItem,
    unit: Callable[[float], float],
) -> tuple[int, list[int]]:
    """
    各脚のガーターの帯の外に結び目を足す。帯の中心の脚の軸から、前から外側へ BOW_ANGLE 回した向きに帯の外面を
    探し、そこを原点に脚の軸の周りに巻き付ける。各頂点は、脚の軸から自分の向きに測った帯の外面（帯の外では、肌に
    付け根での帯の厚さを足した面）の上に置く（脚の断面は円ではないので、一定の半径では浮く・沈む）。頂点の体の点は
    付け根の肌の点。陰影は布自身の法線（`taut`）。垂れの頂点には、垂れの番号（lw_swing、1 から）と根元からの割合
    （lw_swing_t）を付ける（Unity が揺れ物の骨に結び付ける）。（足した頂点の数, 垂れの番号）を返す。
    """
    swing = bm.verts.layers.int.get("lw_swing") or bm.verts.layers.int.new("lw_swing")
    swing_t = bm.verts.layers.float.get("lw_swing_t") or bm.verts.layers.float.new("lw_swing_t")
    chains = []
    W = unit(float(g["garterWidthMm"]))
    th, gap = unit(RIBBON_THICK_MM), unit(BOW_GAP_MM)
    tree = BVHTree.FromBMesh(bm)
    fwd = body_forward(lm, up, lateral)
    region = bm.faces.layers.int.get("lw_region")
    added = 0
    for side in (1, -1):
        c, toward_hip = chain_point(chain_for(lm, side), float(g["garterAt"]))
        a = normalize(toward_hip)
        d = normalize(fwd * np.cos(BOW_ANGLE) + lateral * side * np.sin(BOW_ANGLE))
        d = normalize(d - a * float(d @ a))
        hit = tree.ray_cast(Vector(c), Vector(d))
        skin = body.bvh.ray_cast(Vector(c), Vector(d))
        if hit[0] is None or skin[0] is None:
            continue
        r0 = float(np.linalg.norm(np.array(hit[0]) - c))
        skin_pt = np.array(skin[0])
        band_th = r0 - float(np.linalg.norm(skin_pt - c))

        def surface(base: np.ndarray, direction: np.ndarray) -> float:
            """脚の軸の点 base から向き direction に測った、帯（外では肌 + 帯の厚さ）の外面までの距離。"""
            h = tree.ray_cast(Vector(base), Vector(direction))
            if h[0] is not None and h[3] < r0 * 1.5:
                return float(h[3])
            h = body.bvh.ray_cast(Vector(base), Vector(direction))
            return float(h[3]) + band_th if h[0] is not None else r0

        # （外側が正の x: 右脚では周りの向きが逆になる）
        around = normalize(np.cross(a, d)) * (1.0 if float(np.cross(a, d) @ lateral) * side > 0 else -1.0)
        new_faces = []
        for path, widths, closed, w, tail, thick in bow_parts(W, th, gap):
            local, faces, ring = sweep(path, widths, w, thick, closed)
            # （垂れ: 断面の ring 頂点ずつが道筋の 1 点）
            tail_id = len(chains) + 1 if tail else 0
            ang = local[:, 0] / r0
            sign = 1.0 if float(np.cross(d, around) @ a) > 0 else -1.0
            world = []
            for t, y, z in zip(ang, local[:, 1], local[:, 2]):
                direction = np.array(Matrix.Rotation(float(t) * sign, 3, Vector(a)) @ Vector(d))
                base = c + a * float(y)
                world.append(base + direction * (surface(base, direction) + float(z)))
            vs = []
            for i, p in enumerate(world):
                v = bm.verts.new(Vector(p))
                v[src] = Vector(skin_pt)
                v[exact] = 0
                v[utri] = -1
                v[taut] = 1
                if tail_id:
                    v[swing] = tail_id
                    v[swing_t] = (i // ring) / max(1, len(path) - 1)
                vs.append(v)
            if tail_id:
                chains.append(tail_id)
            for f in faces:
                new_faces.append(bm.faces.new([vs[i] for i in f]))
            added += len(vs)
        if region is not None:
            for f in new_faces:
                f[region] = 0  # 脚（GarmentRegion.Leg。帯と同じマテリアル）
        bmesh.ops.recalc_face_normals(bm, faces=new_faces)
    return added, chains
