# SPDX-FileCopyrightText: 2026 MerciaGrimauge
# SPDX-License-Identifier: GPL-3.0-or-later

# LegWeaver の Blender 側: 衣装の表面。エンベロープ（SDF）、お尻の橋渡し、くぼみ、細分化、くしゃくしゃ、品質の報告。

import bpy
import bmesh
from typing import Any
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from lw_common import (
    BodySurface,
    Plane,
    body_bmesh,
    hip_width,
    left_foot_forward,
    leg_length,
    log,
    new_object,
    remove_loose,
)


def quality_report(
    co: np.ndarray,
    tris: np.ndarray,
    binds: dict[int, tuple[int, np.ndarray, np.ndarray]],
    body: BodySurface,
    far: float,
    holes: list[np.ndarray],
    hip_mid: np.ndarray,
    lateral: np.ndarray,
    up: np.ndarray,
    feet_fwd: np.ndarray,
    mpu: float,
    bulge: float,
) -> dict[str, Any]:
    """
    形から見つけた破綻。場所を問わない（確認用で、修復はしない）:
    - spikes: 鋭い辺。2 つの面が互いに 100 度より大きく曲がる所。
    - far: 体の点から、衣装としてありえないほど離れて立つ頂点（`far`）。
    - slivers: 針のような三角形（最小の角が 2 度未満で、最長の辺が辺の中央値の 3 倍を超える）。
    - holes: 縁から離れた所にある開いた辺（`holes`。辺に厚みを付ける前に見つける）。
    - bulges: 肌からの距離が、隣の頂点の中央値より `bulge` を超えて大きい頂点。
    - jagged: 衣装がその下の肌より 35 度多く折れている辺（細かい凹凸）。
    個数と、それが最も集まる場所を返す（20 mm のセル。腰からの mm: lateral, up, forward, 個数）。
    """
    a, b, c = co[tris[:, 0]], co[tris[:, 1]], co[tris[:, 2]]
    fn = np.cross(b - a, c - a)
    area = np.linalg.norm(fn, axis=1)
    ok = area > 1e-14
    fn[ok] /= area[ok][:, None]
    edges = {}
    for t, (i, j, k) in enumerate(tris):
        for x, y in ((i, j), (j, k), (k, i)):
            edges.setdefault((min(x, y), max(x, y)), []).append(t)
    spikes = []
    face_normals = {}

    def face_normal(t):
        """体の三角形の法線（三角形ごとに一度だけ計算する）。"""
        if t not in face_normals:
            face_normals[t] = body.face_normal(t)
        return face_normals[t]

    # jagged: 衣装がその下の肌より 35 度多く折れている辺（細かい凹凸。報告のみ）
    jagged = []
    for (x, y), ts in edges.items():
        if len(ts) == 2 and ok[ts[0]] and ok[ts[1]] and x in binds and y in binds:
            turn = np.degrees(np.arccos(np.clip(float(fn[ts[0]] @ fn[ts[1]]), -1, 1)))
            # そこでの肌自身の折れ: 両端の下にある体の三角形どうしの間（ポリゴン数の少ない体も折れる）
            skin = np.degrees(np.arccos(np.clip(float(face_normal(binds[x][0]) @ face_normal(binds[y][0])), -1, 1)))
            if turn > 30 and turn - skin > 35:
                jagged.append((co[x] + co[y]) / 2)
    for (x, y), ts in edges.items():
        if len(ts) == 2 and ok[ts[0]] and ok[ts[1]] and float(fn[ts[0]] @ fn[ts[1]]) < np.cos(np.radians(100)):
            # 体自身がそこでそれほど鋭く曲がっている所は除く（体自身の溝、ポリゴン数の少ない体の角）
            if x in binds and y in binds and float(body.normal(*binds[x][:2]) @ body.normal(*binds[y][:2])) < 0.5:
                continue
            spikes.append((co[x] + co[y]) / 2)
    far_pts = [co[i] for i, (t, bary, loc) in binds.items() if np.linalg.norm(co[i] - loc) > far]
    el = np.stack([np.linalg.norm(b - a, axis=1), np.linalg.norm(c - b, axis=1), np.linalg.norm(a - c, axis=1)], 1)
    med = float(np.median(el))
    longest = el.max(axis=1)
    min_angle_sin = area / np.maximum(longest * np.sort(el, axis=1)[:, 1], 1e-20)
    sliver = (min_angle_sin < np.sin(np.radians(2))) & (longest > med * 3)
    sliver_pts = list(((a + b + c) / 3)[sliver])
    fwd = feet_fwd

    def where(pts):
        # 最も混んだ 20 mm のセル 5 つ: セルの中心の [lateral, up, forward] と、そこに入る個数
        cells = {}
        for p in pts:
            k = tuple(int(np.floor(float((p - hip_mid) @ ax) * mpu * 1000 / 20)) for ax in (lateral, up, fwd))
            cells[k] = cells.get(k, 0) + 1
        return [
            [k[0] * 20 + 10, k[1] * 20 + 10, k[2] * 20 + 10, n]
            for k, n in sorted(cells.items(), key=lambda kv: -kv[1])[:5]
        ]

    # bulges: 隣よりずっと肌から離れて立つ頂点（ひれ、こぶ）
    height = {i: float(np.linalg.norm(co[i] - loc)) for i, (t, bary, loc) in binds.items()}
    nbrs = {}
    for i, j, k in tris:
        for x, y in ((i, j), (j, k), (k, i)):
            nbrs.setdefault(x, set()).add(y)
            nbrs.setdefault(y, set()).add(x)
    bulge_pts = []
    for i, ns in nbrs.items():
        hs = [height[j] for j in ns if j in height]
        if i in height and len(hs) >= 3 and height[i] - float(np.median(hs)) > bulge:
            bulge_pts.append(co[i])
    q = {
        "spikes": len(spikes),
        "far": len(far_pts),
        "slivers": len(sliver_pts),
        "holes": len(holes),
        "bulges": len(bulge_pts),
        "bulgesAt": where(bulge_pts),
        "jagged": len(jagged),
        "jaggedAt": where(jagged),
        "spikesAt": where(spikes),
        "farAt": where(far_pts),
        "sliversAt": where(sliver_pts),
        "holesAt": where(holes),
    }
    log(
        f"quality: spikes {q['spikes']} far {q['far']} slivers {q['slivers']} holes {q['holes']} bulges {q['bulges']} "
        f"jagged {q['jagged']} | jagged at {q['jaggedAt']} bulges at {q['bulgesAt']} spikes at "
        f"{q['spikesAt']} far at {q['farAt']} slivers at {q['sliversAt']} holes at {q['holesAt']}"
    )
    return q


