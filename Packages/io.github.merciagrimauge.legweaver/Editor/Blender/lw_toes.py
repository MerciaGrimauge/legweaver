# SPDX-FileCopyrightText: 2026 MerciaGrimauge
# SPDX-License-Identifier: GPL-3.0-or-later

# LegWeaver の Blender 側: 指をまとめたつま先。母指球より前の SDF 表面を切り出し、体の部分へ縫い合わせる。
# 足袋型の親指の仕切りと、親指だけを覆うサックの切り方も。

import bmesh
from typing import Callable
import bpy
import numpy as np
from mathutils import Vector
from mathutils.kdtree import KDTree
from lw_common import (
    TOE_BASE,
    Field,
    Plane,
    body_bmesh,
    cut,
    face_side,
    foot_frame,
    log,
    new_object,
    normalize,
    remove_loose,
    smooth_min,
)

CAP_BASE = 0.70  # 親指のサックの口の、足首から前への到達に対する割合の最も前（親指の付け根のあたり）
CAP_BEHIND_WEB = 0.04  # 親指と人差し指の股が分かるとき、サックの口を股より足首から前への到達のこの割合だけ後ろに置く
CAP_WEB_DEPTH = (
    0.10  # 股と認めるのは、親指の先から足首から前への到達のこの割合以上後ろにあるとき（指の分かれていない足の
)
#                      低い面の粗さを股と取り違えない）


class ToeZone:
    """片足の前部（母指球より前、足首より下）: そこの衣装は SDF 表面である。"""

    def __init__(self, side: int, ankle: np.ndarray, fwd: np.ndarray, up: np.ndarray, reach: float):
        self.side = side
        self.ankle = ankle
        self.up = up
        self.plane = Plane(ankle + fwd * reach, fwd, True, side)
        # 足の甲がくるぶしより高くなることがある（短く太い足）: それも含める。さもないと足を切った断面が閉じた輪に
        # ならず、つま先を残りとつなげられない（本当の上端は toe_zones が求める）
        self.top = 0.0

    def holds(self, p: np.ndarray, hip_mid: np.ndarray, lateral: np.ndarray) -> bool:
        return (
            face_side(p, hip_mid, lateral) == self.side
            and (p - self.ankle) @ self.up <= self.top
            and self.plane.signed(p) >= 0
        )

    def near(self, p: np.ndarray, hip_mid: np.ndarray, lateral: np.ndarray) -> bool:
        return face_side(p, hip_mid, lateral) == self.side and (p - self.ankle) @ self.up <= self.top


def toe_zones(
    bm: bmesh.types.BMesh, lm: dict[str, np.ndarray], up: np.ndarray, lateral: np.ndarray, hip_mid: np.ndarray
) -> list[ToeZone]:
    """
    範囲が覆う足ごとに 1 つのゾーン。その平面は、足首から、その前方で覆われる最も遠い点までの 55% の位置にある
    （母指球の後ろ、指が分かれ始める所）。メッシュから求めるので、指のボーンがないアバターでも同じように動く。
    """
    co = np.array([v.co for v in bm.verts])
    zones = []
    for side in (1, -1):
        frame = foot_frame(co, lm, up, lateral, hip_mid, side)
        if frame is None or frame[2] <= 0:
            continue
        ankle, fwd, ahead, _ = frame
        z = ToeZone(side, ankle, fwd, up, ahead * TOE_BASE)
        # 平面より前の足の上端: 足裏から上へたどり、平面より前の点が途切れる所（足の甲と、さらに上で前にある
        # 何か、たとえばお腹との間のすき間）。そこで足を水平に切ってはならない。さもないと、つま先と残りがそれぞれ
        # 開いた辺を持ち、決してつながらない。
        mine = np.array([face_side(p, hip_mid, lateral) == side for p in co])
        h = np.sort(((co - ankle) @ up)[mine & (((co - z.plane.co) @ z.plane.no) >= 0)])
        gap = ahead * 0.15
        top = 0.0
        if len(h):
            jumps = np.nonzero(np.diff(h) > gap)[0]
            # 上に余裕を持たせて（つま先の SDF 表面は体の部分より高く立つ）、次のまとまりには届かない所まで
            top = float(h[jumps[0]] if len(jumps) else h[-1]) + ahead * 0.5
            if len(jumps):
                top = min(top, float(h[jumps[0] + 1]) - gap * 0.2)
            top = max(0.0, top)
        z.top = top
        zones.append(z)
    return zones


