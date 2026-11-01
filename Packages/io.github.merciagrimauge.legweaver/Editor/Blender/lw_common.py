# SPDX-FileCopyrightText: 2026 MerciaGrimauge
# SPDX-License-Identifier: GPL-3.0-or-later

# LegWeaver の Blender 側: 共通の補助。ジョブの入力、ランドマークと脚の連なり、切断平面、体の表面、メッシュの道具。

import bpy
import bmesh
import sys
import time
from typing import Any, Callable
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

_start = time.time()


def log(msg: str):
    print(f"[LW] {time.time() - _start:6.1f}s " + msg, flush=True)


def read_job() -> str:
    argv = sys.argv
    return argv[argv.index("--") + 1]


def v3(a: list[float] | None) -> np.ndarray | None:
    return np.array(a, dtype=np.float64) if a is not None else None


def normalize(v: np.ndarray) -> np.ndarray:
    n = np.linalg.norm(v)
    return v / n if n > 1e-12 else v


class Plane:
    """半空間。keep_positive=True なら dot(p - co, no) >= 0 の点を残す。"""

    def __init__(
        self,
        co: np.ndarray,
        no: np.ndarray,
        keep_positive: bool,
        side: int,
        below: tuple[np.ndarray, np.ndarray] | None = None,
        near: tuple[np.ndarray, float] | None = None,
    ):
        self.co = co
        self.no = normalize(no)
        self.keep_positive = keep_positive
        self.side = side  # 0: 両方、1: 左、-1: 右
        # (点, 上): 平面はその点より下だけを切る（それより上はすべて残す。この縁から遠い所）
        self.below = below
        # (点, 半径): 平面はその点からその距離以内だけを切る
        self.near = near

    def limited(self, p: np.ndarray) -> bool:
        """平面が切る空間の部分の外側にあるか。"""
        return (
            self.below is not None
            and (p - self.below[0]) @ self.below[1] > 0
            or self.near is not None
            and np.linalg.norm(p - self.near[0]) > self.near[1]
        )

    def signed(self, p: np.ndarray) -> float:
        if self.limited(p):
            return 1e9
        d = (p - self.co) @ self.no
        return d if self.keep_positive else -d

    def on_edge(self, p: np.ndarray, eps: float) -> bool:
        """切り口の上にあるか（制限は見ない）。"""
        return abs(float((p - self.co) @ self.no)) < eps


class Field:
    """
    滑らかな形で切る: fn(p) はおよその符号付き距離（正なら残す）。平面の制限（球の中など）の境目にできる段を
    作らないために、辺をちょうど fn = 0 の所で分けて切る。
    """

    keep_positive = True

    def __init__(self, fn: Callable[[np.ndarray], float], side: int):
        self.fn = fn
        self.side = side

    def signed(self, p: np.ndarray) -> float:
        return float(self.fn(p))

    def on_edge(self, p: np.ndarray, eps: float) -> bool:
        return abs(self.signed(p)) < eps


def smooth_min(a: float, b: float, k: float) -> float:
    """角を半径 k ほどで丸めた min。"""
    h = min(max(0.5 + 0.5 * (b - a) / k, 0.0), 1.0)
    return b * (1 - h) + a * h - k * h * (1 - h)


def smooth_max(a: float, b: float, k: float) -> float:
    return -smooth_min(-a, -b, k)


