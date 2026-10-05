import sys, collections
sys.path.insert(0, '/home/tool/git/i-can-haz-reverse-engineering/src')
from haz.pe import Image
from haz.disasm import Code
img = Image('/home/tool/.local/share/Steam/steamapps/common/MultiVersus/MultiVersus/Binaries/Win64/MultiVersus-Win64-Shipping.exe'); code = Code(img)
targets = collections.Counter()
rows = []
for line in open(sys.argv[1]):
    f, _, pieces = line.rstrip('\n').partition('\t')
    fa = int(f, 16)
    if not (0x144f9f000 <= fa <= 0x14506a000): continue
    ins = code.function(fa)
    found = []
    for i, x in enumerate(ins):
        if x.mnemonic == 'call' and x.op_str.startswith('0x'):
            # third argument: last write to r8/r8d within 12 instructions
            r8 = None
            for j in range(i - 1, max(i - 12, -1), -1):
                y = ins[j]
                if y.op_str.startswith(('r8d,', 'r8,')) and y.mnemonic in ('mov', 'lea', 'xor'):
                    r8 = f"{y.mnemonic} {y.op_str}"; break
            found.append((x.op_str, r8))
            targets[x.op_str] += 1
    rows.append((f, pieces, found))
common = [t for t, n in targets.most_common(15)]
print("most-called:", targets.most_common(12))
for f, pieces, found in rows:
    disp = [(t, r) for t, r in found if t in ('0x144ffaa40',)]
    print(f, '|', pieces[:70], '|', disp)
