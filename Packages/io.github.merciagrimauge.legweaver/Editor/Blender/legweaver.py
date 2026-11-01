# SPDX-FileCopyrightText: 2026 MerciaGrimauge
# SPDX-License-Identifier: GPL-3.0-or-later

# LegWeaver の Blender 側。
# 実行: blender -b --factory-startup --python legweaver.py -- <job.json>
#
# 入力（Unity が書く）:
#   job.json  : 個数、ランドマーク（体のメッシュ空間）、衣装のパラメーター
#   input.bin : positions f32[3n], normals f32[3n], uv f32[2n], legWeight f32[n], jointMix f32[n],
#               underHeight f32[n]（メートル）, overHeight f32[n]（メートル）, tris i32[3t]
#   under.bin : 衣装の下に着るもの（体のメッシュ空間）: 頂点数 i32, 三角形数 i32,
#               positions f32[3u], tris i32[3w]（lw_under.load_under）
# 出力（Unity が読む。GarmentMeshBuilder.Build）:
#   output.bin  : positions f32[3m], normals f32[3m], uv f32[2m], bindTri i32[m], bindBary f32[3m],
#                 tris i32[3k], covered f32[n], stretchMask f32[m], region i32[k], legCoord f32[2m],
#                 underTri i32[m]（-1: 下に着るものの上に作っていない）, underBary f32[3m]
#   output.json : 個数、統計、エラー
#
# 衣装は、覆われる体の範囲そのものを、体の法線に沿って clearance + thickness だけ外へ動かしたものである: どの頂点も
# 体の頂点の上にあり、そのウェイトをそのまま受け取るので、衣装は肌とまったく同じに曲がる。範囲の SDF（同じだけ
# オフセット）がエンベロープである: その少し下に沈んだ衣装の頂点（へそ、小さな溝）はエンベロープの上へ持ち上げ、その奥深くに
# ある形状は捨てる。母指球より前では、2 つ目の SDF 表面（足にモルフォロジーのクロージングをかけたもの）が衣装で
# ある（指の間のすき間を橋渡しする）。両者を縫い合わせる。縁の厚みは Solidify で付ける。

import bpy
import bmesh
import json
import os
import sys
import traceback
from typing import Any, Callable
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

# 部品はこのスクリプトの隣にある（バイトコードのキャッシュは作らない: フォルダが Unity プロジェクトの中にあるため）
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from lw_common import (
    BodySurface,
    Field,
    barycentric,
    body_bmesh,
    body_edge_planes,
    build_landmarks,
    chain_for,  # noqa: E402
    chain_param,
    crotch_point,
    top_below_crotch,
    cut_by_planes,
    decimate,
    face_side,
    garter_planes,
    feet_forward,
    foot_frame,
    keep_components,
    load_input,
    log,
    make_planes,
    new_object,
    hip_width,
    normalize,
    read_job,
    remove_loose,
    weld,
)
from lw_toes import (
    big_toe_splits,
    boundary_loop,
    closed_feet,
    near_zones,
    split_by_zones,
    toe_zones,  # noqa: E402
    toecap_cut,
    zip_loops,
)
from lw_surface import (
    Seat,
    cover_body,
    cover_islands,
    fill_crevices,
    fill_inner_holes,
    flatten_bulges,  # noqa: E402
    quality_report,
    refine,
    relax_crumples,
    sdf_surface,
    smooth_folds,
    tension_over,
)
from lw_regions import fake_tops, leg_coords, legwrap_cut, stretch_mask, structure_regions  # noqa: E402
from lw_options import add_wrinkles  # noqa: E402
from lw_bow import add_bows  # noqa: E402
from lw_arms import ARM_TYPES, arm_chain, arm_planes, arm_stretch_mask, fingertip_zone  # noqa: E402
from lw_under import load_under, snap_to_under  # noqa: E402

# どのジョブも設定しない大きさ（どの衣装でも同じ）
SDF_SMOOTH = 3  # SDF 表面（エンベロープ、つま先、お尻）を均す回数
CLINGING_OVER_MM = 8.0  # ガーター・レッグラップ: 上に着ている服がこれ以内なら、その下の所は作らない
ACCESSORY_REACH_MM = 15.0  # ガーター・レッグラップ: 肌から下の層の一番外の面を探す距離（向かいの脚の服に届かない。分厚い服の中には隠れる）
WORN_EXTRA_MM = 2.5  # 下に着るものの上に足す余裕（それは自分のウェイトで動き、肌とまったく同じには動かない）
HEM_BAND_MM = 25.0  # 縁からどこまで、衣装が肌から持ち上がって立つか（だんだん弱める）
HEM_LIFT_MM = 2.5  # 縁でどれだけ持ち上がって立つか（本物のタイツのゴムのように）
HEM_ON_PLANE_MM = 0.3  # 体の点が衣装の平面にこれだけ近い開いた辺は、穴ではなく縁である
# 体の三角形の中を布が覆うか確かめる点（重心座標）: 重心、辺の中点、重心と各頂点・各辺の中点の中ほど
TRI_SAMPLES = np.array(
    [
        [1 / 3, 1 / 3, 1 / 3],
        [0.5, 0.5, 0],
        [0, 0.5, 0.5],
        [0.5, 0, 0.5],
        [2 / 3, 1 / 6, 1 / 6],
        [1 / 6, 2 / 3, 1 / 6],
        [1 / 6, 1 / 6, 2 / 3],
        [5 / 12, 5 / 12, 1 / 6],
        [1 / 6, 5 / 12, 5 / 12],
    ]
)
WRAP_EDGE_FRAC = 0.9  # レッグラップ: 縁の近くでこれ（リボンの幅に対する割合）より長い辺を分ける
WRAP_REFINE_ROUNDS = 3  # その最大の回数（長い辺が無くなるまで。リボンが大きな三角形の頂点の間をすり抜けて消えない）
TABI_SPREAD = np.radians(25.0)  # 足袋型で、SDF にする前に親指の側を内側へ開く角度（指の間を閉じないように）
SEAT_CLOSE_MM = 40.0  # お尻の割れ目を閉じる半径
SEAT_VOXEL_MM = 3.0  # お尻の表面の最も粗い格子（割れ目をまたぐだけなので）
ENVELOPE_VOXEL_MM = 1.6  # エンベロープの最も粗い格子
BRIDGE_FADE_MM = 1.5  # オフセットよりこれ以上肌から離れると、メッシュ自身の法線に切り替わる


def under_cap(wover: np.ndarray, wtris: np.ndarray, t: int, bary: np.ndarray, unit: Callable[[float], float]) -> float:
    """
    上に着るもの（靴下など）の下で、体の点（三角形 `t`、重心座標 `bary`）において衣装が肌からどこまで離れて
    立ってよいか: その内側に 0.4 mm。それですき間がどれほど薄くなっても。上に何も着ていない所では inf。
    """
    ceiling = min([float(wover[c]) for c, b in zip(wtris[t], bary) if b > 1e-4] or [np.inf])
    return max(unit(0.3), ceiling - unit(0.4)) if np.isfinite(ceiling) else np.inf


def press_under_caps(caps: dict[bmesh.types.BMVert, tuple], skip: dict[bmesh.types.BMVert, float]):
    """上限より肌から離れて立つ頂点（頂点 -> (肌の点, 法線, 上限)）を法線に沿って押し戻す。`skip` にあるもの
    （橋渡し済み: その法線は当てにならない）と、もうメッシュにないものは除く。"""
    for v, (loc, nrm_v, cap) in caps.items():
        if v.is_valid and v not in skip and (np.array(v.co) - loc) @ nrm_v > cap:
            v.co = Vector(np.array(v.co) - nrm_v * ((np.array(v.co) - loc) @ nrm_v - cap))


def smooth_halfway(verts: list[bmesh.types.BMVert], rounds: int):
    """`verts` のそれぞれを、隣の平均との中間まで動かす。これを `rounds` 回（各回ですべてを同時に動かす）。"""
    for _ in range(rounds):
        moved = {}
        for v in verts:
            nb = [e.other_vert(v).co for e in v.link_edges]
            if nb:
                moved[v] = v.co * 0.5 + (sum(nb, Vector()) / len(nb)) * 0.5
        for v, c in moved.items():
            v.co = c


def main():
    job_path = read_job()
    with open(job_path, "r", encoding="utf-8") as fp:
        job = json.load(fp)
    result = {"ok": False}
    try:
        generate(job, result)
        result["ok"] = True
    except Exception as e:
        result["error"] = f"{e}\n{traceback.format_exc()}"
        log("ERROR " + result["error"])
    with open(job["outputJson"], "w", encoding="utf-8") as fp:
        json.dump(result, fp, ensure_ascii=False, indent=1)


class Work:
    """
    generate() の各段階が互いに受け渡すもの。進むにつれて埋まる（それぞれの分担は各段階を参照）。設定できるのは
    これらの名前だけ: 綴りを誤ると、後で読む所ではなく設定した所で失敗する。
    """

    __slots__ = (
        "g",
        "mpu",
        "unit",
        "nrm",
        "uv",
        "tris",
        "n",
        "first",
        "inv",
        "wpos",
        "wleg",
        "wmix",
        "wunder",
        "wover",
        "wtris",
        "tri_ids",
        "body",
        "lm",
        "up",
        "lateral",
        "hip_mid",
        "planes",
        "seeds",
        "covered",
        "body_part",
        "region_obj",
        "offset",
        "thickness",
        "bridge",
        "surf",
        "envelope",
        "toe_surf",
        "seat",
        "seat_env",
        "joint_extra",
        "worn_extra",
        "band_lift",
        "src",
        "exact",
        "utri",
        "ubary",
        "capped",
        "narrow",
        "frames",
        "seated",
        "worn_usual",
        "hold",
        "worn_cap",
        "hole_pts",
        "wraise",
        "islands",
        "toe_zones",
        "toe_digit",
        "tabi_splits",
        "swing_chains",
    )


def generate(job: dict[str, Any], result: dict[str, Any]):
    work = read_body(job)
    cover_region(work)
    make_envelopes(work)
    lift_off_skin(work)
    replace_toes(work)
    finish_surface(work, job)
    write_output(work, thicken(work), job, result)