def load_input(
    job: dict[str, Any],
) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    n = int(job["vertexCount"])
    t = int(job["triangleCount"])
    raw = np.fromfile(job["inputBin"], dtype=np.uint8)
    # 個数どおりちょうど: 頂点ごとに 48 バイト（位置、法線、uv、脚、関節の混ざり、下、上）、三角形ごとに 12
    if n <= 0 or t <= 0 or len(raw) != 48 * n + 12 * t:
        raise ValueError(f"input.bin does not match its counts ({n} vertices, {t} triangles, {len(raw)} bytes)")
    off = 0

    def take(dtype, count):
        nonlocal off
        size = np.dtype(dtype).itemsize * count
        arr = np.frombuffer(raw[off : off + size].tobytes(), dtype=dtype)
        off += size
        return arr

    pos = take(np.float32, n * 3).reshape(n, 3).astype(np.float64)
    nrm = take(np.float32, n * 3).reshape(n, 3).astype(np.float64)
    uv = take(np.float32, n * 2).reshape(n, 2).astype(np.float64)
    leg = take(np.float32, n).astype(np.float64)
    mix = take(np.float32, n).astype(np.float64)
    under = take(np.float32, n).astype(np.float64)
    over = take(np.float32, n).astype(np.float64)
    tris = take(np.int32, t * 3).reshape(t, 3)
    if tris.min() < 0 or tris.max() >= n:
        raise ValueError("input.bin has triangle corners out of range")
    return pos, nrm, uv, leg, mix, under, over, tris


