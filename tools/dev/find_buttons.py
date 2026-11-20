"""Finds the website buttons in 7rhythm_Data\\resources.assets and prints the file offset of each button's
m_Interactable byte (what offline\\patch_buttons.ps1 sets to 0). Needs UnityPy (pip install UnityPy).

usage: python find_buttons.py <path to 7rhythm_Data\\resources.assets>

The buttons are the game's EventButtonScale components (a UnityEngine.UI.Button subclass). Their persistent
onClick target names the handler (OnClickOfficial, OnClickInfo, OnClickOfficialPage, OnClickSupport,
OnClickGuide). The game has no type trees, so the Selectable fields are found by layout: the animation
trigger strings end with "Disabled", and m_Interactable is the next aligned int.
"""
import re
import sys

import UnityPy

HANDLERS = {
    b"OnClickOfficial": "title screen menu: official page",
    b"OnClickInfo": "title screen menu: contact",
    b"OnClickOfficialPage": "in-game menu: official page",
    b"OnClickSupport": "in-game menu: contact",
    b"OnClickGuide": "window title bar: official page",
}

env = UnityPy.load(sys.argv[1])
assets = list(env.files.values())[0]
for pid, obj in assets.objects.items():
    if obj.type.name != "MonoBehaviour":
        continue
    raw = obj.get_raw_data()
    m = re.search(rb"OnClick\w+", raw)
    if not m or m.group(0) not in HANDLERS:
        continue
    i = raw.find(b"\x08\x00\x00\x00Disabled")
    if i < 0:
        continue
    off = i + 12
    go = assets.objects[int.from_bytes(raw[4:12], "little", signed=True)].read().m_Name
    print("%-30s %-20s offset %d  m_Interactable=%d" % (HANDLERS[m.group(0)], go, obj.byte_start + off, raw[off]))
