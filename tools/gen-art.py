#!/usr/bin/env python3
"""Reproducible Rim Prison art, using only the Python standard library.

Run: python3 tools/gen-art.py [--preview /tmp/rim-prison-art.png]
Then: python3 tools/check-art.py
Coordinates are final pixels, at 64 pixels/cell. Shapes are rasterized at 3x
and alpha-weighted downsampled for clean silhouettes, including at UI scale.
"""
import argparse
import math
from pathlib import Path
import struct
import xml.etree.ElementTree as ET
import zlib

REPO = Path(__file__).resolve().parents[1]
ROOT = REPO / 'Mods/RimPrison/Textures'
SS = 3
INK = '#303938'
STEEL = '#99aaa8'
LIGHT = '#d5dfd7'
DARK = '#596c6b'
TEAL = '#587d79'
WOOD = '#a98a60'
PAPER = '#e5e1cd'
WHITE = '#ffffff'


def rgba(c):
    if isinstance(c, str):
        return tuple(int(c[i:i+2], 16) for i in (1, 3, 5)) + (255,)
    return c


def tint(c, delta):
    return tuple(max(0, min(255, v + delta)) for v in rgba(c)[:3]) + (255,)


class Canvas:
    def __init__(self, w=64, h=64):
        self.w, self.h = w, h
        self.sw, self.sh = w * SS, h * SS
        self.px = bytearray(self.sw * self.sh * 4)

    def paint(self, x, y, c):
        if not (0 <= x < self.sw and 0 <= y < self.sh):
            return
        i = (y * self.sw + x) * 4
        if c is None:
            self.px[i:i+4] = b'\0\0\0\0'
            return
        if c[3] == 255:
            self.px[i:i+4] = bytes(c)
            return
        a = c[3] / 255
        ba = self.px[i+3] / 255
        oa = a + ba * (1-a)
        if oa:
            self.px[i:i+4] = bytes([round((c[k]*a + self.px[i+k]*ba*(1-a))/oa) for k in range(3)] + [round(oa*255)])

    def rect(self, x0, y0, x1, y1, c, radius=0):
        c = rgba(c)
        for y in range(max(0, math.floor(y0*SS)), min(self.sh, math.ceil(y1*SS))):
            for x in range(max(0, math.floor(x0*SS)), min(self.sw, math.ceil(x1*SS))):
                px, py = (x+.5)/SS, (y+.5)/SS
                if radius:
                    cx = min(max(px, x0+radius), x1-radius)
                    cy = min(max(py, y0+radius), y1-radius)
                    if (px-cx)**2 + (py-cy)**2 > radius**2:
                        continue
                self.paint(x, y, c)

    def ellipse(self, cx, cy, rx, ry, c):
        if rx <= 0 or ry <= 0:
            return
        c = rgba(c)
        for y in range(max(0, math.floor((cy-ry)*SS)), min(self.sh, math.ceil((cy+ry)*SS))):
            for x in range(max(0, math.floor((cx-rx)*SS)), min(self.sw, math.ceil((cx+rx)*SS))):
                if (((x+.5)/SS-cx)/rx)**2 + (((y+.5)/SS-cy)/ry)**2 <= 1:
                    self.paint(x, y, c)

    def poly(self, pts, c):
        c = rgba(c)
        for y in range(max(0, math.floor(min(p[1] for p in pts)*SS)), min(self.sh, math.ceil(max(p[1] for p in pts)*SS))):
            py = (y+.5)/SS
            xs = []
            for (ax, ay), (bx, by) in zip(pts, pts[1:]+pts[:1]):
                if (ay <= py < by) or (by <= py < ay):
                    xs.append(ax + (py-ay)*(bx-ax)/(by-ay))
            xs.sort()
            for a, b in zip(xs[::2], xs[1::2]):
                for x in range(max(0, math.ceil(a*SS-.5)), min(self.sw, math.ceil(b*SS-.5))):
                    self.paint(x, y, c)

    def line(self, pts, c, width=2):
        for (ax, ay), (bx, by) in zip(pts, pts[1:]):
            d = math.hypot(bx-ax, by-ay)
            if not d:
                continue
            dx, dy = (by-ay)*width/(2*d), (ax-bx)*width/(2*d)
            self.poly([(ax+dx,ay+dy),(bx+dx,by+dy),(bx-dx,by-dy),(ax-dx,ay-dy)], c)
        for x, y in pts:
            self.ellipse(x, y, width/2, width/2, c)

    def ring(self, x, y, rx, ry, c, width=2):
        self.line([(x+rx*math.cos(a*math.tau/64), y+ry*math.sin(a*math.tau/64)) for a in range(65)], c, width)

    def panel(self, x0, y0, x1, y1, c=STEEL, r=3, edge=2):
        """Dark seam, upper-left bevel and quiet lower-right shading."""
        self.rect(x0+1, y0+2, x1+1.5, y1+2, (20,27,27,55), r)
        self.rect(x0, y0, x1, y1, INK, r)
        self.rect(x0+edge, y0+edge, x1-edge, y1-edge, tint(c,25), max(0,r-1))
        self.rect(x0+edge+1, y0+edge+1, x1-edge, y1-edge, c, max(0,r-1))
        self.rect(x0+edge+2, y1-edge-3, x1-edge, y1-edge, tint(c,-21), 1)
        self.rect(x1-edge-2, y0+edge+3, x1-edge, y1-edge, tint(c,-17), 1)

    def disk(self, x, y, rx, ry, c):
        self.ellipse(x, y, rx, ry, INK)
        self.ellipse(x-.3, y-.5, rx-2, ry-2, tint(c,18))
        self.ellipse(x+.4, y+.7, rx-3, ry-3, c)

    def bolt(self, x, y):
        self.ellipse(x, y, 1.6, 1.6, INK)
        self.ellipse(x-.4, y-.4, .7, .7, LIGHT)

    def outline(self, c=INK, width=1):
        src = self.px[:]
        offsets = [(dx,dy) for dy in range(-math.ceil(width*SS),math.ceil(width*SS)+1) for dx in range(-math.ceil(width*SS),math.ceil(width*SS)+1) if dx*dx+dy*dy <= (width*SS)**2]
        for y in range(self.sh):
            for x in range(self.sw):
                i = (y*self.sw+x)*4
                if src[i+3]:
                    continue
                if any(0<=x+dx<self.sw and 0<=y+dy<self.sh and src[((y+dy)*self.sw+x+dx)*4+3]>128 for dx,dy in offsets):
                    self.paint(x,y,rgba(c))

    def pixels(self):
        result = bytearray()
        step = self.sw // self.w
        for y in range(self.h):
            for x in range(self.w):
                ps = [self.px[((y*step+dy)*self.sw+x*step+dx)*4:((y*step+dy)*self.sw+x*step+dx)*4+4] for dy in range(step) for dx in range(step)]
                a = sum(p[3] for p in ps)
                result.extend([round(sum(p[k]*p[3] for p in ps)/a) if a else 0 for k in range(3)] + [round(a/(step*step))])
        return result

    def save(self, path):
        save_png(path, self.w, self.h, self.pixels())