def weld(pos: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    ext = pos.max(axis=0) - pos.min(axis=0)
    eps = max(float(np.linalg.norm(ext)) * 1e-7, 1e-9)
    key = np.round(pos / eps).astype(np.int64)
    _, first, inverse = np.unique(key, axis=0, return_index=True, return_inverse=True)
    return first, inverse.reshape(-1)


def build_landmarks(job: dict[str, Any]) -> tuple[dict[str, np.ndarray], np.ndarray, np.ndarray, np.ndarray]:
    lm = {k: v3(v) for k, v in job["landmarks"].items()}
    hip_l, hip_r = lm["upperLegL"], lm["upperLegR"]
    ank_l, ank_r = lm["footL"], lm["footR"]
    hip_mid = (hip_l + hip_r) * 0.5
    ank_mid = (ank_l + ank_r) * 0.5
    up = normalize(hip_mid - ank_mid)
    lateral = hip_l - hip_r
    lateral = normalize(lateral - up * (lateral @ up))
    return lm, up, lateral, hip_mid


def body_forward(lm: dict[str, np.ndarray], up: np.ndarray, lateral: np.ndarray) -> np.ndarray:
    """
    体の向く方向（水平）: 足のボーンからつま先のボーンへ（メッシュに足がなくてもそこにある）。無ければ
    up x lateral。（lateral x up は後ろを向く: lateral は右の股関節から左の股関節へ向かう。）
    """
    tw = [
        lm["toes" + s] - lm["foot" + s]
        for s in "LR"
        if lm.get("toes" + s) is not None and lm.get("foot" + s) is not None
    ]
    f = np.mean(tw, axis=0) if tw else np.cross(up, lateral)
    f = f - up * (f @ up)
    return normalize(f) if np.linalg.norm(f) > 1e-12 else normalize(np.cross(up, lateral))


def side_key(side: int) -> str:
    """左右（1 左、-1 右）に対応するランドマークの接尾辞。"""
    return "L" if side > 0 else "R"


def leg_length(lm: dict[str, np.ndarray]) -> float:
    """左脚の長さ（股関節から足首まで）: 脚の各部の尺度。"""
    return float(np.linalg.norm(lm["upperLegL"] - lm["footL"]))


def hip_width(lm: dict[str, np.ndarray]) -> float:
    """左右の股関節の間の距離。"""
    return float(np.linalg.norm(lm["upperLegL"] - lm["upperLegR"]))


# 指の付け根: 足首からつま先の先端までのこの割合の位置（指が分かれる所。まとめたつま先のゾーンはここから始まる）
TOE_BASE = 0.55


def chain_for(lm: dict[str, np.ndarray], side: int) -> list[np.ndarray]:
    s = side_key(side)
    pts = [lm["upperLeg" + s], lm["lowerLeg" + s], lm["foot" + s]]
    if lm.get("toes" + s) is not None:
        pts.append(lm["toes" + s])
    return pts


def chain_point(chain: list[np.ndarray], s: float) -> tuple[np.ndarray, np.ndarray]:
    """連なりのパラメーター s（0 股関節、1 膝、2 足首、3 つま先）での点と方向（股関節へ向かう）。"""
    i = int(min(max(np.floor(s), 0), len(chain) - 2))
    f = s - i
    a, b = chain[i], chain[i + 1]
    return a + (b - a) * f, normalize(a - b)


def make_planes(
    g: dict[str, Any], lm: dict[str, np.ndarray], up: np.ndarray, hip_mid: np.ndarray
) -> tuple[list[Plane], list[np.ndarray]]:
    kind = g["type"]
    planes = []
    seeds = []
    chest = lm.get("chest")
    if chest is None:
        chest = lm.get("spine")
    top_h = float((chest - hip_mid) @ up)
    waist_co = hip_mid + up * (top_h * g["waistHeight"])
    if kind in ("tights", "spats"):
        planes.append(Plane(waist_co, up, False, 0))
    for side in (1, -1):
        chain = chain_for(lm, side)
        if kind == "spats":
            co, no = chain_point(chain, g["legCut"])
            planes.append(Plane(co, no, True, side))
            seeds.append(chain_point(chain, g["legCut"] * 0.5)[0])
        elif kind == "thighhigh":
            co, no = chain_point(chain, g["thighTop"])
            planes.append(Plane(co, no, False, side))
            seeds.append(chain_point(chain, (g["thighTop"] + 2.0) * 0.5)[0])
        else:
            seeds.append(chain[1])
    return planes, seeds


def crotch_point(
    co: np.ndarray, nrm: np.ndarray, lm: dict[str, np.ndarray], up: np.ndarray, lateral: np.ndarray, hip_mid: np.ndarray
) -> np.ndarray | None:
    """
    股の底: 体の中心線の近く（左右の股関節の間隔の 5% 以内）で、股関節より下・膝より上の、下を向いた（法線が真下から
    45° 以内）点のうち最も高い物。無ければ None。
    """
    hw = hip_width(lm)
    knee_h = max(float(lm["lowerLegL"] @ up), float(lm["lowerLegR"] @ up))
    h = co @ up
    sel = (
        (np.abs((co - hip_mid) @ lateral) < hw * 0.05)
        & (h < float(hip_mid @ up))
        & (h > knee_h)
        & ((nrm @ -up) > np.cos(np.radians(45.0)))
    )
    if not sel.any():
        return None
    pts = co[sel]
    return pts[int(np.argmax(pts @ up))]


def top_below_crotch(crotch: np.ndarray | None, lm: dict[str, np.ndarray], half: float, margin: float) -> float:
    """
    帯の上の縁（脚の鎖の s − half の、脚に直交する平面）が、股の底 `crotch` より margin 以上下を通る最小の s
    （両脚で大きい方。股の底が無ければ 0）。
    """
    if crotch is None:
        return 0.0
    lowest = 0.0
    for side in (1, -1):
        chain = chain_for(lm, side)
        for s in np.arange(0.0, 1.0, 0.01):
            i = int(min(max(np.floor(s), 0), len(chain) - 2))
            span = float(np.linalg.norm(chain[i + 1] - chain[i]))
            co, no = chain_point(chain, s - half / span)
            if float((crotch - co) @ no) > margin:
                lowest = max(lowest, float(s))
                break
    return lowest


def garter_planes(
    g: dict[str, Any], lm: dict[str, np.ndarray], unit: Callable[[float], float]
) -> tuple[list[Plane], list[np.ndarray]]:
    """
    ガーター: 脚ごとに、脚の鎖 s = garterAt を中心に幅 garterWidthMm の輪を残す（上の平面の足首側と、下の平面の
    股関節側）。幅はその区間の骨の長さで脚の鎖の長さに直す。
    """
    planes, seeds = [], []
    at = float(g["garterAt"])
    for side in (1, -1):
        chain = chain_for(lm, side)
        i = int(min(max(np.floor(at), 0), len(chain) - 2))
        half = unit(float(g["garterWidthMm"])) * 0.5 / float(np.linalg.norm(chain[i + 1] - chain[i]))
        co, no = chain_point(chain, at - half)
        planes.append(Plane(co, no, False, side))
        co, no = chain_point(chain, at + half)
        planes.append(Plane(co, no, True, side))
        seeds.append(chain_point(chain, at)[0])
    return planes, seeds


def chain_param(chain: list[np.ndarray], p: np.ndarray) -> tuple[float, float]:
    """（脚の連なりに沿ったパラメーター, 連なりまでの距離）。パラメーターが負: 股関節より上。"""
    best = (0.0, 1e30)
    for i in range(len(chain) - 1):
        a, b = chain[i], chain[i + 1]
        ab = b - a
        raw = ((p - a) @ ab) / max(ab @ ab, 1e-30)
        f = min(max(raw, 0.0), 1.0)
        d = float(np.linalg.norm(a + ab * f - p))
        if d < best[1]:
            best = (i + (raw if i == 0 and raw < 0 else f), d)
    return best


def face_side(center: np.ndarray, hip_mid: np.ndarray, lateral: np.ndarray) -> int:
    return 1 if (center - hip_mid) @ lateral >= 0 else -1


def cut_by_planes(bm: bmesh.types.BMesh, planes: list[Plane], hip_mid: np.ndarray, lateral: np.ndarray):
    def applies(plane, center):
        if plane.limited(center):
            return False
        return plane.side == 0 or face_side(center, hip_mid, lateral) == plane.side

    for plane in planes:
        if isinstance(plane, Field):
            cut_field(bm, plane, lambda c, plane=plane: plane.side == 0 or face_side(c, hip_mid, lateral) == plane.side)
            continue
        if not cut(bm, plane.co, plane.no, lambda c, plane=plane: applies(plane, c)):
            continue
        drop = []
        for f in bm.faces:
            c = np.array(f.calc_center_median())
            if applies(plane, c) and plane.signed(c) < 0:
                drop.append(f)
        bmesh.ops.delete(bm, geom=drop, context="FACES_ONLY")
    remove_loose(bm)


def body_edge_planes(
    bm: bmesh.types.BMesh,
    planes: list,
    up: np.ndarray | Callable[[np.ndarray], np.ndarray],
    hip_mid: np.ndarray,
    lateral: np.ndarray,
    girth: float,
    eps: float,
) -> list[Plane]:
    """
    体の切り口（肌が途中までしか無い体）で切りそろえる平面。範囲の開いた縁のうち、切断平面の上に無く（頂点の半分
    以上が平面から eps より遠い）、長さが girth 以上の輪（脚を一周する）ごとに、縁の下に布があれば縁の一番低い点の
    少し下に上端の平面（下を残す）を、上に布があれば一番高い点の少し上に下端の平面（上を残す）を置く。体の切り口が
    斜めだと、その縁に沿った衣装の端も斜めになり、角が尖るため。up に関数を渡すと、縁の中心での向き（腕の軸など）を
    上下の代わりに使う。
    """
    seen = set()
    out = []
    for e in [e for e in bm.edges if e.is_boundary]:
        if e in seen:
            continue
        group, stack = [], [e]
        seen.add(e)
        while stack:
            cur = stack.pop()
            group.append(cur)
            for v in cur.verts:
                for ne in v.link_edges:
                    if ne.is_boundary and ne not in seen:
                        seen.add(ne)
                        stack.append(ne)
        verts = list({v for x in group for v in x.verts})
        pts = np.array([v.co for v in verts])
        on = sum(1 for p in pts if any(pl.on_edge(p, eps) for pl in planes))
        if on * 2 >= len(pts) or sum(x.calc_length() for x in group) < girth:
            continue
        axis = up(pts.mean(axis=0)) if callable(up) else up
        h = pts @ axis
        fc = np.array([f.calc_center_median() for x in group for f in x.link_faces])
        side = face_side(pts.mean(axis=0), hip_mid, lateral)
        margin = eps * 10.0
        if float(np.mean(fc @ axis)) < float(np.mean(h)):
            out.append(Plane(pts[np.argmin(h)] - axis * margin, axis, False, side))
        else:
            out.append(Plane(pts[np.argmax(h)] + axis * margin, axis, True, side))
    return out


def remove_loose(bm: bmesh.types.BMesh):
    loose = [v for v in bm.verts if not v.link_faces]
    bmesh.ops.delete(bm, geom=loose, context="VERTS")
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()


def components(bm: bmesh.types.BMesh) -> tuple[dict[bmesh.types.BMVert, int], list[list[bmesh.types.BMVert]]]:
    comp = {}
    comps = []
    for f in bm.faces:
        if f in comp:
            continue
        cid = len(comps)
        stack = [f]
        comp[f] = cid
        members = []
        while stack:
            cur = stack.pop()
            members.append(cur)
            for v in cur.verts:
                for nf in v.link_faces:
                    if nf not in comp:
                        comp[nf] = cid
                        stack.append(nf)
        comps.append(members)
    return comp, comps


def keep_components(
    bm: bmesh.types.BMesh,
    seeds: list[np.ndarray],
    keep_extra: Callable[[list[bmesh.types.BMVert]], bool] | None = None,
    near: float = 0.0,
) -> tuple[int, int, list[np.ndarray]]:
    """
    種（最も近い頂点）を含まず、keep_extra にも受け入れられない面のつながりを削除する。near > 0 なら、種で残す面から
    全頂点が near 以内にある塊（指から離れた島になっている爪など）は keep_extra に関わらず削除し、その頂点の位置を
    返す（残すとそれぞれが別の布の小片になる）。
    """
    comp, comps = components(bm)
    keep = set()
    if seeds and bm.verts:
        co = np.array([v.co for v in bm.verts])
        for s in seeds:
            idx = int(np.argmin(np.linalg.norm(co - s, axis=1)))
            for f in bm.verts[idx].link_faces:
                keep.add(comp[f])
    islands = []
    island_ids = set()
    if near > 0 and keep:
        faces = [f for f in bm.faces if comp[f] in keep]
        index = {v: i for i, v in enumerate({v for f in faces for v in f.verts})}
        tree = BVHTree.FromPolygons([v.co for v in index], [[index[v] for v in f.verts] for f in faces])
        for cid, members in enumerate(comps):
            pts = {v for f in members for v in f.verts}
            if cid not in keep and all(tree.find_nearest(v.co, near)[0] is not None for v in pts):
                islands.append(np.array([v.co for v in pts]))
                island_ids.add(cid)
    if keep_extra is not None:
        for cid, members in enumerate(comps):
            if cid not in island_ids and keep_extra(members):
                keep.add(cid)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if comp[f] not in keep], context="FACES_ONLY")
    remove_loose(bm)
    return len(keep), len(comps), islands