def read_body(job: dict[str, Any]) -> Work:
    """
    Unity から来た体（同じ位置を共有する頂点を溶接する。UV の継ぎ目）、ランドマーク、衣装を切り出す平面。
    """
    work = Work()
    g = job["garment"]
    mpu = float(job["metersPerUnit"])
    # 作業が依存する大きさ。ウィンドウが提示する範囲内に限る（手で書き換えたジョブは、メモリーに収まらないほど細かい
    # ボクセルの格子を求めかねない）
    if not mpu > 0:
        raise ValueError(f"metersPerUnit must be positive ({mpu})")
    for key, lo, hi in (("voxelMm", 0.4, 2.0), ("clearanceMm", 0.2, 5.0), ("thicknessMm", 0.05, 5.0)):
        if key in g and not lo <= float(g[key]) <= hi:
            raise ValueError(f"{key} {g[key]} is outside {lo}-{hi}")

    def unit(mm):
        return mm / 1000.0 / mpu

    pos, nrm, uv, leg, mix, under, over, tris = load_input(job)
    n = len(pos)
    first, inv = weld(pos)
    wpos = pos[first]
    wleg = np.zeros(len(first))
    np.maximum.at(wleg, inv, leg)
    wmix = np.zeros(len(first))
    np.maximum.at(wmix, inv, mix)
    wunder = np.zeros(len(first))
    np.maximum.at(wunder, inv, under / mpu)  # 衣装の下に着るもの。メッシュの単位
    # ガーター・レッグラップ: 体の頂点ごとに測った下の層の高さは使わない（向かいの脚に当たるなどでばらつき、帯が凸凹に
    # なる。下の層が何枚も重なると一番外が分からない）
    if g["type"] in ("garter", "legwrap"):
        wunder = np.zeros(len(first))  # （下の層の上へは、仕上げの後に面から直接測って載せる: finish_surface）
    wover = np.full(len(first), np.inf)  # 上に着るもの: 衣装はその下にとどまらなければならない
    np.minimum.at(wover, inv, np.where(over > 0, over / mpu, np.inf))
    wtris = inv[tris]
    valid = (wtris[:, 0] != wtris[:, 1]) & (wtris[:, 1] != wtris[:, 2]) & (wtris[:, 0] != wtris[:, 2])
    tri_ids = np.nonzero(valid)[0]
    log(f"input verts {n} welded {len(first)} tris {len(tris)} valid {len(tri_ids)}")

    lm, up, lateral, hip_mid = build_landmarks(job)
    # 足の指ごとに骨を持つアバター: 溶接した頂点ごとの足の指の番号（0: どの指でもない）
    toe_digit = np.zeros(len(first), dtype=np.int64)
    pairs = np.array(job.get("toeDigits", []), dtype=np.int64).reshape(-1, 2)
    if len(pairs):
        np.maximum.at(toe_digit, inv[pairs[:, 0]], pairs[:, 1])
    work.toe_digit = toe_digit
    if g["type"] in ARM_TYPES:
        planes, seeds = arm_planes(g, lm, wpos, wtris[tri_ids], lateral, hip_mid)
    else:
        planes, seeds = make_planes(g, lm, up, hip_mid)
    body = BodySurface(wpos, wtris, tri_ids)
    work.g, work.mpu, work.unit, work.nrm, work.uv, work.tris, work.n = g, mpu, unit, nrm, uv, tris, n
    work.first, work.inv, work.wpos, work.wleg, work.wmix = first, inv, wpos, wleg, wmix
    work.wunder, work.wover = wunder, wover
    work.wtris, work.tri_ids, work.body = wtris, tri_ids, body
    work.lm, work.up, work.lateral, work.hip_mid = lm, up, lateral, hip_mid
    work.planes, work.seeds = planes, seeds
    if g["type"] in ("garter", "legwrap"):
        # 付け根の近く: 帯の上の縁が股の底より下に来るまで下げる（股をまたいで左右の脚がつながらない）。小物は 1 枚の
        # リボンなので、ゴムの帯などの部位に分けない（分けると、編み方の違うマテリアルが縞のように交互に出る）
        g = dict(g, waistband=False, runGuard=False, pantyPart=False)
        crotch = crotch_point(wpos, nrm[first], lm, up, lateral, hip_mid)
        key = "garterAt" if g["type"] == "garter" else ("wrapFrom" if g["wrapFrom"] < g["wrapTo"] else "wrapTo")
        half = unit(float(g["garterWidthMm"] if g["type"] == "garter" else g["wrapWidthMm"])) * 0.5
        lowest = top_below_crotch(crotch, lm, half if g["type"] == "garter" else 0.0, unit(3.0))
        if float(g[key]) < lowest:
            log(f"{key} {float(g[key]):.2f} lowered to {lowest:.2f} (below the crotch)")
            g[key] = lowest
        work.g = g
        work.planes, work.seeds = (
            garter_planes(g, lm, unit)
            if g["type"] == "garter"
            else legwrap_cut(g, lm, up, lateral, hip_mid, unit, wpos)
        )
    if g["type"] == "toecap":
        # 親指のサック: 足の指の塊で親指とほかの指の境目を切る（形で分けるときは、溶接した体のつながりを使う）
        work.body_part = None
        work.planes, work.seeds = toecap_cut(wpos, toe_groups(work), lm, up, lateral, hip_mid)
    return work


def refine_wrap_range(
    bm: bmesh.types.BMesh,
    wid: bmesh.types.BMLayerItem,
    g: dict[str, Any],
    lm: dict[str, np.ndarray],
    planes: list,
    hip_mid: np.ndarray,
    lateral: np.ndarray,
    unit: Callable[[float], float],
):
    """
    レッグラップ: 巻く範囲（両端から脚の鎖で 0.1 の余裕）で、リボンの縁の近くを通る辺（両端のらせんの形の値の小さい方が
    辺の長さより小さい）のうち、リボンの幅の WRAP_EDGE_FRAC 倍より長い物を WRAP_REFINE_ROUNDS 回まで二分する（粗い体では、らせんの縁が三角形の辺に
    沿ってぎざぎざになる。リボンの中や外は分けない）。足した頂点は体の頂点ではない（wid = −1）。
    """
    fields = {pl.side: pl for pl in planes if isinstance(pl, Field)}
    lo, hi = sorted((float(g["wrapFrom"]), float(g["wrapTo"])))
    longest = unit(float(g["wrapWidthMm"])) * WRAP_EDGE_FRAC
    chains = {side: chain_for(lm, side) for side in (1, -1)}

    def inside(v):
        s, _ = chain_param(chains[face_side(np.array(v.co), hip_mid, lateral)], np.array(v.co))
        return lo - 0.1 <= s <= hi + 0.1

    for _ in range(WRAP_REFINE_ROUNDS):
        old = set(bm.verts)
        val = {
            v: abs(fields[face_side(np.array(v.co), hip_mid, lateral)].signed(np.array(v.co)))
            for v in bm.verts
            if inside(v)
        }
        edges = [
            e
            for e in bm.edges
            if e.calc_length() > longest
            and all(v in val for v in e.verts)
            and min(val[v] for v in e.verts) < e.calc_length()
        ]
        if not edges:
            break
        bmesh.ops.subdivide_edges(bm, edges=edges, cuts=1, use_grid_fill=True)
        bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 3])
        for v in bm.verts:
            if v not in old:
                v[wid] = -1
    log(f"wrap range refined: {len(bm.faces)} faces")


