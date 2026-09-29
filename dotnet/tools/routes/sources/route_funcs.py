# For every function referencing a route-like string, list the strings it references in instruction order.
import re, sys
sys.path.insert(0, '/home/tool/git/i-can-haz-reverse-engineering/src')
from haz.pe import Image
from haz.disasm import Code
EXE = '/home/tool/.local/share/Steam/steamapps/common/MultiVersus/MultiVersus/Binaries/Win64/MultiVersus-Win64-Shipping.exe'
img = Image(EXE); code = Code(img)
secs = {s[0]: s for s in img.sections}
def mem(va, n):
    for name, sva, vsz, ptr, rsz in img.sections:
        a = img.base + sva
        if a <= va < a + min(vsz, rsz):
            off = ptr + (va - a); return img.buf[off:off + n]
    return b''
def string_at(va):
    raw = mem(va, 400)
    if not raw: return None
    m = re.match(rb'((?:[\x20-\x7e]\x00){1,190})\x00\x00', raw)
    if m and len(m.group(1)) >= 2: return ('w', m.group(1).decode('utf-16-le'))
    m = re.match(rb'([\x20-\x7e]{1,300})\x00', raw)
    if m: return ('a', m.group(1).decode())
    return None
routeish = re.compile(r'^/?[a-z][a-z0-9_\-]*(/[a-zA-Z0-9_\-{}%.]*)*/?$')
skip = re.compile(r'(?i)\.cpp|/game/|/script/|/engine/|unreal/|application/|image/|text/|audio/|multipart/|calendar/gregorian|fields/day')
refs = img.rip_refs()
route_strings = {}
for t in refs:
    s = string_at(t)
    if s and '/' in s[1] and routeish.match(s[1]) and not skip.search(s[1]) and len(s[1]) >= 4:
        route_strings[t] = s
funcs = {}
for t in route_strings:
    for ins in refs[t]:
        f = img.function_at(ins)
        if f: funcs.setdefault(f[0], set()).add(t)
print(f"# {len(route_strings)} route strings, {len(funcs)} functions", file=sys.stderr)
for f in sorted(funcs):
    seq = []
    for ins in code.function(f):
        if 'rip +' in ins.op_str or 'rip -' in ins.op_str:
            for op in ins.operands:
                if op.type == 3 and op.mem.base == 41:  # X86_OP_MEM, RIP
                    tgt = ins.address + ins.size + op.mem.disp
                    s = string_at(tgt)
                    if s: seq.append(f"{s[0]}:{s[1]!r}")
    print(f"{f:#x}\t" + ' '.join(seq))
