# SPDX-FileCopyrightText: 2026 MerciaGrimauge
# SPDX-License-Identifier: GPL-3.0-or-later

# LegWeaver の Blender 側: 本物のタイツの部位（regions）、脚の座標、伸びのマスク。レッグラップの切り方。

from typing import Any, Callable
import bmesh
import numpy as np
from lw_common import (
    Field,
    Plane,
    body_forward,
    chain_for,
    chain_param,
    chain_point,
    cut,
    face_side,
    foot_frame,
    girth_stretch,
    hip_width,
    joint_bump,
    leg_length,
    log,
    normalize,
    side_key,
    stretch_to_mask,
)

# どのジョブも設定しない部位の大きさ。脚の長さに対する割合（structure_regions, crotch_parts）
BAND_FRAC = 0.035  # ウエストのゴム（と靴下の上端のゴム）。縁から下へ
RUN_GUARD_FRAC = 0.03  # 伝線止めの幅
RUN_GUARD_AT = 0.2  # 脚に沿った伝線止めの位置（0 股関節、1 膝）
SEAM_FRAC = 0.006  # センターシームの幅


def stretch_mask(
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
    編み地が伸びた所でどれだけ多く光が透けるか: 頂点ごとに 1/s を返す（s = 面積の伸び）。
    s は、同じ脚の覆う部分のうち最も細い所に対する脚の周囲の長さに応じて大きくなり（太ももとふくらはぎは足首より
    編み地を伸ばす）、膝の前でも大きくなる。骨盤と足は s = 1 のまま（本物のタイツで補強されたパンティ部とつま先）。
    """
    n = len(co)
    s = np.ones(n)
    if strength <= 0:
        return s
    sides = np.array([face_side(p, hip_mid, lateral) for p in co])
    for side in (1, -1):
        chain = chain_for(lm, side)
        idx = np.nonzero(sides == side)[0]
        if len(idx) == 0:
            continue
        tr = np.array([chain_param(chain, co[i]) for i in idx])
        t, r = tr[:, 0], tr[:, 1]
        # （足: 周囲の長さの項はない。足首より上で強まり、股関節より下で弱まる）
        sc = girth_stretch(t, r)
        if sc is None:
            continue
        # 膝の前
        toes = lm.get("toes" + side_key(side))
        ank = chain[2]
        fwd = (toes - ank) if toes is not None else np.cross(up, lateral)
        fwd = normalize(fwd - up * (fwd @ up))
        knee = joint_bump(t, 1.0, 0.15) * np.clip(normals[idx] @ fwd, 0.0, 1.0)
        s[idx] = sc + 0.35 * knee
    return stretch_to_mask(s, neighbors, strength)


# 本物のタイツで専用のマテリアルで描く部位（部位の番号。出力の三角形ごとに書く）。読み取る側の GarmentOptions.cs の
# GarmentRegion と同じ順: 両者をそろえておく。
REGION_LEG, REGION_PANTY, REGION_BAND, REGION_REINFORCE, REGION_UPPER, REGION_SOCK_BAND = 0, 1, 2, 3, 4, 5
REGION_SEAM, REGION_GUSSET = 6, 7
REGION_COUNT = 8


def crotch_point(
    co: np.ndarray, lm: dict[str, np.ndarray], up: np.ndarray, lateral: np.ndarray, hip_mid: np.ndarray, fwd: np.ndarray
) -> np.ndarray | None:
    """体の正中線付近（脚の間）で衣装の最も低い点。無ければ None。"""
    rel = co - hip_mid
    leg_len = leg_length(lm)
    mid = (np.abs(rel @ lateral) < leg_len * 0.03) & (np.abs(rel @ fwd) < leg_len * 0.25) & (rel @ up < 0)
    if not mid.any():
        return None
    return co[mid][np.argmin(rel[mid] @ up)]


def crotch_parts(
    bm: bmesh.types.BMesh,
    g: dict[str, Any],
    lm: dict[str, np.ndarray],
    up: np.ndarray,
    lateral: np.ndarray,
    hip_mid: np.ndarray,
    fwd: np.ndarray,
):
    """
    センターシーム（正中線に沿った細い帯。前・後ろまたは両方で、ウエストから股まで）と、股の下のひし形のマチ:
    それぞれの境目に沿って切る。面の中心がシームにあるかマチにあるか（どちらでもないか）を答える関数を返す。
    """
    seam = g.get("centerSeam", 0)  # 0 なし、1 前、2 後ろ、3 両方
    gusset = g.get("gusset", False)
    if g["type"] != "tights" or (seam == 0 and not gusset):
        return lambda c: None
    co = np.array([v.co for v in bm.verts])
    crotch = crotch_point(co, lm, up, lateral, hip_mid, fwd)
    if crotch is None:
        return lambda c: None
    leg_len = leg_length(lm)
    hip_w = hip_width(lm)
    crotch_h = float((crotch - hip_mid) @ up)
    crotch_f = float((crotch - hip_mid) @ fwd)
    torso = lambda c: abs(float((c - hip_mid) @ lateral)) < hip_w * 0.6

    # マチ: 股の周りのひし形。横に `a`、縦（前から後ろ）に `b`
    a = hip_w * 0.2 * float(g.get("gussetSize", 1.0))
    b = a * float(g.get("gussetAspect", 1.6))
    centre = hip_mid + up * crotch_h + fwd * crotch_f
    low = lambda c: float((c - hip_mid) @ up) < crotch_h + a * 1.5 and torso(c)
    diamond = []
    if gusset:
        for sx in (1, -1):
            for sf in (1, -1):
                no = normalize(lateral * (sx / a) + fwd * (sf / b))
                co_p = centre + lateral * (sx * a)
                diamond.append((co_p, no))
                cut(bm, co_p, no, low)

    def in_gusset(c):
        if not diamond or not low(c):
            return False
        return all(float((c - co_p) @ no) <= 0 for co_p, no in diamond)

    # シーム: |lateral| <= w / 2、股より上、前・後ろまたは両方
    w = leg_len * SEAM_FRAC
    want = lambda c: ((seam & 1) and float((c - hip_mid) @ fwd) > crotch_f) or (
        (seam & 2) and float((c - hip_mid) @ fwd) <= crotch_f
    )
    if seam:
        for sx in (1, -1):
            cut(bm, hip_mid + lateral * (sx * w * 0.5), lateral, lambda c: torso(c) and want(c))

    def which(c):
        if in_gusset(c):
            return REGION_GUSSET
        if (
            seam
            and want(c)
            and abs(float((c - hip_mid) @ lateral)) <= w * 0.5
            and float((c - hip_mid) @ up) >= crotch_h - leg_len * 0.05
        ):
            return REGION_SEAM
        return None

    return which


def heel_parts(
    co_all: np.ndarray, ankle: np.ndarray, fwd: np.ndarray, behind: float, ahead: float, up: np.ndarray, shape: str
) -> list[tuple[np.ndarray, np.ndarray]]:
    """
    かかとの補強を (点, 法線) の平面の並びとして返す。面がすべての平面の後ろ（<= 0）にあるとき補強に入る。
    "cup": 編んだかかとの袋の U 字（足首の少し下の水平な上端、土踏まずの手前の前端、その間の丸い角）。
    "y": 足裏へ向かって前に傾いた平面 1 枚（Y ヒールの線）。
    """
    if shape == "y":
        return [(ankle - fwd * behind * 0.10, normalize(fwd + up * 0.8))]
    h = (co_all - ankle) @ up
    sole = float(h.min()) if len(h) else -behind
    back = ankle - fwd * behind
    top = ankle + up * (sole * 0.35)
    front_d = (behind + ahead) * 0.30
    corner = back + fwd * front_d + up * float((top - back) @ up)
    r = (-sole) * 0.3
    return [(top, up), (back + fwd * front_d, fwd), (corner - normalize(up + fwd) * r, normalize(up + fwd))]


def toe_parts(
    toe_co: np.ndarray, fwd: np.ndarray, lateral: np.ndarray, ahead: float, shape: str
) -> list[tuple[np.ndarray, np.ndarray]]:
    """
    つま先の補強を平面の並びとして返す。面がいずれかの平面の前（>= 0）にあるとき補強に入る。"straight": 横切る
    線 1 本。"rounded": その線に、左右へ向けた 2 本を足したもの。トゥキャップのように、指の側面を包んで後ろへ
    回り込む。
    """
    planes = [(toe_co, fwd)]
    if shape == "rounded":
        for sx in (1, -1):
            n = normalize(fwd * np.cos(np.radians(40)) + lateral * (sx * np.sin(np.radians(40))))
            planes.append((toe_co - fwd * ahead * 0.12 + lateral * (sx * ahead * 0.12), n))
    return planes


def fake_tops(g: dict[str, Any], lm: dict[str, np.ndarray]) -> list[tuple[int, np.ndarray, np.ndarray]]:
    """フェイクサイハイ: 脚ごとに、上のストッキング部分と下の靴下部分の境目（点と、股関節へ向かう方向）。
    脚に沿って `fakeTop` の位置（0 股関節、1 膝）。"""
    if g["type"] != "tights" or not g.get("fakeThighHigh", False):
        return []
    return [(side,) + chain_point(chain_for(lm, side), float(g.get("fakeTop", 0.55))) for side in (1, -1)]


def structure_regions(
    bm: bmesh.types.BMesh,
    g: dict[str, Any],
    lm: dict[str, np.ndarray],
    up: np.ndarray,
    lateral: np.ndarray,
    hip_mid: np.ndarray,
    planes: list[Plane],
):
    """
    衣装を、本物のタイツで編み方の違う部位に分け、各面に印を付ける（レイヤー "lw_region"）:
    - ゴム: ウエストのゴム（とサイハイの上端のゴム）。上の縁から脚の長さの 3.5% 下まで。
    - 伝線止め（ゴム）: 太ももの上部、膝までの 20% の位置にある輪。幅は脚の長さの 3%。
    - パンティ部: ウエストのゴムと伝線止めの間のすべて（タイツのみ）。
    - フェイクサイハイ: 境目より上のストッキング部分（upper）と、その下の靴下の上端のゴム。
    - 補強: つま先（足首からつま先の先端までの 70% より前。まっすぐか、側面へ回り込む）とかかと（U 字のカップか、
      Y ヒールの斜めの線）。どちらも足首より下。
    - 股のセンターシームとひし形のマチ（crotch_parts）。
    切り口は平面に沿うので、境目は編まれたもののようにきれいな線になる。大きさは脚に合わせて変わる。
    """
    reg = bm.faces.layers.int.new("lw_region")
    leg_len = leg_length(lm)
    side_of = lambda c: face_side(c, hip_mid, lateral)
    tops = [pl for pl in planes if not pl.keep_positive] if g.get("waistband", True) else []
    band_w = leg_len * BAND_FRAC
    for pl in tops:
        cut(bm, pl.co - pl.no * band_w, pl.no, lambda c, pl=pl: pl.side in (0, side_of(c)))
    guards = []
    if g["type"] == "tights" and (g.get("pantyPart", True) or g.get("runGuard", True)):
        guard_w = leg_len * RUN_GUARD_FRAC if g.get("runGuard", True) else 0.0
        for side in (1, -1):
            co, no = chain_point(chain_for(lm, side), RUN_GUARD_AT)
            guards.append((side, co, no, guard_w))
            cut(bm, co, no, lambda c, side=side: side_of(c) == side)
            if guard_w > 0:
                cut(bm, co - no * guard_w, no, lambda c, side=side: side_of(c) == side)
    # フェイクサイハイ: 境目と、その下の靴下の上端のゴム
    fakes = fake_tops(g, lm)
    sock_band = band_w if g.get("fakeBand", True) else 0.0
    for side, co, no in fakes:
        cut(bm, co, no, lambda c, side=side: side_of(c) == side)
        if sock_band > 0:
            cut(bm, co - no * sock_band, no, lambda c, side=side: side_of(c) == side)
    fwd_body = body_forward(lm, up, lateral)
    crotch_region = crotch_parts(bm, g, lm, up, lateral, hip_mid, fwd_body)
    feet = []
    toe_on = g.get("toeReinforce", 2) > 0
    heel_on = g.get("heelReinforce", True)
    if g["type"] in ("tights", "thighhigh") and (toe_on or heel_on):
        co_all = np.array([v.co for v in bm.verts])
        for side in (1, -1):
            frame = foot_frame(co_all, lm, up, lateral, hip_mid, side)
            if frame is None:
                continue
            ankle, fwd, ahead, behind = frame
            below = lambda c, side=side, ankle=ankle: side_of(c) == side and (c - ankle) @ up <= 0
            # 部位は足首の高さで終わる: 先にそこで切り、その境目もきれいな線にする
            near = lambda c, side=side, ankle=ankle: side_of(c) == side and np.linalg.norm(c - ankle) < leg_len * 0.25
            cut(bm, ankle, up, near)
            toe_co = ankle + fwd * ahead * 0.70
            mine = co_all[np.array([side_of(p) == side for p in co_all])]
            toes = toe_parts(toe_co, fwd, lateral * side, ahead, g.get("toeShape", "straight")) if toe_on else []
            heel = heel_parts(mine, ankle, fwd, behind, ahead, up, g.get("heelShape", "cup")) if heel_on else []
            for co_p, no in toes + heel:
                cut(bm, co_p, no, below)
            feet.append((side, ankle, fwd, toes, heel))

    counts = [0] * REGION_COUNT
    for f in bm.faces:
        c = np.array(f.calc_center_median())
        side = side_of(c)
        region = REGION_LEG
        for fs, ankle, fwd, toes, heel in feet:
            if fs != side or (c - ankle) @ up > 0:
                continue
            if any((c - co_p) @ no >= 0 for co_p, no in toes) or (
                heel and all((c - co_p) @ no <= 0 for co_p, no in heel)
            ):
                region = REGION_REINFORCE
        if region == REGION_LEG:
            for gs, co, no, w in guards:
                if gs != side:
                    continue
                d = (c - co) @ no  # 股関節へ向かう向きが正
                if -w <= d <= 0:
                    region = REGION_BAND
                elif d > 0 and g.get("pantyPart", True):
                    region = REGION_PANTY
        for pl in tops:
            if pl.side in (0, side) and 0 <= pl.signed(c) <= band_w:
                region = REGION_BAND
        for fs, co, no in fakes:
            if fs != side or region == REGION_REINFORCE:
                continue
            d = (c - co) @ no  # 股関節へ向かう向きが正
            if d >= 0 and region == REGION_LEG:
                region = REGION_UPPER
            elif -sock_band <= d < 0:
                region = REGION_SOCK_BAND
        crotch = crotch_region(c)
        if crotch is not None:
            region = crotch
        f[reg] = region
        counts[region] += 1
    log(f"regions leg/panty/band/reinforce/upper/sock-band/seam/gusset faces {counts}")


def leg_coords(
    points: np.ndarray, lm: dict[str, np.ndarray], lateral: np.ndarray, hip_mid: np.ndarray, fwd: np.ndarray
) -> np.ndarray:
    """
    点ごとに (s, u)。s は脚の骨に沿って進む（0 股関節、1 膝、2 足首、3 つま先。股関節より上では負）。u は脚の周りの
    角度を回転数で表したもので、前で 0。外側が両脚で同じになるよう鏡映し、右脚では 2 を足す（模様が脚を区別
    できるように）。脚に沿う模様の土台。
    """
    out = np.zeros((len(points), 2))
    for k, p in enumerate(points):
        side = face_side(p, hip_mid, lateral)
        chain = chain_for(lm, side)
        s, _ = chain_param(chain, p)
        i = int(np.clip(np.floor(s), 0, len(chain) - 2))
        a, b = chain[i], chain[i + 1]
        axis = normalize(b - a)
        r = p - (a + axis * ((p - a) @ axis))
        ref = normalize(fwd - axis * (fwd @ axis))
        theta = np.arctan2(np.cross(ref, r) @ axis, ref @ r) * side
        out[k] = (s, (theta / (2 * np.pi)) % 1.0 + (0.0 if side > 0 else 2.0))
    return out


def legwrap_cut(
    g: dict[str, Any],
    lm: dict[str, np.ndarray],
    up: np.ndarray,
    lateral: np.ndarray,
    hip_mid: np.ndarray,
    unit: Callable[[float], float],
    co: np.ndarray,
) -> tuple[list, list[np.ndarray]]:
    """
    レッグラップ: 脚ごとに、脚の鎖 wrapFrom と wrapTo の間（両端は脚に直交する平面で切る）で、脚の周りを wrapTurns
    回巻くらせんの帯（幅 wrapWidthMm）を残す。wrapCross なら逆向きのらせんも足して交差させる（同じ面から切るので、
    交差する所は 1 枚）。帯の中心線までの距離は、脚の周りのずれを弧の長さにし、らせんの傾きの分だけ縮めて測る。両端には、
    脚を水平に 1 周するリボンの幅の輪も残す（らせんが途中で切れて終わらない）。らせんは、脚の太さ（体の点 `co` から
    求めた、脚の鎖に沿った半径）に合わせてどこでも同じ傾きで巻く（太い所はゆっくり回る。等間隔で回すと太ももで寝て、
    交差が横に潰れる）。（切る形, 種）を返す。
    """
    lo, hi = sorted((float(g["wrapFrom"]), float(g["wrapTo"])))
    turns, half = float(g["wrapTurns"]), unit(float(g["wrapWidthMm"])) * 0.5
    dirs = (1.0, -1.0) if g.get("wrapCross", True) else (1.0,)
    fwd = body_forward(lm, up, lateral)
    cuts, seeds = [], []
    for side in (1, -1):
        chain = chain_for(lm, side)
        top, top_no = chain_point(chain, lo)
        bottom, bottom_no = chain_point(chain, hi)
        cuts.append(Plane(top, top_no, False, side))
        cuts.append(Plane(bottom, bottom_no, True, side))
        length = float(np.linalg.norm(top - bottom))
        # 脚の鎖に沿った半径（40 区間の中央値）と、同じ傾きで巻くための回転の進み（∫ d(長さ) / 半径 に比例）
        grid = np.linspace(lo, hi, 41)
        mine = [p for p in co if face_side(p, hip_mid, lateral) == side]
        sd = np.array([chain_param(chain, p) for p in mine]) if mine else np.zeros((0, 2))
        radius = []
        for a0, a1 in zip(grid[:-1], grid[1:]):
            sel = sd[(sd[:, 0] >= a0) & (sd[:, 0] < a1), 1] if len(sd) else []
            radius.append(float(np.median(sel)) if len(sel) else np.nan)
        radius = np.array(radius)
        if np.isnan(radius).all():
            radius[:] = 1.0
        ok = ~np.isnan(radius)
        mid = (grid[:-1] + grid[1:]) * 0.5
        radius = np.interp(mid, mid[ok], radius[ok])
        progress = np.concatenate([[0.0], np.cumsum(length / 40.0 / radius)])
        slope = 2.0 * np.pi * turns / float(progress[-1])  # 脚の周りの弧 / 脚に沿った長さ（どこでも同じ）

        def fn(p, chain=chain, length=length, progress=progress, slope=slope, grid=grid):
            s, u = leg_coords(np.array([p]), lm, lateral, hip_mid, fwd)[0]
            a, _ = chain_point(chain, s)
            r = float(np.linalg.norm(p - a))
            phase = turns * float(np.interp(s, grid, progress)) / float(progress[-1])
            # 両端の輪: 脚を水平に 1 周する、リボンの幅の帯（らせんの端はそこにつながる。実物は端で 1 周して留める）
            along = (s - lo) / max(hi - lo, 1e-6) * length
            ring = max(half - abs(along - half), half - abs(length - along - half))
            spiral = -1e9
            for d in dirs:
                du = ((u - d * phase + 0.5) % 1.0) - 0.5
                arc = abs(du) * 2.0 * np.pi * r
                spiral = max(spiral, half - arc / float(np.hypot(1.0, slope)))
            # （らせんは両端の輪の中心線の間だけ: 輪の外側へ小さな端が飛び出さない）
            return max(ring, min(spiral, along - half, length - half - along))

        cuts.append(Field(fn, side))
        seeds.append(chain_point(chain, lo)[0])
    return cuts, seeds