def vertex_normals(co: np.ndarray, faces_idx: list[list[int]]) -> np.ndarray:
    n = np.zeros_like(co)
    if faces_idx and all(len(f) == 3 for f in faces_idx):
        # 三角形だけなら一度に足す（np.add.at は下のループと同じ順番: 面ごとに角 0, 1, 2）
        t = np.asarray(faces_idx, dtype=np.int64)
        a, b, c = co[t[:, 0]], co[t[:, 1]], co[t[:, 2]]
        np.add.at(n, t.reshape(-1), np.repeat(np.cross(b - a, c - a), 3, axis=0))
        faces_idx = []
    for f in faces_idx:
        for i in range(1, len(f) - 1):
            a, b, c = co[f[0]], co[f[i]], co[f[i + 1]]
            fn = np.cross(b - a, c - a)
            n[f[0]] += fn
            n[f[i]] += fn
            n[f[i + 1]] += fn
    ln = np.linalg.norm(n, axis=1, keepdims=True)
    return n / np.maximum(ln, 1e-20)


def barycentric(p: np.ndarray, a: np.ndarray, b: np.ndarray, c: np.ndarray, clip: bool = True) -> np.ndarray:
    v0, v1, v2 = b - a, c - a, p - a
    d00, d01, d11 = v0 @ v0, v0 @ v1, v1 @ v1
    d20, d21 = v2 @ v0, v2 @ v1
    den = d00 * d11 - d01 * d01
    if abs(den) < 1e-30:
        return np.array([1.0, 0.0, 0.0])
    v = (d11 * d20 - d01 * d21) / den
    w = (d00 * d21 - d01 * d20) / den
    if not clip:
        return np.array([1.0 - v - w, v, w])
    bary = np.clip(np.array([1.0 - v - w, v, w]), 0.0, 1.0)
    return bary / max(bary.sum(), 1e-12)


