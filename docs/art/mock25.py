import sys
exec(open(sys.argv[4] + "/gen_part.py").read())
exec(open(sys.argv[4] + "/util_part.py").read())

P = 6                      # bake pixels per map cell
OFFS = np.array([0, 5, 10, 16, 23, 32, 44])
PAD = 46
lev = np.zeros((H, W), int)
lev[land] = 1 + np.searchsorted(np.array([0.24, 0.40, 0.52, 0.64, 0.76]), hl[land])
off = OFFS[lev]
Hb, Wb = H * P + PAD, W * P
img = np.zeros((Hb, Wb, 3)) + np.array((38, 84, 150))
cell = -np.ones((Hb, Wb), int)
kind = np.zeros((Hb, Wb), np.int8)   # 0 water 1 top 2 cliff 3 decor

bys, bxs = np.mgrid[0:Hb, 0:Wb]
bh = (bxs * 73856093 ^ bys * 19349663) % 997
bh2 = ((bxs // 2) * 83492791 ^ (bys // 2) * 2971215073) % 991

cliffcol = {DEEP: (0, 0, 0), MID: (0, 0, 0), SHAL: (0, 0, 0), BEACH: (184, 152, 96), GRASS: (122, 88, 56),
            FOREST: (104, 76, 50), FARM: (126, 92, 58), STEPPE: (136, 104, 64), DESERT: (186, 140, 82),
            TUNDRA: (110, 112, 104), HILLS: (108, 92, 70), MOUNT: (78, 74, 76), SNOW: (120, 126, 140)}
topcol = dict(pal)
topcol[FARM] = (170, 186, 86)
waterc = {1: (96, 172, 214), 2: (70, 140, 200), 3: (52, 110, 178), 4: (38, 84, 150)}

offN = shift(off, 1, 0, 0)   # north neighbour offset (row y-1)
offS = shift(off, -1, 0, 0)
for y in range(H):
    by = y * P + PAD
    o = np.repeat(off[y], P)
    bi = np.repeat(B[y], P)
    cid = np.repeat(np.arange(W) + y * W, P)
    on = np.repeat(offN[y], P)
    os_ = np.repeat(offS[y], P)
    isl = np.repeat(land[y], P)
    wd = np.repeat(np.clip(wdist[y], 1, 4), P)
    tc = np.array([topcol[b] for b in B[y]]).repeat(P, 0)
    cc = np.array([cliffcol[b] for b in B[y]]).repeat(P, 0)
    for dy in range(-OFFS.max(), P):
        r = by + dy
        top = (dy >= -o) & (dy < -o + P)
        cl = isl & (dy >= -o + P) & (dy < P)
        if top.any():
            c = tc.copy()
            v = 1 + ((bh[r] % 7) - 3) * 0.018 + ((bh2[r] % 5) - 2) * 0.025
            c = c * v[:, None]
            k = dy + o  # row inside top face 0..P-1
            c = c * (1 + 0.035 * np.repeat(lev[y], P))[:, None]
            # rim light on south lip when south lower, AO under taller north neighbour
            c[(k == P - 1) & (os_ < o) & isl] *= 1.13
            ao = isl & (on > o) & (k < np.minimum(on - o, 4))
            c[ao] *= 0.80
            # farmland stripes
            fm = bi == FARM
            c[fm & (((bxs[r] // 2) + y) % 3 == 0)] *= 0.86
            # water
            w = ~isl
            if w.any():
                wc = np.array([waterc[d] for d in wd])
                wc = wc * (1 + ((bh2[r] % 13 == 0) * 0.07))[:, None]
                wc[(wd == 1) & (bh[r] % 3 == 0)] = (150, 206, 232)
                c[w] = wc[w]
                # shadow of coast cliff on water just south of land
                c[w & (on > 0) & (dy < 3)] *= 0.78
            m = top & (cell[r] < 0) | top
            img[r, m] = c[m]
            cell[r, m] = cid[m]
            kind[r, m] = np.where(isl[m], 1, 0)
        if cl.any():
            c = cc.copy()
            depth = (dy - (-o + P)) / np.maximum(o, 1)
            c = c * (1.0 - 0.35 * depth)[:, None]
            c[((dy - (-o + P)) % 4 == 3)] *= 0.86          # strata
            c *= (1 + ((bh[r] % 5) - 2) * 0.02)[:, None]
            # snow caps keep white top only; cliff rows on snow are rock
            img[r, cl] = c[cl]
            cell[r, cl] = cid[cl]
            kind[r, cl] = 2

# ---------------- decor sprites ----------------
def spr(rows, cmap):
    h_ = len(rows); w_ = len(rows[0])
    a = np.zeros((h_, w_, 4))
    for j, row in enumerate(rows):
        for i, ch in enumerate(row):
            if ch != ".":
                a[j, i, :3] = cmap[ch]; a[j, i, 3] = 1
    return a

TREE = spr(["..ggg..", ".gGGgg.", "gGGgggg", "gGggggd", "ggggddd", ".gdddd.", "..ddd..", "...t...", "...t..."],
           {"G": (132, 196, 84), "g": (82, 152, 62), "d": (46, 104, 48), "t": (96, 66, 40)})
TREEL = spr(["...ooo...", "..oGGgo..", ".oGGgggo.", "oGGggggdo", "oGgggggdo", "ogggggddo", "oggggdddo", ".ogdddddo", "..oddddo.", "...ooto..", "....t....", "....t...."],
            {"o": (34, 70, 36), "G": (140, 204, 90), "g": (88, 160, 64), "d": (52, 112, 50), "t": (96, 66, 40)})
PINEL = spr(["....o....", "...oPo...", "...opdo..", "..oPpdo..", "..oppdo..", ".oPppddo.", ".opppddo.", "oPpppdddo", "oooopoooo", "....t....", "....t...."],
            {"o": (22, 52, 40), "P": (96, 156, 106), "p": (52, 110, 78), "d": (32, 76, 58), "t": (90, 62, 40)})
TREE2 = spr([".ggg.", "gGggd", "ggggd", ".gdd.", "..t.."],
            {"G": (150, 206, 96), "g": (98, 168, 70), "d": (58, 118, 52), "t": (96, 66, 40)})
PINE = spr(["..p..", ".pPp.", ".ppd.", "pPppd", ".ppd.", "pPpdd", "ppddd", "..t..", "..t.."],
           {"P": (92, 150, 104), "p": (50, 106, 76), "d": (30, 72, 56), "t": (90, 62, 40)})
SPINE = spr(["..s..", ".sPs.", ".ppd.", "sPpsd", ".ppd.", "sPpdd", "ppddd", "..t.."],
            {"s": (236, 242, 248), "P": (92, 150, 104), "p": (50, 106, 76), "d": (30, 72, 56), "t": (90, 62, 40)})
CACT = spr([".c.", ".cC", "cCc", "Cc.", ".c.", ".c."], {"c": (70, 140, 70), "C": (112, 180, 96)})
ROCK = spr([".lll.", "llmmd", "lmmdd", ".ddd."], {"l": (168, 164, 158), "m": (126, 122, 118), "d": (84, 80, 80)})
BUSH = spr([".gg.", "gGgd", "gggd"], {"G": (140, 200, 90), "g": (90, 156, 64), "d": (56, 110, 50)})
HOUSE = spr(["rrrrrr", "RRRRRR", "wwwwww", "wbwwbw", "wwwwww"],
            {"r": (196, 92, 72), "R": (150, 64, 52), "w": (226, 220, 206), "b": (70, 100, 140)})
HOUSE2 = spr(["rrrrr", "RRRRR", "wwwww", "wbwbw", "wwwww"],
             {"r": (120, 132, 150), "R": (86, 96, 112), "w": (212, 214, 218), "b": (70, 100, 140)})
BLOCK = spr(["lllll", "lllll", "wbwbw", "wwwww", "wbwbw", "wwwww", "wbwbw", "wwwww", "wbwbw", "wwwww"],
            {"l": (176, 182, 190), "w": (150, 156, 168), "b": (96, 140, 186)})
TOWER = spr(["..f..", "..|..", ".lll.", ".lll.", ".wbw.", ".www.", ".wbw.", ".www.", ".wbw.", ".www.", ".wbw.", ".www.", ".wbw.", "wwwww"],
            {"f": (240, 240, 240), "|": (60, 60, 60), "l": (200, 206, 214), "w": (160, 166, 178), "b": (110, 160, 210)})
SAND = spr(["....F", "....|", ".sss|", "sSsSs", "s...s", "sSsSs"],
           {"s": (170, 138, 90), "S": (132, 104, 66), "|": (50, 40, 30), "F": (255, 255, 255)})

up2 = lambda a: np.repeat(np.repeat(a, 2, 0), 2, 1)
HOUSE, HOUSE2, BLOCK, TOWER, SAND = map(up2, (HOUSE, HOUSE2, BLOCK, TOWER, SAND))
decor = []   # (bx, by_bottom, sprite, cellid, tintcolor_override)
rh = (xs * 2654435761 ^ ys * 40503) % 1000
for y in range(H):
    for x in range(W):
        if not land[y, x]:
            continue
        b = B[y, x]; r_ = rh[y, x]
        sp = None
        if b == FOREST and r_ < 850: sp = (PINEL if r_ % 2 else PINE) if temp[y, x] < 0.32 else (TREEL if r_ % 3 == 0 else (TREE if r_ % 3 == 1 else TREE2))
        elif b == GRASS and r_ < 50: sp = TREE if r_ % 2 else BUSH
        elif b == TUNDRA and r_ < 140: sp = PINEL if r_ % 3 == 0 else PINE
        elif b == SNOW and r_ < 50: sp = SPINE
        elif b == DESERT and r_ < 30: sp = CACT
        elif b == DESERT and r_ < 45: sp = ROCK
        elif b == HILLS and r_ < 110: sp = ROCK if r_ % 2 else TREE2
        elif b == MOUNT and r_ < 90: sp = ROCK
        elif b == STEPPE and r_ < 30: sp = BUSH
        if sp is not None:
            jx = (r_ // 7) % 3 - 1
            decor.append((x * P + P // 2 + jx, y * P + PAD - off[y, x] + P - 1, sp, y * W + x))

# towns around capitals (stage 2 preview): modern houses + blocks, capital tower
for k in range(1, 9):
    cy_, cx_ = caps[k - 1]
    rr = np.random.default_rng(100 + k)
    for i in range(22):
        dx, dy = rr.integers(-6, 7), rr.integers(-4, 5)
        y, x = cy_ + dy, cx_ + dx
        if 0 <= y < H and 0 <= x < W and land[y, x] and (dx or dy):
            sp = BLOCK if abs(dx) + abs(dy) < 4 and i % 3 == 0 else (HOUSE if i % 2 else HOUSE2)
            decor.append((x * P + P // 2, y * P + PAD - off[y, x] + P - 1, sp, y * W + x))
    decor.append((cx_ * P + P // 2, cy_ * P + PAD - off[cy_, cx_] + P - 1, TOWER, cy_ * W + cx_))

# forts
fort_cells = []
for k in (PL, lose_n, 7):
    sel = np.argwhere((own == k) & edge_any)
    if len(sel):
        p_ = sel[len(sel) // 3]
        p_ = p_ + (np.sign(caps[k - 1] - p_) * 6).astype(int)
        fort_cells.append((p_[0], p_[1], k))
        decor.append((p_[1] * P + P // 2, p_[0] * P + PAD - off[p_[0], p_[1]] + P - 1, SAND, p_[0] * W + p_[1]))

decor.sort(key=lambda d: d[1])
for (bx, bb, sp, cid) in decor:
    h_, w_ = sp.shape[:2]
    x0 = bx - w_ // 2; y0 = bb - h_ + 1
    # shadow: offset right/down ellipse
    for j in range(2 if h_ < 10 else 3):
        for i in range(w_ + 1):
            yy, xx = bb + j, x0 + i + 1
            if 0 <= yy < Hb and 0 <= xx < Wb and kind[yy, xx] in (1,):
                img[yy, xx] *= 0.72
    for j in range(h_):
        for i in range(w_):
            if sp[j, i, 3] > 0:
                yy, xx = y0 + j, x0 + i
                if 0 <= yy < Hb and 0 <= xx < Wb:
                    img[yy, xx] = sp[j, i, :3]
                    cell[yy, xx] = cid
                    kind[yy, xx] = 3

# ground owner map (before decor overwrote cells) -> recompute from geometry: use cell of non-decor
ground_cell = cell.copy()
# for decor pixels take owner of the base cell they stand on (already cid) -> fine for tint
ownpix = np.where(cell >= 0, own.ravel()[np.maximum(cell, 0)], 0)
ownpix[kind == 0] = -1

def compose(at, atp, ac, ad, bw, dw=0):
    # ---------------- ownership tint ----------------
    lumf = lambda c: c[..., 0] * 0.299 + c[..., 1] * 0.587 + c[..., 2] * 0.114
    tm = ownpix > 0
    alpha = np.where(kind == 3, ad, np.where(kind == 2, ac, np.where(ownpix == PL, atp, at)))
    nc = nat[np.maximum(ownpix, 0)]
    blend = np.clip(nc * (lumf(img) / np.maximum(lumf(nc), 1))[..., None] * 1.05, 0, 255)
    out = img.copy()
    out[tm] = img[tm] * (1 - alpha[tm, None]) + blend[tm] * alpha[tm, None]
    
    # front fx on top faces (cells flipped recently)
    fx_g = np.zeros((H, W), bool); fx_g[pg] = True
    fx_l = np.zeros((H, W), bool); fx_l[pl] = True
    cm = np.maximum(cell, 0)
    gpx = fx_g.ravel()[cm] & (kind == 1)
    lpx = fx_l.ravel()[cm] & (kind == 1)
    out[gpx] = out[gpx] * 0.55 + 255 * 0.45
    out[lpx] = out[lpx] * 0.62
    
    # ---------------- borders (screen space, always on top) ----------------
    # use ground ownership: decor pixels inherit base cell owner which is ok;
    o_ = ownpix
    def edges(a, dy, dx):
        b_ = np.full_like(a, -1)
        ys0 = slice(max(dy, 0), Hb + min(dy, 0)); ys1 = slice(max(-dy, 0), Hb + min(-dy, 0))
        xs0 = slice(max(dx, 0), Wb + min(dx, 0)); xs1 = slice(max(-dx, 0), Wb + min(-dx, 0))
        b_[ys0, xs0] = a[ys1, xs1]
        return b_
    dark = np.zeros((Hb, Wb), bool); innerN = np.zeros((Hb, Wb), bool); neutralE = np.zeros((Hb, Wb), bool)
    strike = np.zeros((Hb, Wb), bool)
    for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
        nb = edges(o_, dy, dx)
        e = (o_ > 0) & (nb > 0) & (nb != o_)
        dark |= e
        neutralE |= (o_ > 0) & (nb == 0)
        strike |= e & (((o_ == PL) & (nb == gain_n)) | ((o_ == gain_n) & (nb == PL)))
    # inner band: pixels within 2 of a dark edge on own side
    band = np.zeros((Hb, Wb), bool)
    for dy in range(-bw, bw + 1):
        for dx in range(-bw, bw + 1):
            band |= edges(dark.astype(int), dy, dx) > 0
    band &= ~dark & (o_ > 0)
    for k in range(1, 9):
        s_ = band & (o_ == k)
        c = nat[k] * 0.25 + 255 * 0.75 if k == PL else np.minimum(nat[k] * 1.15, 255)
        out[s_] = out[s_] * 0.15 + c * 0.85
        s2 = neutralE & (o_ == k)
        out[s2] = c
    for _ in range(dw):
        dark = dark | (edges(dark.astype(int), 0, 1) > 0) | (edges(dark.astype(int), 1, 0) > 0)
        dark &= (o_ > 0)
    out[dark] = (28, 24, 32)
    dash = ((bxs + bys) // 6) % 2 == 0
    out[strike & dash] = (255, 200, 64)
    
    return np.clip(out, 0, 255).astype(np.uint8)
bake = compose(0.42, 0.50, 0.30, 0.16, 3, 1)
bake_close = compose(0.30, 0.36, 0.20, 0.10, 3)

Image.fromarray(bake).save(OUT + "/bake_full.png")

# ---------------- full-screen PC mock ----------------
full = Image.fromarray(bake[::2, ::2])
S = 3
canvas = Image.new("RGB", (1920, 1080), waterc[4])
cv = np.array(canvas).astype(float)
cy_, cx_ = np.mgrid[0:1080, 0:1920]
cv[((cx_ // 3 * 73856093 ^ cy_ // 3 * 19349663) % 13 == 0)] *= 1.07
canvas = Image.fromarray(cv.astype(np.uint8))
OX, OY = 192, 150 - PAD // 2
canvas.paste(full, (OX, OY))
OY = OY + PAD // 2      # cell (0,0) top-left in screen
dr = ImageDraw.Draw(canvas, "RGBA")
army = {1: 6, 2: 5, 3: 4, 4: 7, 5: 3, 6: 5, 7: 8, 8: 4}
for k in range(1, 9):
    ys_k, xs_k = np.nonzero(own == k)
    if len(ys_k):
        ly, lx = np.median(ys_k), np.median(xs_k)
        lab = names[k] + (" · вы" if k == PL else "")
        text_outline(dr, (OX + lx * S, OY + ly * S - 26), lab, fntb(22), (255, 255, 255))
        text_outline(dr, (OX + lx * S, OY + ly * S - 4), f"{army[k]} бат.", fnt(18), (230, 230, 230))
    c = caps[k - 1]
    star(dr, OX + c[1] * S + 1, OY + c[0] * S - off[c[0], c[1]] // 2 - 8, 10, (255, 255, 255), (25, 22, 30))
exec(open(sys.argv[4] + "/hud_part.py").read().replace('"Мокап, не скриншот · шрифт Tiny5"', '"Мокап, не скриншот · 2.5D-запекание рельефа · шрифт Tiny5"').replace("art-01-hud-pc.png", "art-01-map-pc.png"))

# ---------------- close-up of a front (bake x2) ----------------
fy0, fx0 = 62, 262          # cells
cw, ch_ = 160, 90
crop = bake_close[fy0 * P + PAD - 0: fy0 * P + PAD + ch_ * P, fx0 * P: fx0 * P + cw * P]
z = Image.fromarray(crop).resize((crop.shape[1] * 2, crop.shape[0] * 2), Image.NEAREST)
zd = ImageDraw.Draw(z, "RGBA")
Z = P * 2
for k in range(1, 9):
    ys_k, xs_k = np.nonzero(own[fy0:fy0 + ch_, fx0:fx0 + cw] == k)
    if len(ys_k) > 200:
        ly, lx = np.median(ys_k), np.median(xs_k)
        text_outline(zd, (lx * Z, ly * Z), names[k] + (" · вы" if k == PL else ""), ImageFont.truetype(FONT, 40), (255, 255, 255))
# front plate
PANEL = (24, 29, 39, 235)
zd.rectangle([1440, 24, 1896, 214], fill=PANEL, outline=(255, 196, 64, 255), width=3)
zd.text((1460, 40), "Фронт с Ровенией", font=ImageFont.truetype(FONT, 30), fill=(236, 238, 242))
zd.text((1460, 90), "Мы 4,0 бат. на 80 px", font=ImageFont.truetype(FONT, 20), fill=(236, 238, 242))
zd.text((1460, 118), "Они 6,8 бат. на 80 px", font=ImageFont.truetype(FONT, 20), fill=(236, 238, 242))
zd.text((1460, 150), "Они давят 1,7 : 1", font=ImageFont.truetype(FONT, 20), fill=(232, 92, 80))
zd.text((1460, 180), "▲ их укрепрайон рядом", font=ImageFont.truetype(FONT, 20), fill=(244, 196, 72))
zd.text((16, 1060), "Крупный план фронта, зум ×4 (12 px на клетку). Мокап, не скриншот: прямые границы — артефакт генератора мокапа.", font=ImageFont.truetype(FONT, 20), fill=(240, 244, 250), anchor="lm")
z.save(OUT + "/art-02-front-closeup.png")
print("ok", Hb, Wb, len(decor))

# ---------------- default camera 1:1 (6 px per cell) around player capital ----------------
c = caps[PL - 1]
cx0 = int(np.clip(c[1] * P - 960, 0, Wb - 1920)); cy0 = int(np.clip(c[0] * P + PAD - 420, 0, Hb - 1080))
d1 = Image.fromarray(bake_close[cy0:cy0 + 1080, cx0:cx0 + 1920].copy())
dd = ImageDraw.Draw(d1, "RGBA")
for k in range(1, 9):
    cc = caps[k - 1]
    sx, sy = cc[1] * P - cx0, cc[0] * P + PAD - off[cc[0], cc[1]] - cy0
    if 0 < sx < 1920 and 0 < sy < 1080:
        text_outline(dd, (sx, sy - 70), names[k] + (" · вы" if k == PL else ""), ImageFont.truetype(FONT, 30), (255, 255, 255))
dd.text((16, 1060), "Камера по умолчанию: 1:1 к запечённой карте (6 px на клетку), центр на своей столице. Колесо — вся карта или крупный план.", font=ImageFont.truetype(FONT, 20), fill=(240, 244, 250), anchor="lm")
d1.save(OUT + "/art-03-default-camera.png")