def cover_region(work: Work):
    """
    1. 体の覆われる範囲（平面で切る）: 衣装の元になる体の部分、エンベロープ用にそれを閉じた複製、各体の頂点が範囲の
    どれだけ内側にあるか。
    """
    g, mpu, first, inv, wpos = work.g, work.mpu, work.first, work.inv, work.wpos
    wleg, wtris, tri_ids, lm, lateral = work.wleg, work.wtris, work.tri_ids, work.lm, work.lateral
    hip_mid, planes, seeds, unit = work.hip_mid, work.planes, work.seeds, work.unit
    # 体の覆われる範囲
    bm, _ = body_bmesh(wpos, wtris, tri_ids)
    wid = bm.verts.layers.int.new("wid")
    for i, v in enumerate(bm.verts):  # レイヤーを作ると、それ以前の BMVert の参照が無効になる
        v[wid] = i
    # ガーター・レッグラップ: 上に着ている、肌に密着した服（CLINGING_OVER_MM 以内。靴下・レッグウォーマーなど）で覆われる
    # 所の面は作らない（スカートの裏などのゆったりした物の下は作る）（タイツの上の
    # リボンは服の高さより外に出るので、服の下に収められず突き抜ける。覆われる所はどのみち外から見えない）
    if g["type"] in ("garter", "legwrap"):
        covered_by = [
            f
            for f in bm.faces
            if all(0 <= v[wid] < len(first) and work.wover[v[wid]] < unit(CLINGING_OVER_MM) for v in f.verts)
        ]
        bmesh.ops.delete(bm, geom=covered_by, context="FACES")
        remove_loose(bm)
        if covered_by:
            log(f"faces under clothes worn over dropped: {len(covered_by)}")

    if g["type"] == "legwrap":
        refine_wrap_range(bm, wid, g, lm, planes, hip_mid, lateral, unit)
    cut_by_planes(bm, planes, hip_mid, lateral)

    def mostly_leg(members):
        ids = {v[wid] for f in members for v in f.verts if 0 <= v[wid] < len(first)}
        return bool(ids) and float(np.mean(wleg[list(ids)])) >= 0.5

    # （腕の衣装は腕だけを残す: 垂れた腕に対する無限の平面では、脚の一部も通ってしまう）
    arm = g["type"] in ARM_TYPES
    # （ガーター・レッグラップは切り方が正確なので、切った後に残った面を、左右の脚にまたがる塊のほかはすべて残す: 縁を
    # 細かくした頂点は体の頂点の番号を持たず脚の塊と判定できない。らせんの切れ端を爪の島として外さない）
    accessory = g["type"] in ("garter", "legwrap")

    def one_leg(members):
        """（小物）塊の面がすべて同じ脚の側にあるか: 股の間で左右にまたがる切れ端は残さない。"""
        return len({face_side(np.array(f.calc_center_median()), hip_mid, lateral) for f in members}) == 1

    kept, total, islands = keep_components(
        bm, seeds, one_leg if accessory else None if arm else mostly_leg, 0.0 if accessory else unit(8.0)
    )
    log(f"region components kept {kept}/{total}, faces {len(bm.faces)}")
    if not bm.faces:
        raise RuntimeError("no faces in the garment region")

    # 体の切り口（肌が途中までしか無い体）: 斜めの縁を、まっすぐな上端・下端で切りそろえる（腕では、縁に近い腕の区間の
    # 向きで。腕を一周する長さは脚より短い）
    def arm_axis(p: np.ndarray) -> np.ndarray:
        chain = arm_chain(lm, face_side(p, hip_mid, lateral))[:3]
        segs = [(chain[i], chain[i + 1]) for i in range(len(chain) - 1)]
        a, b = min(segs, key=lambda s: float(np.linalg.norm(p - (s[0] + s[1]) * 0.5)))
        return normalize(b - a)

    if arm:
        edges = body_edge_planes(bm, planes, arm_axis, hip_mid, lateral, hip_width(lm) * 0.5, unit(0.3))
    else:
        edges = body_edge_planes(bm, planes, work.up, hip_mid, lateral, hip_width(lm), unit(0.3))
    if edges:
        # （面が半分未満に減るなら切りそろえない: 脚の形が人と違う体では、切り口がいくつもあって何も残らない）
        before = bm.copy()
        cut_by_planes(bm, edges, hip_mid, lateral)
        keep_components(bm, seeds, None if arm else mostly_leg)
        if len(bm.faces) * 2 < len(before.faces):
            log(
                f"not trimmed at the body's own edges: {len(edges)} planes would leave {len(bm.faces)} of "
                f"{len(before.faces)} faces"
            )
            bm.free()
            bm = before
            wid = bm.verts.layers.int["wid"]
        else:
            before.free()
            planes = planes + edges
            work.planes = planes
            log(f"trimmed at the body's own edges: {len(edges)} planes, faces {len(bm.faces)}")
    # 手足の指から離れた島になっている爪: 布にはせず（それぞれが別の布片になる）、その下の肌の頂点で、布を肌から
    # 少なくともその高さだけ離す
    wraise = np.zeros(len(first))
    if islands:
        bm.faces.ensure_lookup_table()
        tree = BVHTree.FromBMesh(bm)
        for pts in islands:
            for p in pts:
                loc, nrm_f, fi, _ = tree.find_nearest(Vector(p))
                if loc is None:
                    continue
                h = float((p - np.array(loc)) @ np.array(nrm_f))
                for v in bm.faces[fi].verts:
                    if 0 <= v[wid] < len(first):
                        wraise[v[wid]] = max(wraise[v[wid]], h)
        log(f"islands under the garment {len(islands)}, raised verts {int((wraise > 0).sum())}")
    work.wraise, work.islands = wraise, islands

    # 元の体の頂点の、覆われ方の距離（メートル、範囲の内側）
    wcover = np.full(len(first), -1.0)
    # フェイクサイハイのストッキング部分は何も覆わない（その肌が透けて見える）。ただし肌を衣装の内側へ写す場合
    # （肌の写し）は除く
    fakes = [] if g.get("skinLining", False) else fake_tops(g, lm)
    for v in bm.verts:
        w = v[wid]
        if w < 0 or w >= len(first):
            continue
        p = wpos[w]
        side = face_side(p, hip_mid, lateral)
        wcover[w] = min(
            [pl.signed(p) * mpu for pl in planes if pl.side in (0, side)]
            + [-((p - co) @ no) * mpu for fs, co, no in fakes if fs == side]
            or [1e9]
        )
    covered = wcover[inv].astype(np.float32)
    body_part = bm.copy()
    bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary], sides=0)
    region_obj = new_object("region", bm)
    bm.free()
    work.covered, work.body_part, work.region_obj = covered, body_part, region_obj


def make_envelopes(work: Work):
    """
    2. エンベロープ: 閉じた範囲を clearance + thickness の SDF 表面にしたもの（足は指の間を閉じる）、つま先の周りは
    より細かく、お尻は割れ目を橋渡しする。
    """
    g, unit, nrm, first, wpos = work.g, work.unit, work.nrm, work.first, work.wpos
    wtris, tri_ids, lm, up, lateral = work.wtris, work.tri_ids, work.lm, work.up, work.lateral
    hip_mid, region_obj = work.hip_mid, work.region_obj
    # エンベロープ: 閉じた範囲 -> clearance + thickness の SDF 表面（足は指の間を閉じる）
    clearance = unit(g["clearanceMm"])
    thickness = unit(g["thicknessMm"])
    offset = clearance + thickness
    bridge = g["type"] not in ("spats",) + ARM_TYPES and g.get("toeBridgeMm", 4.0) > 0
    feet = closed_feet(wpos, wtris, tri_ids, lm, up) if bridge else None
    # エンベロープ（沈んだ頂点を持ち上げ、内側の形状を捨てるための目安）は粗い: 範囲全体の SDF を細かい格子で作ると、
    # 時間の大半を占める。SDF 表面から作るつま先だけを、足だけから細かい格子で作る（厚い布は肌から遠くに立ち、
    # 滑らかなので、そこも粗くする）。
    fine = unit(max(g.get("voxelMm", 0.8), (g["clearanceMm"] + g["thicknessMm"]) * 0.4))
    coarse = max(fine, unit(ENVELOPE_VOXEL_MM))
    smooth = SDF_SMOOTH
    close_r = unit(g.get("toeBridgeMm", 4.0) + g["thicknessMm"])
    surf = sdf_surface(region_obj, None, coarse, offset, close_r, smooth)
    log(f"sdf surface verts {len(surf.verts)}")
    envelope = BVHTree.FromBMesh(surf)
    toe_surf = None
    # 足袋型: 親指以外の指を外側へ開いてから SDF にし（親指と人差し指の間を閉じない）、表面を元の位置へ戻す
    splits = []
    if feet is not None and g.get("tabi", False):
        fco = np.array([v.co for v in feet.data.vertices])
        splits = big_toe_splits(fco, toe_groups(work), lm, up, lateral, hip_mid)
        for sp in splits:
            fco = sp.spread(fco, TABI_SPREAD)
        feet.data.vertices.foreach_set("co", fco.ravel())
        feet.data.update()
        log(f"tabi: big toe split on {len(splits)} feet")
    if feet is not None:
        feet_region = feet.copy()
        feet_region.data = feet.data.copy()
        bpy.context.scene.collection.objects.link(feet_region)
        toe_surf = sdf_surface(feet_region, feet, fine, offset, close_r, smooth, feet_only=True)
        log(f"toe surface verts {len(toe_surf.verts)}")
        if splits:
            tco = np.array([v.co for v in toe_surf.verts])
            for sp in splits:
                tco = sp.unspread(tco, TABI_SPREAD)
            for v, q in zip(toe_surf.verts, tco):
                v.co = Vector(q)
    work.tabi_splits = splits
    # 割れ目を閉じたお尻（クロージングの半径の球は入り込めない: 割れ目は入り口が広い）
    wnrm = nrm[first]
    seat = Seat(wpos, wnrm, lm, up, lateral, hip_mid, wtris) if g["type"] in ("tights", "spats") else None
    seat_env = None
    if seat is not None:
        seat_obj, seat_info = seat.body(wpos, wtris, tri_ids)
        if seat_obj is not None:
            # 割れ目をまたぐだけの表面には粗い格子で足りる（細かい格子では、この半径で数分かかる）
            seat_surf = sdf_surface(
                seat_obj,
                seat_info,
                unit(max(SEAT_VOXEL_MM, g.get("voxelMm", 0.8))),
                offset,
                unit(SEAT_CLOSE_MM),
                SDF_SMOOTH,
            )
            seat_env = BVHTree.FromBMesh(seat_surf)
            log(f"seat surface verts {len(seat_surf.verts)}")
            seat_surf.free()
    work.offset, work.thickness, work.bridge, work.surf, work.envelope = offset, thickness, bridge, surf, envelope
    work.toe_surf, work.seat, work.seat_env = toe_surf, seat, seat_env


def seat_cleft(
    seat: Seat | None,
    seat_env: BVHTree | None,
    body: BodySurface,
    loc: np.ndarray,
    worn: float,
    worn_usual: float,
    offset: float,
    up: np.ndarray,
    unit: Callable[[float], float],
) -> tuple[float, tuple[np.ndarray, np.ndarray] | None, float]:
    """
    体の点がお尻の割れ目にどう位置するか: そこでのお尻のウェイト（外では 0。橋渡し面への道が股から下へ向かう所では
    弱める）、橋渡し面の最も近い点と法線（橋渡ししない所では None: 表面の下にないか、体が間にある）、そこで橋渡し面
    の上で測った、下に着るもの。
    """
    sw = seat.weight(loc) if seat_env is not None else 0.0
    seat_hit = None
    if sw > 0:
        hit, hn, _, _ = seat_env.find_nearest(Vector(loc))
        if hit is not None:
            # 体の点が橋渡し面（肌から `offset` 離れて立つ）の下にどれだけ深くあるか
            cleft = -float((loc - np.array(hit)) @ np.array(hn)) - offset
            # 開いた割れ目を通してだけ: 太ももの脇の溝では、最も近い橋渡し面が太ももの向こう側にあるかもしれない
            way = np.array(hit) - loc
            # （少し進んだ所から: 曲がった壁を浅い角度で離れると、その壁自体をかすめる）
            start = loc + normalize(way) * unit(1.0)
            block = body.bvh.ray_cast(Vector(start), Vector(normalize(way)), float(np.linalg.norm(way)))[0]
            # そして股から下へではない: 太ももの間では、閉じた表面が股よりずっと下に垂れ下がる（弱めていくので、
            # 橋渡しが折れ目で終わらない）
            sw *= float(np.clip((normalize(way) @ up + 0.8) / 0.5, 0.0, 1.0))
            if cleft > unit(0.2) and block is None and sw > 0:
                seat_hit = (np.array(hit), np.array(hn))
                # 割れ目の上では、下に着るものは高さの残りの分だけ橋渡し面より上に立つ。ただし通常の厚みより
                # 小さくはしない（その高さは肌の法線に沿って、割れ目を横切って測っている）
                over_bridge = max(worn_usual + unit(1.0), worn - cleft)
                worn += (min(over_bridge, worn) - worn) * sw
    return sw, seat_hit, worn