def fill_inner_holes(bm: bmesh.types.BMesh, planes: list[Plane], eps: float):
    """範囲の穴をふさぐ。ただし縁（衣装の平面上にある境界）はふさがない。"""
    boundary = [e for e in bm.edges if e.is_boundary]
    seen = set()
    for e in boundary:
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
        on_plane = all(any(pl.on_edge(np.array(v.co), eps) for pl in planes) for x in group for v in x.verts)
        if not on_plane:
            bmesh.ops.holes_fill(bm, edges=group, sides=0)


def fill_crevices(bm: bmesh.types.BMesh, cap: float, iterations: int = 10):
    """
    外向きだけの平滑化: 隣の平均より下にある頂点（切れ込みや狭い溝の中）は、法線に沿って隣の方へ外向きに動き、
    内へは決して動かない。布はこうした細部をなぞらずに橋渡しする。上限は、各頂点がウェイトを受け継ぐ体の点の
    近くに頂点を保つ。
    """
    bm.verts.ensure_lookup_table()
    verts = list(bm.verts)
    index = {v: i for i, v in enumerate(verts)}
    nbrs = [[index[e.other_vert(v)] for e in v.link_edges] for v in verts]
    co = np.array([v.co for v in verts])
    start = co.copy()
    bm.normal_update()
    nrm = np.array([v.normal for v in verts])
    for _ in range(iterations):
        moved = co.copy()
        for i, nb in enumerate(nbrs):
            if not nb:
                continue
            out = float((co[nb].mean(axis=0) - co[i]) @ nrm[i])
            if out > 0:
                moved[i] = co[i] + nrm[i] * out * 0.5
        co = moved
    lift = np.clip(((co - start) * nrm).sum(axis=1), 0.0, cap)
    for i, v in enumerate(verts):
        v.co = Vector(start[i] + nrm[i] * lift[i])


