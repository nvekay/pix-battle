import numpy as np
from collections import deque
from PIL import Image, ImageDraw, ImageFont
import sys

OUT = sys.argv[1]
FONT = sys.argv[2]
FONTB = sys.argv[3]
rng = np.random.default_rng(7)
W, H = 512, 256

def smooth(t):
    return t * t * (3 - 2 * t)

def value_noise(cell, r):
    gw, gh = W // cell + 2, H // cell + 2
    g = r.random((gh, gw))
    ys, xs = np.mgrid[0:H, 0:W]
    fx, fy = xs / cell, ys / cell
    x0, y0 = fx.astype(int), fy.astype(int)
    tx, ty = smooth(fx - x0), smooth(fy - y0)
    a = g[y0, x0]; b = g[y0, x0 + 1]; c = g[y0 + 1, x0]; d = g[y0 + 1, x0 + 1]
    return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty

def fbm(r, cells=(64, 32, 16, 8, 4), p=0.5):
    s = np.zeros((H, W)); amp = 1.0; tot = 0
    for c in cells:
        s += value_noise(c, r) * amp; tot += amp; amp *= p
    return s / tot

h = fbm(rng)
ys, xs = np.mgrid[0:H, 0:W]
d = np.minimum(np.minimum(xs, W - 1 - xs), np.minimum(ys, H - 1 - ys))
h -= np.clip(24 - d, 0, None) * 0.02
thr = np.quantile(h, 0.53)
land = h > thr

# largest component
lab = -np.ones((H, W), int); best = None; bestn = 0; cid = 0
for y0 in range(H):
    for x0 in range(W):
        if land[y0, x0] and lab[y0, x0] < 0:
            q = deque([(y0, x0)]); lab[y0, x0] = cid; n = 0
            while q:
                y, x = q.popleft(); n += 1
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    yy, xx = y + dy, x + dx
                    if 0 <= yy < H and 0 <= xx < W and land[yy, xx] and lab[yy, xx] < 0:
                        lab[yy, xx] = cid; q.append((yy, xx))
            if n > bestn: bestn, best = n, cid
            cid += 1
land = lab == best

def shift(a, dy, dx, fill):
    r = np.full_like(a, fill)
    ys0 = slice(max(dy, 0), H + min(dy, 0)); ys1 = slice(max(-dy, 0), H + min(-dy, 0))
    xs0 = slice(max(dx, 0), W + min(dx, 0)); xs1 = slice(max(-dx, 0), W + min(-dx, 0))
    r[ys0, xs0] = a[ys1, xs1]
    return r

# distance to land for water / to water for land (cheap chamfer by dilation)
def dist_from(mask, maxd):
    dist = np.where(mask, 0, maxd + 1)
    cur = mask.copy()
    for k in range(1, maxd + 1):
        nb = cur | shift(cur, 1, 0, False) | shift(cur, -1, 0, False) | shift(cur, 0, 1, False) | shift(cur, 0, -1, False)
        dist[(nb & ~cur)] = k
        cur = nb
    return dist

wdist = dist_from(land, 10)          # for water pixels: distance to land
ldist = dist_from(~land, 10)         # for land pixels: distance to water

hl = np.clip((h - thr) / (h[land].max() - thr), 0, 1)
moist = fbm(np.random.default_rng(11), (64, 32, 16, 8))
moist = (moist - moist.min()) / (moist.max() - moist.min())
temp = ys / H - hl * 0.5   # north is cold

B = np.zeros((H, W), int)  # biome ids
DEEP, MID, SHAL, BEACH, GRASS, FOREST, FARM, STEPPE, DESERT, TUNDRA, HILLS, MOUNT, SNOW = range(13)
B[~land] = DEEP
B[(~land) & (wdist <= 5)] = MID
B[(~land) & (wdist <= 2)] = SHAL
L = land
B[L] = GRASS
B[L & (moist > 0.58)] = FOREST
B[L & (moist < 0.42)] = STEPPE
B[L & (moist < 0.33) & (temp > 0.55)] = DESERT
B[L & (temp < 0.2)] = TUNDRA
B[L & (hl > 0.52)] = HILLS
B[L & (hl > 0.68)] = MOUNT
B[L & (hl > 0.82)] = SNOW
B[L & (ldist <= 1) & (hl < 0.25) & (temp > 0.2)] = BEACH

pal = {
    DEEP: (34, 78, 140), MID: (46, 102, 170), SHAL: (78, 146, 202), BEACH: (232, 214, 160),
    GRASS: (118, 176, 72), FOREST: (58, 128, 62), FARM: (176, 190, 92), STEPPE: (182, 178, 98),
    DESERT: (226, 196, 128), TUNDRA: (164, 178, 156), HILLS: (130, 148, 86), MOUNT: (122, 116, 110),
    SNOW: (238, 242, 246),
}

# capitals: farthest point sampling on land away from coast
cand = np.argwhere(land & (ldist >= 7))
caps = [cand[rng.integers(len(cand))]]
while len(caps) < 8:
    best = None; bd = -1
    for c in cand[rng.integers(len(cand), size=200)]:
        md = min(((c - k) ** 2).sum() for k in caps)
        if md > bd: bd, best = md, c
    caps.append(best)