def crease_room(
    body: BodySurface,
    loc: np.ndarray,
    nrm_l: np.ndarray,
    lift: float,
    hip_mid: np.ndarray,
    lateral: np.ndarray,
    up: np.ndarray,
    unit: Callable[[float], float],
) -> float:
    """
    狭い溝の中で、体の点を肌からどこまで持ち上げてよいか（溝の中でなければ inf）: 向かいの肌までの半分以内
    （向かい合う壁の衣装が互いに交差し、暗い折れ目として見える）。脚の間で体の中心を越えることは決してない。
    """
    half = np.inf
    facing = body.bvh.ray_cast(Vector(loc + nrm_l * unit(0.3)), Vector(nrm_l), float(lift * 2.0))
    if facing[0] is not None:
        half = (facing[3] + unit(0.3)) * 0.5
    # 体の中心は決して越えない: 太ももの間では向かいの肌がないことがあり、下に着るもの（法線に沿って測る）が
    # 反対側の上にあることがある
    side_d = float((loc - hip_mid) @ lateral)
    toward_mid = float(nrm_l @ lateral) * -np.sign(side_d)
    # （脚の間、股関節より下だけ: ほかの所では中心の溝は体自身の形である）
    if abs(side_d) > unit(0.5) and toward_mid > 0.05 and float((loc - hip_mid) @ up) < 0:
        half = min(half, abs(side_d) / toward_mid * 0.9)
    return half


def onto_envelope(
    envelope: BVHTree, q: np.ndarray, bridged: bool, unit: Callable[[float], float]
) -> tuple[np.ndarray, bool, bool]:
    """
    持ち上げた点をエンベロープと照らし合わせる: （行き先, 体の奥深くにあるか: 表面のものではない内側の部分, エンベロープの
    上へ持ち上げたか）。橋渡しした点はそのままにする（体の内側ではなく、割れ目の中へ引き戻すべきでもない）。
    """
    hit, hn, _, _ = envelope.find_nearest(Vector(q))
    if bridged or hit is None:
        return q, False, False
    depth = -float((q - np.array(hit)) @ np.array(hn))
    if depth > unit(6.0):
        return q, True, False
    if unit(0.2) < depth <= unit(3.0):
        # 少し沈んだ（へそ、小さな溝）: エンベロープの上へ持ち上げる。より深い溝はなぞる: ウェイトの元の体の点から遠くへ
        # 動かした頂点は、関節が回ると肌から離れていく。
        return np.array(hit), False, True
    return q, False, False


def taut_zone(work: Work, bm: bmesh.types.BMesh, src: bmesh.types.BMLayerItem) -> dict:
    """
    布を膜のように張る指先の頂点と、その指の番号（元の体の点で決める）: 指を包む手袋の指先と、5 本指の足の指
    （足の指ごとに骨を持つアバターは骨で、無いアバターは形で指を分ける: toe_tips）。切り口の縁の頂点は動かさない。
    """
    if work.g["type"] == "gloves" and not work.g.get("fingerless", False):
        tips = fingertip_zone(np.array([v[src] for v in bm.verts]), work.lm, work.lateral, work.hip_mid)
        return {v: int(k) for v, k in zip(bm.verts, tips) if k >= 0 and not v.is_boundary}
    if work.g["type"] == "toecap":
        # 親指のサック: 足ごとに 1 つの膜（爪の上に張る）。口から先端までの前半分だけ（口の近くまで凸包に張ると、
        # 口が外へ開く）
        out = {}
        for side in (1, -1):
            mine = [v for v in bm.verts if face_side(np.array(v[src]), work.hip_mid, work.lateral) == side]
            mouth = [np.array(v[src]) for v in mine if v.is_boundary]
            if not mouth:
                continue
            c0 = np.mean(mouth, axis=0)
            far = max(mine, key=lambda v: float(np.linalg.norm(np.array(v[src]) - c0)))
            axis = np.array(far[src]) - c0
            for v in mine:
                if not v.is_boundary and float((np.array(v[src]) - c0) @ axis) > 0.5 * float(axis @ axis):
                    out[v] = 1 if side > 0 else 2
        return out
    if work.g["type"] not in ARM_TYPES and not work.bridge and work.toe_digit.any():
        # 元の体の点で最も重心の大きい角の、足の指の番号ごと
        out = {}
        for v in bm.verts:
            if v.is_boundary:
                continue
            t, bary, _ = work.body.bind(np.array(v[src]))
            d = int(work.toe_digit[work.wtris[t][int(np.argmax(bary))]])
            if d > 0:
                out[v] = d
        return out
    if work.g["type"] not in ARM_TYPES and not work.bridge:
        return {v: k for v, k in toe_tips(work, bm, np.array([v[src] for v in bm.verts])).items() if not v.is_boundary}
    return {}


def toe_tips(work: Work, bm: bmesh.types.BMesh, pts: np.ndarray, skip: np.ndarray | None = None) -> dict:
    """
    足の指の骨の無いアバターの 5 本指の足の指先: 足ごとに、足首から前への到達の割合 r（0.6 から 0.95 まで）より前で
    足首より下の頂点を辺のつながりで塊（8 頂点以上）に分け、塊がちょうど 5 つになる最小の r の塊を、指ごとの区間に
    する（足の指は付け根でつながっているので、指が 1 本ずつに分かれる所より先だけ。5 つにならない足は張らない）。
    `pts` は頂点の体の点（bm.verts の順）。`skip` の頂点は数えない。頂点 -> 指の番号。
    """
    bm.verts.ensure_lookup_table()  # （高品質の細分化の後にも呼ばれる）
    index = {v: i for i, v in enumerate(bm.verts)}
    sides = np.array([face_side(p, work.hip_mid, work.lateral) for p in pts])
    out = {}
    for n, side in enumerate((1, -1)):
        frame = foot_frame(pts, work.lm, work.up, work.lateral, work.hip_mid, side)
        if frame is None:
            continue
        ankle, fwd, ahead, _ = frame
        along = (pts - ankle) @ fwd / ahead
        low = (sides == side) & ((pts - ankle) @ work.up < 0)
        if skip is not None:
            low &= ~skip
        for r in np.arange(0.6, 0.951, 0.025):
            inside = low & (along > r)
            seen = np.zeros(len(pts), dtype=bool)
            groups = []
            for i in np.nonzero(inside)[0]:
                if seen[i]:
                    continue
                group, stack = [], [i]
                seen[i] = True
                while stack:
                    cur = stack.pop()
                    group.append(cur)
                    for e in bm.verts[cur].link_edges:
                        o = index[e.other_vert(bm.verts[cur])]
                        if inside[o] and not seen[o]:
                            seen[o] = True
                            stack.append(o)
                if len(group) >= 8:
                    groups.append(group)
            if len(groups) == 5:
                for k, group in enumerate(groups):
                    for i in group:
                        out[bm.verts[i]] = 1000 * (n + 1) + k
                break
    return out


def small_parts(bm: bmesh.types.BMesh) -> np.ndarray:
    """
    辺でつながった塊のうち、最も大きい塊の 5% より小さい物の頂点（bm.verts の順）: 爪などの離れた部品（指の塊と
    数えない）。
    """
    index = {v: i for i, v in enumerate(bm.verts)}
    part = np.full(len(bm.verts), -1, dtype=np.int64)
    sizes = []
    for v0 in bm.verts:
        if part[index[v0]] >= 0:
            continue
        part[index[v0]] = len(sizes)
        stack, size = [v0], 0
        while stack:
            v = stack.pop()
            size += 1
            for e in v.link_edges:
                o = e.other_vert(v)
                if part[index[o]] < 0:
                    part[index[o]] = len(sizes)
                    stack.append(o)
        sizes.append(size)
    sizes = np.array(sizes)
    return sizes[part] < sizes.max() * 0.05 if len(sizes) else np.zeros(0, dtype=bool)


def toe_groups(work: Work) -> tuple[np.ndarray, np.ndarray]:
    """
    足の指ごとの体の点とその指の番号: 足の指ごとに骨を持つアバターは骨のウェイトで、無いアバターは形で分ける
    （toe_tips。5 本に分かれない足は含まない）。
    """
    if work.toe_digit.any():
        keep = work.toe_digit > 0
        return work.wpos[keep], work.toe_digit[keep]
    bm = work.body_part if work.body_part is not None else body_bmesh(work.wpos, work.wtris, work.tri_ids)[0]
    bm.verts.ensure_lookup_table()
    tips = toe_tips(work, bm, np.array([v.co for v in bm.verts]), small_parts(bm))
    if tips:
        out = np.array([v.co for v in tips]), np.array(list(tips.values()))
    else:
        out = np.zeros((0, 3)), np.zeros(0, dtype=np.int64)
    if bm is not work.body_part:
        bm.free()
    return out


