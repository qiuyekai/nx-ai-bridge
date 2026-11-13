#!/usr/bin/env python3
"""Generate the self-check loader journals from the receiver loader journals.

The two journals differ only in their name and default mode, so the self-check
ones are generated to keep them from drifting apart.  Run after editing
nx_receiver/NxAiBridgeReceiver.cs or nx_receiver/vb/NxAiBridgeReceiver.vb:

    python scripts/sync_loaders.py          # rewrite
    python scripts/sync_loaders.py --check  # exit 1 when out of date (used by the tests)
"""
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / "nx_receiver"
PAIRS = [
    (ROOT / "NxAiBridgeReceiver.cs", ROOT / "NxAiBridgeSelfCheck.cs", "//"),
    (ROOT / "vb" / "NxAiBridgeReceiver.vb", ROOT / "vb" / "NxAiBridgeSelfCheck.vb", "'"),
]
BANNER = [
    "{c} NX AI Bridge - SELF-CHECK journal (generated from {src} by scripts/sync_loaders.py - do not edit).",
    "{c}",
    "{c} Play this file once inside NX: Alt+F8 (Tools > Journal > Play...), select it, click Run.",
    "{c} It tests every modelling tool in two temporary parts, takes about a minute, and writes",
    "{c}   %USERPROFILE%\\.nx-ai-bridge\\reports\\nx-ai-bridge-selfcheck-<time>.json   (also copied to the Desktop).",
    "{c} Close your own work first: the journal creates and closes parts.",
    "{c}",
]


def generate(src: Path, comment: str) -> str:
    text = src.read_text(encoding="ascii")
    out = text.replace("class NxAiBridgeReceiver", "class NxAiBridgeSelfCheck")
    out = out.replace("Module NxAiBridgeReceiver", "Module NxAiBridgeSelfCheck")
    out = out.replace('DefaultMode = "--live"', 'DefaultMode = "--selfcheck"')
    out = out.replace('DefaultMode As String = "--live"', 'DefaultMode As String = "--selfcheck"')
    assert out != text and '"--selfcheck"' in out
    banner = "\n".join(line.format(c=comment, src=src.name) for line in BANNER)
    return banner + "\n" + out


def main() -> int:
    check = "--check" in sys.argv
    stale = []
    for src, dst, comment in PAIRS:
        wanted = generate(src, comment)
        if check:
            if not dst.is_file() or dst.read_text(encoding="ascii") != wanted:
                stale.append(dst.name)
        else:
            dst.write_text(wanted, encoding="ascii", newline="\n")
            print("wrote", dst)
    if stale:
        print("out of date:", ", ".join(stale), "- run: python scripts/sync_loaders.py")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