def tension_over(
    bm: bmesh.types.BMesh,
    zone: dict,
    body: BodySurface,
    src: bmesh.types.BMLayerItem,
    floor: float,
    cap: float,
    islands: list[np.ndarray] | None = None,
    rounds: int = 10,
    nearest: bool = False,
):
    """
    張った布（指先など）: 伸びた布は膜のように、へこみ（爪の付け根や両脇の溝、長い爪の先の下）には入り込まず、
    出っ張りの間に張る。`zone`（頂点 -> 指の番号）を、同じ番号の辺でつながる塊（指ごと）に分け、塊ごとの凸包の
    面まで、各頂点を法線の向きに持ち上げる（`cap` まで: 曲げた指の内側に張りすぎない）。続けて平均へ寄せて
    出っ張りの角を丸める。丸めでは、元の体の点から法線方向に `floor` より内へは入れず、最後に凸包の面より
    `floor` の半分を超えて内へ入った頂点を凸包の面まで戻す（肌や爪が布から出ないように）。指から離れた島に
    なっている爪（`islands`）の中心が塊の布から `floor` の 2 倍以内にあれば（隣の指の爪は入れない）、その点を
    `floor` だけ 6 方向へ広げて凸包に入れる（布は爪の上に張る。爪が面の間から出ないようにするのは cover_islands）。
    `nearest` なら、法線の向きでなく凸包の最も近い面まで持ち上げる（指の間の溝の頂点は法線が隣の指の方を向き、
    法線の向きでは溝を埋められない。足の指をまとめて包むとき）。
    """
    if not zone:
        return
    bm.normal_update()
    verts = list(bm.verts)
    index = {v: i for i, v in enumerate(verts)}
    co = np.array([v.co for v in verts])
    nrm = np.array([v.normal for v in verts])
    # 指ごとの塊
    seen = set()
    groups = []
    for v in verts:
        if v not in zone or v in seen:
            continue
        group, stack = [], [v]
        seen.add(v)
        while stack:
            cur = stack.pop()
            group.append(index[cur])
            for e in cur.link_edges:
                o = e.other_vert(cur)
                if zone.get(o) == zone[cur] and o not in seen:
                    seen.add(o)
                    stack.append(o)
        groups.append(group)
    hulls = []
    hull_of = {}  # 頂点 -> (その指の凸包, 指の中心)
    for group in groups:
        if len(group) < 8:
            continue
        hull = bmesh.new()
        for i in group:
            hull.verts.new(Vector(co[i]))
        gp = co[group]
        for pts in islands or []:
            if float(np.min(np.linalg.norm(gp - pts.mean(axis=0), axis=1))) < floor * 2:
                for d in np.vstack([np.eye(3), -np.eye(3)]) * floor:
                    for p in pts:
                        hull.verts.new(Vector(p + d))
        try:
            # （内側の点は面を持たないので、面から作る探索木には入らない）
            bmesh.ops.convex_hull(hull, input=list(hull.verts))
        except RuntimeError:
            hull.free()
            continue
        if not hull.faces:
            hull.free()
            continue
        tree = BVHTree.FromBMesh(hull)
        center = co[group].mean(axis=0)
        for i in group:
            if nearest:
                hit, _, _, dist = tree.find_nearest(Vector(co[i]))
                if hit is not None and dist <= cap:
                    co[i] = np.array(hit)
            else:
                hit = tree.ray_cast(Vector(co[i] + nrm[i] * 1e-6), Vector(nrm[i]), cap)
                if hit[0] is not None:
                    co[i] = co[i] + nrm[i] * min(float(hit[3]), cap)
            hull_of[i] = (tree, center)
        hulls.append(hull)
    ids = [index[v] for v in verts if v in zone]
    nbrs = {i: [index[e.other_vert(verts[i])] for e in verts[i].link_edges] for i in ids}
    frame = {}
    for i in ids:
        t, bary, loc = body.bind(np.array(verts[i][src]))
        frame[i] = (loc, body.normal(t, bary))
    for _ in range(rounds):
        moved = co.copy()
        for i in ids:
            nb = nbrs[i]
            if nb:
                moved[i] = co[i] * 0.5 + co[nb].mean(axis=0) * 0.5
            loc, n = frame[i]
            short = floor - float((moved[i] - loc) @ n)
            if short > 0:
                moved[i] = moved[i] + n * short
        co = moved
    # 最後に、丸めで凸包の面より `floor` の半分を超えて内へ入った頂点を、凸包の面まで戻す（体の面に押し出すと、
    # 長い爪の先の下のすき間では最も近い面が爪の裏になり、布がすき間へ押し込まれる）
    for i in ids:
        if i not in hull_of:
            continue
        tree, center = hull_of[i]
        hit, hn, _, _ = tree.find_nearest(Vector(co[i]))
        if hit is None:
            continue
        hit, hn = np.array(hit), np.array(hn)
        if hn @ (hit - center) < 0:
            hn = -hn  # 凸包の外向き
        depth = float((hit - co[i]) @ hn) - floor * 0.5
        if depth > 0:
            co[i] = co[i] + hn * depth
    for i in ids:
        verts[i].co = Vector(co[i])
    for hull in hulls:
        hull.free()