def lift_off_skin(work: Work):
    """
    3. 体の法線に沿って外へ動かした体の部分。各頂点は元の体の点を覚えている。
    """
    g, unit, body, wmix, wunder, wraise = work.g, work.unit, work.body, work.wmix, work.wunder, work.wraise
    wover, wtris, up, lateral, hip_mid = work.wover, work.wtris, work.up, work.lateral, work.hip_mid
    planes = work.planes
    body_part, offset, envelope, seat, seat_env = work.body_part, work.offset, work.envelope, work.seat, work.seat_env
    # 範囲を体の法線に沿って外へ動かす。各頂点は元の体の点を覚えている。ボーンが混ざる所（関節）では、
    # 線形ブレンドスキニングで関節を曲げるとオフセットが縮むので、そこでは余裕を多めにとる。
    joint_extra = unit(g.get("jointExtraMm", 2.0))
    # 縁は体の三角形を横切って切るので、そのウェイトは補間され、腰が動くと肌から離れていく。本物のタイツの
    # ゴムのように、縁の近くでは衣装を肌から少し離して立たせる。
    # （ガーター・レッグラップは下の層の高さを使わず、仕上げの後に一番外の面に載せる）
    worn_extra = unit(WORN_EXTRA_MM) if g["type"] not in ("garter", "legwrap") else 0.0
    band_width = unit(HEM_BAND_MM)
    # （親指のサックは小さく、口は指に沿ってぴったり締まる。ガーター・レッグラップは帯そのものが締まる）
    band_lift = unit(HEM_LIFT_MM) if g["type"] not in ("toecap", "garter", "legwrap") else 0.0

    # （腕の衣類で手を切る滑らかな形（指の付け根・親指の穴）の縁は浮かせない: 手は小さく、縁から 25 mm の帯で
    # 口が釣り鐘のように開く。浮かせるのは腕の上端・手首の平面の縁だけ）
    hem_planes = [pl for pl in planes if not (g["type"] in ARM_TYPES and isinstance(pl, Field))]

    def hem_lift(p):
        side = face_side(p, hip_mid, lateral)
        d = min([pl.signed(p) for pl in hem_planes if pl.side in (0, side)] or [1e9])
        f = float(np.clip(1.0 - d / band_width, 0.0, 1.0))
        return band_lift * f * f * (3.0 - 2.0 * f)

    src = body_part.verts.layers.float_vector.new("lw_src")
    exact = body_part.verts.layers.int.new("lw_exact")
    taut = body_part.verts.layers.int.new("lw_taut")  # 膜のように張った指先（出力の陰影は布自身の法線）
    # 衣装を作る土台（lw_under.snap_to_under）: どの頂点も dict に入れる前に、ここで作る（レイヤーを足すと参照が
    # 無効になり、下の frames が頂点を失う）
    utri = body_part.verts.layers.int.new("lw_utri")
    ubary = body_part.verts.layers.float_vector.new("lw_ubary")
    for v in body_part.verts:
        v[utri] = -1
    fill_inner_holes(body_part, planes, unit(0.05))
    deep = set()
    capped = {}
    narrow = {}
    frames = {}
    lifted = 0
    bridged = 0
    seated = {}  # 橋渡しした頂点 -> 橋渡し面の上の余裕
    # 下に着るものの通常の厚み（割れ目の中では、その高さは割れ目を横切って測る）
    worn_usual = float(np.median(wunder[wunder > 0])) if (wunder > 0).any() else 0.0
    # 下に着るものは衣装の内側にとどめる（"wornInside"。それを隠す不透明な衣装では常に）: そのすべての上に
    # 保つ。そうでない場合（透ける衣装で、股の見た目を優先）は、その浮いた部分を低めに保つので、衣装は太ももの間の
    # フリルの周りで自分と交差しない。その一部は突き抜けてもよい。
    hold = g.get("wornInside", True)
    worn_cap = np.inf if hold else max(unit(4.0), worn_usual * 2.0)
    for v in body_part.verts:
        t, bary, loc = body.bind(np.array(v.co))
        joint = float(np.clip(bary @ wmix[wtris[t]] * 2.0, 0.0, 1.0))
        worn = float(bary @ wunder[wtris[t]])
        worn_raw = worn
        # その浮いた部分（フリル、溝をまたぐ所）は肌から遠くに立つ: 法線に沿ってそこまで持ち上げると、衣装が
        # 折り重なる。代わりに衣装がそれを押さえ込む
        worn = min(worn, worn_cap)
        sw, seat_hit, worn = seat_cleft(seat, seat_env, body, loc, worn, worn_usual, offset, up, unit)
        if worn > 0:
            # 下に着るものは自分のウェイトで動き、肌とまったく同じには動かない: 余裕を多めにとる
            worn += worn_extra * float(np.clip(worn / unit(1.0), 0.0, 1.0))
        lift = offset + worn + max(joint_extra * joint, hem_lift(loc))
        # 島になっている爪の上（cover_region）
        raised = float(bary @ wraise[wtris[t]])
        raise_to = offset + raised if raised > 0 else 0.0
        lift = max(lift, raise_to)
        nrm_l = body.normal(t, bary)
        half = crease_room(body, loc, nrm_l, lift, hip_mid, lateral, up, unit)
        if np.isfinite(half):
            # それでも下に着るものと、島になっている爪の上: それより低くはしない。ただし股（股関節より下で体の中心の
            # 近く）でくぼみが下に着るものの高さより狭い所は除く: 向かい合う壁をそれぞれ下に着るものの高さまで持ち上げる
            # と、左右の衣装が交差して折れ目になり、後のならしでも取れない（下に着るものはくぼみをまたいでいる）
            floor = offset + worn if hold and worn > 0 else 0.0
            if floor > half and float((loc - hip_mid) @ up) < 0 and abs(float((loc - hip_mid) @ lateral)) < unit(40.0):
                floor = 0.0
            half = max(unit(0.1), half, floor, raise_to)
            lift = min(lift, half)
            narrow[v] = (loc, nrm_l, half)  # エンベロープとくぼみの埋めの後で、もう一度強制する
        cap = under_cap(wover, wtris, t, bary, unit)
        if np.isfinite(cap):
            # 靴下などの下: エンベロープとくぼみの埋めの後で、もう一度強制する（それらは外へ押し出してしまう）
            capped[v] = (loc, body.normal(t, bary), cap)
            lift = min(lift, cap)
        q = loc + body.normal(t, bary) * lift
        if seat_hit is not None:
            # お尻の割れ目の中: 橋渡し面の上へ外向きに（内へは決して動かさない）、同じ余裕をとって。体の点から
            # 測る: それぞれの法線に沿って持ち上げると、狭い割れ目の向かい合う壁が互いに交差する。
            room = lift - offset
            if worn_raw > 0:
                # そして下に着るもの（これも割れ目をまたぐ。橋渡し面より平らなことが多い）の外側: その表面は、
                # 肌の法線がそれと交わる所にある
                worn_pt = loc + body.normal(t, bary) * worn_raw
                # （下に着るものの通常の高さより高くはしない: 深い割れ目を横切って測ると、肌の法線がずっと外で
                # それと交わることがあり、衣装がお尻からひれのように突き出る）
                over = min(float((worn_pt - seat_hit[0]) @ seat_hit[1]), worn_usual * 1.5)
                room = max(room, over + worn_extra)
            q = q + (seat_hit[0] + seat_hit[1] * room - q) * sw
            bridged += 1
            if sw > 0.5:
                seated[v] = room
        q, inner, onto = onto_envelope(envelope, q, sw > 0.5, unit)
        if inner:
            deep.add(v)
        lifted += onto
        v.co = Vector(q)
        v[src] = Vector(loc)
        v[exact] = 1
        # （橋渡しした頂点は、橋渡し面を肌とみなして、その上の高さを測る）
        # （縁での持ち上げも含める: 続く平滑化は、その及ぶ範囲が縁まで広がる所で持ち上げを消してしまい、
        # 平滑化が届かない頂点にだけ残る。ウエストに沿ったのこぎりの歯になる）
        frames[v] = (
            (seat_hit[0] - seat_hit[1] * offset, seat_hit[1], offset + seated[v])
            if v in seated
            else (loc, nrm_l, max(min(lift, (offset + worn if hold else offset) + hem_lift(loc)), raise_to))
        )
    # 割れ目の橋渡しした壁は、橋渡し面の上に順序が乱れて載ることがある（折れた面）: 橋渡し面に沿って均す
    for _ in range(8):
        moved = {}
        for v in seated:
            nb = [e.other_vert(v).co for e in v.link_edges]
            if nb:
                c = sum(nb, Vector()) / len(nb)
                hit, hn, _, _ = seat_env.find_nearest(c)
                if hit is not None:
                    moved[v] = hit + hn * seated[v]
        for v, c in moved.items():
            v.co = c
    keep_seated = {v: v.co.copy() for v in seated}
    fill_crevices(body_part, unit(4.0))
    # 手足の指先: 伸びた布は爪の付け根や両脇の溝、長い爪の先の下に入り込まず膜のように渡り、出っ張りの角は丸まる
    # （爪の形を写さない）。後の、肌に沿わせる処理（くしゃくしゃのならし、こぶを下げる）からは外す: 張った面は
    # 爪の下の肌とは向きが違い、くしゃくしゃとみなされて元へ戻される
    zone = taut_zone(work, body_part, src)
    if zone:
        tension_over(body_part, zone, body, src, offset, unit(15.0 if g["type"] in ARM_TYPES else 6.0), work.islands)
        for v in zone:
            v[taut] = 1
        cover_islands(body_part, work.islands, offset * 0.75)
        for v in zone:
            frames.pop(v, None)
            # （狭い溝の制限も外す: 爪の先の下の溝では向かいの肌が爪の裏で、布が溝へ押し戻されて段になる）
            narrow.pop(v, None)
        log(f"fingertips drawn taut: {len(zone)} verts")
    for v, c in keep_seated.items():
        v.co = c  # 橋渡し済み。そこでの法線は当てにならない
    press_under_caps(capped, seated)
    press_under_caps(narrow, seated)
    bmesh.ops.delete(
        body_part, geom=[f for f in body_part.faces if all(x in deep for x in f.verts)], context="FACES_ONLY"
    )
    remove_loose(body_part)
    log(
        f"body part faces {len(body_part.faces)}, lifted onto the envelope {lifted}, bridged at the seat {bridged}, "
        f"inner verts {len(deep)}"
    )
    work.joint_extra, work.worn_extra, work.band_lift = joint_extra, worn_extra, band_lift
    work.src, work.exact, work.utri, work.ubary = src, exact, utri, ubary
    work.capped, work.narrow, work.frames, work.seated = capped, narrow, frames, seated
    work.worn_usual, work.hold, work.worn_cap = worn_usual, hold, worn_cap


