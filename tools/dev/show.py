"""Decodes the captured requests and replies for one endpoint from pairs.json.

usage: python show.py pairs.json <endpoint part, e.g. card/sell> [--full]
"""
import json
import sys
import urllib.parse

from apicrypto import decode_param, decode_reply

rows = json.load(open(sys.argv[1]))
want, full = sys.argv[2], "--full" in sys.argv
COMMON = ("UserStatusData", "NotificationData", "CommonData", "UserLevelUpData", "NewCardDictionaryData")
last = req = None
for stream, uri, code, body in rows:
    if uri:
        last, req = uri, body
        continue
    if not last or want not in last:
        continue
    q = urllib.parse.parse_qs(req)
    try:
        p = decode_param(q["param"][0]) if "param" in q else req
    except Exception as ex:
        p = "(could not decode: %s)" % ex
    print("REQ ", last.split("?")[0], json.dumps(p, ensure_ascii=False))
    try:
        r = decode_reply(body)
    except Exception:
        print("RESP", code, "(not JSON)", body[:200].replace("\n", " "))
        continue
    d = r.get("data", {})
    if isinstance(d, dict) and not full:
        d = {k: (v if k not in COMMON else "..") for k, v in d.items()}
    print("RESP", code, r.get("result"), json.dumps(d, ensure_ascii=False)[:3000])
    print()
