def star(dr, cx, cy, r, fill, outline):
    import math
    pts = []
    for i in range(10):
        ang = -math.pi / 2 + i * math.pi / 5
        rr = r if i % 2 == 0 else r * 0.45
        pts.append((cx + rr * math.cos(ang), cy + rr * math.sin(ang)))
    dr.polygon(pts, fill=fill, outline=outline, width=2)

def bunker(dr, cx, cy, s, ocol):
    # sandbag ring: brown octagon w/ dark center, owner-colored flag
    dr.rectangle([cx - s, cy - s + 2, cx + s, cy + s - 2], fill=(150, 120, 78), outline=(40, 30, 24), width=2)
    dr.rectangle([cx - s // 2, cy - s // 2 + 1, cx + s // 2, cy + s // 2 - 1], fill=(60, 50, 40))
    dr.rectangle([cx - 2, cy - s - 8, cx + 0, cy - s + 2], fill=(40, 30, 24))
    dr.rectangle([cx + 1, cy - s - 8, cx + 9, cy - s - 3], fill=ocol)

fnt = lambda s: ImageFont.truetype(FONT, 20 if s <= 24 else 30)
fntb = lambda s: ImageFont.truetype(FONT, 20 if s <= 22 else 30)

def text_outline(dr, xy, s, f, fill, out=(20, 18, 24), anchor="mm"):
    x, y = xy
    for dx in (-2, -1, 0, 1, 2):
        for dy in (-2, -1, 0, 1, 2):
            if dx or dy:
                dr.text((x + dx, y + dy), s, font=f, fill=out, anchor=anchor)
    dr.text((x, y), s, font=f, fill=fill, anchor=anchor)

