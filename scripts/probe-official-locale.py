#!/usr/bin/env python3
"""一次性取证脚本（v0.2.0 反馈 9 官方用词对账用）：
从游戏安装的 Locale.cok（普通 ZIP，成员 <lang>.loc，STORED 未压缩）里读官方词条，
按 key 片段与"值里出现的词"两种方式检索，结果写成 UTF-8 文本供人工/回归壳比对。

用法：
  python scripts/probe-official-locale.py                # 检索默认那批 key/词
  python scripts/probe-official-locale.py --keys Snap,ToolOptions --values 快速对齐, sidewalk
输出：stdout（建议重定向到 research/locale-probe.txt 再看，Windows 控制台打不出中日韩）
"""
import argparse, io, os, sys, zipfile

DEFAULT_COK = r"F:\SteamLibrary\steamapps\common\Cities Skylines II"

def varint(b, i):
    shift = out = 0
    while True:
        c = b[i]; i += 1
        out |= (c & 0x7F) << shift
        if not (c & 0x80):
            return out, i
        shift += 7

def parse_loc(data):
    """返回 {lockey: value}。头部：varint×2 + 3 个字符串 + varint 记录数，之后是 [len][key][len][value] 重复。"""
    i = 0
    _ver, i = varint(data, i)
    _fld, i = varint(data, i)
    for _ in range(3):
        ln, i = varint(data, i); i += ln
    cnt, i = varint(data, i)
    out = {}
    for _ in range(cnt):
        ln, i = varint(data, i); k = data[i:i + ln].decode('utf-8', 'replace'); i += ln
        ln, i = varint(data, i); v = data[i:i + ln].decode('utf-8', 'replace'); i += ln
        if k:
            out[k] = v
    return out

def coks(root):
    base = os.path.join(root, 'Cities2_Data', 'Content')
    for dirpath, _dirnames, filenames in os.walk(base):
        for f in filenames:
            if f == 'Locale.cok':
                yield os.path.join(dirpath, f)

def load(path, langs=None):
    z = zipfile.ZipFile(path)
    tables = {}
    for n in z.namelist():
        if n.endswith('.loc') and (langs is None or n in langs):
            tables[n[:-4]] = parse_loc(z.read(n))
    return tables

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=DEFAULT_COK)
    ap.add_argument('--keys', default='SNAPPING,ToolOptions,SUB_SERVICE_NAME,Assets.NAME[District Area,'
                                      'Assets.NAME[Extractor Lot,Assets.NAME[Surface Area,Assets.NAME[Map Tiles,'
                                      'Toolbar.ELEVATION,UNDERGROUND')
    ap.add_argument('--values', default='快速对齐,对齐,吸附, sidewalk, kerb, curb, Snapping, snapping')
    a = ap.parse_args()
    keys = [k.strip() for k in a.keys.split(',') if k.strip()]
    vals = [v.strip() for v in a.values.split(',') if v.strip()]
    out = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', line_buffering=True)
    for cok in sorted(coks(a.game)):
        rel = os.path.relpath(cok, a.game)
        tables = load(cok)
        print(f'===== {rel}  languages: {sorted(tables)}', file=out)
        en = tables.get('en-US', {})
        zh = tables.get('zh-HANS', {})
        ht = tables.get('zh-HANT', {})
        ja = tables.get('ja-JP', {})
        ru = tables.get('ru-RU', {})
        for pat in keys:
            hits = sorted(k for k in en if pat.lower() in k.lower())
            print(f'--- key~{pat}: {len(hits)}', file=out)
            for k in hits[:40]:
                print(f'  {k}\n      en={en[k]!r}\n      zh={zh.get(k)!r}  hant={ht.get(k)!r}  ja={ja.get(k)!r}  ru={ru.get(k)!r}',
                      file=out)
        for w in vals:
            hits = sorted(k for k in en if w in en[k])
            zhits = sorted(k for k in zh if w in zh[k])
            print(f'--- value~{w!r}: en {len(hits)} / zh-HANS {len(zhits)}', file=out)
            for k in (zhits or hits)[:40]:
                print(f'  {k}\n      en={en.get(k)!r}  zh={zh.get(k)!r}', file=out)
        print(file=out)

if __name__ == '__main__':
    main()
