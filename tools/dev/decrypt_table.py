"""Decrypts a master table (M_*.json from cdn\\Files\\resource\\...\\parameter) with its key from table_keys.json.

usage: python decrypt_table.py <offline\\data\\static\\table_keys.json> M_card.json [out.json]
"""
import base64
import json
import os
import sys

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

keys = {e["name"]: e for e in json.load(open(sys.argv[1]))["list"]}
src = sys.argv[2]
name = os.path.splitext(os.path.basename(src))[0]
text = open(src, encoding="utf8").read().strip().lstrip("﻿")


def b64u(s):
    s = s.strip().replace("-", "+").replace("_", "/")
    return base64.b64decode(s + "=" * (-len(s) % 4))


if not text.startswith(("[", "{")):
    k = keys[name]
    dec = Cipher(algorithms.AES(b64u(k["key"])), modes.CBC(b64u(k["iv"]))).decryptor()
    raw = dec.update(b64u(text)) + dec.finalize()
    text = raw[:-raw[-1]].decode("utf8").lstrip("﻿")
rows = json.loads(text)
out = sys.argv[3] if len(sys.argv) > 3 else name + ".decrypted.json"
json.dump(rows, open(out, "w", encoding="utf8"), ensure_ascii=False, indent=1)
print(name, len(rows) if isinstance(rows, list) else "", "rows ->", out)
