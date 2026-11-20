"""Reads Unity IL2CPP global-metadata.dat (metadata version 24.1, as used by 7RHYTHM).

usage:
  python meta.py literals <global-metadata.dat> > literals.txt   all string literals, one per line (quoted)
  python meta.py fields <global-metadata.dat> TypeName [...]       field names of the named classes
                                                                   (reply layouts, master table columns)
"""
import struct
import sys

mode, path = sys.argv[1], sys.argv[2]
d = open(path, "rb").read()
h = struct.unpack_from("<64I", d)
if h[0] != 0xFAB11BAF:
    sys.exit("not an IL2CPP metadata file")

if mode == "literals":
    lit_off, lit_size, data_off = h[2], h[3], h[4]
    for i in range(lit_size // 8):
        length, idx = struct.unpack_from("<II", d, lit_off + i * 8)
        print(repr(d[data_off + idx:data_off + idx + length].decode("utf8", "replace")))
    sys.exit()

str_off = h[6]
field_off = h[24]
type_off, type_size = h[40], h[41]
TYPE_DEF_SIZE = 100   # Il2CppTypeDefinition size for v24.1


def name(i):
    e = d.index(b"\0", str_off + i)
    return d[str_off + i:e].decode("utf8", "replace")


want = set(sys.argv[3:])
for i in range(type_size // TYPE_DEF_SIZE):
    v = struct.unpack_from("<19i8H2I", d, type_off + i * TYPE_DEF_SIZE)
    tname, field_start, field_count = name(v[0]), v[11], v[21]
    if tname in want:
        fields = [name(struct.unpack_from("<i", d, field_off + (field_start + j) * 12)[0]) for j in range(field_count)]
        print(tname, fields)