def split_by_zones(
    bm: bmesh.types.BMesh, zones: list[ToeZone], hip_mid: np.ndarray, lateral: np.ndarray, keep_toes: bool
):
    """ゾーンの平面で切る。つま先の部分（keep_toes）か、それ以外のすべてのどちらかを残す。"""
    for z in zones:
        cut(bm, z.plane.co, z.plane.no, lambda c, z=z: z.near(c, hip_mid, lateral))
    drop = []
    for f in bm.faces:
        c = np.array(f.calc_center_median())
        in_toes = any(z.holds(c, hip_mid, lateral) for z in zones)
        if in_toes != keep_toes:
            drop.append(f)
    bmesh.ops.delete(bm, geom=drop, context="FACES_ONLY")
    remove_loose(bm)


def near_zones(
    surf: bmesh.types.BMesh, zones: list[ToeZone], hip_mid: np.ndarray, lateral: np.ndarray
) -> bmesh.types.BMesh:
    """`surf` のうち、つま先のゾーンの周り（平面の少し後ろから）の面を持つ新しい bmesh。SDF 表面は衣装全体を
    覆っていて、まるごと複製して切るには大きすぎる。"""
    surf.verts.index_update()
    co = np.array([v.co[:] for v in surf.verts])
    keep = np.zeros(len(co), dtype=bool)
    for z in zones:
        margin = float(np.linalg.norm(z.plane.co - z.ankle)) * 0.2
        side = np.sign((co - hip_mid) @ lateral) == z.side
        keep |= side & ((co - z.ankle) @ z.up <= z.top + margin) & (((co - z.plane.co) @ z.plane.no) >= -margin)
    out = bmesh.new()
    made = {}
    for f in surf.faces:
        idx = [v.index for v in f.verts]
        if not keep[idx].all():
            continue
        vs = []
        for i in idx:
            if i not in made:
                made[i] = out.verts.new(Vector(co[i]))
            vs.append(made[i])
        try:
            out.faces.new(vs)
        except ValueError:
            pass
    return out


def boundary_loop(bm: bmesh.types.BMesh, pick: Callable[[bmesh.types.BMVert], bool]) -> list[bmesh.types.BMVert] | None:
    """頂点が `pick` を満たす境界辺の閉じたループを、順に並べて返す。無ければ None。"""
    edges = [e for e in bm.edges if e.is_boundary and pick(e.verts[0]) and pick(e.verts[1])]
    if len(edges) < 3:
        return None
    nb = {}
    for e in edges:
        a, b = e.verts
        nb.setdefault(a, []).append(b)
        nb.setdefault(b, []).append(a)
    ends = [v for v, n in nb.items() if len(n) == 1]
    if len(ends) == 2 and all(len(n) <= 2 for n in nb.values()):
        # 小さな切れ目のある鎖（表面を切って簡略化した所で面が 1 つ失われた）: 閉じる
        a, b = ends
        if (a.co - b.co).length < max(e.calc_length() for e in edges) * 3:
            nb[a].append(b)
            nb[b].append(a)
    if any(len(v) != 2 for v in nb.values()):
        return None
    start = edges[0].verts[0]
    loop, prev, cur = [start], None, start
    while True:
        nxt = nb[cur][0] if nb[cur][0] is not prev else nb[cur][1]
        if nxt is start:
            break
        loop.append(nxt)
        prev, cur = cur, nxt
        if len(loop) > len(nb):
            return None
    return loop if len(loop) == len(nb) else None