def cover_islands(bm: bmesh.types.BMesh, islands: list[np.ndarray], clear: float, rounds: int = 4) -> int:
    """
    指から離れた島になっている爪（`islands`）の、布から `clear` の 4 倍以内の各点が、布の面の内側に `clear` 以上
    入っているようにする: 点に最も近い布の面の内側への深さが足りなければ、その面の頂点を面の法線の向きへ足りない
    分だけ押し出す（布の頂点の間の面の下から爪が出ないように）。押し出した回数を返す。
    """
    pushed = 0
    pts = [Vector(p) for group in islands for p in group]
    if not pts:
        return 0
    for _ in range(rounds):
        bm.faces.ensure_lookup_table()
        bm.normal_update()
        tree = BVHTree.FromBMesh(bm)
        moves = {}
        for p in pts:
            # （布から遠い点は、その布が覆わない所の爪（指なしの指先など）: 見ない）
            loc, nrm, fi, _ = tree.find_nearest(p, clear * 4)
            if loc is None:
                continue
            short = float((p - loc).dot(nrm)) + clear
            if short > 0:
                for v in bm.faces[fi].verts:
                    if short > moves.get(v, (0.0, None))[0]:
                        moves[v] = (short, nrm.copy())
        if not moves:
            break
        for v, (short, nrm) in moves.items():
            v.co += nrm * short
        pushed += len(moves)
    return pushed


def cover_body(
    bm: bmesh.types.BMesh,
    src: bmesh.types.BMLayerItem,
    pts: np.ndarray,
    reach: np.ndarray,
    clear: float,
    rounds: int = 4,
) -> int:
    """
    布の面の外へ突き抜けた体の点 `pts` を、面の内側 `clear` まで入れる: 点に最も近い布の面が、その点の近く（元の体の
    点の平均が `reach` 以内）の肌から作った面なら、足りない分だけ面の頂点を面の法線の向きへ押し出す（隣の指など、
    別の所の肌から作った面は押さない）。押し出した回数を返す。
    """
    pushed = 0
    for _ in range(rounds):
        bm.faces.ensure_lookup_table()
        bm.normal_update()
        tree = BVHTree.FromBMesh(bm)
        moves = {}
        for p, r in zip(pts, reach):
            loc, nrm, fi, _ = tree.find_nearest(Vector(p))
            if loc is None:
                continue
            f = bm.faces[fi]
            if np.linalg.norm(np.mean([np.array(v[src]) for v in f.verts], axis=0) - p) > r:
                continue
            out = float((Vector(p) - loc).dot(nrm))
            if out > 0:
                short = out + clear
                for v in f.verts:
                    if short > moves.get(v, (0.0, None))[0]:
                        moves[v] = (short, nrm.copy())
        if not moves:
            break
        pushed += len(moves)
        # 押し出す量を隣の頂点へ半分ずつ 2 段まで分ける（1 つの面だけが飛び出して尖らないように）
        disp = {v: nrm * short for v, (short, nrm) in moves.items()}
        for _ in range(2):
            spread = {}
            for v, d in disp.items():
                for e in v.link_edges:
                    u = e.other_vert(v)
                    if d.length * 0.5 > max(disp.get(u, Vector()).length, spread.get(u, Vector()).length):
                        spread[u] = d * 0.5
            disp.update(spread)
        for v, d in disp.items():
            v.co += d
    return pushed


