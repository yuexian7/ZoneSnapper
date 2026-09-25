#!/usr/bin/env python3
"""定位词条表里「值等于 slug 本身」与「繁简同形」的行（改表时的快速回音，口径同 L3/L5 断言）。"""
import io, re, sys
try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

src = io.open("Engine/LocaleKit.cs", encoding="utf-8").read()
body = src[src.index("kTable ="):]
row_re = re.compile(r"new\[\]\s*\{(.*?)\}\s*,", re.S)
cell_re = re.compile(r'"((?:[^"\\]|\\.)*)"')
loc = ["en-US", "zh-HANS", "zh-HANT", "de-DE", "es-ES", "fr-FR",
       "it-IT", "ja-JP", "ko-KR", "pl-PL", "pt-BR", "ru-RU"]

hits = 0
HAN = lambda s: any(0x4E00 <= ord(ch) <= 0x9FFF for ch in s)
for m in row_re.finditer(body):
    cells = cell_re.findall(m.group(1))
    if not cells:
        continue
    slug = cells[0]
    if len(cells) != 13:
        print(f"列数不对 {slug}: {len(cells)}")
        hits += 1
        continue
    # 列 0 就是 slug 本身，跳过；只看文案列
    for i in range(1, len(cells)):
        if cells[i] == slug:
            print(f"值等于 slug: {slug} 列 {i} ({loc[i - 1]}) 值={cells[i]!r}")
            hits += 1
    # cells[2]=zh-HANS, cells[3]=zh-HANT
    if cells[2] == cells[3] and HAN(cells[2]):
        print(f"繁简同形: {slug} | {cells[2]}")
        hits += 1
print(f"共 {hits} 处")