class BodySurface:
    def __init__(self, wpos: np.ndarray, wtris: np.ndarray, tri_ids: np.ndarray):
        self.pos = wpos
        self.tris = wtris
        self.tri_ids = tri_ids
        self.bvh = BVHTree.FromPolygons([Vector(p) for p in wpos], wtris[tri_ids].tolist(), all_triangles=True)
        self.vnormals = vertex_normals(wpos, wtris[tri_ids].tolist())

    def bind(self, p: np.ndarray) -> tuple[int, np.ndarray, np.ndarray]:
        """最も近い体の点の（元の三角形の番号, 重心座標, 最も近い位置）。"""
        loc, _, fi, _ = self.bvh.find_nearest(Vector(p))
        t = int(self.tri_ids[fi])
        loc = np.array(loc)
        return t, barycentric(loc, *self.pos[self.tris[t]]), loc

    def bind_below(self, p: np.ndarray, n: np.ndarray, reach: float) -> tuple[int, np.ndarray, np.ndarray]:
        """
        `p` から `n` の逆向きにまっすぐ下の体の点（`reach` 以内に無ければ最も近い点）の（元の三角形の番号, 重心座標,
        位置）。体を橋渡しする面（まとめたつま先）は、最も近い点だと隣り合う指の間で飛び、写しの肌の色が引き伸ばされる。
        """
        loc, _, fi, _ = self.bvh.ray_cast(Vector(p), Vector(-n), reach)
        if loc is None:
            return self.bind(p)
        t = int(self.tri_ids[fi])
        loc = np.array(loc)
        return t, barycentric(loc, *self.pos[self.tris[t]]), loc

    def face_normal(self, t: int) -> np.ndarray:
        a, b, c = self.pos[self.tris[t]]
        return normalize(np.cross(b - a, c - a))

    def normal(self, t: int, bary: np.ndarray) -> np.ndarray:
        return normalize(bary @ self.vnormals[self.tris[t]])