def refine(
    bm: bmesh.types.BMesh,
    max_tris: int,
    src: bmesh.types.BMLayerItem,
    exact: bmesh.types.BMLayerItem,
    body: BodySurface,
    offset: float,
):
    """
    高品質: 衣装の辺を分割し（長いものから、`max_tris` が許すだけ。どの辺も高々 1 回）、新しい頂点を隣の頂点が
    作る曲線の上へ丸めるので、角なく曲がる。新しい頂点はそれぞれ隣の頂点の体の点の間にある体の点に結び付け
    （肌の上へ吸着）、そこから少なくとも `offset` 離しておく。
    """
    tris = sum(len(f.verts) - 2 for f in bm.faces)
    # 縁には後で厚みを付け（縁の辺ごとに三角形 2 つ、分割後はその倍）、つま先と出力の分割された頂点も少し増やす:
    # 15% の余裕を残す
    hem = sum(1 for e in bm.edges if e.is_boundary)
    room = int(max_tris * 0.85) - tris - hem * 4
    edges = sorted(bm.edges, key=lambda e: -e.calc_length())
    # 辺を 1 本分割すると三角形がおよそ 2 つ増える（両側に 1 つずつ）
    edges = edges[: max(0, min(len(edges), room // 2))]
    if not edges:
        log(f"refine: no room ({tris} triangles)")
        return
    old = set(bm.verts)
    bmesh.ops.subdivide_edges(bm, edges=edges, cuts=1, use_grid_fill=True, smooth=0.3)
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])
    new = [v for v in bm.verts if v not in old]
    for v in new:
        nb = [e.other_vert(v) for e in v.link_edges if e.other_vert(v) in old]
        if not nb:
            continue
        mid = sum((Vector(o[src]) for o in nb), Vector()) / len(nb)
        is_exact = all(o[exact] for o in nb)
        if is_exact:
            t, bary, loc = body.bind(np.array(mid))
            v[src] = Vector(loc)
            n = body.normal(t, bary)
            h = float((np.array(v.co) - loc) @ n)
            if h < offset:
                v.co = Vector(np.array(v.co) + n * (offset - h))
        else:
            v[src] = v.co.copy()
        v[exact] = 1 if is_exact else 0
    log(f"refine: split {len(edges)} edges, {tris} -> {sum(len(f.verts) - 2 for f in bm.faces)} triangles")


def height_over(frames: dict[bmesh.types.BMVert, tuple], v: bmesh.types.BMVert) -> float:
    """頂点がその肌から、肌の法線に沿ってどれだけ離れて立っているか（頂点 -> (肌の点, 法線, ...)）。"""
    loc, n = frames[v][:2]
    return float((np.array(v.co) - loc) @ n)


def flatten_bulges(frames: dict[bmesh.types.BMVert, tuple], limit: float, rounds: int = 6) -> int:
    """
    形から見つけたこぶ。場所を問わない: 肌からの距離が隣の頂点の中央値より `limit` を超えて大きい頂点を、その
    中央値まで（肌の法線に沿って）下げる。これを何回も繰り返す。下げた頂点の数を返す。
    """

    def height(v):
        return height_over(frames, v)

    lowered = 0
    for _ in range(rounds):
        moved = {}
        for v in frames:
            nb = [height(e.other_vert(v)) for e in v.link_edges if e.other_vert(v) in frames]
            if len(nb) < 3:
                continue
            m = float(np.median(nb))
            if height(v) - m > limit:
                loc, n = frames[v][:2]
                # 立つはずだった所より下には決して下げない（たとえば、下に着るものの上）
                low = frames[v][2] if len(frames[v]) > 2 else 0.0
                target = max(m + limit * 0.25, low)
                if target < height(v) - 1e-9:
                    moved[v] = Vector(loc + n * target)
        for v, c in moved.items():
            v.co = c
        lowered += len(moved)
        if not moved:
            break
    return lowered


def crumple_targets(
    bm: bmesh.types.BMesh, frames: dict[bmesh.types.BMVert, tuple], fixed: set[bmesh.types.BMVert]
) -> tuple[list[bmesh.types.BMFace], list[np.ndarray]]:
    """
    くしゃくしゃを調べる面と、その角での肌の滑らかな法線の和の向き（衣装を押し出した方向）。頂点を動かしても
    変わらない（面のつながりと肌の点はそのまま）ので、繰り返しの前に一度だけ作る。
    """
    faces, dirs = [], []
    for f in bm.faces:
        # （橋渡しした面は橋渡し面の上にあり、肌に沿ってはいない）
        if any(v not in frames or v in fixed for v in f.verts):
            continue
        nb = sum(frames[v][1] for v in f.verts)
        nl = np.linalg.norm(nb)
        if nl < 1e-12:
            continue
        faces.append(f)
        dirs.append(nb / nl)
    return faces, dirs


def crumpled_faces(targets: tuple[list[bmesh.types.BMFace], list[np.ndarray]]) -> list[bmesh.types.BMFace]:
    """
    作った元の肌から大きく向きが変わった面（折り返された、または溝の中で立ち上がった）: `crumple_targets` の
    向きと比べる。（ポリゴン数の少ない体の深い溝では、肌自身の三角形がすでに折れていることがある。それは衣装の
    せいではない。）
    """
    faces, dirs = targets
    return [f for f, d in zip(faces, dirs) if float(np.array(f.normal) @ d) < 0.5]


def relax_crumples(
    bm: bmesh.types.BMesh,
    frames: dict[bmesh.types.BMVert, tuple],
    fixed: set[bmesh.types.BMVert],
    floor: float,
    rounds: int = 24,
) -> tuple[int, int]:
    """
    衣装のくしゃくしゃを、場所を問わず形から見つける: その下の肌から 60 度より大きく向きが変わった面。その頂点を、
    肌の法線の上で隣の頂点の平均の高さへ戻す。これを何回も繰り返し、くしゃくしゃが残る間は隣へも広げる。回数の
    半分を過ぎると、そこの高さを `floor` へ向かって縮めもする（肌の近くでは面は常に肌と同じ方を向く）。（見つけた
    くしゃくしゃの面, 最後に残った面）を返す。
    """

    def height(v):
        return height_over(frames, v)

    bm.normal_update()
    targets = crumple_targets(bm, frames, fixed)
    found = crumpled_faces(targets)
    first = len(found)
    ring = set()
    for r in range(rounds):
        if not found:
            break
        shrink = 0.8 if r >= rounds // 2 else 1.0
        ring |= {v for f in found for v in f.verts if v not in fixed}
        # 毎回 1 周ずつ広げ、しつこい所はより広い範囲で均す
        ring |= {
            e.other_vert(v)
            for v in list(ring)
            for e in v.link_edges
            if e.other_vert(v) in frames and e.other_vert(v) not in fixed
        }
        new = {}
        for v in ring:
            nb = [e.other_vert(v) for e in v.link_edges if e.other_vert(v) in frames]
            if not nb:
                continue
            h = float(np.mean([height(o) for o in nb]))
            # 頂点が立つはずだった高さより低くは決してしない（たとえば、下に着るものの上）
            low = max(floor, frames[v][2] if len(frames[v]) > 2 else floor)
            if shrink < 1.0:
                h = low + (min(h, height(v)) - low) * shrink
            loc, n = frames[v][:2]
            new[v] = Vector(loc + n * max(h, low))
        for v, c in new.items():
            v.co = c
        bm.normal_update()
        found = crumpled_faces(targets)
    return first, len(found)


def smooth_folds(
    bm: bmesh.types.BMesh, floors: dict[bmesh.types.BMVert, tuple], astray: float, rounds: int = 10
) -> tuple[int, int]:
    """
    形から見つけた折れ。場所を問わない（たとえば、脚と橋渡し面が出会う股の継ぎ目）: 衣装が 60 度より大きく、かつ
    その下の肌より 35 度多く曲がる辺。その頂点と周りの 1 周の頂点を、隣の平均へ半分ずつ動かす。これを何回も
    繰り返し、`floors`（頂点 -> (肌の点, 法線, 高さ)）が与える高さより下には決して下げない。肌の点の法線の線から
    `astray` より離れた頂点（その後に動かされたもの: つま先を差し替えた、お尻の輪郭の内に抑えた）はそのままにする。
    （見つけた折れの頂点, 最後に残った頂点）を返す。
    """
    verts = [v for v in bm.verts]
    index = {v: i for i, v in enumerate(verts)}
    co = np.array([v.co for v in verts])
    has = np.array([v in floors for v in verts])
    loc = np.array([floors[v][0] if v in floors else (0, 0, 0) for v in verts], dtype=float)
    nrm = np.array([floors[v][1] if v in floors else (0, 0, 0) for v in verts], dtype=float)
    low = np.array([floors[v][2] if v in floors else 0.0 for v in verts])
    tri = np.array([[index[x] for x in f.verts[:3]] for f in bm.faces])
    fidx = {f: i for i, f in enumerate(bm.faces)}
    pairs = np.array(
        [
            (index[e.verts[0]], index[e.verts[1]], fidx[e.link_faces[0]], fidx[e.link_faces[1]])
            for e in bm.edges
            if len(e.link_faces) == 2
        ]
    ).reshape(-1, 4)
    ends = np.array([(index[e.verts[0]], index[e.verts[1]]) for e in bm.edges]).reshape(-1, 2)
    rel = co - loc
    side = np.linalg.norm(rel - nrm * np.sum(rel * nrm, 1)[:, None], axis=1)
    movable = has & (side <= astray) & np.array([not v.is_boundary for v in verts])
    skin = np.degrees(np.arccos(np.clip(np.sum(nrm[pairs[:, 0]] * nrm[pairs[:, 1]], 1), -1, 1)))
    usable = has[pairs[:, 0]] & has[pairs[:, 1]]

    def folds():
        fn = np.cross(co[tri[:, 1]] - co[tri[:, 0]], co[tri[:, 2]] - co[tri[:, 0]])
        fn /= np.maximum(np.linalg.norm(fn, axis=1), 1e-20)[:, None]
        turn = np.degrees(np.arccos(np.clip(np.sum(fn[pairs[:, 2]] * fn[pairs[:, 3]], 1), -1, 1)))
        hit = usable & (turn > 60) & (turn - skin > 35)
        out = np.zeros(len(verts), bool)
        out[pairs[hit, 0]] = True
        out[pairs[hit, 1]] = True
        return out

    dist0 = np.linalg.norm(co - loc, axis=1)
    found = folds()
    first = int(found.sum())
    for _ in range(rounds):
        if not found.any():
            break
        ring = found.copy()
        ring[ends[found[ends[:, 0]], 1]] = True
        ring[ends[found[ends[:, 1]], 0]] = True
        ring &= movable
        prev = co.copy()
        total = np.zeros_like(co)
        count = np.zeros(len(verts))
        np.add.at(total, ends[:, 0], co[ends[:, 1]])
        np.add.at(total, ends[:, 1], co[ends[:, 0]])
        np.add.at(count, ends[:, 0], 1)
        np.add.at(count, ends[:, 1], 1)
        mean = total / np.maximum(count, 1)[:, None]
        co[ring] = co[ring] * 0.5 + mean[ring] * 0.5
        # 立たなければならない所より下には決して下げない（肌の上、または下に着るものの上）
        short = low - np.sum((co - loc) * nrm, 1)
        lift = ring & (short > 0)
        co[lift] += nrm[lift] * short[lift][:, None]
        # 最初に立っていた所より肌から遠くへは押し出さない（`astray` を超えて）: 持ち上げが積み重なる所では、
        # 元の場所にとどめる
        off = np.linalg.norm(co - loc, axis=1) > dist0 + astray
        co[off] = prev[off]
        found = folds()
    for v, c in zip(verts, co):
        v.co = Vector(c)
    return first, int(found.sum())


class Seat:
    """
    お尻（体の中ほど、後ろ側、股から股関節より上まで）: 布はお尻の割れ目へ入り込まずに、それを橋渡しする。
    `weight` は真ん中で 1、左右・上・下へ向かって弱まる。
    """

    def __init__(
        self,
        wpos: np.ndarray,
        wnrm: np.ndarray,
        lm: dict[str, np.ndarray],
        up: np.ndarray,
        lateral: np.ndarray,
        hip_mid: np.ndarray,
        wtris: np.ndarray,
    ):
        self.up, self.lateral, self.hip_mid = up, lateral, hip_mid
        self.fwd = left_foot_forward(lm, up, lateral)
        hip_w = hip_width(lm)
        leg_len = leg_length(lm)
        self.half_w = hip_w * 0.35
        rel = wpos - hip_mid
        fwd = self.fwd
        # 股: 真ん中で下を向いた最も低い点（真ん中で触れ合う太ももは横を向く）
        mid = (
            (np.abs(rel @ lateral) < leg_len * 0.02)
            & (np.abs(rel @ fwd) < leg_len * 0.2)
            & (rel @ up < 0)
            & (rel @ up > -leg_len * 0.4)
            & (wnrm @ up < -0.5)
        )
        crotch = float((rel[mid] @ up).min()) if mid.any() else -leg_len * 0.1
        self.bottom = crotch - leg_len * 0.01
        self.top = leg_len * 0.25
        self.fade = leg_len * 0.04
        # お尻が各高さでどこまで後ろへ出ているか（脚の長さの 0.4%、約 2 mm の薄切り）: 橋渡しは自分の薄切りの中で
        # 割れ目をまたぎ、それより後ろへは決して出ない（クロージングはお尻の下の折れ目もまたいでしまい、カーテンに
        # なる）
        self.slice = leg_len * 0.004
        # （お尻の中の三角形は、角がまたぐすべての薄切りに数える: 頂点だけでは、三角形が薄切りより大きい所で薄切りが
        # 空になり、そこで橋渡しが抑えられずにひれになる）
        near = (
            (np.abs(rel @ lateral) < self.half_w)
            & (rel @ up > self.bottom - self.fade)
            & (rel @ up < self.top + self.fade)
        )
        h_all = rel @ up
        z_all = rel @ fwd
        self.back = {}
        for t in wtris[near[wtris].any(axis=1)]:
            lo, hi = int(np.floor(h_all[t].min() / self.slice)), int(np.floor(h_all[t].max() / self.slice))
            z = float(z_all[t].min())
            for h in range(lo, hi + 1):
                self.back[h] = min(self.back.get(h, 0.0), z)

    def back_limit(self, p: np.ndarray) -> float | None:
        """p の高さ（その 2 mm の薄切り）で、体が（-forward に沿って）どこまで後ろへ出ているか。"""
        h = int(np.floor(float((p - self.hip_mid) @ self.up) / self.slice))
        return self.back.get(h)

    def weight(self, p: np.ndarray) -> float:
        rel = p - self.hip_mid
        side = abs(float(rel @ self.lateral)) / self.half_w
        h = float(rel @ self.up)
        w = np.clip((1.0 - side) / 0.4, 0.0, 1.0)
        w *= np.clip((h - self.bottom) / self.fade, 0.0, 1.0) * np.clip((self.top - h) / self.fade, 0.0, 1.0)
        # 後ろ側のみ: 前では股はなぞった方がよい（橋渡しすると、その溝で太ももの間の衣装が裂けて開く）
        w *= np.clip(-float(rel @ self.fwd) / self.fade, 0.0, 1.0)
        return float(w * w * (3.0 - 2.0 * w))

    def body(
        self, wpos: np.ndarray, wtris: np.ndarray, tri_ids: np.ndarray
    ) -> tuple[bpy.types.Object, bpy.types.Object]:
        """お尻の範囲（余白付き）の体を閉じたもの。SDF 用。"""
        bm, _ = body_bmesh(wpos, wtris, tri_ids)
        m = self.fade
        for co, no in (
            (self.hip_mid + self.up * (self.top + m), self.up),
            (self.hip_mid + self.up * (self.bottom - m), -self.up),
            (self.hip_mid + self.lateral * (self.half_w + m), self.lateral),
            (self.hip_mid - self.lateral * (self.half_w + m), -self.lateral),
        ):
            bmesh.ops.bisect_plane(
                bm,
                geom=bm.verts[:] + bm.edges[:] + bm.faces[:],
                dist=1e-9,
                plane_co=Vector(co),
                plane_no=Vector(no),
                clear_outer=True,
            )
        remove_loose(bm)
        if not bm.faces:
            bm.free()
            return None, None
        bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary], sides=0)
        a = new_object("seat", bm)
        b = new_object("seat_info", bm)  # クロージングは形を 2 つ目のオブジェクトから読む（自分自身は参照できない）
        bm.free()
        return a, b