def save_png(path, w, h, pixels):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    def chunk(t, d):
        return struct.pack('>I',len(d))+t+d+struct.pack('>I',zlib.crc32(t+d)&0xffffffff)
    raw = b''.join(b'\0'+pixels[y*w*4:(y+1)*w*4] for y in range(h))
    path.write_bytes(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',w,h,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(raw,9))+chunk(b'IEND',b''))


def toilet():
    c = Canvas()
    c.panel(19,8,45,25,'#d1d9cd',4)
    c.rect(23,11,40,13,'#f0eee0',1)
    c.panel(36,15,42,19,STEEL,1,1)
    c.disk(32,38,16,21,'#b4c3b9')
    c.disk(32,36,14,18,'#e4e7da')
    c.ellipse(32,37,9,12,DARK)
    c.ellipse(32,39,7,9,'#6d999d')
    c.ellipse(30,40,4,6,'#a2c1bd')
    c.line([(25,32),(25,27),(30,24)],'#f8f3e5',1.5)
    c.panel(23,22,41,27,'#d6ded0',2,1)
    return c


def shower():
    c = Canvas()
    c.panel(5,6,59,59,STEEL,4)
    c.panel(9,10,55,55,'#bfcac1',2,1)
    for p in (24,40):
        c.line([(p,11),(p,53)],'#92a6a1',.8)
        c.line([(11,p),(53,p)],'#92a6a1',.8)
    c.disk(33,38,6,6,STEEL)
    for x in (30,33,36):
        c.line([(x,35),(x,41)],INK,.8)
    c.line([(16,17),(16,9),(32,9),(32,18)],INK,5)
    c.line([(16,16),(16,9),(32,9),(32,17)],LIGHT,2.5)
    c.disk(32,19,7,4,'#b8c8c3')
    c.line([(28,19),(36,19)],DARK,1)
    for x in (26,32,38):
        c.line([(x,25),(x-2,29)],'#779fa6',1.1)
    c.disk(15,18,3.2,3.2,STEEL)
    c.line([(14,17),(17,19)],'#9b6657',1)
    return c


def serving_counter():
    c = Canvas(192,64)
    c.panel(4,9,188,55,STEEL,4)
    c.panel(8,12,184,20,'#bcc7ba',2,1)
    c.line([(12,15),(179,15)],LIGHT,1)
    for x, food in ((12,'#9b7953'),(51,'#9a664a'),(90,'#89945e'),(129,'#c1ae78')):
        c.panel(x,24,x+34,49,DARK,3,1)
        c.panel(x+3,26,x+31,46,food,3,1)
        for dx,dy in ((8,7),(17,13),(24,6)):
            c.ellipse(x+dx,27+dy,3.3,2.2,tint(food,16))
        c.line([(x+28,29),(x+32,23)],INK,2.2)
        c.line([(x+28,29),(x+32,23)],LIGHT,1)
    for y in (29,34,39,44):
        c.panel(169,y,181,y+5,PAPER,1,1)
    for x in (9,183):
        c.bolt(x,51)
    return c


def commissary():
    c = Canvas(128,64)
    c.panel(4,9,124,55,WOOD,3)
    c.panel(8,13,90,48,'#78918b',2)
    for x,col in ((13,'#ad7058'),(30,'#c7b27d'),(48,'#8d9eac'),(66,'#a67d66')):
        c.panel(x,28,x+13,43,col,1,1)
        c.rect(x+3,31,x+10,35,PAPER,1)
    c.line([(10,19),(88,19)],LIGHT,1)
    c.poly([(10,15),(25,15),(56,46),(43,46)],(216,235,224,55))
    c.line([(47,15),(47,46)],DARK,2)
    c.panel(96,21,119,44,'#616e68',3)
    c.panel(98,18,117,30,'#b0b8a1',2,1)
    c.rect(101,21,114,26,'#374d45',1)
    for x in (100,106,112):
        for y in (33,37): c.rect(x,y,x+3,y+2,PAPER)
    c.line([(13,51),(85,51)],'#755d46',1)
    return c


def press():
    c = Canvas(128,64)
    c.panel(4,8,124,56,'#687e77',3)
    c.panel(10,13,72,51,'#46635f',3)
    c.panel(20,18,62,46,DARK,2)
    c.panel(25,21,57,43,'#a6b4a9',2)
    c.panel(13,10,69,22,'#7f9c91',3)
    c.panel(17,12,65,17,'#b2bfb1',1,1)
    c.panel(34,19,48,31,STEEL,1,1)
    c.line([(20,45),(62,45)],'#c8ad6d',3)
    for x in range(21,61,9): c.line([(x,44),(x+3,46)],INK,1.5)
    c.panel(78,27,115,49,STEEL,2)
    for y in (31,35,39):
        c.panel(84,y,109,y+8,PAPER,1,1)
        c.line([(89,y+4),(103,y+4)],'#637e89',1.6)
    c.panel(81,13,113,23,DARK,2,1)
    c.ellipse(87,18,2,2,'#a4bd79')
    c.ellipse(96,18,2,2,'#b47a60')
    c.line([(103,18),(109,18)],LIGHT,1)
    c.line([(66,29),(74,23),(76,14)],INK,4)
    c.line([(66,29),(74,23)],STEEL,1.5)
    c.disk(76,13,3,3,'#a6684d')
    for x in (9,119):
        c.bolt(x,51)
    return c


def chair(c,x,y,w=22):
    c.panel(x,y,x+w,y+15,'#647b74',4)
    c.panel(x+1,y+10,x+w-1,y+17,'#4b625c',2)
    c.line([(x+4,y+3),(x+w-5,y+3)],'#96a396',1)


def school_desk():
    c = Canvas()
    chair(c,21,43)
    c.panel(7,9,57,39,WOOD,3)
    c.line([(12,15),(51,15)],'#cab387',1)
    c.line([(11,32),(29,32)],'#846e50',.8)
    c.panel(15,17,34,33,PAPER,1,1)
    c.line([(19,21),(29,21)],'#919b8b',1)
    c.line([(19,25),(30,25)],'#919b8b',1)
    c.line([(19,29),(25,29)],'#919b8b',1)
    c.line([(41,30),(45,18)],INK,3)
    c.line([(41,29),(44,20)],'#cfb267',1.5)
    c.ellipse(49,32,2,2,'#725d46')
    return c


def office_desk():
    c = Canvas()
    chair(c,22,43,23)
    c.panel(5,7,59,42,'#98774f',3)
    c.line([(10,12),(53,12)],'#bda075',1)
    c.panel(10,20,28,36,PAPER,1,1)
    c.panel(13,16,29,32,'#edebd7',1,1)
    for y in (21,25,28): c.line([(16,y),(25,y)],'#9ba696',.8)
    c.panel(34,12,53,25,'#4d5e59',2)
    c.rect(37,14,50,21,'#819f9a',1)
    c.line([(38,15),(48,15)],'#b2c4b4',1)
    c.line([(43,25),(43,28)],INK,3)
    c.panel(34,29,53,35,'#c5c7b6',1,1)
    for x in range(37,51,4): c.line([(x,31),(x,33)],DARK,.8)
    c.disk(53,38,3,3,'#b5a37c')
    return c


def handset(c,x,y,flip=False):
    d = -1 if flip else 1
    c.line([(x+3*d,y),(x,y+2),(x,y+11),(x+3*d,y+13)],INK,4.3)
    c.line([(x+.2*d,y+3),(x+.2*d,y+10)],'#64716b',1)


def phone_booth():
    c = Canvas()
    c.panel(9,8,55,51,'#64817b',3)
    c.panel(16,12,48,43,'#adb7a9',2)
    c.panel(30,16,43,22,'#485e57',1,1)
    c.line([(33,19),(40,19)],'#b9c8a7',1)
    for x in (31,35,39):
        for y in (26,30,34): c.rect(x,y,x+2,y+2,INK,.4)
    handset(c,22,18)
    c.line([(24,31),(26,39),(22,44),(24,47),(31,46)],INK,1.3)
    c.panel(7,45,57,53,STEEL,2)
    c.panel(6,11,12,48,'#7e9c92',2)
    c.panel(52,11,58,48,'#7e9c92',2)
    return c


def visitation_booth():
    c = Canvas()
    chair(c,22,5,20)
    chair(c,22,44,20)
    c.panel(7,22,57,43,'#b1ad90',3)
    c.panel(6,29,58,35,'#698883',1)
    c.line([(10,30),(53,30)],'#c4d8cb',1.3)
    c.rect(10,31,54,33,'#89aca9')
    c.panel(5,19,10,45,DARK,1,1)
    c.panel(54,19,59,45,DARK,1,1)
    handset(c,16,20)
    handset(c,48,36,True)
    c.line([(19,22),(24,25),(28,24)],DARK,1)
    c.line([(46,48),(40,44),(35,45)],DARK,1)
    return c


def washing_machine():
    c = Canvas()
    c.panel(7,6,57,57,'#c0cbbf',4)
    c.panel(10,9,54,20,STEEL,2,1)
    c.panel(13,11,24,17,'#7f9590',1,1)
    c.disk(44,14,3.5,3.5,'#d1d6c6')
    c.ellipse(33,14,1.6,1.6,'#98b67b')
    c.disk(32,37,17,17,'#899b94')
    c.disk(32,37,13,13,'#3d5c60')
    c.ellipse(33,39,9,8,'#67898b')
    c.poly([(24,40),(29,34),(36,37),(40,43),(31,45)],'#a5bbb2')
    c.line([(23,33),(26,29),(32,28)],'#cad6c9',1.5)
    c.panel(45,34,49,42,LIGHT,1,1)
    c.line([(13,53),(50,53)],DARK,1)
    return c


def execution_table():
    c = Canvas(64,128)
    for x in (15,44):
        c.panel(x,13,x+6,116,DARK,2,1)
    c.panel(12,7,52,120,STEEL,6)
    c.panel(17,11,47,115,'#829d8c',6)
    c.panel(20,15,44,37,'#b4c3a9',5,1)
    c.line([(23,18),(39,18)],'#d4dbc3',1)
    c.panel(4,54,60,66,STEEL,2)
    c.panel(7,56,21,63,'#8da594',2,1)
    c.panel(43,56,57,63,'#8da594',2,1)
    for y in (45,72,96):
        c.panel(13,y,51,y+7,'#73614a',1,1)
        c.panel(38,y,45,y+7,STEEL,1,1)
        c.rect(40,y+2,43,y+5,DARK)
    c.line([(22,104),(22,109),(39,109)],'#b4c1a8',1.2)
    return c


def tower():
    c = Canvas(128,128)
    c.panel(7,7,121,121,'#626f60',4)
    c.panel(13,13,115,115,'#9c9070',2)
    for x in range(20,114,12):
        c.line([(x,16),(x,112)],'#776e54',1)
        c.line([(x+2,17),(x+2,111)],'#b5a581',.7)
    c.panel(29,27,99,91,'#667f72',4)
    c.panel(35,33,93,85,'#a7b3a0',3)
    # Roof viewed directly from above: four flat planes and a raised ridge.
    c.poly([(33,31),(64,42),(95,31)],'#8b9f8c')
    c.poly([(33,31),(64,42),(64,77),(33,87)],'#809783')
    c.poly([(95,31),(64,42),(64,77),(95,87)],'#5b7465')
    c.poly([(33,87),(64,77),(95,87)],'#4e665b')
    c.line([(64,42),(64,76)],'#bcc5a6',2)
    c.line([(35,31),(93,31)],'#c1c6aa',2)
    for x,y in ((12,12),(108,12),(12,108),(108,108)):
        c.panel(x-4,y-4,x+8,y+8,DARK,2)
        c.bolt(x+1,y+1)
    for a,b in (((22,12),(99,12)),((12,22),(12,99)),((115,22),(115,99)),((22,115),(47,115)),((79,115),(99,115))):
        c.line([a,b],INK,4)
        c.line([a,b],'#b4b9a1',1.7)
    c.panel(52,93,76,120,DARK,1)
    for y in range(96,119,6): c.line([(55,y),(73,y)],STEEL,2)
    c.disk(101,64,10,10,DARK)
    c.panel(94,54,109,70,'#aab6a2',3)
    c.panel(97,51,106,57,'#d5ce9a',1,1)
    return c


def camera():
    c = Canvas()
    c.panel(21,39,42,55,STEEL,3)
    c.line([(32,44),(32,30)],INK,8)
    c.line([(30,44),(30,30)],DARK,3)
    c.poly([(14,14),(45,14),(51,33),(17,33)],INK)
    c.poly([(16,16),(43,16),(47,29),(19,29)],'#bcc9bd')
    c.poly([(18,24),(47,24),(49,31),(18,31)],'#8a9c95')
    c.panel(13,15,23,32,'#526763',2)
    c.disk(17,23,5,6,'#2f5158')
    c.ellipse(16,21,1.4,1.8,'#9bb9b4')
    c.line([(25,18),(41,18)],'#e0e2cd',1.2)
    c.ellipse(43,27,1.5,1.5,'#ad6d54')
    c.bolt(26,49)
    c.bolt(37,49)
    return c


def guard_post():
    """A floor marker like vanilla's spots: a disc on the ground with a shield on it."""
    c = Canvas()
    c.ellipse(32,32,25,25,(48,57,56,190))
    c.ellipse(32,32,22,22,(88,125,121,190))
    c.ring(32,32,19,19,LIGHT,1.5)
    c.poly([(21,19),(43,19),(43,33),(32,46),(21,33)],INK)
    c.poly([(23,21),(41,21),(41,32),(32,43),(23,32)],LIGHT)
    c.poly([(32,21),(41,21),(41,32),(32,43)],STEEL)
    c.line([(32,23),(32,40)],INK,1.2)
    return c


def metal_detector():
    c = Canvas()
    # Overhead arch: open center remains transparent for the walk-through cell.
    c.panel(7,10,20,56,STEEL,3)
    c.panel(44,10,57,56,STEEL,3)
    c.panel(7,8,57,20,'#7e9690',3)
    c.line([(11,11),(52,11)],'#c7d2c2',1.5)
    c.panel(24,10,40,17,DARK,1,1)
    c.ellipse(29,13.5,1.5,1.5,'#a6bd79')
    c.ellipse(35,13.5,1.5,1.5,'#b2755d')
    for x in (11,48):
        c.panel(x,25,x+5,46,DARK,1,1)
        c.line([(x+2,29),(x+2,42)],'#90a79a',1)
        c.bolt(x+2,51)
    return c


def security_door():
    c = Canvas()
    # DoorBase moves this horizontal slab sideways; do not draw a fixed frame.
    c.panel(2,20,62,45,'#87968f',2)
    c.panel(7,24,57,39,'#b5bdb2',1,1)
    c.panel(20,24,43,37,DARK,1,1)
    for x in (24,31,38): c.rect(x,25,x+3,36,LIGHT)
    c.panel(48,30,54,38,DARK,1,1)
    c.line([(49,33),(52,33)],LIGHT,1)
    for x in (6,58): c.bolt(x,41)
    return c


def shiv():
    c = Canvas()
    c.poly([(10,49),(43,13),(54,7),(48,23),(20,56)],INK)
    c.poly([(21,42),(43,16),(51,11),(46,23),(26,46)],'#a7b6b2')
    c.poly([(24,41),(51,11),(46,23),(26,46)],'#dce0cf')
    c.poly([(9,49),(18,41),(27,48),(18,58)],'#685c4a')
    for x,y in ((13,49),(16,46),(20,46),(22,49),(19,53)):
        c.line([(x,y),(x+4,y+4)],'#b3a78b',2)
    c.line([(20,41),(28,48)],INK,3)
    return c


def tools():
    c = Canvas()
    c.line([(15,13),(46,49)],INK,8)
    c.line([(15,13),(46,49)],STEEL,4)
    c.poly([(7,12),(13,7),(13,15),(18,19),(22,15),(21,7),(27,13),(26,21),(20,26),(13,23)],INK)
    c.poly([(10,13),(12,11),(12,16),(18,22),(24,17),(24,20),(19,23),(14,21)],LIGHT)
    c.disk(46,49,6,6,STEEL)
    c.ellipse(46,49,2,2,INK)
    c.line([(15,49),(43,17)],INK,5)
    c.line([(15,49),(43,17)],LIGHT,2.5)
    c.poly([(39,16),(47,8),(51,12),(44,21)],INK)
    c.poly([(42,16),(47,11),(48,12),(44,18)],STEEL)
    c.line([(14,50),(27,36)],INK,10)
    c.line([(14,50),(27,36)],'#a27050',6)
    c.line([(14,48),(24,37)],'#c6a078',1.5)
    return c


def cellphone():
    c = Canvas()
    c.panel(18,6,46,58,'#485753',5)
    c.panel(22,15,42,38,'#7b9d96',1,1)
    c.poly([(24,17),(32,17),(39,29),(39,34)],'#a8bdb0')
    c.line([(29,11),(35,11)],'#a5b2a3',1.3)
    c.disk(32,43,4,3,'#a7b2a1')
    for x in (24,31,38):
        for y in (48,52): c.rect(x,y,x+3,y+2,'#bec6b4',.6)
    return c


def cigarettes():
    c = Canvas()
    c.panel(15,19,49,55,PAPER,2)
    c.poly([(17,21),(24,13),(49,14),(47,25)],INK)
    c.poly([(20,20),(25,15),(46,16),(44,23)],'#c6bda0')
    for x,y in ((24,6),(32,8),(40,10)):
        c.panel(x,y,x+6,25,'#e3ddc3',1,1)
        c.rect(x+1,y+1,x+5,y+6,'#b29260',.5)
    c.panel(15,24,49,36,'#9f5e4c',1,1)
    c.poly([(26,41),(32,37),(38,41),(32,47)],'#9f5e4c')
    c.line([(23,50),(41,50)],'#b3aa8d',1)
    return c


def hooch():
    """A plastic jug of cloudy hooch, cap off."""
    c = Canvas()
    c.panel(19,22,45,56,'#b9a45a',2)
    c.rect(22,31,42,52,'#d9c47a',1)
    c.rect(22,31,42,36,'#efe3ae',1)
    c.panel(27,12,37,22,'#b9a45a',1)
    c.rect(26,9,38,13,INK,1)
    c.line([(24,40),(40,40)],'#a89242',1)
    return c


def hooch_mash():
    """A dented tin of fruit mash with a cloth tied over it."""
    c = Canvas()
    c.panel(16,24,48,54,STEEL,2)
    c.rect(18,26,46,32,LIGHT,1)
    c.poly([(14,24),(20,16),(44,16),(50,24)],'#b78c78')
    c.line([(18,23),(46,23)],'#8f6a5b',1.5)
    for x,y in ((24,40),(33,44),(40,38)):
        c.ellipse(x,y,3,3,'#a35e5e')
    return c


def license_plates():
    c = Canvas()
    for x,y in ((8,32),(11,23),(8,14)):
        c.panel(x,y,x+47,y+20,'#bac7b5',2)
        c.rect(x+3,y+3,x+44,y+6,'#617c86',1)
        for dx in (9,16,24,32):
            c.line([(x+dx,y+10),(x+dx,y+15),(x+dx+4,y+15),(x+dx+4,y+10)],'#506b74',1.5)
        c.ellipse(x+4,y+10,1,1,INK)
        c.ellipse(x+43,y+10,1,1,INK)
    return c


def weight_bench():
    """Top view: a padded bench with a barbell lying across a rack at its head."""
    c = Canvas()
    c.panel(22,8,42,56,'#687e77',3)
    c.panel(25,13,39,52,'#9b6657',3,1)
    c.line([(32,16),(32,49)],'#bd8a74',1)
    for y in (16,49): c.bolt(32,y)
    # The rack uprights and the bar resting on them.
    for x in (13,51):
        c.panel(x-3,13,x+3,24,DARK,1,1)
        c.bolt(x,18)
    c.line([(5,18),(59,18)],INK,5)
    c.line([(5,18),(59,18)],LIGHT,2.2)
    for x in (9,55):
        c.disk(x,18,5,9,DARK)
        c.disk(x,18,3,6,STEEL)
    c.panel(14,49,22,57,DARK,1,1)
    c.panel(42,49,50,57,DARK,1,1)
    return c


def book_shelf():
    """Top view of a tall shelf: the spines of two rows of books under a rail."""
    c = Canvas()
    c.panel(6,10,58,54,WOOD,3)
    c.panel(10,14,54,30,'#7d6445',2,1)
    c.panel(10,34,54,50,'#7d6445',2,1)
    for row, y0 in enumerate((16,36)):
        x = 12
        for k, (w, col) in enumerate(((5,'#9f5e4c'),(4,'#587d79'),(6,'#c7b27d'),(4,'#8d9eac'),(5,'#a67d66'),(5,'#6f8c66'),(4,'#b0b8a1'),(5,'#9b7953'))):
            c.panel(x, y0 + (k + row) % 3, x + w, y0 + 12, col, 1, 1)
            x += w + 0.5
    c.line([(8,32),(56,32)],INK,2)
    c.line([(8,31.5),(56,31.5)],'#c9ad82',1)
    for x in (9,55): c.bolt(x,12)
    return c


def weight_plate():
    c = Canvas()
    c.disk(32,32,22,22,DARK)
    c.disk(32,32,19,19,STEEL)
    c.ring(32,32,14,14,INK,1.4)
    c.disk(32,32,6,6,'#d5dfd7')
    c.ellipse(32,32,2.6,2.6,INK)
    c.line([(18,17),(24,13)],LIGHT,1.6)
    return c


def shuttle_pad():
    """The state shuttle's landing pad, 9x7 cells: poured concrete slabs, a painted landing ring, the hull's outline (5x3) and the ramp's boarding strip."""
    c = Canvas(576, 448)
    c.rect(2, 2, 574, 446, '#464f4e', 14)
    c.rect(6, 6, 570, 442, '#7d8685', 11)
    c.rect(8, 8, 568, 440, '#868f8d', 10)
    # Slab seams every cell.
    for x in range(64, 576, 64):
        c.line([(x, 10), (x, 438)], '#6d7675', 1.4)
    for y in range(64, 448, 64):
        c.line([(10, y), (566, y)], '#6d7675', 1.4)
    # Hazard stripes along the short edges.
    for side in (22, 538):
        c.rect(side - 8, 22, side + 8, 426, INK)
        for i in range(16, 388, 32):
            c.poly([(side - 8, 24 + i), (side + 8, 24 + i - 16), (side + 8, 24 + i + 2), (side - 8, 24 + i + 18)], '#e0b84a')
    # Painted border and landing ring.
    c.line([(44, 24), (532, 24), (532, 424), (44, 424), (44, 24)], '#ebe7cf', 3.5)
    c.ring(288, 224, 176, 176, '#ebe7cf', 5)
    c.ring(288, 224, 160, 160, '#e0b84a', 2.4)
    # The hull's outline (5x3 cells) with its corners marked, and the ramp strip below it.
    for (x0, y0, x1, y1) in ((128, 128, 448, 320),):
        for (ax, ay, dx, dy) in ((x0, y0, 1, 1), (x1, y0, -1, 1), (x0, y1, 1, -1), (x1, y1, -1, -1)):
            c.line([(ax, ay + 34 * dy), (ax, ay), (ax + 34 * dx, ay)], '#ebe7cf', 4)
    for i in range(7):
        x = 262 + i * 6
        c.rect(x, 330, x + 3.4, 372, '#e0b84a')
    c.poly([(288, 392), (262, 366), (314, 366)], '#ebe7cf')
    # Landing lights at the corners of the hull outline.
    for (lx, ly) in ((116, 116), (460, 116), (116, 332), (460, 332)):
        c.disk(lx, ly, 6, 6, '#d9a441')
        c.ellipse(lx - 1.2, ly - 1.4, 2, 2, '#fff4c0')
    return c


def padded_bed(direction):
    """A padded cell's bed from above: a quilted grey-blue mattress and a pillow in a padded frame; the head end is north (east for the sideways ones, flipped for south and west)."""
    horizontal = direction in ('east', 'west')
    flip = direction in ('south', 'west')
    c = Canvas(128, 64) if horizontal else Canvas(64, 128)
    def place(u0, v0, u1, v1):
        if flip: v0, v1 = 128 - v1, 128 - v0
        return (v0, u0, v1, u1) if horizontal else (u0, v0, u1, v1)
    def rect(u0, v0, u1, v1, col, r=0): c.rect(*place(u0, v0, u1, v1), col, r)
    def panel(u0, v0, u1, v1, col, r=3, edge=2): c.panel(*place(u0, v0, u1, v1), col, r, edge)
    def line(u0, v0, u1, v1, col, w=1):
        x0, y0, x1, y1 = place(u0, v0, u1, v1)
        c.line([(x0, y0), (x1, y1)], col, w)
    panel(5, 4, 59, 124, '#6e7f87', 6)
    panel(10, 9, 54, 119, '#a3b6bd', 5)
    # The quilting: stitched squares, a button at each crossing.
    for v in (45, 70, 95):
        line(12, v, 52, v, '#7c8f97', 1.4)
    for u in (24, 40):
        line(u, 38, u, 117, '#7c8f97', 1.4)
    for v in (45, 70, 95):
        for u in (24, 40):
            rect(u - 1.6, v - 1.6, u + 1.6, v + 1.6, '#586a72', 1.6)
    panel(15, 13, 49, 33, '#dbe5e8', 7, 1)
    line(20, 23, 44, 23, '#bccbd0', 1.2)
    return c


def antipsychotic():
    """An amber bottle of pills with a white cap and a paper label."""
    c = Canvas()
    c.panel(21,22,45,57,'#c98f3a',4)
    c.rect(24,31,42,50,'#e8b15a',2)
    c.panel(23,11,43,24,'#e8ece8',3,1)
    for x in (27,31,35,39): c.line([(x,13),(x,22)],'#b6bfb9',1)
    c.panel(25,34,41,48,PAPER,1,1)
    c.line([(33,37),(33,45)],TEAL,2.4)
    c.line([(29,41),(37,41)],TEAL,2.4)
    c.ellipse(52,53,5,3,'#e8ece8')
    c.line([(47,53),(57,53)],'#b6bfb9',1)
    c.ellipse(11,54,4,2.6,'#e8ece8')
    return c


def drug_test_kit():
    """A sealed test kit: a white box with a teal band and a cross, a specimen cup and a test strip beside it."""
    c = Canvas()
    c.panel(14,24,50,54,'#e8ece8',4)
    c.rect(14,30,50,38,TEAL,0)
    c.line([(32,40),(32,50)],TEAL,2.4)
    c.line([(27,45),(37,45)],TEAL,2.4)
    c.panel(40,10,56,26,'#d5dfd7',3,1)
    c.rect(41,12,55,16,'#e8b15a',0)
    c.rect(8,10,13,36,PAPER,1)
    c.rect(9,13,12,18,'#9f5e4c',0)
    return c


BUILDINGS = {
    'RP_Toilet': toilet, 'RP_Shower': shower, 'RP_ServingCounter': serving_counter,
    'RP_CommissaryCounter': commissary, 'RP_LicensePlatePress': press,
    'RP_SchoolDesk': school_desk, 'RP_OfficeDesk': office_desk,
    'RP_PhoneBooth': phone_booth, 'RP_VisitationBooth': visitation_booth,
    'RP_WashingMachine': washing_machine, 'RP_ExecutionTable': execution_table,
    'RP_GuardTower': tower, 'RP_GuardPost': guard_post, 'RP_Camera': camera, 'RP_MetalDetector': metal_detector,
    'RP_SecurityDoor': security_door, 'RP_WeightBench': weight_bench, 'RP_BookShelf': book_shelf,
    'RP_ShuttlePad': shuttle_pad,
    **{f'RP_PaddedBed_{d}': (lambda d=d: padded_bed(d)) for d in ('north', 'east', 'south', 'west')},
}
ITEMS = {'RP_Shiv': shiv, 'RP_Tools': tools, 'RP_Cellphone': cellphone,
         'RP_Hooch': hooch, 'RP_HoochMash': hooch_mash, 'RP_Antipsychotic': antipsychotic, 'RP_DrugTestKit': drug_test_kit,
         'RP_Cigarettes': cigarettes, 'RP_LicensePlates': license_plates, 'RP_WeightPlate': weight_plate}


def glyph(kind, size=64, outlined=False):
    """Shared silhouettes: white alpha masks, broad strokes, open counters."""
    c = Canvas()
    w = WHITE
    def line(pts, width=4.5): c.line(pts,w,width)
    def rect(a,b,d,e,r=1): c.rect(a,b,d,e,w,r)
    def poly(pts): c.poly(pts,w)
    def ring(x,y,rx,ry=None,width=4.5): c.ring(x,y,rx,ry or rx,w,width)
    if kind == 'house':
        line([(9,29),(32,10),(55,29)],5)
        line([(15,27),(15,53),(27,53),(27,38),(38,38),(38,53),(49,53),(49,27)],4.5)
    elif kind == 'shield':
        line([(12,14),(32,9),(52,14),(50,36),(44,46),(32,55),(20,46),(14,36),(12,14)],4.5)
        line([(22,31),(29,38),(43,24)],5)
    elif kind == 'clock':
        ring(32,32,22)
        line([(32,18),(32,32),(42,38)],4.5)
        for x,y in ((32,12),(52,32),(32,52),(12,32)): c.ellipse(x,y,1.6,1.6,w)
    elif kind == 'gavel':
        poly([(18,12),(25,7),(43,25),(36,32)])
        poly([(12,17),(17,12),(37,32),(32,37)])
        line([(29,26),(47,44)],7)
        rect(13,49,45,54,2)
        rect(19,44,39,50,2)
    elif kind == 'badge':
        poly([(32,7),(39,16),(50,17),(48,29),(54,38),(43,44),(39,55),(27,51),(16,55),(13,43),(6,35),(15,26),(15,14),(26,15)])
        c.ellipse(30,31,12,12,None)
        ring(30,31,7,width=3)
    elif kind == 'crate':
        line([(10,20),(32,10),(54,20),(54,45),(32,55),(10,45),(10,20),(32,30),(54,20)],4)
        line([(32,30),(32,55)],4)
        line([(21,16),(43,25),(43,35)],4)
    elif kind == 'eye':
        line([(7,32),(15,23),(24,18),(32,16),(40,18),(49,23),(57,32),(49,41),(40,46),(32,48),(24,46),(15,41),(7,32)],4)
        ring(32,32,9,width=4.5)
    elif kind == 'folder':
        line([(10,52),(10,14),(27,14),(32,21),(54,21),(54,52),(10,52)],4.5)
        line([(10,29),(54,29)],4)
    elif kind == 'chart':
        line([(10,10),(10,54),(55,54)],4)
        rect(18,34,25,48)
        rect(31,23,38,48)
        rect(44,12,51,48)
    elif kind == 'plate':
        ring(33,32,17,width=4)
        ring(33,32,11,width=2.5)
        line([(9,12),(9,52)],3)
        line([(5,12),(5,25),(13,25),(13,12)],2.6)
        line([(56,12),(53,29),(57,31),(57,52)],3.5)
    elif kind == 'tree':
        rect(28,36,36,55,1)
        poly([(32,7),(12,29),(21,29),(7,43),(57,43),(43,29),(52,29)])
    elif kind == 'drop':
        poly([(32,7),(18,26),(12,38),(14,47),(21,54),(32,57),(43,54),(50,47),(52,38),(46,26)])
        c.line([(23,33),(20,40),(23,47)],None,3)
    elif kind == 'bench':
        rect(9,17,55,24,2)
        rect(9,28,55,35,2)
        rect(7,39,57,45,2)
        rect(13,22,18,53)
        rect(46,22,51,53)
    elif kind == 'pot':
        line([(15,27),(17,49),(22,53),(43,53),(48,49),(50,27),(15,27)],4.5)
        line([(10,23),(54,23)],4)
        line([(26,17),(26,13),(38,13),(38,17)],4)
        line([(14,31),(8,31),(8,38),(15,38)],3.5)
        line([(50,31),(56,31),(56,38),(50,38)],3.5)
    elif kind == 'truck':
        line([(8,17),(36,17),(36,44),(8,44),(8,17)],4)
        line([(36,25),(47,25),(56,35),(56,44),(36,44)],4)
        line([(43,28),(43,35),(53,35)],3)
        ring(19,47,6,width=4)
        ring(46,47,6,width=4)
    elif kind == 'shuttle':
        poly([(5,37),(14,28),(30,23),(48,27),(59,36),(48,45),(14,44)])
        poly([(22,26),(28,10),(37,10),(35,25)])
        poly([(22,45),(28,56),(37,56),(35,45)])
        c.line([(14,36),(24,36)],None,2.6)
        for x in (33,40,47): c.ellipse(x,34,2.3,2.3,None)
    elif kind == 'candle':
        line([(15,54),(49,54)],4)
        line([(24,51),(24,31),(40,31),(40,51)],4)
        poly([(32,7),(24,19),(26,25),(32,28),(38,25),(40,19)])
        line([(32,27),(32,33)],3)
    elif kind == 'visit':
        rect(24,25,40,40,1)
        rect(29,40,35,50,1)
        line([(8,17),(8,43),(19,43),(19,53)],4)
        line([(8,33),(18,33),(18,43)],4)
        line([(56,17),(56,43),(45,43),(45,53)],4)
        line([(56,33),(46,33),(46,43)],4)
    elif kind == 'bars':
        line([(11,54),(11,10),(53,10),(53,54)],4)
        line([(11,24),(53,24)],4)
        line([(11,48),(53,48)],4)
        for x in (22,32,42): line([(x,12),(x,52)],3.5)
    elif kind == 'hammer':
        poly([(11,17),(22,7),(37,13),(46,23),(36,33),(24,21),(20,25)])
        line([(31,26),(49,50)],8)
    elif kind == 'shirt':
        line([(22,11),(15,15),(5,28),(16,36),(21,28),(21,53),(44,53),(44,28),(49,36),(59,28),(49,15),(42,11)],4)
        line([(22,11),(25,18),(32,21),(39,18),(42,11)],4)
    elif kind == 'book':
        line([(32,17),(23,12),(9,12),(9,48),(23,48),(32,53),(41,48),(55,48),(55,12),(41,12),(32,17),(32,53)],4)
        for y in (23,31,39):
            line([(16,y),(24,y+1)],2.5)
            line([(40,y+1),(48,y)],2.5)
    elif kind == 'shelves':
        line([(9,12),(55,12)],4)
        line([(9,34),(55,34)],4)
        line([(9,54),(55,54)],4)
        line([(10,12),(10,54)],3.5)
        line([(54,12),(54,54)],3.5)
        for x,top in ((17,17),(24,19),(31,16),(41,18)): rect(x,top,x+4,32)
        for x,top in ((17,40),(24,38),(34,41),(44,39)): rect(x,top,x+5,52)
    elif kind == 'leaf':
        poly([(55,8),(36,10),(21,17),(11,29),(10,42),(17,51),(29,52),(42,44),(50,29)])
        c.line([(13,52),(43,23)],None,4)
        line([(10,55),(17,48)],3.5)
    elif kind == 'sofa':
        line([(15,31),(15,16),(20,12),(44,12),(49,16),(49,31)],4)
        line([(10,29),(16,29),(18,42),(46,42),(48,29),(54,29),(56,34),(54,49),(10,49),(8,34),(10,29)],4)
        line([(15,49),(15,54)],4)
        line([(49,49),(49,54)],4)
        line([(32,16),(32,36)],3)
        line([(22,37),(42,37)],3)
    elif kind == 'envelope':
        line([(8,17),(56,17),(56,47),(8,47),(8,17)],4)
        line([(8,18),(32,35),(56,18)],4)
        line([(9,46),(25,30)],3)
        line([(55,46),(39,30)],3)
    elif kind == 'cup':
        line([(14,19),(16,50),(42,50),(44,19),(14,19)],4.5)
        ring(49,32,7,9,4)
        line([(22,6),(20,12),(23,16)],3)
        line([(30,5),(28,11),(31,15)],3)
        line([(10,55),(48,55)],4)
    elif kind == 'door':
        line([(12,55),(12,9),(52,9),(52,55)],4)
        line([(19,55),(19,16),(45,16),(45,55),(19,55)],3)
        for x in (26,33,40): line([(x,20),(x,34)],2.8)
        line([(22,35),(42,35)],2.8)
        c.ellipse(39,43,2,2,w)
    elif kind == 'padlock':
        line([(21,28),(21,19),(24,12),(32,9),(40,12),(43,19),(43,28)],5)
        rect(12,27,52,55,4)
        c.ellipse(32,38,4,4,None)
        c.rect(30,38,34,47,None,1)
    elif kind == 'bed':
        line([(10,14),(10,52)],5)
        line([(54,29),(54,52)],5)
        rect(12,30,54,44,2)
        rect(15,21,28,29,2)
        line([(30,26),(50,26),(54,30)],4)
    elif kind == 'cross':
        poly([(25,9),(39,9),(39,25),(55,25),(55,39),(39,39),(39,55),(25,55),(25,39),(9,39),(9,25),(25,25)])
    elif kind == 'fist':
        poly([(17,50),(11,34),(13,26),(18,25),(18,14),(23,11),(28,14),(30,9),(35,9),(38,14),(42,12),(47,15),(47,25),(52,29),(51,42),(43,54),(24,54)])
        for x in (25,33,41): c.line([(x,17),(x,29)],None,2.5)
        c.line([(17,32),(24,29),(34,34),(31,41)],None,3)
    elif kind == 'star':
        poly([(32+25*math.sin(i*math.pi/5)*(1 if i%2==0 else .46),32-25*math.cos(i*math.pi/5)*(1 if i%2==0 else .46)) for i in range(10)])
    elif kind == 'thermometer':
        line([(25,40),(25,14),(28,9),(34,9),(38,14),(38,40)],4)
        ring(31.5,46,10,width=4)
        line([(31.5,22),(31.5,47)],4)
        for y in (18,26,34): line([(44,y),(50,y)],3)
    elif kind == 'gang':
        c.ellipse(22,21,9,10,w)
        c.ellipse(44,25,8,9,w)
        c.poly([(7,51),(9,39),(16,34),(28,34),(36,40),(38,51)],w)
        c.poly([(38,37),(47,37),(54,42),(57,51),(42,51),(40,41)],w)
    else:
        raise ValueError(f'No glyph design: {kind}')
    if outlined:
        c.outline('#353e3f',.85)
    # Keep the 192px working image, sampling 6x6 per pixel for 32px glyphs.
    c.w = c.h = size
    return c


MAIN_BUTTONS = {
    'RP_Rooms': 'house', 'RP_Deployment': 'shield', 'RP_Regime': 'clock',
    'RP_Policy': 'gavel', 'RP_Staff': 'badge', 'RP_Logistics': 'crate',
    'RP_Intel': 'eye', 'RP_Office': 'folder', 'RP_Reports': 'chart',
}
ZONE_GLYPHS = {
    'RP_Canteen': 'plate', 'RP_Yard': 'tree', 'RP_CommonRoom': 'sofa',
    'RP_Shower': 'drop', 'RP_Holding': 'bench', 'RP_Kitchen': 'pot',
    'RP_StaffOnly': 'badge', 'RP_Deliveries': 'shuttle', 'RP_Exports': 'crate',
    'RP_Chapel': 'candle', 'RP_Visitation': 'visit', 'RP_SolitaryZone': 'bars', 'RP_HearingRoom': 'gavel',
    'RP_Workshop': 'hammer', 'RP_Laundry': 'shirt', 'RP_Classroom': 'book',
    'RP_Garden': 'leaf', 'RP_Library': 'shelves', 'RP_Mailroom': 'envelope', 'RP_StaffRoom': 'cup',
    **{f'RP_Wing{wing}': 'door' for wing in ('Minimum','Medium','Maximum','Supermax','Protective','DeathRow','Psych')},
    'RP_SupermaxYard': 'tree',
    # (The morgue and the burial plot came with the death-inside work, which left no icon for them: this run's art generation stopped at them.)
    'RP_Morgue': 'bed', 'RP_BurialPlot': 'leaf',
}
SMALL_ICONS = {'Padlock': 'padlock', 'Bed': 'bed', 'Cross': 'cross',
               'Fist': 'fist', 'Star': 'star', 'Door': 'door', 'Eye': 'eye',
               'Thermometer': 'thermometer', 'Gang': 'gang', 'Clock': 'clock'}


def zone_names():
    return sorted(e.findtext('defName') for path in (REPO/'Mods/RimPrison/Defs').glob('*.xml')
                  for e in ET.parse(path).getroot() if e.tag == 'RimPrison.PrisonZoneDef')


def textures():
    for name, fn in BUILDINGS.items():
        yield 'Things/Building/'+name, fn()
    for name, fn in ITEMS.items():
        yield 'Things/Item/'+name, fn()
    for name, kind in MAIN_BUTTONS.items():
        yield 'UI/MainButtons/'+name, glyph(kind, outlined=True)
    # Def-driven coverage: new zones need an explicit design, never a silent fallback.
    for name in zone_names():
        yield 'UI/Zones/'+name, glyph(ZONE_GLYPHS[name])
    for name, kind in SMALL_ICONS.items():
        yield 'UI/Icons/'+name, glyph(kind,32)


# Compact bitmap lettering is used only on the optional contact sheet.
FONT = dict(zip('ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-',[
    '01110100011000111111100011000110001','11110100011000111110100011000111110',
    '01111100001000010000100001000001111','11110100011000110001100011000111110',
    '11111100001000011110100001000011111','11111100001000011110100001000010000',
    '01111100001000010111100011000101111','10001100011000111111100011000110001',
    '11111001000010000100001000010011111','00111000100001000010100101001001100',
    '10001100101010011000101001001010001','10000100001000010000100001000011111',
    '10001110111010110101100011000110001','10001110011010110011100011000110001',
    '01110100011000110001100011000101110','11110100011000111110100001000010000',
    '01110100011000110001101011001001101','11110100011000111110101001001010001',
    '01111100001000001110000010000111110','11111001000010000100001000010000100',
    '10001100011000110001100011000101110','10001100011000110001100010101000100',
    '10001100011000110101101011101110001','10001100010101000100010101000110001',
    '10001100010101000100001000010000100','11111000010001000100010001000011111',
    '01110100011001110101110011000101110','00100011000010000100001000010001110',
    '01110100010000100010001000100011111','11110000010000101110000010000111110',
    '00010001100101010010111110001000010','11111100001000011110000010000111110',
    '01110100001000011110100011000101110','11111000010001000100010000100001000',
    '01110100011000101110100011000101110','01110100011000101111000010000101110',
    '00000000000000000000000000000011111','00000000000000011111000000000000000',
]))


def preview(path, arts):
    """Labeled sheet with game-sized assets and 32px toolbar samples."""
    cols, tw, th = 5, 210, 173
    w, h = cols*tw, math.ceil(len(arts)/cols)*th+38
    pixels = bytearray(bytes((53,62,59,255))*(w*h))
    def put(x,y,col):
        if 0<=x<w and 0<=y<h:
            i=(y*w+x)*4
            a=col[3]/255
            pixels[i:i+4]=bytes([round(col[k]*a+pixels[i+k]*(1-a)) for k in range(3)]+[255])
    def label(x,y,s):
        for ch in s.upper():
            bits=FONT.get(ch,'0'*35)
            for j,b in enumerate(bits):
                if b=='1': put(x+j%5,y+j//5,(207,216,204,255))
            x+=6
    label(12,13,'RIM PRISON - BUILDINGS 64 PX PER CELL - UI GLYPHS')
    for i,(name,c) in enumerate(arts):
        x,y=(i%cols)*tw,(i//cols)*th+34
        for yy in range(y+3,y+th-4):
            for xx in range(x+4,x+tw-4):
                tone = 69 if ((xx//12)+(yy//12))%2 else 74
                put(xx,yy,(tone,tone+8,tone+3,255))
        data=c.pixels()
        ox,oy=x+(tw-c.w)//2,y+12+(128-c.h)//2
        for yy in range(c.h):
            for xx in range(c.w):
                j=(yy*c.w+xx)*4
                put(ox+xx,oy+yy,data[j:j+4])
        label(x+9,y+146,name.split('/')[-1].removeprefix('RP_'))
        label(x+9,y+158,f'{c.w}X{c.h}')
    save_png(path,w,h,pixels)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--preview',type=Path,help='optional labeled PNG contact sheet')
    args=parser.parse_args()
    arts=list(textures())
    for name,c in arts:
        path=ROOT/(name+'.png')
        c.save(path)
        print(f'{path.relative_to(REPO)}  {c.w}x{c.h}  {path.stat().st_size} bytes')
    if args.preview:
        # Include actual 32px main buttons as a second row of samples at the end.
        samples=[('32PX_'+name,glyph(kind,32,True)) for name,kind in MAIN_BUTTONS.items()]
        preview(args.preview,arts+samples)
        print(f'Preview: {args.preview}')
    print(f'Generated {len(arts)} textures (stdlib only).')


if __name__=='__main__':
    main()
