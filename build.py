#!/usr/bin/env python3
"""TikArena · DarkRing Studio — build script.

Steps:
  1. src/TikArena.3.cs  ->  dist/TikArena.3.cs
     every `Hash.NAME` is replaced with `(Hash)0x...UL` from src/native-hashes.txt,
     so the script does not depend on the Hash enum of a specific SHVDN version.
     The output is saved as UTF-8 with BOM.
  2. dist/TikArena.dll  (only when SHVDN_REF points to ScriptHookVDotNet3.dll)
     compiled with `mcs -langversion:5` (same language level SHVDN uses for .cs scripts).
     Without SHVDN_REF the existing dist/TikArena.dll is kept.
  3. src/generator.html -> index.html
     "%%ENGINE%%" is replaced with the engine source (JSON string) and
     "%%ENGINE_DLL%%" with the base64 of dist/TikArena.dll.

Usage:
  SHVDN_REF=/path/ScriptHookVDotNet3.dll python3 build.py
  (ScriptHookVDotNet3.dll: NuGet ScriptHookVDotNet3 3.6.0, or the v3.6.0 GitHub release zip)
"""
import base64
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(ROOT, "src")
DIST = os.path.join(ROOT, "dist")


def read(path):
    with open(path, "r", encoding="utf-8-sig") as f:
        return f.read()


def load_hashes():
    table = {}
    for line in read(os.path.join(SRC, "native-hashes.txt")).splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        name, value = line.split("=", 1)
        table[name.strip()] = value.strip()
    return table


def build_engine():
    src = read(os.path.join(SRC, "TikArena.3.cs"))
    table = load_hashes()
    missing = set()

    def repl(m):
        name = m.group(1)
        if name not in table:
            missing.add(name)
            return m.group(0)
        return "(Hash)" + table[name] + "UL"

    out = re.sub(r"(?<![\w.])Hash\.([A-Z0-9_]+)\b", repl, src)
    if missing:
        sys.exit("native-hashes.txt is missing: " + ", ".join(sorted(missing)))
    os.makedirs(DIST, exist_ok=True)
    with open(os.path.join(DIST, "TikArena.3.cs"), "w", encoding="utf-8-sig", newline="\n") as f:
        f.write(out)
    used = len(set(re.findall(r"(?<![\w.])Hash\.([A-Z0-9_]+)\b", src)))
    print("engine: %d natives replaced -> dist/TikArena.3.cs" % used)
    return out


def compile_dll():
    ref = os.environ.get("SHVDN_REF")
    dll = os.path.join(DIST, "TikArena.dll")
    if not ref:
        print("dll: SHVDN_REF not set, keeping existing dist/TikArena.dll" if os.path.exists(dll)
              else "dll: SHVDN_REF not set and no dist/TikArena.dll (index.html will not embed a DLL)")
        return
    mcs = shutil.which("mcs")
    if not mcs:
        sys.exit("mcs not found (install mono-mcs)")
    cmd = [mcs, "-langversion:5", "-target:library", "-optimize+", "-nologo",
           "-r:" + ref, "-r:System.dll", "-r:System.Core.dll", "-r:System.Drawing.dll",
           "-r:System.Windows.Forms.dll", "-out:" + dll, os.path.join(DIST, "TikArena.3.cs")]
    r = subprocess.run(cmd, capture_output=True, text=True)
    output = (r.stdout + r.stderr).strip()
    if output:
        print(output)
    if r.returncode != 0:
        sys.exit("dll: compilation failed")
    # the original source must compile too (Hash.NAME form)
    with tempfile.TemporaryDirectory() as tmp:
        check = [mcs, "-langversion:5", "-target:library", "-nologo", "-r:" + ref, "-r:System.Drawing.dll",
                 "-r:System.Windows.Forms.dll", "-out:" + os.path.join(tmp, "check.dll"),
                 os.path.join(SRC, "TikArena.3.cs")]
        r = subprocess.run(check, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout + r.stderr)
        sys.exit("src/TikArena.3.cs does not compile")
    print("dll: dist/TikArena.dll (%d bytes)" % os.path.getsize(dll))


def build_page(engine):
    page = read(os.path.join(SRC, "generator.html"))
    if '"%%ENGINE%%"' not in page or '"%%ENGINE_DLL%%"' not in page:
        sys.exit('src/generator.html must contain "%%ENGINE%%" and "%%ENGINE_DLL%%"')
    eng = json.dumps(engine, ensure_ascii=False).replace("</", "<\\/")
    dll_path = os.path.join(DIST, "TikArena.dll")
    dll = ""
    if os.path.exists(dll_path):
        with open(dll_path, "rb") as f:
            dll = base64.b64encode(f.read()).decode("ascii")
    page = page.replace('"%%ENGINE%%"', eng).replace('"%%ENGINE_DLL%%"', '"' + dll + '"')
    with open(os.path.join(ROOT, "index.html"), "w", encoding="utf-8", newline="\n") as f:
        f.write(page)
    print("page: index.html (%d KB)" % (len(page.encode("utf-8")) // 1024))


if __name__ == "__main__":
    engine = build_engine()
    compile_dll()
    build_page(engine)