def apply_modifiers(obj: bpy.types.Object):
    """モディファイアーの積み重ねをメッシュのデータへ焼き込む（オペレーターのコンテキストは不要）。"""
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(obj.evaluated_get(dg))
    obj.modifiers.clear()
    obj.data = me


def decimate(obj: bpy.types.Object, target_tris: int):
    """Collapse によるデシメーションで、およそ `target_tris` 個の三角形まで減らす。"""
    tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    ratio = min(1.0, target_tris / max(tris, 1))
    if ratio >= 1.0:
        return
    dec = obj.modifiers.new("decimate", "DECIMATE")
    dec.decimate_type = "COLLAPSE"
    dec.ratio = ratio
    apply_modifiers(obj)


def foot_frame(
    co: np.ndarray, lm: dict[str, np.ndarray], up: np.ndarray, lateral: np.ndarray, hip_mid: np.ndarray, side: int
) -> tuple[np.ndarray, np.ndarray, float, float]:
    """足首より下の点から求めた、片足の（足首, 前方, 足首より前の長さ, 足首より後ろの長さ）。"""
    ankle = lm["foot" + side_key(side)]
    low = np.array([q for q in co if (q - ankle) @ up < 0 and face_side(q, hip_mid, lateral) == side])
    if len(low) < 10:
        return None
    toes = lm.get("toes" + side_key(side))
    fwd = (toes - ankle) if toes is not None else body_forward(lm, up, lateral)
    fwd = normalize(fwd - up * (fwd @ up))
    if np.mean((low - ankle) @ fwd) < 0:
        fwd = -fwd
    d = (low - ankle) @ fwd
    return ankle, fwd, float(d.max()), float(-d.min())


