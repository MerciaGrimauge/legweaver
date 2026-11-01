# SPDX-FileCopyrightText: 2026 MerciaGrimauge
# SPDX-License-Identifier: GPL-3.0-or-later

# LegWeaver の Blender 側: 腕の衣装（手袋・アームカバー）。腕の連なりと切断平面。

from typing import Any, Callable
import numpy as np
from lw_common import (
    Field,
    Plane,
    chain_param,
    chain_point,
    face_side,
    girth_stretch,
    joint_bump,
    left_foot_forward,
    normalize,
    side_key,
    smooth_max,
    smooth_min,
    stretch_to_mask,
)

ARM_TYPES = ("gloves", "armcovers")


def arm_chain(lm: dict[str, np.ndarray], side: int) -> list[np.ndarray]:
    """肩関節（0）、肘（1）、手首（2）、アバターにあれば中指の付け根（3）。"""
    s = side_key(side)
    pts = [lm["upperArm" + s], lm["lowerArm" + s], lm["hand" + s]]
    if lm.get("middle" + s) is not None:
        pts.append(lm["middle" + s])
    return pts


def hand_frame(
    co: np.ndarray, tris: np.ndarray, lm: dict[str, np.ndarray], lateral: np.ndarray, hip_mid: np.ndarray, side: int
) -> tuple[np.ndarray, np.ndarray, float]:
    """
    手首より先の点から求めた（手首、指先へ向かう方向、手首から先の手の長さ）。手の長さは、手首より先の同じ側の点の
    うち、中指の付け根（無ければ手首）に一番近い点と三角形でつながる物だけで測る: 腕を下ろした体では手首より先に
    同じ側の脚もあり、それを数えると手の長さが脚の先まで伸びる。
    """
    s = side_key(side)
    wrist = lm["hand" + s]
    d = normalize(wrist - lm["lowerArm" + s])
    ahead = (co - wrist) @ d
    mine = np.array([face_side(p, hip_mid, lateral) == side for p in co]) & (ahead > 0)
    reach = 0.0
    if mine.any():
        # 手首より先の点どうしの辺だけでつながりを広げる
        e = np.concatenate([tris[:, [0, 1]], tris[:, [1, 2]], tris[:, [2, 0]]])
        e = e[mine[e[:, 0]] & mine[e[:, 1]]]
        nbr: list[list[int]] = [[] for _ in range(len(co))]
        for a, b in e.tolist():
            nbr[a].append(b)
            nbr[b].append(a)
        target = lm.get("middle" + s)
        target = wrist if target is None else target
        idx = np.nonzero(mine)[0]
        start = int(idx[np.argmin(np.linalg.norm(co[idx] - target, axis=1))])
        seen = {start}
        todo = [start]
        while todo:
            for b in nbr[todo.pop()]:
                if b not in seen:
                    seen.add(b)
                    todo.append(b)
        reach = float(ahead[list(seen)].max())
    if reach <= 0:
        reach = float(np.linalg.norm(wrist - lm["lowerArm" + s])) * 0.7
    return wrist, d, reach


def thumb_ellipsoid(lm: dict[str, np.ndarray], s: str, co: np.ndarray) -> Callable[[np.ndarray], float] | None:
    """
    親指を包む楕円体の、およその符号付き距離（外が正）。軸は親指の付け根→中間の関節（長さ L）の向きで、付け根から
    0.25 L の所から親指の先の少し先まで。骨の長さに対する親指の太さはアバターでまちまちなので、太さと先は体の点で
    測る: 太さは付け根から 0.6〜1.0 L の、軸から 1.2 L 以内の点の距離の中央値、楕円体の半径はその 1.6 倍
    （0.55 L 以上）。先は、軸から太さの 1.2 倍以内の点の、軸に沿った最大に 0.3 L を足した所（2.55 L 以上）。
    親指の骨が無ければ None。
    """
    if lm.get("thumb" + s) is None or lm.get("thumbTip" + s) is None:
        return None
    t0, t1 = lm["thumb" + s], lm["thumbTip" + s]
    length = float(np.linalg.norm(t1 - t0))
    if length <= 0:
        return None
    u = normalize(t1 - t0)
    q = co - t0
    x = q @ u
    y = np.linalg.norm(q - np.outer(x, u), axis=1)
    win = (x > 0.6 * length) & (x < 1.0 * length) & (y < 1.2 * length)
    thick = float(np.median(y[win])) if win.sum() >= 8 else 0.5 * length
    r = max(0.55 * length, thick * 1.6)
    # （付け根から 6 L まで: 腕を下ろした体では、軸の先に太ももがある）
    along = x[(x > 0) & (x < 6.0 * length) & (y < thick * 1.2)]
    end = max(2.55 * length, (float(along.max()) if len(along) else 0.0) + 0.3 * length)
    start = 0.25 * length
    c = t0 + u * (start + end) * 0.5
    a = (end - start) * 0.5

    def fn(p: np.ndarray) -> float:
        qq = p - c
        xx = float(qq @ u)
        yy = float(np.linalg.norm(qq - u * xx))
        return (float(np.hypot(xx / a, yy / r)) - 1.0) * r

    return fn


