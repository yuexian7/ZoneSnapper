#!/usr/bin/env python3
"""把游戏官方词条从 Locale.cok 抽成一张 markdown 对照表（L6 段期望串的证据文件）。

输出 research/locale-official-terms.md：每个官方 key × 12 种语言的原文。
这是"证据即断言"的那半：L6 里每个期望串都必须能在这张表里找到；表里没有的词
（例如 curb/kerb、Snapping 这个标题）就是游戏里根本没有的，只能我们自己说，
并且要在笔记里标成"自创"，不能声称"照抄官方"。

用法：python scripts/extract-official-terms.py > research/locale-official-terms.md
"""
import os, sys, zipfile

GAME = os.environ.get('CSII_INSTALLATIONPATH',
                      r"F:\SteamLibrary\steamapps\common\Cities Skylines II")
COK = os.path.join(GAME, 'Cities2_Data', 'Content', 'Game', 'Locale.cok')

# 官方 key → 我们注册表里对应的行（也是我们抄词的用途）
WANTED = [
    ('Assets.NAME[District Area]', 'SnapDistrict / 工具悬停标题'),
    ('Assets.NAME[Extractor Lot]', 'SnapLot / 工具悬停标题'),
    ('Assets.NAME[Surface Area]', 'SnapSurface / 工具悬停标题'),
    ('Assets.NAME[Map Tiles]', 'SnapMapTile / 工具悬停标题'),
    ('Assets.DESCRIPTION[District Area]', '市辖区工具说明'),
    ('Assets.DESCRIPTION[Extractor Lot]', '产业区工具说明'),
    ('Assets.DESCRIPTION[Surface Area]', '表面区域工具说明'),
    ('Assets.SUB_SERVICE_DESCRIPTION[Districts]', '服务区页签说明'),
    ('Assets.SUB_SERVICE_DESCRIPTION[ZonesExtractors]', '服务区页签说明'),
    ('Assets.SUB_SERVICE_DESCRIPTION[Surfaces]', '服务区页签说明'),
    ('Common.ACTION[Tool Options]', '「工具选项」这个标题长什么样'),
    ('Toolbar.BRUSH_SIZE', 'Toolbar.* 命名空间存在性的对照样本'),
]
# 检索这些片段：用来回答"游戏里到底有没有某个说法"（命中 0 次就是没有）
PROBES = ['SNAPPING', 'SNAP', 'ToolOptions', 'CENTERLINE', 'SIDEWALK', 'KERB', 'CURB']
LANGS = ['en-US', 'de-DE', 'fr-FR', 'es-ES', 'it-IT', 'pl-PL', 'pt-BR', 'ru-RU',
         'ja-JP', 'ko-KR', 'zh-HANS', 'zh-HANT']


def varint(b, i):
    shift = out = 0
    while True:
        c = b[i]; i += 1
        out |= (c & 0x7F) << shift
        if not (c & 0x80):
            return out, i
        shift += 7


def parse(data):
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


def main():
    z = zipfile.ZipFile(COK)
    tables = {lg: parse(z.read(lg + '.loc')) for lg in LANGS}
    sys.stdout.reconfigure(encoding='utf-8')
    out = sys.stdout
    print(f'> 证据文件：`{COK}`（普通 ZIP，成员 `<lang>.loc`，STORED 未压缩）', file=out)
    print(f'> 由 `scripts/extract-official-terms.py` 生成，勿手改。每语言 {len(tables["en-US"])} 条 key。', file=out)
    print('', file=out)
    print('| 官方 key | ' + ' | '.join(LANGS) + ' |', file=out)
    print('|---|' + '---|' * len(LANGS), file=out)
    for key, why in WANTED:
        cells = []
        for lg in LANGS:
            v = tables[lg].get(key)
            cells.append((v or '—').replace('|', '\|').replace('\n', ' '))
        print(f'| `{key}`<br>_{why}_ | ' + ' | '.join(cells) + ' |', file=out)
    print('', file=out)
    print('## 「游戏里到底有没有这个词」检索结果（key 命中数 / 值命中数，按 en-US）', file=out)
    en = tables['en-US']
    zh = tables['zh-HANS']
    for p in PROBES:
        kh = [k for k in en if p.lower() in k.lower()]
        vh = [k for k in en if p.lower() in en[k].lower()]
        zhv = [k for k in zh if p in zh[k]] if p.isascii() is False else []
        print(f'- `{p}`：key 命中 **{len(kh)}**，en 值命中 **{len(vh)}**，zh-HANS 值命中 **{len(zhv)}**', file=out)
        for k in kh[:6]:
            print(f'  - key `{k}` = {en[k]!r} / zh {zh.get(k)!r}', file=out)
        for k in vh[:6]:
            print(f'  - 值 in `{k}` = {en[k]!r}', file=out)
    print('', file=out)
    print('## 抽样： curb / kerb / sidewalk 在 12 种语言的值里各出现多少次', file=out)
    for w in ['curb', 'kerb', 'sidewalk', 'Curb', 'Kerb']:
        tot = sum(sum(1 for k in tables[lg] if w in tables[lg][k]) for lg in LANGS)
        print(f'- `{w}`：全 12 语言合计 **{tot}** 次', file=out)


if __name__ == '__main__':
    main()
