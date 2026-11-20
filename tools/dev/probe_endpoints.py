"""Finds which game API requests the offline server still answers with its generic reply.

Sends every endpoint name found in the game's string literals (see meta.py literals) with empty params and
checks the server log for "not yet supported".

usage: python probe_endpoints.py literals.txt <path to offline\\logs\\server.log> [http://127.0.0.1:45558]
"""
import re
import sys
import time
import urllib.parse
import urllib.request

from apicrypto import encrypt_param

lits, log = sys.argv[1], sys.argv[2]
base = sys.argv[3] if len(sys.argv) > 3 else "http://127.0.0.1:45558"
eps = sorted(set(re.findall(r"'([a-z]+(?:/[a-z0-9_]+)+)'", open(lits, encoding="utf8").read())))
eps = [e for e in eps if not e.startswith(("net/", "ident/"))]


def log_size():
    return len(open(log, encoding="utf8", errors="replace").read())


generic = []
for ep in eps:
    n0 = log_size()
    try:
        body = urllib.parse.urlencode({"request_id": "1", "param": encrypt_param({})}).encode()
        urllib.request.urlopen(urllib.request.Request(base + "/7r/" + ep, data=body), timeout=5).read()
    except Exception:
        pass
    time.sleep(0.05)
    if "not yet supported" in open(log, encoding="utf8", errors="replace").read()[n0:]:
        generic.append(ep)
print(len(generic), "endpoints get the generic reply:")
print("\n".join(generic))
print("\nNote: this sends real requests - run it against a copy of the save folder.")