def replace_toes(work: Work):
    """
    4. つま先: 母指球より前の SDF 表面を、体の部分へ縫い合わせる。エンベロープを解放する。
    """
    g, unit, body, wover, wtris = work.g, work.unit, work.body, work.wover, work.wtris
    lm, up, lateral, hip_mid = work.lm, work.up, work.lateral, work.hip_mid
    body_part, bridge, surf, toe_surf, src = work.body_part, work.bridge, work.surf, work.toe_surf, work.src
    exact = work.exact
    # つま先: 母指球より前の SDF 表面を、体の部分へ縫い合わせる（足の表面がなければ無し）
    zones = toe_zones(body_part, lm, up, lateral, hip_mid) if bridge and toe_surf is not None else []
    work.toe_zones = zones
    if zones:
        # そこの体と同じ密度
        toe_target = sum(
            len(f.verts) - 2
            for f in body_part.faces
            if any(z.holds(np.array(f.calc_center_median()), hip_mid, lateral) for z in zones)
        )
        toe_target = max(200, int(toe_target * float(g.get("density", 1.0))))
        split_by_zones(body_part, zones, hip_mid, lateral, keep_toes=False)
        toes = near_zones(toe_surf, zones, hip_mid, lateral)
        split_by_zones(toes, zones, hip_mid, lateral, keep_toes=True)
        toe_obj = new_object("toes", toes)
        toes.free()
        decimate(toe_obj, toe_target)
        tbm = bmesh.new()
        tbm.from_mesh(toe_obj.data)
        vmap = {}
        for v in tbm.verts:
            nv = body_part.verts.new(v.co)
            nv[src] = v.co
            nv[exact] = 0
            vmap[v] = nv
        for f in tbm.faces:
            try:
                body_part.faces.new([vmap[v] for v in f.verts])
            except ValueError:
                pass
        tbm.free()
        body_part.verts.ensure_lookup_table()
        eps = unit(0.05)
        for z in zones:

            def on_plane(v, want):
                return (
                    v[exact] == want
                    and abs(z.plane.signed(np.array(v.co))) < eps
                    and z.near(np.array(v.co), hip_mid, lateral)
                )

            a = boundary_loop(body_part, lambda v: on_plane(v, 1))
            b = boundary_loop(body_part, lambda v: on_plane(v, 0))
            if a is None or b is None:
                log(f"WARNING toe seam on side {z.side} not zipped (loops {a is not None}/{b is not None})")
                continue
            log(f"toe seam side {z.side}: {len(a)} + {len(b)} verts, {zip_loops(body_part, a, b, z.plane.no)} tris")
        bmesh.ops.recalc_face_normals(body_part, faces=body_part.faces[:])
        # 2 つの部分は少し違う高さで出会う（SDF 表面は均してあり、体の部分は肌に沿う）: 継ぎ目の周りの帯を均し、
        # 足を横切る線として見えないようにする
        for z in zones:
            band_w = float(np.linalg.norm(z.plane.co - z.ankle)) * 0.12
            band = [
                v
                for v in body_part.verts
                if abs(z.plane.signed(np.array(v.co))) < band_w
                and z.near(np.array(v.co), hip_mid, lateral)
                and not v.is_boundary
            ]
            smooth_halfway(band, 6)
        # つま先を、実物のストッキングのように、5 本の指をまとめて包む 1 つの面に張る: 足ごとの凸包まで持ち上げ
        # （指の間や爪の溝に入り込まない）、角を丸める。縁（継ぎ目の帯の外側）は動かさない
        taut = {
            v: k
            for v in body_part.verts
            if not v.is_boundary
            for k, z in enumerate(zones)
            if z.holds(np.array(v.co), hip_mid, lateral)
        }
        # 足袋型: 親指はそれだけで張る（ほかの 4 本の指とは別の凸包）
        for sp in work.tabi_splits:
            for v in taut:
                if sp.big_toe(np.array(v.co)):
                    taut[v] = len(zones) + (1 if sp.side > 0 else 2)
        tension_over(body_part, taut, body, src, work.offset, unit(6.0), work.islands, nearest=True)
        log(f"toes drawn taut: {len(taut)} verts")
        # 靴の中も: つま先はエンベロープで、自分の体の点を持たないので、最も近い体の点で決める
        toe_caps = {}
        for v in body_part.verts:
            if v[exact]:
                continue
            t, bary, loc = body.bind(np.array(v.co))
            cap = under_cap(wover, wtris, t, bary, unit)
            if np.isfinite(cap):
                toe_caps[v] = (loc, body.normal(t, bary), cap)

        def cap_toes():
            hit = 0
            for v, (loc, nrm_v, cap) in toe_caps.items():
                out = float((np.array(v.co) - loc) @ nrm_v)
                if out > cap:
                    v.co = Vector(np.array(v.co) - nrm_v * (out - cap))
                    hit += 1
            return hit

        toe_capped = cap_toes()
        # 最も近い肌の法線（指の先の周りで急に向きを変える）に沿って 1 つずつ押し込むと、くしゃくしゃになる:
        # 均してから内側に戻すことを、何回か繰り返す
        inner = [v for v in toe_caps if not v.is_boundary]
        for _ in range(5 if toe_capped else 0):
            smooth_halfway(inner, 1)
            cap_toes()
        log(f"toe verts kept inside what is worn over them: {toe_capped}")
    # 指から離れた島の爪（布にしていない）を、つま先の面が覆うまで押し出す（つま先の面は島を含まない範囲から作る）
    pushed = cover_islands(body_part, work.islands, work.offset * 0.75) if zones else 0
    if pushed:
        log(f"islands covered by pushing the garment out: {pushed}")
    surf.free()
    if toe_surf is not None:
        toe_surf.free()


def hold_in_seat(
    body_part: bmesh.types.BMesh,
    seat: Seat,
    src: bmesh.types.BMLayerItem,
    hip_mid: np.ndarray,
    floors: dict[bmesh.types.BMVert, tuple],
    slack: float,
    central: float = 0.0,
) -> int:
    """
    お尻の高さで、その輪郭より後ろへ `slack` を超えて突き出るものはない（下に着るものは、割れ目を横切って法線に
    沿って測ると、ずっと外まで届くことがある: その後ろのひれ）。`floors`（頂点 -> (肌の点, 法線, 高さ)）が立つべきと
    する所より下には決してしない。お尻のウェイトが `central` を超える所だけ。抑えた数を返す。
    """
    fins = 0
    for v in body_part.verts:
        limit = seat.back_limit(np.array(v[src]))
        if limit is None or seat.weight(np.array(v[src])) <= central:
            continue
        behind = float((np.array(v.co) - hip_mid) @ seat.fwd) - (limit - slack)
        if behind < 0:
            moved = np.array(v.co) - seat.fwd * behind
            if v in floors:
                f_loc, f_nrm, f_low = floors[v][:3]
                if float((moved - f_loc) @ f_nrm) < f_low:
                    continue
            v.co = Vector(moved)
            fins += 1
    return fins


