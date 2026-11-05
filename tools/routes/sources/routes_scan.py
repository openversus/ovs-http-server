# MVS_EXE: the game's MultiVersus-Win64-Shipping.exe. HAZ_SRC: i-can-haz-reverse-engineering/src when haz is not installed.
import os, pefile, re, sys, collections
pe = pefile.PE(os.environ['MVS_EXE'], fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase; img = pe.get_memory_mapped_image()
rd = [s for s in pe.sections if s.Name.rstrip(b'\0') in (b'.rdata', b'.data', b'.rodata')]
out = {}
path = re.compile(r'^/?[a-z0-9_\-{}%]+(/[a-zA-Z0-9_\-{}%:.]*)+$')
for s in rd:
    a0 = s.VirtualAddress; data = img[a0:a0+s.Misc_VirtualSize]
    for m in re.finditer(rb'[\x20-\x7e]{4,200}', data):
        t = m.group().decode()
        if path.match(t): out.setdefault(t, set()).add('a')
    for m in re.finditer(rb'(?:[\x20-\x7e]\x00){4,200}', data):
        t = m.group().decode('utf-16-le')
        if path.match(t): out.setdefault(t, set()).add('w')
for t in sorted(out): print(''.join(sorted(out[t])), t)