def zip_loops(
    bm: bmesh.types.BMesh, a: list[bmesh.types.BMVert], b: list[bmesh.types.BMVert], normal: np.ndarray
) -> int:
    """同じ平面にある 2 つの閉じたループ（一方が他方を囲む）の間を三角形で埋める。"""
    center = np.mean([np.array(v.co) for v in a], axis=0)
    u = normalize(np.cross(normal, [0.0, 0.0, 1.0] if abs(normal[2]) < 0.9 else [1.0, 0.0, 0.0]))
    w = np.cross(normal, u)

    def ordered(loop):
        ang = [float(np.arctan2((np.array(v.co) - center) @ w, (np.array(v.co) - center) @ u)) for v in loop]
        if np.sum(np.sign(np.diff(np.unwrap(ang)))) < 0:  # ループに沿って角度が増える向きにする
            loop, ang = loop[::-1], ang[::-1]
        k = int(np.argmin(ang))
        loop, ang = loop[k:] + loop[:k], ang[k:] + ang[:k]
        ang = np.unwrap(ang)
        return loop + [loop[0]], list(ang) + [ang[0] + 2 * np.pi]

    la, aa = ordered(a)
    lb, ab = ordered(b)
    i = j = 0
    made = 0
    while i < len(la) - 1 or j < len(lb) - 1:
        if j >= len(lb) - 1 or (i < len(la) - 1 and aa[i + 1] <= ab[j + 1]):
            tri = (la[i], la[i + 1], lb[j])
            i += 1
        else:
            tri = (la[i], lb[j + 1], lb[j])
            j += 1
        if len(set(tri)) == 3:
            try:
                bm.faces.new(tri)
                made += 1
            except ValueError:
                pass
    return made


def closed_feet(
    wpos: np.ndarray, wtris: np.ndarray, tri_ids: np.ndarray, lm: dict[str, np.ndarray], up: np.ndarray
) -> bpy.types.Object | None:
    """すねの中ほどより下の体。切り口をふさぎ、SDF のためにきちんとした内側を持たせる。"""
    bm, _ = body_bmesh(wpos, wtris, tri_ids)
    knee_h = min(float(lm["lowerLegL"] @ up), float(lm["lowerLegR"] @ up))
    ank_h = max(float(lm["footL"] @ up), float(lm["footR"] @ up))
    cut_h = ank_h + (knee_h - ank_h) * 0.2  # （ここから作るのはつま先だけ: 短い部分にすると SDF が小さく済む）
    bmesh.ops.bisect_plane(
        bm,
        geom=bm.verts[:] + bm.edges[:] + bm.faces[:],
        dist=1e-9,
        plane_co=Vector(up * cut_h),
        plane_no=Vector(up),
        clear_outer=True,
    )
    remove_loose(bm)
    if not bm.faces:
        bm.free()
        return None
    bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary], sides=0)
    obj = new_object("feet", bm)
    bm.free()
    return obj


