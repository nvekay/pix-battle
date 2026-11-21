# ---------- HUD ----------
PANEL = (24, 29, 39, 235); EDGE = (64, 74, 92, 255); HI = (88, 100, 122, 255)
TXT = (236, 238, 242); DIM = (150, 158, 172); GOLD = (244, 196, 72); RED = (232, 92, 80); GREEN = (120, 210, 110)

def panel(x0, y0, x1, y1, edge=EDGE):
    dr.rectangle([x0, y0, x1, y1], fill=PANEL)
    dr.rectangle([x0, y0, x1, y1], outline=edge, width=3)
    dr.line([x0 + 3, y0 + 3, x1 - 3, y0 + 3], fill=HI, width=1)

def flag(x, y, w, h, k):
    c = tuple(int(v) for v in nat[k])
    dr.rectangle([x, y, x + w, y + h], fill=c)
    dr.rectangle([x, y + h // 3, x + w, y + 2 * h // 3], fill=(245, 245, 245))
    dr.rectangle([x, y, x + w // 3, y + h], fill=tuple(int(v * 0.6) for v in c))
    dr.rectangle([x, y, x + w, y + h], outline=(15, 15, 20), width=2)

# top-left nation card
panel(16, 16, 380, 92)
flag(30, 30, 66, 46, PL)
dr.text((112, 30), "Республика", font=fnt(18), fill=DIM)
dr.text((112, 50), "Альвария", font=fntb(28), fill=TXT)
dr.rectangle([300, 34, 362, 74], outline=tuple(int(v) for v in nat[PL]), width=3)
dr.text((331, 54), "Вы", font=fntb(20), fill=TXT, anchor="mm")

# top-center resources
panel(400, 16, 1480, 92)
def coin(x, y):
    dr.rectangle([x, y + 4, x + 26, y + 20], fill=(104, 168, 92), outline=(30, 60, 30), width=2)
    dr.rectangle([x + 9, y + 8, x + 17, y + 16], fill=(200, 236, 180))
def helmet(x, y):
    dr.pieslice([x, y, x + 26, y + 26], 180, 360, fill=(110, 128, 84), outline=(30, 34, 24), width=2)
    dr.rectangle([x - 2, y + 12, x + 28, y + 16], fill=(80, 94, 60), outline=(30, 34, 24))
coin(420, 34)
dr.text((456, 26), "Бюджет", font=fnt(16), fill=DIM)
dr.text((456, 44), "441", font=fntb(30), fill=TXT)
dr.text((520, 50), "+4,2/с", font=fnt(20), fill=GREEN)
helmet(640, 34)
dr.text((676, 26), "Армия", font=fnt(16), fill=DIM)
dr.text((676, 44), "4 бат.", font=fntb(30), fill=TXT)
dr.text((780, 50), "−1,0/с", font=fnt(20), fill=RED)
# territory bar
dr.text((900, 26), "Территория", font=fnt(16), fill=DIM)
bx0, bx1, by0, by1 = 900, 1290, 50, 74
dr.rectangle([bx0, by0, bx1, by1], fill=(40, 46, 58), outline=(12, 14, 18), width=2)
share, goal, lead = 0.12, 0.40, 0.31
sc = lambda v: bx0 + (bx1 - bx0) * v / 0.5
dr.rectangle([bx0 + 2, by0 + 2, sc(share), by1 - 2], fill=tuple(int(v) for v in nat[PL]))
dr.rectangle([sc(goal) - 2, by0 - 6, sc(goal) + 2, by1 + 6], fill=GOLD)
dr.polygon([(sc(lead) - 7, by0 - 10), (sc(lead) + 7, by0 - 10), (sc(lead), by0 - 1)], fill=tuple(int(v) for v in nat[7]), outline=(10, 10, 10))
dr.text((sc(share) + 8, 62), "12%", font=fntb(18), fill=TXT, anchor="lm")
dr.text((sc(lead), 82), "Дальмира 31%", font=fnt(14), fill=DIM, anchor="mt")
dr.text((sc(goal) + 8, 30), "40% победа", font=fnt(14), fill=GOLD, anchor="lm")
dr.text((1330, 26), "До конца", font=fnt(16), fill=DIM)
dr.text((1330, 44), "38:12", font=fntb(30), fill=TXT)

# top-right speed
panel(1500, 16, 1904, 92)
labels = ["II", "×1", "×2", "×4"]
for i, l in enumerate(labels):
    x = 1514 + i * 86
    on = i == 3
    dr.rectangle([x, 28, x + 76, 80], fill=(64, 104, 200) if on else (46, 54, 70), outline=(12, 14, 18), width=2)
    dr.text((x + 38, 54), l, font=fntb(24), fill=TXT, anchor="mm")
# menu
# left news feed
def news(y, k, head, body, col):
    panel(16, y, 470, y + 84, edge=col)
    dr.rectangle([19, y + 3, 27, y + 81], fill=col)
    dr.text((40, y + 12), head, font=fntb(16), fill=col)
    dr.text((40, y + 36), body, font=fnt(20), fill=TXT)
news(796, 1, "СРОЧНО · 0:14 назад", "Борсен перешёл в наступление", (232, 92, 80))
news(888, 7, "СВОДКА · 0:52 назад", "Пала столица Ровении", (200, 204, 214))

# right fronts panel
panel(1580, 118, 1904, 410)
dr.text((1598, 132), "Фронты", font=fntb(24), fill=TXT)
rows = [(gain_n, 1.6, "+9"), (lose_n, 0.7, "−7")] + [(n, 1.0, "0") for n in neigh if n not in (gain_n, lose_n)][:2]
for i, (k, ratio, tr) in enumerate(rows):
    y = 172 + i * 72
    on = i == 0
    dr.rectangle([1594, y, 1890, y + 62], fill=(36, 44, 58) if not on else (52, 60, 40), outline=(255, 196, 64) if on else (12, 14, 18), width=3 if on else 2)
    flag(1604, y + 12, 40, 28, k)
    dr.text((1654, y + 8), names[k], font=fntb(20), fill=TXT)
    # pressure bar: us vs them
    x0b, x1b = 1654, 1810
    mid = x0b + (x1b - x0b) * ratio / (1 + ratio)
    dr.rectangle([x0b, y + 38, mid, y + 50], fill=tuple(int(v) for v in nat[PL]))
    dr.rectangle([mid, y + 38, x1b, y + 50], fill=tuple(int(v) for v in nat[k]))
    dr.rectangle([x0b, y + 38, x1b, y + 50], outline=(10, 10, 14), width=2)
    c = GREEN if tr.startswith("+") else (RED if tr.startswith("−") else DIM)
    dr.text((1880, y + 31), tr, font=fntb(20), fill=c, anchor="rm")
dr.text((1598, 392), "px за 10 с · тап — к фронту", font=fnt(14), fill=DIM, anchor="lm")

# front tooltip panel near the active front
sel = np.argwhere((own == PL) & (front_with == gain_n))
if len(sel):
    p = sel[len(sel) // 2]
    fx_, fy_ = OX + p[1] * S, OY + p[0] * S
else:
    fx_, fy_ = 960, 540
tx0 = 740; ty0 = 470
panel(tx0, ty0, tx0 + 380, ty0 + 160, edge=(255, 196, 64, 255))
dr.line([fx_, fy_, tx0 + 40, ty0], fill=(255, 196, 64), width=3)
dr.text((tx0 + 16, ty0 + 12), f"Фронт с {('Борсеном' if gain_n == 1 else names[gain_n])}", font=fntb(22), fill=TXT)
dr.text((tx0 + 16, ty0 + 44), "Мы 4,8 бат. на 120 px", font=fnt(18), fill=TXT)
dr.text((tx0 + 16, ty0 + 68), "Они 3,0 бат. на 120 px", font=fnt(18), fill=TXT)
dr.text((tx0 + 16, ty0 + 96), "Мы давим 1,6 : 1", font=fntb(20), fill=GREEN)
dr.text((tx0 + 16, ty0 + 126), "▲ наше наступление, 14 с", font=fnt(16), fill=GOLD)

# bottom action bar
def action(x, title, price, sub, icon, state, prog=1.0):
    w, hgt, y = 250, 92, 970
    fill = (40, 48, 64) if state != "off" else (30, 34, 44)
    dr.rectangle([x, y, x + w, y + hgt], fill=fill, outline=(12, 14, 18), width=3)
    if state == "off":
        dr.rectangle([x + 3, y + hgt - 10, x + 3 + (w - 6) * prog, y + hgt - 4], fill=GOLD)
    else:
        dr.line([x + 3, y + 4, x + w - 3, y + 4], fill=HI, width=2)
    icon(x + 18, y + 26)
    tc = TXT if state != "off" else DIM
    dr.text((x + 62, y + 16), title, font=fntb(24), fill=tc)
    dr.text((x + 62, y + 50), price, font=fnt(20), fill=GOLD if state != "off" else DIM)
    if sub:
        dr.text((x + w - 12, y + 52), sub, font=fnt(16), fill=DIM, anchor="ra")
def sand(x, y):
    for i in range(3):
        dr.rectangle([x + i * 9, y + 14, x + i * 9 + 10, y + 24], fill=(170, 138, 90), outline=(50, 38, 26))
    for i in range(2):
        dr.rectangle([x + 4 + i * 9, y + 4, x + 4 + i * 9 + 10, y + 14], fill=(170, 138, 90), outline=(50, 38, 26))
def arrow(x, y):
    dr.polygon([(x, y + 8), (x + 16, y + 8), (x + 16, y), (x + 30, y + 13), (x + 16, y + 26), (x + 16, y + 18), (x, y + 18)], fill=(232, 92, 80), outline=(50, 20, 16))
action(560, "Батальон", "200", "+1 к армии", helmet, "on")
action(835, "Укрепрайон", "600", "~25 с", sand, "off", 0.62)
action(1110, "Наступление", "−1 бат.", "20 с", arrow, "on")

# legend tag
dr.text((16, 1062), "Мокап, не скриншот · шрифт Tiny5", font=fnt(14), fill=(200, 214, 232), anchor="lm")
canvas.save(OUT + "/art-01-hud-pc.png")