def sdf_surface(
    region_obj: bpy.types.Object,
    feet_obj: bpy.types.Object | None,
    voxel: float,
    offset: float,
    close_r: float,
    smooth: float,
    feet_only: bool = False,
) -> bmesh.types.BMesh:
    """
    ジオメトリーノード: SDF(region) [U closing(SDF(feet))] を `offset` だけオフセットし、メッシュにする。
    `feet_only`: region が feet と同じ（つま先の面）。closing は元の形を含むので和は closing だけになり、縮める量と
    `offset` を、平滑化の後にメッシュにする閾値（offset − close_r）にまとめる（距離の大きいオフセットのノードが重いため）。
    """
    ng = bpy.data.node_groups.new("garment_sdf", "GeometryNodeTree")
    ng.interface.new_socket("Geometry", in_out="INPUT", socket_type="NodeSocketGeometry")
    ng.interface.new_socket("Geometry", in_out="OUTPUT", socket_type="NodeSocketGeometry")
    nodes, links = ng.nodes, ng.links
    gin = nodes.new("NodeGroupInput")
    gout = nodes.new("NodeGroupOutput")
    band = int(max(3.0, (close_r + offset) / voxel + 3.0))

    def sdf(src):
        n = nodes.new("GeometryNodeMeshToSDFGrid")
        n.inputs["Voxel Size"].default_value = voxel
        n.inputs["Band Width"].default_value = band
        links.new(src, n.inputs["Mesh"])
        return n.outputs[0]

    def grid_offset(src, dist):
        n = nodes.new("GeometryNodeSDFGridOffset")
        n.inputs["Distance"].default_value = dist
        links.new(src, n.inputs["Grid"])
        return n.outputs[0]

    def to_mesh(src, threshold=0.0):
        n = nodes.new("GeometryNodeGridToMesh")
        n.inputs["Threshold"].default_value = threshold
        links.new(src, n.inputs["Grid"])
        return n.outputs[0]

    def laplacian(src):
        if smooth <= 0:
            return src
        lap = nodes.new("GeometryNodeSDFGridLaplacian")
        lap.inputs["Iterations"].default_value = smooth
        links.new(src, lap.inputs["Grid"])
        return lap.outputs[0]

    if feet_only:
        info = nodes.new("GeometryNodeObjectInfo")
        info.inputs["Object"].default_value = feet_obj
        grid = sdf(to_mesh(grid_offset(sdf(info.outputs["Geometry"]), close_r)))
        links.new(to_mesh(laplacian(grid), offset - close_r), gout.inputs[0])
        return evaluate_nodes(region_obj, ng)
    grid = sdf(gin.outputs[0])
    if feet_obj is not None:
        info = nodes.new("GeometryNodeObjectInfo")
        info.inputs["Object"].default_value = feet_obj
        feet = grid_offset(sdf(to_mesh(grid_offset(sdf(info.outputs["Geometry"]), close_r))), -close_r)
        union = nodes.new("GeometryNodeSDFGridBoolean")
        union.operation = "UNION"
        grids = union.inputs[1]  # UNION はマルチ入力ソケットを取る
        links.new(grid, grids)
        links.new(feet, grids)
        grid = union.outputs[0]
    links.new(to_mesh(laplacian(grid_offset(grid, offset))), gout.inputs[0])
    return evaluate_nodes(region_obj, ng)


def evaluate_nodes(region_obj: bpy.types.Object, ng: bpy.types.NodeTree) -> bmesh.types.BMesh:
    """ノードを region_obj のモディファイアーとして評価した結果のメッシュ。"""
    mod = region_obj.modifiers.new("sdf", "NODES")
    mod.node_group = ng
    dg = bpy.context.evaluated_depsgraph_get()
    em = region_obj.evaluated_get(dg).to_mesh()
    bm = bmesh.new()
    bm.from_mesh(em)
    region_obj.evaluated_get(dg).to_mesh_clear()
    return bm
