import sys, re, collections
sys.path.insert(0, '/home/tool/git/i-can-haz-reverse-engineering/src')
from haz.pe import Image
from haz.disasm import Code
img = Image('/home/tool/.local/share/Steam/steamapps/common/MultiVersus/MultiVersus/Binaries/Win64/MultiVersus-Win64-Shipping.exe'); code = Code(img)
def mem(va, n):
    for name, sva, vsz, ptr, rsz in img.sections:
        a = img.base + sva
        if a <= va < a + min(vsz, rsz): return img.buf[ptr + va - a: ptr + va - a + n]
    return b''
def s_at(va):
    raw = mem(va, 80)
    m = re.match(rb'([a-z][a-z0-9_]{2,70})\x00', raw)
    if m: return m.group(1).decode()
    m = re.match(rb'((?:[a-z0-9_]\x00){3,70})\x00\x00', raw)
    if m: return m.group(1).decode('utf-16-le')
    m = re.match(rb'([a-z0-9_]{8})', raw)   # an 8-byte chunk loaded by movsd/mov
    if m: return '~' + m.group(1).decode()
    return None
def callers(t):
    out = {fn[0] for fn in img.callers(t)}
    for site, kind in img.branches_to(t):
        fn = img.function_at(site)
        if fn: out.add(fn[0])
    return out
level = {0x145068aa0: 0, 0x145068bf0: 0, 0x145068d50: 0}
frontier = list(level)
for depth in (1, 2, 3, 4):
    nxt = []
    for f in frontier:
        for c in callers(f):
            if c not in level: level[c] = depth; nxt.append(c)
    frontier = nxt
names = collections.defaultdict(set)
for f, d in level.items():
    if d == 0: continue
    for x in code.function(f):
        for op in x.operands:
            if op.type == 3 and op.mem.base == 41:
                s = s_at(x.address + x.size + op.mem.disp)
                if s and '_' in s: names[s].add((d, f))
print(f"# {len(level)} functions within 4 levels", file=sys.stderr)
for s in sorted(names): print(s, '\t', sorted({f'{f:#x}@{d}' for d, f in names[s]})[:3])