def knuckle_cut(
    lm: dict[str, np.ndarray], knuckle: np.ndarray, d: np.ndarray, reach: float, s: str, side: int, co: np.ndarray
) -> list[Field]:
    """
    指の付け根の手前で切り（knuckle を通り d に直交する平面より先を切る）、親指は楕円形の穴から出す。2 つの切り取りは
    手の長さの 15% の半径で丸めて合わせる（平面が親指の付け根を斜めに横切って、親指の股に尖った布が残らない）。
    """
    thumb = thumb_ellipsoid(lm, s, co)
    k = reach * 0.15

    def fn(p: np.ndarray) -> float:
        cut = float((p - knuckle) @ d)
        return -(cut if thumb is None else smooth_max(cut, -thumb(p), k))

    return [Field(fn, side)]


def drawing_cut(
    lm: dict[str, np.ndarray], d: np.ndarray, reach: float, s: str, side: int, co: np.ndarray
) -> list[Field]:
    """
    絵描き用: 親指を楕円形の穴から出し、人差し指と中指を付け根で切る。指の側の切り取りは、2 本の付け根の中点を
    通り中指の向きに直交する平面より先、かつ中指と薬指の間の仕切りより人差し指側（薬指と小指は残す）で、その角は
    付け根の間の半分、親指の穴とのつなぎ目は手の長さの 15% の半径で丸める（段や尖りを作らない）。人差し指か中指の付け根が無ければ親指の穴だけ。
    """
    thumb = thumb_ellipsoid(lm, s, co)
    index, middle = lm.get("index" + s), lm.get("middle" + s)
    if index is None or middle is None:
        return [] if thumb is None else [Field(thumb, side)]
    tip = lm.get("middleTip" + s)
    fd = normalize(tip - middle) if tip is not None else d
    across = index - middle
    width = float(np.linalg.norm(across))
    across = normalize(across - fd * (across @ fd))
    base = (index + middle) * 0.5
    sep = middle - (index - middle) * 0.5
    k = width * 0.5

    def fn(p: np.ndarray) -> float:
        # 切り取る量（正なら切る）: 指の側と親指の穴の、丸めた和
        fingers = smooth_min(float((p - base) @ fd), float((p - sep) @ across), k)
        cut = fingers if thumb is None else smooth_max(fingers, -thumb(p), reach * 0.15)
        return -cut

    return [Field(fn, side)]


def arm_planes(
    g: dict[str, Any],
    lm: dict[str, np.ndarray],
    co: np.ndarray,
    tris: np.ndarray,
    lateral: np.ndarray,
    hip_mid: np.ndarray,
) -> tuple[list[Plane], list[np.ndarray]]:
    """
    腕の衣装の切断平面と種を、腕ごとに返す（腕の連なり: 0 肩関節、1 肘、2 手首）:
    - 手袋: 腕に沿って `gloveTop` より先をすべて残す。指なしのものは指の付け根の関節の手前で終わり、親指を穴から
      出す。絵描き用のものは親指を穴から出し、人差し指と中指を付け根で切る。
    - アームカバー: `armTop` から手首までを残すか、親指用の穴を開けて指の付け根の関節の手前まで残す。
    """
    kind = g["type"]
    planes, seeds = [], []
    for side in (1, -1):
        s = side_key(side)
        if lm.get("upperArm" + s) is None or lm.get("hand" + s) is None:
            continue
        chain = arm_chain(lm, side)
        wrist, d, reach = hand_frame(co, tris, lm, lateral, hip_mid, side)
        # 手の甲の縁は指の付け根の関節の手前で止める: 関節の上だと、そこの肌（一部は指に動かされる）が
        # 拳を握ったときに突き出てしまう
        knuckle = lm["middle" + s] - d * reach * 0.18 if lm.get("middle" + s) is not None else wrist + d * reach * 0.3
        if kind == "gloves":
            # （Unity は常にこれを送る: GarmentSettings.GloveTopAt。手首の長さは 1.92）
            top = float(g.get("gloveTop", 1.92))
            co_t, no_t = chain_point(chain, top)
            planes.append(Plane(co_t, no_t, False, side))
            seeds.append(wrist + d * reach * 0.2)
            if g.get("fingerless", False):
                # 指なし: 手の長さに比べて手のひらが短い体では、指の付け根の手前の切り口が手首の切り口より手前に
                # 来て、何も残らない。その時だけ、手首の切り口から中指の付け根までの 4 分の 1 の所へ進める
                if lm.get("middle" + s) is not None:
                    start = float((co_t - wrist) @ d)
                    end = float((lm["middle" + s] - wrist) @ d)
                    at = float((knuckle - wrist) @ d)
                    if end > start and at < start + (end - start) * 0.25:
                        knuckle = wrist + d * (start + (end - start) * 0.25)
                planes += knuckle_cut(lm, knuckle, d, reach, s, side, co)
            elif g.get("drawing", False):
                planes += drawing_cut(lm, d, reach, s, side, co)
        else:
            co_t, no_t = chain_point(chain, float(g.get("armTop", 0.6)))
            planes.append(Plane(co_t, no_t, False, side))
            if g.get("armToHand", False):
                planes += knuckle_cut(lm, knuckle, d, reach, s, side, co)
            else:
                co_b, no_b = chain_point(chain, 1.97)
                planes.append(Plane(co_b, no_b, True, side))
            seeds.append(chain_point(chain, 1.5)[0])
    return planes, seeds