def finish_surface(work: Work, job: dict[str, Any]):
    """
    5. くしゃくしゃ・折れ・こぶを均して取り、部位（パンティ部、ゴム、シーム）を分け、下に着るものの上の部分を
    その表面から作る。
    """
    g, unit, body, wunder, wtris, wraise = work.g, work.unit, work.body, work.wunder, work.wtris, work.wraise
    lm, up, lateral, hip_mid, planes = work.lm, work.up, work.lateral, work.hip_mid, work.planes
    body_part, offset, seat, seat_env, src = work.body_part, work.offset, work.seat, work.seat_env, work.src
    exact, utri, ubary, worn_extra = work.exact, work.utri, work.ubary, work.worn_extra
    capped, narrow, frames, seated, worn_usual = work.capped, work.narrow, work.frames, work.seated, work.worn_usual
    hold, worn_cap = work.hold, work.worn_cap
    # くしゃくしゃを形から見つける（つま先を差し替えた後: 体自身の指はその間でくしゃくしゃになっている）
    frames = {v: fr for v, fr in frames.items() if v.is_valid}
    crumpled, left = relax_crumples(body_part, frames, set(), unit(0.5))
    press_under_caps(capped, seated)
    press_under_caps(narrow, seated)
    log(f"crumpled faces {crumpled}, left after relaxing {left}")
    if g.get("quality", "normal") == "high":
        # 衣装全体（裏地を含む。2 層式のタイツはそれを 2 回描く）を予算内に
        budget = int(g.get("maxTriangles", 30000)) // (2 if g.get("twoLayer", False) else 1)
        refine(body_part, budget, src, exact, body, offset)
        # 分割はメッシュの要素を置き換える: frames を、各頂点の体の点から作り直し（下に着るものの上では、最初と
        # 同じくその高さも）、丸めで残ったくしゃくしゃを均して取る
        frames = {}
        for v in body_part.verts:
            if v[exact]:
                t, bary, loc = body.bind(np.array(v[src]))
                worn = min(float(bary @ wunder[wtris[t]]), worn_cap)
                raised = float(bary @ wraise[wtris[t]])
                frames[v] = (
                    loc,
                    body.normal(t, bary),
                    max(offset + worn if hold and worn > 0 else offset, offset + raised if raised > 0 else 0.0),
                )
        # （膜のように張った指先は、肌に沿わせない）
        for v in taut_zone(work, body_part, src):
            frames.pop(v, None)
        crumpled, left = relax_crumples(body_part, frames, set(), unit(0.5))
        log(f"after refining: crumpled faces {crumpled}, left after relaxing {left}")
    flattened = flatten_bulges(frames, unit(3.0))
    log(f"lumps lowered: {flattened}")
    if seat_env is not None:
        fins = hold_in_seat(
            body_part, seat, src, hip_mid, {v: fr for v, fr in frames.items() if len(fr) > 2}, offset + worn_usual
        )
        log(f"held inside the seat's outline: {fins}")
    wrinkled = add_wrinkles(body_part, g, frames, src, lm, up, lateral, hip_mid, unit)
    if wrinkled:
        log(f"wrinkles on {wrinkled} verts")

    # 部分をつないだ所に残った小さな穴: 縁ではない開いた辺（縁の体の点は衣装の平面の上にある）。それをふさぐ。
    def hole_edges():
        return [
            e
            for e in body_part.edges
            if e.is_boundary
            and not any(abs(pl.signed(np.array(v[src]))) < unit(HEM_ON_PLANE_MM) for v in e.verts for pl in planes)
        ]

    small = hole_edges()
    if 0 < len(small) <= 24:
        bmesh.ops.holes_fill(body_part, edges=small, sides=0)
    structure_regions(body_part, g, lm, up, lateral, hip_mid, planes)
    # 残った穴（品質の報告用）
    hole_pts = [np.array((e.verts[0].co + e.verts[1].co) / 2) for e in hole_edges()]
    # 下に着るものの上: その表面から作る（そしてそれと一緒に動く）
    under = load_under(job)
    # まだ何も下に着るものの上に作っていない: レイヤーを作った後に足した頂点（つま先）は自分の値を持たない
    # （0 で、Unity はそれを最初の三角形と読み、裏地を付けない）。下に何か着ていてもいなくても
    for v in body_part.verts:
        v[utri] = -1
    need = {}
    # （ガーター・レッグラップの下の層は前に作ったタイツ・靴下で、体のウェイトで動く: その面から作り直すと、左右の
    # 膝の間などでつながったタイツの面をなぞって左右の脚にまたがる。高さの持ち上げだけで足りる）
    if under is not None and g["type"] not in ("garter", "legwrap"):
        bound, need = snap_to_under(body_part, under, body, src, wunder, wtris, offset, worn_extra, unit)
        for v, (fi, b) in bound.items():
            v[utri] = fi
            v[ubary] = Vector(b)
        log(f"made from what is worn under: {len(bound)} verts")
    # どこかに残った折れ（股の継ぎ目、脚と橋渡し面が出会う所）: 均して取る
    floors = {
        v: (np.array(fr[0]), np.array(fr[1]), fr[2] if len(fr) > 2 else offset)
        for v, fr in frames.items()
        if v.is_valid
    }
    for v, (u_loc, u_nrm, u_h) in need.items():
        if v in floors and floors[v][2] >= u_h:
            continue
        floors[v] = (u_loc, u_nrm, u_h)
    folds, left = smooth_folds(body_part, floors, unit(2.0))
    log(f"folds smoothed: {folds}, left {left}")
    # お尻の中ほどをもう一度。今度はそこに着ているものの上だけ: 橋渡し面自身の高さ（上記）のせいで、割れ目の底が
    # お尻の後ろへ突き出たままになっていた。横から見るとひれである
    if seat_env is not None:
        log(
            f"held inside the seat's outline again: "
            f"{hold_in_seat(body_part, seat, src, hip_mid, floors, offset + worn_usual, 0.5)}"
        )
    # 布で覆う体の三角形の中（重心と辺の中点）が布の外に出ないように: 布の頂点は肌の外でも、橋渡しなどで頂点が横に
    # ずれると、肌の三角形の真ん中が布の面を突き抜ける（股の付け根など）。最も近い布の面を押し出す
    # （脚の衣類の、足首より上だけ: 足の指や手の指の間では布どうしが近く、どの面を押しても隣の指の布とぶつかる）
    covered_w = work.covered[work.first]
    ankle_h = max(float(lm["footL"] @ up), float(lm["footR"] @ up))
    pts, reach = [], []
    for tri in wtris[work.tri_ids] if g["type"] not in ARM_TYPES else []:
        if min(covered_w[tri]) < 0.002 or min(work.wpos[tri] @ up) < ankle_h:
            continue  # （範囲の縁から 2 mm より内の三角形だけ。covered はメートル）
        # （三角形の中の 9 点: 重心、辺の中点、重心と各頂点・各辺の中点の中ほど。押すのは、三角形の大きさの 2 倍
        # 以内の肌から作った面だけ）
        p3 = work.wpos[tri]
        pts += list(TRI_SAMPLES @ p3)
        reach += [
            2.0 * max(np.linalg.norm(p3[0] - p3[1]), np.linalg.norm(p3[1] - p3[2]), np.linalg.norm(p3[2] - p3[0]))
        ] * 9
    if pts:
        pushed = cover_body(body_part, src, np.array(pts), np.array(reach), offset * 0.2)
        if pushed:
            log(f"body triangles kept inside the garment: {pushed} pushes")
    work.hole_pts = hole_pts
    # ガーター・レッグラップ: 下の層（先に作ったタイツ・靴下、アバターの靴下・ブーツ）の一番外の面の外へ、offset だけ
    # 出して載せる。頂点から法線の向きに光線を飛ばし、ACCESSORY_REACH_MM 以内で最後に当たった面を一番外とする
    if g["type"] in ("garter", "legwrap") and under is not None:
        body_part.normal_update()
        reach, eps, lifted = unit(ACCESSORY_REACH_MM), unit(0.01), 0
        need = {}
        for v in body_part.verts:
            n = Vector(v.normal)
            if n.length == 0:
                continue
            start, p, last = Vector(v.co) - n * offset, Vector(v.co) - n * offset, None
            while True:
                hit = under[0].ray_cast(p + n * eps, n, reach - (p - start).length)
                if hit[0] is None:
                    break
                last, p = hit[0], hit[0]
            if last is not None:
                need[v] = max(0.0, (last - v.co).dot(n) + offset)
        # 持ち上げる量を隣となだらかにする（服の細かな凹凸を頂点ごとに拾うと帯が裂けて見える）: 隣との平均を 3 回とり、
        # 各頂点で自分に要る量を下回らない（隣の最大で広げると、全体が服の面から数 mm 浮く）
        raw = {v: need.get(v, 0.0) for v in body_part.verts}
        need = dict(raw)
        for _ in range(3):
            need = {
                v: max(raw[v], (need[v] + sum(need[e.other_vert(v)] for e in v.link_edges)) / (1 + len(v.link_edges)))
                for v in body_part.verts
            }
        for v, h in need.items():
            if h > 0:
                # （体の中心線の手前で止める: 脚がくっついた体で、内ももが向かいの脚の側へ出て左右にまたがらない）
                lat = float((np.array(v.co) - hip_mid) @ lateral)
                toward = -float(np.array(v.normal) @ lateral) * np.sign(lat)
                if toward > 1e-3:
                    h = min(h, max(0.0, (abs(lat) - unit(0.5)) / toward))
                v.co += Vector(v.normal) * h
                lifted += 1
        log(f"laid on the outermost layer under it: {lifted} verts")
    # ガーターのリボンの結び目（帯の外に足す閉じた立体。体の点は結び目の下の肌）
    work.swing_chains = []
    if g["type"] == "garter" and g.get("ribbon", False):
        added, work.swing_chains = add_bows(
            body_part, g, lm, up, lateral, body, src, exact, utri, body_part.verts.layers.int["lw_taut"], unit
        )
        log(f"ribbon bows: {added} verts, {len(work.swing_chains)} tails")


def thicken(work: Work) -> bpy.types.Object:
    """
    6. 体の部分を Blender のオブジェクトにし、その端に厚みを付ける（Solidify、縁のみ）。
    """
    body_part, thickness = work.body_part, work.thickness
    garment = new_object("garment", body_part)
    body_part.free()

    me = garment.data
    sol = garment.modifiers.new("solidify", "SOLIDIFY")
    sol.thickness = thickness
    sol.offset = -1.0
    # （"even" は使わない: 鋭い角でオフセットを際限なく拡大し、とげを突き出す）
    sol.use_even_offset = False
    sol.use_quality_normals = True
    sol.use_rim = True
    # 衣装の内側は決して見えない（体が埋める）。厚みが必要なのは端だけ
    sol.use_rim_only = True
    for p in me.polygons:
        p.use_smooth = True
    log(f"garment faces {len(me.polygons)}")
    return garment


def evaluated_arrays(
    em: bpy.types.Mesh,
) -> tuple[
    np.ndarray,
    np.ndarray,
    np.ndarray,
    np.ndarray,
    np.ndarray,
    list[list[np.int64]],
    np.ndarray,
    np.ndarray,
    np.ndarray,
    np.ndarray,
    np.ndarray,
    np.ndarray,
    np.ndarray,
    np.ndarray,
]:
    """
    評価済みの衣装を配列として返す: 頂点の位置、各コーナーの頂点と（分割した）法線、ループ三角形のコーナー、
    頂点の法線、各頂点の隣、その体の点（lw_src）、何の上に作ったか（lw_utri, lw_ubary）、体に従うか（lw_exact）、
    各ループ三角形の部位（lw_region）、各面の最初のコーナーとコーナーの数、膜のように張った指先か（lw_taut）。
    """
    vco = np.zeros(len(em.vertices) * 3)
    em.vertices.foreach_get("co", vco)
    vco = vco.reshape(-1, 3)
    nl = len(em.loops)
    lvert = np.zeros(nl, dtype=np.int64)
    em.loops.foreach_get("vertex_index", lvert)
    lnrm = np.zeros(nl * 3)
    em.corner_normals.foreach_get("vector", lnrm)
    lnrm = lnrm.reshape(-1, 3)
    tl = np.zeros(len(em.loop_triangles) * 3, dtype=np.int64)
    em.loop_triangles.foreach_get("loops", tl)
    vnrm = np.zeros(len(em.vertices) * 3)
    em.vertex_normals.foreach_get("vector", vnrm)
    vnrm = vnrm.reshape(-1, 3)
    ev = np.zeros(len(em.edges) * 2, dtype=np.int64)
    em.edges.foreach_get("vertices", ev)
    neighbors = [[] for _ in range(len(vco))]
    for a, b in ev.reshape(-1, 2):
        neighbors[a].append(b)
        neighbors[b].append(a)
    vsrc = np.zeros(len(vco) * 3)
    em.attributes["lw_src"].data.foreach_get("vector", vsrc)
    vsrc = vsrc.reshape(-1, 3)
    vutri = np.full(len(vco), -1, dtype=np.int64)
    vubary = np.zeros(len(vco) * 3)
    if "lw_utri" in em.attributes:
        em.attributes["lw_utri"].data.foreach_get("value", vutri)
        em.attributes["lw_ubary"].data.foreach_get("vector", vubary)
    vubary = vubary.reshape(-1, 3)
    vexact = np.zeros(len(vco), dtype=np.int64)
    em.attributes["lw_exact"].data.foreach_get("value", vexact)
    vtaut = np.zeros(len(vco), dtype=np.int64)
    if "lw_taut" in em.attributes:
        em.attributes["lw_taut"].data.foreach_get("value", vtaut)
    tpoly = np.zeros(len(em.loop_triangles), dtype=np.int64)
    em.loop_triangles.foreach_get("polygon_index", tpoly)
    preg = np.zeros(len(em.polygons), dtype=np.int64)
    if "lw_region" in em.attributes:
        em.attributes["lw_region"].data.foreach_get("value", preg)
    ploop = np.zeros(len(em.polygons), dtype=np.int64)
    em.polygons.foreach_get("loop_start", ploop)
    pcount = np.zeros(len(em.polygons), dtype=np.int64)
    em.polygons.foreach_get("loop_total", pcount)
    return vco, lvert, lnrm, tl, vnrm, neighbors, vsrc, vutri, vubary, vexact, preg[tpoly], ploop, pcount, vtaut


