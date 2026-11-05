import sys, re
sys.path.insert(0, '/home/tool/git/i-can-haz-reverse-engineering/src')
from haz.pe import Image
from haz.disasm import Code
img = Image('/home/tool/.local/share/Steam/steamapps/common/MultiVersus/MultiVersus/Binaries/Win64/MultiVersus-Win64-Shipping.exe'); code = Code(img)
def mem(va, n):
    for name, sva, vsz, ptr, rsz in img.sections:
        a = img.base + sva
        if a <= va < a + min(vsz, rsz): return img.buf[ptr + va - a: ptr + va - a + n]
    return b''
known = {l.strip() for l in open(sys.argv[1]) if l.strip()}
out = {}
for site, kind in img.branches_to(0x142a95e60):
    fn = img.function_at(site)
    if not fn or fn[1] - fn[0] > 0x80: continue          # static initializers are tiny
    for x in code.instructions(fn[0], fn[1]):
        if x.mnemonic == 'lea' and x.op_str.startswith('rdx, [rip'):
            t = x.address + x.size + x.operands[1].mem.disp
            m = re.match(rb'([\x20-\x7e]{1,120})\x00', mem(t, 128))
            if m:
                s = m.group(1).decode()
                out[s] = fn[0]
snake = sorted(s for s in out if re.fullmatch(r'[a-z][a-z0-9]*(_[a-z0-9]+)+', s))
print(f"# {len(out)} static FString initializers, {len(snake)} snake_case; known SSC among them: {len(known & set(out))}/{len(known)}", file=sys.stderr)
for s in snake: print(('K ' if s in known else '  ') + s)