caps = np.array(caps)
# put player capital (index 0) roughly center-left for the mock
order = np.argsort(caps[:, 1] + caps[:, 0] * 0.3)
caps = caps[order]
player = 2

# farmland near capitals (modern patchwork), only on grass/steppe
fard = np.min([np.hypot(ys - c[0], xs - c[1]) for c in caps], axis=0)
patch = ((xs // 5 + ys // 3 * 7) * 2654435761 % 7)
farm = L & np.isin(B, [GRASS, STEPPE]) & (fard < 22) & (patch < 4)
B[farm] = FARM

nn = fbm(np.random.default_rng(3), (16, 8, 4)) * 22
D = np.stack([np.hypot(ys - c[0], xs - c[1]) for c in caps]) + nn[None]
R = np.array([78, 70, 95, 66, 84, 72, 90, 64])
own = np.argmin(D, axis=0) + 1
own[np.min(D - R[:, None, None], axis=0) > 0] = 0
own[~land] = 0
# remove tiny enclaves: owner must be reachable - ignore for mock

nat = np.array([
    (0, 0, 0),
    (217, 74, 61),    # 1 Борсен красный
    (232, 138, 46),   # 2 Кесталь оранжевый
    (63, 102, 217),   # 3 Альвария кобальт (игрок)
    (142, 79, 212),   # 4 Норвена фиолетовый
    (217, 68, 143),   # 5 Ровения малиновый
    (31, 166, 160),   # 6 Сальтерра бирюза
    (240, 200, 58),   # 7 Дальмира жёлтый
    (52, 62, 112),    # 8 Вестмарк тёмно-синий
], float)
names = ["", "Борсен", "Кесталь", "Альвария", "Норвена", "Ровения", "Сальтерра", "Дальмира", "Вестмарк"]
PL = 3

hsh = (xs * 73856093 ^ ys * 19349663) % 1000

base = np.zeros((H, W, 3))
for k, v in pal.items():
    base[B == k] = v
# per-pixel variation
var = ((hsh % 5) - 2) * 0.03
base *= (1 + var)[..., None]
# forest canopy texture
can = (B == FOREST) & (hsh % 3 == 0)
base[can] *= 0.78
# hillshade, quantized
hs = shift(hl, 1, 1, 0) - shift(hl, -1, -1, 0)
lvl = np.clip(np.round(hs * 18), -2, 2)
base[L] *= (1 + 0.09 * lvl[L])[..., None]
# deep water dither
dith = (~L) & (B == DEEP) & (hsh % 11 == 0)
base[dith] *= 1.08
# foam
foam = (~L) & (wdist == 1)
base[foam] = base[foam] * 0.55 + np.array((190, 226, 240)) * 0.45

col = base.copy()
a = 0.40
m = own > 0
aa = np.where(own == PL, 0.62, 0.55)[m][:, None]
lum = lambda c: c[..., 0] * 0.299 + c[..., 1] * 0.587 + c[..., 2] * 0.114
nc = nat[own[m]]
blend = np.clip(nc * (lum(col[m]) / np.maximum(lum(nc), 1))[:, None], 0, 255)
col[m] = col[m] * (1 - aa) + blend * aa

# fronts fx near player borders
def nb_owner(dy, dx):
    return shift(own, dy, dx, -1), shift(land, dy, dx, False)

edge_any = np.zeros((H, W), bool)
front_with = np.zeros((H, W), int)
for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
    o2, l2 = nb_owner(dy, dx)
    e = m & l2 & (o2 != own)
    edge_any |= e
    fw = e & (o2 > 0)
    front_with[fw] = o2[fw]

# player's neighbors
neigh = sorted(set(front_with[own == PL].ravel()) - {0})
gain_n = neigh[0] if neigh else 0
lose_n = neigh[-1] if len(neigh) > 1 else 0

inner = col.copy()
for k in range(1, 9):
    sel = edge_any & (own == k)
    c = nat[k] * 1.0
    if k == PL:
        c = nat[k] * 0.25 + 255 * 0.75
    else:
        c = np.minimum(nat[k] * 1.12, 255)
    inner[sel] = c

# recent flips: player gained from gain_n (bright), lost to lose_n (dark)
gain = (own == PL) & (ldist >= 0)
pg = np.zeros((H, W), bool); pl = np.zeros((H, W), bool)
bp = (own == PL) & (front_with == gain_n)
bp2 = (own == lose_n) & np.isin(front_with, [PL])
for dy in range(-2, 3):
    for dx in range(-2, 3):
        pg |= shift(bp, dy, dx, False)
        pl |= shift(bp2, dy, dx, False)
pg &= (own == PL) & ((hsh % 3) != 0)
pl &= (own == lose_n) & ((hsh % 2) == 0)
inner[pg] = inner[pg] * 0.5 + 255 * 0.5
inner[pl] = inner[pl] * 0.65

