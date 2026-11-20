"""Replays every captured request in pairs.json against the running offline server and compares the reply
layout (top-level and data keys, and whether the reply is encrypted) with the captured reply.

usage: python replay.py pairs.json [http://127.0.0.1:45558]
"""
import json
import sys
import urllib.request

from apicrypto import decode_reply

rows = json.load(open(sys.argv[1]))
base = sys.argv[2] if len(sys.argv) > 2 else "http://127.0.0.1:45558"
last = None
bad = 0
for stream, uri, code, body in rows:
    if uri:
        last = (uri, body)
        continue
    if code != "200" or not last:
        continue
    uri, req = last
    try:
        orig = decode_reply(body)
    except Exception:
        print("skip", uri.split("?")[0], "(captured reply is not JSON)")
        continue
    r = urllib.request.urlopen(urllib.request.Request(base + uri, data=req.encode("latin1"),
                               headers={"Content-Type": "application/x-www-form-urlencoded", "Host": "pcgame.7rhythm.jp"}))
    text = r.read().decode()
    mine = decode_reply(text)
    same_keys = set(orig) == set(mine) and set(orig.get("data") or {}) == set(mine.get("data") or {})
    same_enc = body.strip().startswith("{") == text.strip().startswith("{")
    if same_keys and same_enc:
        print("ok  ", uri.split("?")[0])
    else:
        bad += 1
        print("DIFF", uri.split("?")[0], set(orig.get("data") or {}) ^ set(mine.get("data") or {}), "encryption matches" if same_enc else "encryption differs")
print("bad", bad)