class BigToeSplit:
    """
    片足の、親指とほかの指の仕切り（足袋型）。指の付け根の平面（ToeZone の平面）より前で、最も近い指の点が親指以外の
    指の点である所を、外側へ開く側とする（親指を内側へ開くと、両足の親指が近づいてつながる）。`pivot` は付け根の
    平面の上の、親指と人差し指の中ほど。
    """

    def __init__(
        self,
        side: int,
        ankle: np.ndarray,
        fwd: np.ndarray,
        up: np.ndarray,
        med: np.ndarray,
        ahead: float,
        pivot: np.ndarray,
        toe_pts: np.ndarray,
        big: np.ndarray,
        ramp: float,
        hip_mid: np.ndarray,
        lateral: np.ndarray,
    ):
        self.side, self.ankle, self.fwd, self.up, self.ahead, self.pivot = side, ankle, fwd, up, ahead, pivot
        self.big, self.ramp, self.hip_mid, self.lateral = big, ramp, hip_mid, lateral
        self.tree = self.kdtree(toe_pts)
        # （正の角度で、前向きの点が外側へ回る向き）
        self.sgn = 1.0 if float(np.cross(up, fwd) @ med) < 0 else -1.0
        self.toe_pts, self.opened = toe_pts, {}

    @staticmethod
    def kdtree(pts: np.ndarray) -> KDTree:
        tree = KDTree(len(pts))
        for i, q in enumerate(pts):
            tree.insert(Vector(q), i)
        tree.balance()
        return tree

    def lateral_toes(self, p: np.ndarray, tree: KDTree | None = None) -> np.ndarray:
        """この足の、付け根の平面より前で足首より下の、親指以外の指の側の点か（`tree` は指の点の木。既定は開く前）。"""
        p = np.atleast_2d(p)
        tree = tree or self.tree
        ok = (((p - self.ankle) @ self.fwd) / self.ahead > TOE_BASE) & (((p - self.ankle) @ self.up) < 0)
        ok &= np.array([face_side(q, self.hip_mid, self.lateral) == self.side for q in p])
        for i in np.nonzero(ok)[0]:
            ok[i] = not self.big[tree.find(Vector(p[i]))[1]]
        return ok

    def big_toe(self, p: np.ndarray) -> bool:
        """この足の、付け根の平面より前で足首より下の、親指の側の点か。"""
        ahead = float((p - self.ankle) @ self.fwd) / self.ahead > TOE_BASE and float((p - self.ankle) @ self.up) < 0
        return (
            ahead
            and face_side(p, self.hip_mid, self.lateral) == self.side
            and bool(self.big[self.tree.find(Vector(p))[1]])
        )

    def rise(self, p: np.ndarray) -> np.ndarray:
        """付け根の平面から指が分かれ始める所（`ramp`）まで、0 から 1 へなめらかに。"""
        w = np.clip((((p - self.ankle) @ self.fwd) / self.ahead - TOE_BASE) / (self.ramp - TOE_BASE), 0.0, 1.0)
        return w * w * (3.0 - 2.0 * w)

    def weight(self, p: np.ndarray) -> np.ndarray:
        """開く量の割合: 親指以外の指の側で rise、ほかは 0。"""
        p = np.atleast_2d(p)
        return self.rise(p) * self.lateral_toes(p)

    def turn(self, p: np.ndarray, a: np.ndarray) -> np.ndarray:
        """点を、`pivot` を通る上向きの軸の周りに角度 `a`（点ごと）だけ回す。"""
        q = p - self.pivot
        h = (q @ self.up)[:, None] * self.up
        r = q - h
        c, s = np.cos(a)[:, None], np.sin(a)[:, None]
        return self.pivot + h + r * c + np.cross(self.up, r) * s

    def spread(self, p: np.ndarray, angle: float) -> np.ndarray:
        """親指以外の指の側の点を、`pivot` を通る上向きの軸の周りに `angle`（正 = 外側へ開く）× 割合だけ回す。"""
        p = np.atleast_2d(p)
        return self.turn(p, angle * self.sgn * self.weight(p))

    def unspread(self, p: np.ndarray, angle: float) -> np.ndarray:
        """
        spread の逆: 開く側かは開いた後の指の点で見分け（開いた後は親指と人差し指の間が広いので確か）、割合は戻した
        位置で決まるので数回の反復で求める。
        """
        p = np.atleast_2d(p)
        if angle not in self.opened:
            self.opened[angle] = self.kdtree(self.spread(self.toe_pts, angle))
        moving = self.lateral_toes(p, self.opened[angle])
        x = p
        for _ in range(4):
            x = self.turn(p, -angle * self.sgn * self.rise(x) * moving)
        return x


def big_toe_splits(
    co: np.ndarray,
    groups: tuple[np.ndarray, np.ndarray],
    lm: dict[str, np.ndarray],
    up: np.ndarray,
    lateral: np.ndarray,
    hip_mid: np.ndarray,
) -> list[BigToeSplit]:
    """
    足ごとの親指の仕切り。`groups` は足の指ごとの体の点とその指の番号（骨のウェイト、または形で分けたもの）: 足の
    内側への座標の平均が最も大きい指を親指とする。指が 2 本以上に分かれていない足は分けない。
    """
    pts, labels = groups
    out = []
    for side in (1, -1):
        frame = foot_frame(co, lm, up, lateral, hip_mid, side)
        if frame is None or frame[2] <= 0 or not len(pts):
            continue
        ankle, fwd, ahead, _ = frame
        med = -side * lateral
        med = normalize(med - fwd * (med @ fwd) - up * (med @ up))
        mine = np.array([face_side(p, hip_mid, lateral) == side for p in pts]) & (((pts - ankle) @ up) < 0)
        mine &= ((pts - ankle) @ fwd) / ahead > TOE_BASE
        names = np.unique(labels[mine])
        if len(names) < 2:
            continue
        inward = {k: float(np.mean((pts[mine & (labels == k)] - ankle) @ med)) for k in names}
        order = sorted(names, key=lambda k: -inward[k])
        big_pts = pts[mine & (labels == order[0])]
        # （開く割合が急に変わると、外側の指の付け根の面が後ろへ折れ返る: 到達の 15% 以上かけて開く）
        ramp = float(np.clip(np.min((big_pts - ankle) @ fwd) / ahead, TOE_BASE + 0.15, TOE_BASE + 0.3))
        pivot = ankle + fwd * ahead * TOE_BASE + med * (inward[order[0]] + inward[order[1]]) * 0.5
        out.append(
            BigToeSplit(
                side, ankle, fwd, up, med, ahead, pivot, pts[mine], labels[mine] == order[0], ramp, hip_mid, lateral
            )
        )
    return out


