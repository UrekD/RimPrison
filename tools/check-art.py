#!/usr/bin/env python3
"""Decode every mod PNG and check texture coverage/sizes against the XML defs.

Stdlib only. Usage: python3 tools/check-art.py
Checks CRCs, zlib stream, PNG filters, RGBA pixels, transparency and UI masks.
"""
from pathlib import Path
import struct
import xml.etree.ElementTree as ET
import zlib

REPO = Path(__file__).resolve().parents[1]
ROOT = REPO / 'Mods/RimPrison/Textures'


def decode(path):
    data = path.read_bytes()
    assert data[:8] == b'\x89PNG\r\n\x1a\n', f'{path}: PNG signature'
    pos, compressed, kinds = 8, bytearray(), []
    header = None
    while pos < len(data):
        length, = struct.unpack_from('>I',data,pos)
        kind = data[pos+4:pos+8]
        body = data[pos+8:pos+8+length]
        crc, = struct.unpack_from('>I',data,pos+8+length)
        assert len(body) == length and zlib.crc32(kind+body)&0xffffffff == crc, f'{path}: CRC'
        kinds.append(kind)
        if kind == b'IHDR':
            assert header is None and len(kinds) == 1
            header = struct.unpack('>IIBBBBB',body)
        elif kind == b'IDAT': compressed.extend(body)
        elif kind == b'IEND':
            assert length == 0 and pos+12 == len(data), f'{path}: trailing bytes'
            break
        pos += length+12
    assert header and kinds[-1] == b'IEND' and b'IDAT' in kinds, f'{path}: missing chunks'
    w,h,depth,color,compression,filter_method,interlace = header
    assert w>0 and h>0 and (depth,color,compression,filter_method,interlace)==(8,6,0,0,0), f'{path}: RGBA8 format'
    stream = zlib.decompressobj()
    raw = stream.decompress(compressed)+stream.flush()
    assert stream.eof and not stream.unused_data and not stream.unconsumed_tail, f'{path}: zlib stream'
    stride = w*4
    assert len(raw) == h*(stride+1), f'{path}: decoded byte count'
    pixels, prev = bytearray(), bytearray(stride)
    for y in range(h):
        at = y*(stride+1)
        mode, row = raw[at], bytearray(raw[at+1:at+1+stride])
        assert 0<=mode<=4, f'{path}: PNG filter'
        for x in range(stride):
            left, up, upper_left = row[x-4] if x>=4 else 0, prev[x], prev[x-4] if x>=4 else 0
            if mode == 1: predictor = left
            elif mode == 2: predictor = up
            elif mode == 3: predictor = (left+up)//2
            elif mode == 4:
                p = left+up-upper_left
                distances = [abs(p-v) for v in (left,up,upper_left)]
                predictor = (left,up,upper_left)[distances.index(min(distances))]
            else: predictor = 0
            row[x] = (row[x]+predictor)&255
        pixels.extend(row)
        prev = row
    alphas = pixels[3::4]
    assert min(alphas)==0 and max(alphas)==255, f'{path}: empty or nontransparent texture'
    assert any(0<a<255 for a in alphas), f'{path}: missing antialiasing'
    assert all(pixels[(y*w+x)*4+3]==0 for x,y in ((0,0),(w-1,0),(0,h-1),(w-1,h-1))), f'{path}: clipped corner'
    if path.parent.name in ('Zones','Icons'):
        assert all(pixels[i:i+3] == b'\xff\xff\xff' for i in range(0,len(pixels),4) if pixels[i+3]), f'{path}: glyph must be a white tint mask'
    return w,h


def expected_sizes():
    roots = [ET.parse(p).getroot() for p in sorted((REPO/'Mods/RimPrison/Defs').glob('*.xml'))]
    named = {e.get('Name'):e for root in roots for e in root if e.get('Name')}
    def inherited(e,field,default=None):
        value=e.findtext(field)
        if value is not None: return value
        parent=named.get(e.get('ParentName'))
        return inherited(parent,field,default) if parent is not None else default
    def cells(pair): return tuple(float(v.strip()) for v in pair.strip('()').split(','))
    result = {}
    for root in roots:
        for e in root:
            if e.tag == 'RimPrison.PrisonZoneDef':
                result['UI/Zones/'+e.findtext('defName')+'.png'] = (64,64)
            tex=e.findtext('graphicData/texPath','')
            if not tex.startswith(('Things/Building/','Things/Item/')): continue
            graphic=inherited(e,'graphicData/graphicClass','Graphic_Single')
            draw=cells(inherited(e,'graphicData/drawSize','(1,1)'))
            footprint=cells(inherited(e,'size','(1,1)'))
            if tex.startswith('Things/Building/'):
                assert draw == footprint, f'{tex}: drawSize {draw} disagrees with footprint {footprint}'
            size=tuple(round(v*64) for v in draw) if tex.startswith('Things/Building/') else (64,64)
            if graphic == 'Graphic_Multi':
                for direction in ('north','east','south','west'):
                    result[tex+'_'+direction+'.png'] = size[::-1] if direction in ('east','west') else size
            else:
                assert graphic=='Graphic_Single', f'{tex}: unsupported graphic class {graphic}'
                result[tex+'.png'] = size
    for name in ('Rooms','Deployment','Regime','Policy','Staff','Logistics','Intel','Office','Reports'):
        result[f'UI/MainButtons/RP_{name}.png'] = (64,64)
    for name in ('Padlock','Bed','Cross','Fist','Star','Door','Eye','Thermometer','Gang','Clock'):
        result[f'UI/Icons/{name}.png'] = (32,32)
    return result


def main():
    expected = expected_sizes()
    actual = {p.relative_to(ROOT).as_posix():decode(p) for p in sorted(ROOT.rglob('*.png'))}
    assert not expected.keys()-actual.keys(), f'Missing: {sorted(expected.keys()-actual.keys())}'
    for name,size in expected.items():
        assert actual[name]==size, f'{name}: expected {size}, found {actual[name]}'
    for name,size in actual.items():
        assert name in expected, f'{name}: no intended size/definition registered'
        print(f'{name}  {size[0]}x{size[1]}  {(ROOT/name).stat().st_size} bytes')
    print(f'PASS: {len(actual)} PNGs fully decoded; definitions, dimensions, transparency and white masks verified.')


if __name__ == '__main__':
    main()
