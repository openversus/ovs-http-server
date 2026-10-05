#!/usr/bin/env python3
"""Counts Hydra type codes in a corpus made by extract_corpus.py (req/ and resp/), and checks every body parses: which
codes the game sends and accepts in real traffic. Usage: tools/hydra/census.py local/hydra-corpus"""
import struct, zlib, sys, pathlib, collections
NAMES = {0:"ZERO",1:"NULL",2:"TRUE",3:"FALSE",6:"WEBSOCKET",0x10:"INT8",0x11:"UINT8",0x12:"INT16",0x13:"UINT16",0x14:"INT32",0x15:"UINT32",0x16:"INT64",0x17:"UINT64",0x20:"FLOAT",0x21:"DOUBLE",0x30:"CHAR8",0x31:"CHAR16",0x32:"CHAR32",0x33:"BYTES8",0x34:"BYTES16",0x35:"BYTES32",0x36:"BIGINT",0x40:"DATE",0x50:"ARRAY8",0x51:"ARRAY16",0x52:"ARRAY32",0x53:"ARRAY64",0x60:"MAP8",0x61:"MAP16",0x62:"MAP32",0x63:"MAP64",0x67:"COMPRESSED",0x68:"LOCALIZATION",0x69:"CALENDAR",0x70:"FILEREF",0x71:"STOREENABLED"}
FIX = {0x10:1,0x11:1,0x12:2,0x13:2,0x14:4,0x15:4,0x16:8,0x17:8,0x20:4,0x21:8,0x36:8,0x40:4}
class P:
    def __init__(s, b, counts): s.b, s.i, s.c = b, 0, counts
    def u(s, n):
        v = int.from_bytes(s.b[s.i:s.i+n], "big"); s.i += n; return v
    def val(s):
        code = s.b[s.i]; s.i += 1
        s.c[NAMES.get(code, f"UNKNOWN_0x{code:02x}")] += 1
        if code in (0,1,2,3): return
        if code == 6: s.u(2); return s.val()
        if code in FIX:
            if code == 0x21:
                d = struct.unpack(">d", s.b[s.i:s.i+8])[0]
                if d != d: s.c["DOUBLE_NaN"] += 1
                elif d == int(d): s.c["DOUBLE_integral"] += 1
            if code == 0x16:
                v = struct.unpack(">q", s.b[s.i:s.i+8])[0]
                s.c["INT64_negative" if v < 0 else "INT64_nonneg"] += 1
            s.i += FIX[code]; return
        if code in (0x30,0x31,0x32,0x33,0x34,0x35):
            n = s.u({0x30:1,0x31:2,0x32:4,0x33:1,0x34:2,0x35:4}[code]); s.i += n; return
        if code in (0x50,0x51,0x52,0x53):
            for _ in range(s.u({0x50:1,0x51:2,0x52:4,0x53:8}[code])): s.val()
            return
        if code in (0x60,0x61,0x62,0x63):
            for _ in range(s.u({0x60:1,0x61:2,0x62:4,0x63:8}[code])): s.val(); s.val()
            return
        if code == 0x67:
            idx = s.b[s.i]; s.i += 1; s.c[f"COMPRESSED_index_{idx}"] += 1
            bc = s.b[s.i]; s.i += 1; n = s.u({0x33:1,0x34:2,0x35:4}[bc])
            inner = zlib.decompress(s.b[s.i:s.i+n]); s.i += n
            q = P(inner, s.c); q.val()
            if q.i != len(inner): s.c["COMPRESSED_trailing"] += 1
            return
        if code == 0x68: s.val(); s.val(); s.val(); s.val(); return
        if code == 0x69: s.val(); s.val(); return
        if code == 0x70: s.val(); s.val(); s.val(); return
        if code == 0x71: s.val(); s.val(); s.val(); return
        raise ValueError(f"unknown code 0x{code:02x} at {s.i-1}")
for d in ("req", "resp"):
    counts = collections.Counter(); bad = []; trailing = 0
    files = sorted(pathlib.Path(sys.argv[1], d).glob("*.bin"))
    for f in files:
        b = f.read_bytes()
        try:
            p = P(b, counts); p.val()
            if p.i != len(b): trailing += 1; bad.append((f.name, f"trailing {len(b)-p.i} of {len(b)}"))
        except Exception as e:
            bad.append((f.name, str(e)[:80]))
    print(f"== {d}: {len(files)} bodies, {len(bad)} problems ({trailing} with trailing bytes)")
    for k, v in sorted(counts.items(), key=lambda x: -x[1]): print(f"   {k:22} {v}")
    for n, e in bad[:6]: print("   PROBLEM", n, e)