def corner_uvs(
    body: BodySurface,
    binds: dict[int, tuple[int, np.ndarray, np.ndarray]],
    lvert: np.ndarray,
    ploop: np.ndarray,
    pcount: np.ndarray,
    uv: np.ndarray,
    tris: np.ndarray,
) -> np.ndarray:
    """
    コーナーごとの体の UV: 面のどのコーナーも同じ体の三角形を通して写すので、面が 2 つの UV アイランドにまたがる
    ことはない。そのうえで UV の違うコーナーは別の頂点になる。体自身の分割された頂点と同じように。
    """
    luv = np.zeros((len(lvert), 2))
    for pi in range(len(ploop)):
        # 面の中央の下にある体の三角形: 体の部分の面は外へ動かした体の三角形なので、そのコーナーはその中で補間
        # される。ほかの面はそこから少し外挿する
        locs = [binds[int(lvert[li])][2] for li in range(ploop[pi], ploop[pi] + pcount[pi])]
        ref = body.bind(np.mean(locs, axis=0))[0]
        corners = body.pos[body.tris[ref]]
        for li in range(ploop[pi], ploop[pi] + pcount[pi]):
            t, bary, loc = binds[int(lvert[li])]
            luv[li] = barycentric(loc, *corners, clip=False) @ uv[tris[ref]] if t != ref else bary @ uv[tris[t]]
    return luv


def corner_leg_coords(legc: np.ndarray, lvert: np.ndarray, ploop: np.ndarray, pcount: np.ndarray) -> np.ndarray:
    """
    コーナーごとの脚の座標: 面が脚の前を横切る所（u が 1 から 0 へ戻る）では、そのコーナーは 1 を超えて続く。
    模様がその幅全体をその面に押し込まないように（テクスチャーは繰り返す）。
    """
    lleg = legc[lvert].copy()
    for pi in range(len(ploop)):
        sl = slice(ploop[pi], ploop[pi] + pcount[pi])
        u = lleg[sl, 1]
        turn = np.floor(u / 2.0) * 2.0  # 左脚で 0、右脚で 2
        if (turn == turn[0]).all() and u.max() - u.min() > 0.5:
            lleg[sl, 1] = np.where(u - turn < 0.5, u + 1.0, u)
    return lleg


def bind_outputs(
    body: BodySurface,
    binds: dict[int, tuple[int, np.ndarray, np.ndarray]],
    vco: np.ndarray,
    out_vert: np.ndarray,
    out_nrm: np.ndarray,
    ufirst: np.ndarray,
    luv: np.ndarray,
    offset: float,
    fade: float,
    own: np.ndarray | None = None,
) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """
    出力の各頂点の体の三角形、重心座標、UV。衣装がオフセットの距離で体に従う所では、体の滑らかな法線の方が
    デシメーションしたメッシュ自身の法線よりきれいである。橋渡しする所（指の間）、`fade` を超えて離れた所、
    `own` の頂点（指先: 体の法線は爪の段や輪郭を写す）では、メッシュの法線を残す（`out_nrm` はその場で書き換える）。
    """
    m = len(out_vert)
    bind_tri = np.zeros(m, dtype=np.int32)
    bind_bary = np.zeros((m, 3))
    out_uv = np.zeros((m, 2))
    for i in range(m):
        vi = int(out_vert[i])
        t, bary, loc = binds[vi]
        bind_tri[i] = t
        bind_bary[i] = bary
        out_uv[i] = luv[ufirst[i]]
        dist = float(np.linalg.norm(vco[vi] - loc))
        w = float(np.clip((offset + fade - dist) / fade, 0.0, 1.0))
        if w > 0 and (own is None or not own[i]):
            bn = body.normal(t, bary)
            if bn @ out_nrm[i] < 0:
                bn = -bn
            if bn @ out_nrm[i] > 0.3:
                out_nrm[i] = normalize(out_nrm[i] * (1 - w) + bn * w)
    return bind_tri, bind_bary, out_uv


def write_bin(path: str, *arrays):
    """output.bin: 配列を次々に並べたもの。順番はこのファイルの先頭の説明のとおり。"""
    with open(path, "wb") as fp:
        for a in arrays:
            fp.write(a.tobytes())


def write_output(work: Work, garment: bpy.types.Object, job: dict[str, Any], result: dict[str, Any]):
    """
    7. 評価済みの衣装を Unity 用に書き出し（output.bin）、各頂点を体に結び付け、品質の報告をする。
    """
    g, mpu, unit, uv, tris = work.g, work.mpu, work.unit, work.uv, work.tris
    n, wunder, body, lm, up = work.n, work.wunder, work.body, work.lm, work.up
    lateral, hip_mid = work.lateral, work.hip_mid
    covered, offset, worn_cap = work.covered, work.offset, work.worn_cap
    worn_extra, joint_extra = work.worn_extra, work.joint_extra
    band_lift, hole_pts = work.band_lift, work.hole_pts
    dg = bpy.context.evaluated_depsgraph_get()
    em = garment.evaluated_get(dg).to_mesh()
    em.calc_loop_triangles()
    vco, lvert, lnrm, tl, vnrm, neighbors, vsrc, vutri, vubary, vexact, tri_region, ploop, pcount, vtaut = (
        evaluated_arrays(em)
    )
    mask_of = arm_stretch_mask if g["type"] in ARM_TYPES else stretch_mask
    vmask = mask_of(vco, vnrm, neighbors, lm, up, lateral, hip_mid, float(g.get("stretchStrength", 1.0)))
    fwd_all = feet_forward(vsrc, lm, up, lateral, hip_mid)
    legc = leg_coords(vsrc, lm, lateral, hip_mid, fwd_all)

    # 各頂点を体に結び付ける: 体の部分は元の体の点へ（そのウェイトそのまま）、つま先はまっすぐ下の体の点へ
    binds = {}
    for vi in np.unique(lvert):
        binds[int(vi)] = body.bind(vsrc[vi]) if vexact[vi] else body.bind_below(vco[vi], vnrm[vi], unit(15.0))
    luv = corner_uvs(body, binds, lvert, ploop, pcount, uv, tris)
    lleg = corner_leg_coords(legc, lvert, ploop, pcount)

    # 出力の頂点 = 一意な（頂点, 法線, UV, 脚の座標）のコーナー
    key = np.concatenate(
        [lvert[:, None].astype(np.float64), np.round(lnrm * 1e3), np.round(luv * 1e5), np.round(lleg * 1e5)], axis=1
    )
    _, ufirst, uinv = np.unique(key, axis=0, return_index=True, return_inverse=True)
    uinv = uinv.reshape(-1)
    out_vert = lvert[ufirst]
    out_pos = vco[out_vert]
    out_nrm = lnrm[ufirst]
    out_tris = uinv[tl].reshape(-1, 3)

    m = len(out_pos)
    # （膜のように張った手の指先とつま先は、布自身の法線で陰影を付ける）
    if g["type"] == "gloves" and not g.get("fingerless", False):
        own = fingertip_zone(np.array([binds[int(vi)][2] for vi in out_vert]), lm, lateral, hip_mid) >= 0
    else:
        own = vtaut[out_vert] > 0
        if work.toe_zones:
            own |= np.array([any(z.holds(vco[int(vi)], hip_mid, lateral) for z in work.toe_zones) for vi in out_vert])
        if not own.any():
            own = None
    bind_tri, bind_bary, out_uv = bind_outputs(
        body, binds, vco, out_vert, out_nrm, ufirst, luv, offset, unit(BRIDGE_FADE_MM), own
    )
    f32, i32 = np.float32, np.int32
    write_bin(
        job["outputBin"],
        out_pos.astype(f32),
        out_nrm.astype(f32),
        out_uv.astype(f32),
        bind_tri.astype(i32),
        bind_bary.astype(f32),
        out_tris.astype(i32),
        covered,
        vmask[out_vert].astype(f32),
        tri_region.astype(i32),
        lleg[ufirst].astype(f32),
        vutri[out_vert].astype(i32),
        vubary[out_vert].astype(f32),
    )
    log(f"stretch mask min {vmask.min():.2f} median {np.median(vmask):.2f}")
    # 設計上、衣装が肌から離れて立つ最大（くぼみの埋めで最大 4 mm、へこみの持ち上げで最大 3、橋渡しした割れ目、
    # それに 6 mm を足す）: それより外は破綻
    worn_most = min(worn_cap, float(wunder.max()) if len(wunder) else 0.0)
    far = offset + worn_most + worn_extra + max(joint_extra, band_lift) + unit(4.0 + 3.0 + 6.0)
    result["quality"] = quality_report(
        vco, lvert[tl].reshape(-1, 3), binds, body, far, hole_pts, hip_mid, lateral, up, fwd_all, mpu, unit(4.0)
    )
    result["vertexCount"] = int(m)
    result["triangleCount"] = int(len(out_tris))
    result["bodyVertexCount"] = int(n)
    result["coveredCount"] = int((covered >= 0).sum())
    # 揺れ物の垂れ（ガーターのリボン）: 出力の頂点ごとの（番号, 根元からの割合）
    if work.swing_chains and "lw_swing" in em.attributes:
        vs_id = np.zeros(len(vco), dtype=np.int64)
        vs_t = np.zeros(len(vco), dtype=np.float64)
        em.attributes["lw_swing"].data.foreach_get("value", vs_id)
        em.attributes["lw_swing_t"].data.foreach_get("value", vs_t)
        sel = np.nonzero(vs_id[out_vert] > 0)[0]
        result["swing"] = [float(x) for i in sel for x in (i, vs_id[out_vert[i]], vs_t[out_vert[i]])]
    log(f"output verts {m} tris {len(out_tris)}")


if __name__ == "__main__":
    main()