def cut(bm: bmesh.types.BMesh, co: np.ndarray, no: np.ndarray, applies: Callable[[np.ndarray], bool]) -> bool:
    """中心が `applies` を満たす面を平面 (co, no) で二分する。1 つでも二分したかを返す。"""
    faces = [f for f in bm.faces if applies(np.array(f.calc_center_median()))]
    if not faces:
        return False
    edges = list(dict.fromkeys(e for f in faces for e in f.edges))
    verts = list(dict.fromkeys(v for f in faces for v in f.verts))
    bmesh.ops.bisect_plane(bm, geom=faces + edges + verts, dist=1e-9, plane_co=Vector(co), plane_no=Vector(no))
    return True


def cut_field(bm: bmesh.types.BMesh, field: Field, applies: Callable[[np.ndarray], bool]):
    """中心が `applies` を満たす面を、field = 0 の所で分けて、負の側を削除する。"""
    faces = [f for f in bm.faces if applies(np.array(f.calc_center_median()))]
    val = {v: field.signed(np.array(v.co)) for f in faces for v in f.verts}
    zero = {v for v, x in val.items() if x == 0.0}
    # （面の順に並べる: 集合から取り出す順は実行ごとに変わり、分ける順とメッシュの番号が変わって出力が毎回違う）
    edges = list(dict.fromkeys(e for f in faces for e in f.edges))
    for e in edges:
        a, b = e.verts
        if a not in val or b not in val or val[a] * val[b] >= 0:
            continue
        pa, pb = np.array(a.co), np.array(b.co)
        lo, hi = 0.0, 1.0  # a 側の符号を lo に保つ二分法
        for _ in range(30):
            mid = (lo + hi) * 0.5
            if field.signed(pa + (pb - pa) * mid) * val[a] > 0:
                lo = mid
            else:
                hi = mid
        _, nv = bmesh.utils.edge_split(e, a, (lo + hi) * 0.5)
        val[nv] = 0.0
        zero.add(nv)
    for f in faces:
        if not f.is_valid:
            continue
        zs = [v for v in f.verts if v in zero]
        # （三角形は 2 か所でしか横切られない。もっと多いのは曲がりの強い所の小さな面で、分けずに中心で決める）
        if len(zs) == 2 and not any(zs[1] in e.verts for e in zs[0].link_edges):
            bmesh.utils.face_split(f, zs[0], zs[1])
    drop = [
        f
        for f in bm.faces
        if applies(np.array(f.calc_center_median()))
        and np.mean([val.get(v, field.signed(np.array(v.co))) for v in f.verts]) < 0
    ]
    bmesh.ops.delete(bm, geom=drop, context="FACES_ONLY")


def new_object(name: str, bm: bmesh.types.BMesh) -> bpy.types.Object:
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    obj = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def body_bmesh(
    wpos: np.ndarray, wtris: np.ndarray, tri_ids: np.ndarray
) -> tuple[bmesh.types.BMesh, list[bmesh.types.BMVert]]:
    bm = bmesh.new()
    verts = [bm.verts.new(Vector(p)) for p in wpos]
    for t in tri_ids:
        try:
            bm.faces.new([verts[i] for i in wtris[t]])
        except ValueError:
            pass
    return bm, verts