def fingertip_zone(pts: np.ndarray, lm: dict[str, np.ndarray], lateral: np.ndarray, hip_mid: np.ndarray) -> np.ndarray:
    """
    指先の区間にある点の、指ごとの番号（区間の外は −1）。布が爪の溝に入り込まず、膜のように渡る所。指は中指の
    付け根から先の関節までの長さ L で測り、付け根から 0.55 L より先。親指は中間の関節より先。指の番号は、指を
    またぐ向き（人差し指 − 中指の付け根）の位置を指の間隔で割って丸めたもの（人差し指 1、中指 0、薬指 −1、
    小指 −2。手のひら側の指の股はこの区間までつながっていることがあり、つながりだけでは指を分けられない）。親指は、
    中間の関節より先で、親指の軸から親指の区間長の 1.3 倍以内（長さの 3 倍まで）の、ほかの指でない所。
    指の骨が無い腕では無し。
    """
    label = np.full(len(pts), -1)
    sides = np.array([face_side(p, hip_mid, lateral) for p in pts])
    for n, side in enumerate((1, -1)):
        s = side_key(side)
        mine = sides == side
        mid, tip, index = lm.get("middle" + s), lm.get("middleTip" + s), lm.get("index" + s)
        if mid is not None and tip is not None:
            fd = normalize(tip - mid)
            inside = mine & ((pts - mid) @ fd > float(np.linalg.norm(tip - mid)) * 0.55)
            finger = np.zeros(len(pts), dtype=int)
            if index is not None:
                across = index - mid
                across = across - fd * (across @ fd)
                spacing = float(np.linalg.norm(across))
                if spacing > 0:
                    finger = np.clip(np.round((pts - mid) @ (across / spacing) / spacing), -3, 2).astype(int)
            label[inside] = n * 10 + 5 + finger[inside]
        t0, t1 = lm.get("thumb" + s), lm.get("thumbTip" + s)
        if t0 is not None and t1 is not None:
            # （親指の軸の近くだけ: 中間の関節より先の半空間には、ほかの指の先も入る）
            length = float(np.linalg.norm(t1 - t0))
            u = normalize(t1 - t0)
            q = pts - t1
            along = q @ u
            radial = np.linalg.norm(q - np.outer(along, u), axis=1)
            thumb = mine & (label < 0) & (along > 0) & (along < length * 3.0) & (radial < length * 1.3)
            label[thumb] = n * 10 + 9
    return label


def arm_stretch_mask(
    co: np.ndarray,
    normals: np.ndarray,
    neighbors: list[list[np.int64]],
    lm: dict[str, np.ndarray],
    up: np.ndarray,
    lateral: np.ndarray,
    hip_mid: np.ndarray,
    strength: float,
) -> np.ndarray:
    """
    腕用の stretch_mask: 頂点ごとに 1/s を返す（s = 面積の伸び）。s は、覆う部分のうち最も細い所に対する腕の太さに
    応じて大きくなる（前腕と上腕は手首より編み地を伸ばす）。肘の裏（曲げると伸びる）と、手の甲の指の付け根の関節
    の上（拳で伸びる）でも大きくなる。手首より先の手には太さの項がない（手の幅は太さではない）。
    """
    n = len(co)
    s = np.ones(n)
    if strength <= 0:
        return s
    fwd = left_foot_forward(lm, up, lateral)
    sides = np.array([face_side(p, hip_mid, lateral) for p in co])
    for side in (1, -1):
        sx = side_key(side)
        if lm.get("upperArm" + sx) is None or lm.get("hand" + sx) is None:
            continue
        chain = arm_chain(lm, side)
        idx = np.nonzero(sides == side)[0]
        if len(idx) == 0:
            continue
        tr = np.array([chain_param(chain, co[i]) for i in idx])
        t, r = tr[:, 0], tr[:, 1]
        # （腕に沿った点が少なすぎて太さが分からない: 関節の項だけ）
        sc = girth_stretch(t, r)
        if sc is None:
            sc = np.ones(len(idx))
        # 肘の裏: 前腕は前へ曲がり、その裏の肌が伸びる
        elbow = joint_bump(t, 1.0, 0.15) * np.clip(normals[idx] @ -fwd, 0.0, 1.0)
        # 手の甲の指の付け根の関節（基本姿勢では手のひらが下を向く）
        knuckles = joint_bump(t, 3.0, 0.12) * np.clip(normals[idx] @ up, 0.0, 1.0) if len(chain) > 3 else 0.0
        s[idx] = sc + 0.35 * elbow + 0.3 * knuckles
    return stretch_to_mask(s, neighbors, strength)