def toe_gap(
    co: np.ndarray,
    nrm: np.ndarray,
    ankle: np.ndarray,
    fwd: np.ndarray,
    up: np.ndarray,
    med: np.ndarray,
    ahead: float,
    mine: Callable[[np.ndarray], np.ndarray],
) -> float | None:
    """
    親指と人差し指の間の隙間の中心の、内側への座標: この足の足首より下で、足首から前への到達の 85% より前の、上（足の甲の
    側）を向いた体の点を内外に並べ、内側の半分にある点の無いいちばん広い隙間の真ん中（幅の 3% より狭ければ、指が付いて
    いるとして None。足の裏の側は指の先までつながった作りが多いので見ない）。
    指の塊の平均の中間は、幅の広い親指の縁に乗ることがあるので使わない。
    """
    low = co[mine(co) & (((co - ankle) @ up) < 0) & (((co - ankle) @ fwd) / ahead > 0.85) & ((nrm @ up) > 0.3)]
    if len(low) < 10:
        return None
    m = np.sort((low - ankle) @ med)
    gaps = np.diff(m)
    inner = m[:-1] > (m[0] + m[-1]) * 0.5
    if not inner.any():
        return None
    i = int(np.argmax(np.where(inner, gaps, -1.0)))
    return float(m[i] + m[i + 1]) * 0.5 if gaps[i] > (m[-1] - m[0]) * 0.03 else None


def toe_web(
    co: np.ndarray,
    nrm: np.ndarray,
    tris: np.ndarray,
    ankle: np.ndarray,
    fwd: np.ndarray,
    up: np.ndarray,
    med: np.ndarray,
    ahead: float,
    mid: float,
    mine: Callable[[np.ndarray], np.ndarray],
) -> float | None:
    """
    親指と人差し指の股の、足首から前への到達に対する割合: 2 本の指の間の中心線（内側への座標が `mid`）を横切る、足首より下の
    この足の、上（足の甲の側）を向いた体の三角形のうち、最も前の頂点。股より前は指の間に隙間があり、体の面がこの線を横切らない
    （足の裏の側は指の先までつながった作りが多いので、上を向いた面だけを見る）。
    """
    p = co[tris]
    c = p.mean(axis=1)
    keep = mine(c) & (((c - ankle) @ up) < 0) & ((nrm[tris].mean(axis=1) @ up) > 0.3)
    m = (p[keep] - ankle) @ med
    cross = (m.min(axis=1) <= mid) & (m.max(axis=1) >= mid)
    if not cross.any():
        return None
    return float((((p[keep][cross] - ankle) @ fwd).max())) / ahead


