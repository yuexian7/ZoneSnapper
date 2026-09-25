#!/usr/bin/env python3
"""校验 Engine/LocaleKit.cs 的词条表：每行必须正好 1 + len(kLocales) 列，slug 不许重复。
   门禁里也有同样口径的 C# 断言（tests/t3/Tests.Locale.cs），这个脚本只是改表时的快速回音。"""
import io, re, sys
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

path = "Engine/LocaleKit.cs"
src = io.open(path, encoding="utf-8").read()

m = re.search(r"kLocales\s*=\s*\{(.*?)\};", src, re.S)
locales = re.findall(r'"([^"]+)"', m.group(1))
ncol = len(locales) + 1

body = src[src.index("kTable ="):]
rows = re.findall(r"new\[\]\s*\{(.*?)\}\s*,(?=\s*(?:new\[\]|};))", body, re.S)
bad = []
seen = {}
for r in rows:
    cells = re.findall(r'"((?:[^"\\]|\\.)*)"', r)
    slug = cells[0] if cells else "?"
    if len(cells) != ncol:
        bad.append((slug, len(cells), [locales[i] for i in range(min(len(cells), len(locales)), len(locales))] if False else ""))
    if slug in seen:
        bad.append((slug, 0, "DUP"))
    seen[slug] = True
    empty = [locales[i] for i, c in enumerate(cells[1:ncol]) if not c.strip()]
    if empty:
        bad.append((slug, len(cells), "EMPTY:" + ",".join(empty)))

print(f"locales={len(locales)} 需要列数={ncol} 行数={len(rows)}")
for slug, n, extra in bad:
    print(f"  ✗ {slug}: 列数={n} {extra}")
if not bad:
    print("  ✓ 每行列数正确、无重复、无空单元格")
    missing = [s for s in ("SnapDistrict.desc",) if s not in seen]
    print("  现有 slug 数:", len(seen))
sys.exit(1 if bad else 0)
