# SPDX-FileCopyrightText: 2026 MerciaGrimauge
# SPDX-License-Identifier: GPL-3.0-or-later

# LegWeaver の Blender 側: 衣装の形を変えるオプション（たるみのしわ）。

from typing import Any, Callable
import bmesh
import numpy as np
from mathutils import Vector
from lw_common import feet_forward
from lw_regions import leg_coords


def add_wrinkles(
    bm: bmesh.types.BMesh,
    g: dict[str, Any],
    frames: dict[bmesh.types.BMVert, tuple],
    src: bmesh.types.BMLayerItem,
    lm: dict[str, np.ndarray],
    up: np.ndarray,
    lateral: np.ndarray,
    hip_mid: np.ndarray,
    unit: Callable[[float], float],
) -> int:
    """
    たるみのしわ: 足首の周りと膝の裏を横切る、小さな畝の輪。肌の法線に沿って盛り上げる（外向きのみなので、
    何も突き抜けない）。大きさは `wrinkles`（0..1）に従う。
    """
    amount = float(g.get("wrinkles", 0.0))
    if amount <= 0:
        return 0
    verts = [v for v in bm.verts if v in frames]
    if not verts:
        return 0
    pts = np.array([v[src] for v in verts])
    fwd = feet_forward(pts, lm, up, lateral, hip_mid)
    lc = leg_coords(pts, lm, lateral, hip_mid, fwd)
    amp = unit(1.0) * amount
    moved = 0
    for v, (s, u) in zip(verts, lc):
        u = u % 1.0
        # 足首の周り: s 1.75 .. 2.0（両端で弱める）
        ankle = np.clip((s - 1.75) / 0.06, 0, 1) * np.clip((2.0 - s) / 0.06, 0, 1)
        # 膝の裏: s 0.9 .. 1.1、脚の後ろ側（u が 0.5 付近）
        knee = (
            np.clip((s - 0.9) / 0.05, 0, 1)
            * np.clip((1.1 - s) / 0.05, 0, 1)
            * np.clip((0.2 - abs(u - 0.5)) / 0.08, 0, 1)
        )
        w = max(ankle, knee)
        if w <= 0:
            continue
        ridge = 0.5 + 0.5 * np.sin(2 * np.pi * s / 0.035 + 0.6 * np.sin(2 * np.pi * u * 3))
        n = frames[v][1]
        v.co = Vector(np.array(v.co) + n * (amp * w * ridge))
        moved += 1
    return moved