def toecap_cut(
    co: np.ndarray,
    groups: tuple[np.ndarray, np.ndarray],
    lm: dict[str, np.ndarray],
    up: np.ndarray,
    lateral: np.ndarray,
    hip_mid: np.ndarray,
    tris: np.ndarray,
    nrm: np.ndarray,
) -> tuple[list[Field], list[np.ndarray]]:
    """
    親指だけを覆うサック: 足ごとに、足首より下・親指の付け根より前で、ほかの指より親指の点に近い所を残す（後の
    2 つの切り取りは足首より前の長さの 4% の半径で丸めて合わせる）。親指は big_toe_splits と同じく、内側への座標の平均が最も大きい
    指の塊。指が分かれていない足は、指先の内外の幅の内側 35% を親指とする。（切る形, 残す側の種）を返す。
    """
    pts, labels = groups
    fields, seeds = [], []
    for side in (1, -1):
        frame = foot_frame(co, lm, up, lateral, hip_mid, side)
        # （作れない足は、その側をすべて切る: 切る形が無い側は切られずに残る）
        fields.append(Field(lambda p: -1.0, side))
        if frame is None or frame[2] <= 0:
            continue
        ankle, fwd, ahead, _ = frame
        med = -side * lateral
        med = normalize(med - fwd * (med @ fwd) - up * (med @ up))

        def on_side(c, side=side):
            return np.array([face_side(q, hip_mid, lateral) == side for q in c], dtype=bool)

        mine = np.zeros(len(pts), dtype=bool)
        if len(pts):
            mine = np.array([face_side(p, hip_mid, lateral) == side for p in pts], dtype=bool)
            mine &= (((pts - ankle) @ up) < 0) & (((pts - ankle) @ fwd) / ahead > TOE_BASE)
        names = np.unique(labels[mine]) if len(pts) else []
        k = ahead * 0.04
        if len(names) >= 2:
            inward = {n: float(np.mean((pts[mine & (labels == n)] - ankle) @ med)) for n in names}
            order = sorted(names, key=lambda n: -inward[n])
            big_n = order[0]
            big_pts, rest = pts[mine & (labels == big_n)], pts[mine & (labels != big_n)]
            big_tip = float(np.max((big_pts - ankle) @ fwd)) / ahead
            # 付け根: 親指と人差し指の股が分かれば、その少し後ろ（指の骨のウェイトの塊は付け根より奥の足の甲まで届き、
            # Toes の骨も付け根に無いことがある）。指どうしが付いていて股が無い（親指の先の近くまで面が中心線を横切る）
            # なら、親指の塊の最も後ろを、指の付け根の平面より少し前から足首より前の長さの 70% までに収めた所
            # （形で分けた塊は指が 1 本ずつに分かれる所より先だけ）
            gap = toe_gap(co, nrm, ankle, fwd, up, med, ahead, on_side)
            web = toe_web(co, nrm, tris, ankle, fwd, up, med, ahead, gap, on_side) if gap is not None else None
            if web is not None and web < big_tip - CAP_WEB_DEPTH:
                base_at = float(np.clip(web - CAP_BEHIND_WEB, TOE_BASE + 0.05, big_tip - 0.05))
            else:
                base_at = float(np.clip(float(np.min((big_pts - ankle) @ fwd)) / ahead, TOE_BASE + 0.05, CAP_BASE))
            log(f"toecap side {side}: toe groups {len(names)}, web {web}, mouth {base_at:.3f} of the way ahead")
            base = ankle + fwd * ahead * base_at
            big_tree, rest_tree = BigToeSplit.kdtree(big_pts), BigToeSplit.kdtree(rest)

            def fn(p, base=base, fwd=fwd, big_tree=big_tree, rest_tree=rest_tree, k=k, ankle=ankle):
                q = Vector(p)
                nearer = (rest_tree.find(q)[2] - big_tree.find(q)[2]) * 0.5
                # （足首より上は切る: 前にある胸なども親指の点の方が近いことがある）
                return min(smooth_min(float((p - base) @ fwd), nearer, k), float((ankle - p) @ up))

            tip = big_pts[int(np.argmax((big_pts - ankle) @ fwd))]
        else:
            low = co[
                np.array([face_side(p, hip_mid, lateral) == side for p in co])
                & (((co - ankle) @ up) < 0)
                & (((co - ankle) @ fwd) / ahead > 0.8)
            ]
            if len(low) < 10:
                continue
            m = (low - ankle) @ med
            split = float(m.max() - (m.max() - m.min()) * 0.35)
            base_at = CAP_BASE
            # 指が離れている足（股が分かる足）では、親指と人差し指の間の隙間で分け、股の少し後ろに口を置く
            gap = toe_gap(co, nrm, ankle, fwd, up, med, ahead, on_side)
            web = toe_web(co, nrm, tris, ankle, fwd, up, med, ahead, gap, on_side) if gap is not None else None
            if web is not None and web < 1.0 - CAP_WEB_DEPTH:
                split = gap
                base_at = float(np.clip(web - CAP_BEHIND_WEB, TOE_BASE + 0.05, 0.9))
            log(f"toecap side {side}: no toe groups, split {split:.4f}, mouth {base_at:.3f} of the way ahead")
            base = ankle + fwd * ahead * base_at

            def fn(p, base=base, fwd=fwd, ankle=ankle, med=med, split=split, k=k):
                cut = smooth_min(float((p - base) @ fwd), float((p - ankle) @ med) - split, k)
                return min(cut, float((ankle - p) @ up))

            tip = low[int(np.argmax(m + ((low - ankle) @ fwd) * 0.5))]
        fields[-1] = Field(fn, side)
        seeds.append(tip)
    return fields, seeds