# 編み地の伸び（脚は stretch_mask、腕は arm_stretch_mask）: 面積がどれだけ増えるか s を、手足の連なりに沿った太さ
# （パラメーター t: 根元で 0、中間の関節で 1、先端で 2）と関節から求める。透ける光は 1/s で増える。
GIRTH_STRETCH = 0.25  # 覆う部分のうち最も細い所を超える太さ 1 単位あたりの s
GIRTH_FADE = 0.15  # （t で）太さの項は根元を過ぎてから強まり、先端の手前で弱まる
STRETCH_SMOOTHING = 4  # 隣との平均を取る回数。場に継ぎ目が出ないように
MIN_TRANSMIT = 0.3  # 伸びで編み地が薄くなってよい最大


def girth_stretch(t: np.ndarray, r: np.ndarray) -> np.ndarray:
    """
    太さからの s: r（連なりからの距離）を、手足に沿ったその 10 パーセンタイル（t は 0..2）と比べたもの。両端で
    強まり・弱まる。手足に沿った頂点が少なすぎて分からない所では None。
    """
    along = (t >= 0) & (t <= 2.0)
    if along.sum() < 10:
        return None
    r_ref = np.percentile(r[along], 10)
    q = np.where(along, r / max(r_ref, 1e-12), 1.0)
    fade = np.clip(t / GIRTH_FADE, 0, 1) * np.clip((2.0 - t) / GIRTH_FADE, 0, 1)
    return 1.0 + GIRTH_STRETCH * np.maximum(q - 1.0, 0.0) * fade


def joint_bump(t: np.ndarray, at: float, width: float) -> np.ndarray:
    """連なりに沿った `at` で 1、`width` にわたって下がる（ガウス関数）: 関節が編み地を伸ばす所。"""
    return np.exp(-(((t - at) / width) ** 2))


def stretch_to_mask(s: np.ndarray, neighbors: list[list[np.int64]], strength: float) -> np.ndarray:
    """伸び s を表面で均し、`strength` で倍率をかけ、透ける光（1/s）として返す。"""
    # 隣の平均を一度に計算する: 隣が 8 未満の頂点は区間の和 ÷ 個数（np.mean と同じく前から順に足す）。8 以上は
    # np.mean が足す順番を変えるので、1 つずつ np.mean で
    cnt = np.array([len(nb) for nb in neighbors], dtype=np.int64)
    few = (cnt > 0) & (cnt < 8)
    many = np.flatnonzero(cnt >= 8)
    flat = np.array([j for i, nb in enumerate(neighbors) if few[i] for j in nb], dtype=np.int64)
    starts = np.cumsum(cnt[few]) - cnt[few]
    for _ in range(STRETCH_SMOOTHING):
        nxt = s.copy()
        if len(flat):
            nxt[few] = s[few] * 0.5 + 0.5 * (np.add.reduceat(s[flat], starts) / cnt[few])
        for i in many:
            nxt[i] = s[i] * 0.5 + 0.5 * np.mean(s[neighbors[i]])
        s = nxt
    s = 1.0 + strength * (s - 1.0)
    return np.clip(1.0 / s, MIN_TRANSMIT, 1.0)


def feet_forward(
    co: np.ndarray, lm: dict[str, np.ndarray], up: np.ndarray, lateral: np.ndarray, hip_mid: np.ndarray
) -> np.ndarray:
    """足の向く方向としての前方（両足の平均）。無ければ体の前方。"""
    fw = [fr[1] for fr in (foot_frame(co, lm, up, lateral, hip_mid, sd) for sd in (1, -1)) if fr is not None]
    return normalize(np.mean(fw, axis=0)) if fw else body_forward(lm, up, lateral)


def left_foot_forward(lm: dict[str, np.ndarray], up: np.ndarray, lateral: np.ndarray) -> np.ndarray:
    """左足の向く方向としての前方（足首からつま先へ、水平）。つま先がなければ体の前方。"""
    toes = lm.get("toesL")
    fwd = (toes - lm["footL"]) if toes is not None else body_forward(lm, up, lateral)
    return normalize(fwd - up * (fwd @ up))
