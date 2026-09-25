#!/usr/bin/env node
/*
 * Zone Snapper 离线门禁（T1 / T2 / T2b / T3 + 版本一致性）。
 * 形状照抄 BridgeTheLanguageGap/scripts/verify.mjs，常量改成本模组；判据来自
 * D:/WorkSpace/CS2MOD/都市天际线2模组开发标准流程.md 步骤 4：
 *
 *  · 版本一致性：ZoneSnapperMod.cs 的 kVersion 必须 == PublishConfiguration.xml 的 <ModVersion>
 *    —— 版本号散在多处以人记必失。
 *  · T2 判「部署产物是不是本次编的」用「新版字面量在 + 上一版字面量不在」+ mtime，
 *    **不用文件哈希**（MVID 每次构建都变，哈希永远不同）。
 *  · DLL 里查字符串分两个堆：字面量在 #US（UTF-16LE，且不一定落在偶数偏移 ⇒ 双 parity 扫），
 *    类型名/方法名在 #Strings（UTF-8 ⇒ latin1 扫）。普通 grep -a 两头都会漏。
 *  · T2b 用 sha256 比 0Harmony.dll 的身份：这是同一根文件比字节，不违反上一条。
 *
 * 用法：
 *   node scripts/verify.mjs                # 完整：build → 扫部署 DLL → 跑回归壳
 *   node scripts/verify.mjs --skip-build   # 只复核「磁盘这份是不是新版 + 回归绿不绿」
 *   node scripts/verify.mjs --t3-only      # 只跑回归壳
 */
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execSync } from 'node:child_process';
import os from 'node:os';

// 本脚本在 scripts/ 下，工程根是它的上一级。
const HERE = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const ROOT = path.resolve(HERE, '..');
const MOD_NAME = 'ZoneSnapper';

const args = process.argv.slice(2);
const SKIP_BUILD = args.includes('--skip-build');
const T3_ONLY = args.includes('--t3-only');

let fails = 0;
const ok = (m) => console.log('  \x1b[32m✓\x1b[0m ' + m);
const bad = (m) => { fails++; console.log('  \x1b[31m✗\x1b[0m ' + m); };
const note = (m) => console.log('    ' + m);

// —— 部署目录：CSII_LOCALMODSPATH 是「用户级注册表」环境变量，process.env 里取不到 ⇒ 从注册表读。
function userEnv(name) {
  try {
    const out = execSync(
      `reg query "HKCU\\Environment" /v ${name}`,
      { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
    const line = out.split('\n').find(l => l.trim().startsWith(name));
    if (!line) return null;
    return line.split(/\s{2,}/).pop().trim();
  } catch (e) {
    return null;
  }
}

const LOCAL_MODS = userEnv('CSII_LOCALMODSPATH')
  || path.join(process.env.LOCALAPPDATA || '', 'LocalLow/Colossal Order/Cities Skylines II/Mods');
const DEPLOY_DIR = path.join(LOCAL_MODS, MOD_NAME);
const DEPLOY_DLL = path.join(DEPLOY_DIR, MOD_NAME + '.dll');

// —— 版本号两处
const modCs = fs.readFileSync(path.join(ROOT, MOD_NAME + 'Mod.cs'), 'utf8');
const verMatch = modCs.match(/kVersion\s*=\s*"([^"]+)"/);
const VERSION = verMatch ? verMatch[1] : null;

function xmlVersion() {
  const p = path.join(ROOT, 'Properties', 'PublishConfiguration.xml');
  if (!fs.existsSync(p)) return { xml: null, reason: 'PublishConfiguration.xml 不存在（本轮只本地部署时也允许，但发布前必须补）' };
  const t = fs.readFileSync(p, 'utf8');
  const m = t.match(/<ModVersion\s+Value="([^"]*)"/);
  return { xml: m ? m[1] : null };
}

console.log(`\n=== ${MOD_NAME} 离线门禁 ===`);

// —— 0. 版本一致性
console.log('\n[0] 版本一致性');
if (!VERSION) bad('ZoneSnapperMod.cs 里找不到 kVersion 常量');
else {
  const v = xmlVersion();
  if (v.xml === null) note(`xml 侧无值（${v.reason || '未写 <ModVersion>'}）；横幅版本 ${VERSION}`);
  else if (v.xml !== VERSION) bad(`横幅 ${VERSION} != xml <ModVersion> ${v.xml}`);
  else ok(`横幅与 xml 一致：v${VERSION}`);
}

// —— T2d 发布元数据（2026-09-25 首发前加的闸）
//
// 起因：这份 xml 在 v0.3.0 之前写的是 Metadata / Name / Author / Files / PreviewImage 那套字段名，
// 而官方模板（AccessAnarchy/template/content/Properties/PublishConfiguration.xml）与两个已上线模组用的是
// 扁平的 DisplayName / Thumbnail / …。ModPublisher 是**按名字取值**的：名字不对不报错，
// 只会把商店页的名字、简介、封面全传成空 —— 而首发之后想改文案得再占一个版本号（Playbook v1.1 增补 #2）。
// 所以「形状对不对」不能等到推上去才发现。
console.log('\n[T2d] 发布元数据（xml 字段形状 + 素材）');
{
  const p = path.join(ROOT, 'Properties', 'PublishConfiguration.xml');
  if (!fs.existsSync(p)) bad('Properties/PublishConfiguration.xml 不存在');
  else {
    // 注释里出现字段名是允许的（判据不该被注释骗到），所以先把注释整段剥掉再扫。
    const t = fs.readFileSync(p, 'utf8').replace(/<!--[\s\S]*?-->/g, ' ');

    const REQUIRED = ['ModId', 'DisplayName', 'ShortDescription', 'LongDescription', 'Thumbnail',
      'Tag', 'ModVersion', 'GameVersion', 'ChangeLog', 'AccessLevel'];
    const missing = REQUIRED.filter(f => !new RegExp('<' + f + '[ \\n>/]').test(t));
    if (missing.length) bad('缺字段（ModPublisher 按名字取值，缺了就传成空）：' + missing.join(', '));
    else ok('扁平字段齐备：' + REQUIRED.join(' / '));

    // 反向：那套官方不认的写法回来 = 元数据又要传空。
    const legacy = ['<Metadata>', '<Name ', '<Author ', '<PreviewImage', '<Files>', '<Text>', 'CDATA']
      .filter(s => t.includes(s));
    if (legacy.length) bad('发布 xml 里出现了官方不认的写法：' + legacy.join(' '));
    else ok('反向：Metadata/Name/Author/PreviewImage/CDATA 那套老形状没回来');

    // 封面必须真存在：PrepareThumbnail 在文件缺失时静默退回内嵌的 Colossal 占位图（Playbook v1.1 增补 #1）；
    // 单图 > 2.1 MB 会被服务端拒，且只有带二进制的那两条命令校验图片。
    const thumb = (t.match(/<Thumbnail\s+Value="([^"]*)"/) || [])[1] || '';
    if (!thumb) bad('<Thumbnail> 是空的');
    else {
      const tp = path.join(ROOT, thumb.split('/').join(path.sep));
      if (!fs.existsSync(tp)) bad('<Thumbnail> 指向的文件不存在（会静默推官方占位图）：' + thumb);
      else {
        const sz = fs.statSync(tp).size;
        if (sz > 2100000) bad(`封面 ${sz} B 超过服务端单图 2.1 MB 上限`);
        else ok(`封面 ${thumb}（${Math.round(sz / 1024)} KB，上限 2.1 MB）`);
      }
    }
    for (const m of t.matchAll(/<Screenshot\s+Value="([^"]*)"/g)) {
      if (!m[1]) continue;
      const sp = path.join(ROOT, m[1].split('/').join(path.sep));
      if (!fs.existsSync(sp)) bad('<Screenshot> 指向的文件不存在：' + m[1]);
      else if (fs.statSync(sp).size > 2100000) bad('截图超过 2.1 MB：' + m[1]);
    }

    // ChangeLog 只许写当前这一版：NewVersion 会把这一整段当成「该版本的日志」推上去。
    const cl = (t.match(/<ChangeLog>([\s\S]*?)<\/ChangeLog>/) || [])[1] || '';
    // 只认**行首**的版本号（"v0.3.0 - ..." 这种条目头）；正文里的 "ratio 1.414"、"1.6.2f1" 不算条目头
    // —— 第一版按全文扫数字点号，被 1.414 顶出了一个假阳性。
    const heads = [...new Set([...cl.matchAll(/(?:^|\n)\s*v?(\d+\.\d+(?:\.\d+)?)(?=\s|$)/gm)].map(m => m[1]))];
    if (!cl.trim()) bad('<ChangeLog> 是空的');
    else if (VERSION && !cl.includes(VERSION)) bad(`ChangeLog 里没有当前版本号 ${VERSION}`);
    else if (heads.length > 1) bad('ChangeLog 里出现了多个版本（' + heads.join(' ') + '）：只许写当前这一版');
    else ok('ChangeLog 只写当前版本 v' + VERSION);

    const acc = (t.match(/<AccessLevel\s+Value="([^"]*)"/) || [])[1] || '';
    if (!['Public', 'Private', 'Unlisted'].includes(acc)) bad(`AccessLevel="${acc}" 不是三个取值之一`);
    else ok(`AccessLevel=${acc}`);

    const mid = (t.match(/<ModId\s+Value="([^"]*)"/) || [])[1];
    if (mid === undefined) bad('没有 <ModId> 元素');
    else if (mid === '') note('ModId 还是空的：首发（PublishNewMod）之前这是对的，跑完必须回填');
    else if (!/^\d+$/.test(mid)) bad(`ModId="${mid}" 不是数字`);
    else ok(`ModId=${mid}`);

    const gv = (t.match(/<GameVersion\s+Value="([^"]*)"/) || [])[1] || '';
    if (!/\.\*$/.test(gv)) bad(`GameVersion="${gv}" 应写成通配（如 1.6.*）`);
    else ok(`GameVersion=${gv}`);

    const sd = (t.match(/<ShortDescription\s+Value="([^"]*)"/) || [])[1] || '';
    if (!sd) bad('<ShortDescription> 是空的');
    else if (sd.length > 110) note(`ShortDescription ${sd.length} 字符（三个已上线模组分别是 92/71/77，商店卡片会被截）`);
    else ok(`ShortDescription ${sd.length} 字符`);
  }
}

// —— T1 构建
if (!SKIP_BUILD && !T3_ONLY) {
  console.log('\n[T1] dotnet build -c Release');
  try {
    const out = execSync('dotnet build -c Release --nologo', { cwd: ROOT, encoding: 'utf8', maxBuffer: 40 << 20 });
    const errs = (out.match(/: error [A-Z]+[0-9]+/g) || []);
    const uniq = [...new Set(errs)];
    if (uniq.length) { bad(`构建有 ${uniq.length} 类错误`); uniq.slice(0, 12).forEach(e => note(e)); }
    else ok('构建 0 错误（MSB3277 之类算噪音，不计）');
  } catch (e) {
    const out = (e.stdout || '') + '' + (e.stderr || '');
    const uniq = [...new Set(out.match(/: error [A-Z]+[0-9]+.*$/gm) || [])];
    bad('构建失败');
    uniq.slice(0, 15).forEach(l => note(l.trim()));
  }
}

// —— 字符串堆检索（两堆分开，这是 Playbook 明记的坑）
function findUtf16(buf, s, parity = 0) {
  const t = Buffer.from(s, 'utf16le');
  for (let p = parity; p + t.length <= buf.length; p += 2) {
    let hit = true;
    for (let k = 0; k < t.length; k++) { if (buf[p + k] !== t[k]) { hit = false; break; } }
    if (hit) return p;
  }
  return -1;
}
function findUtf16AnyParity(buf, s) {
  return findUtf16(buf, s, 0) >= 0 || findUtf16(buf, s, 1) >= 0;
}
function findLatin(buf, s) { return buf.indexOf(Buffer.from(s, 'latin1')); }

// —— T2 部署产物身份
if (!T3_ONLY) {
  console.log('\n[T2] 部署目录产物');
  if (!fs.existsSync(DEPLOY_DLL)) bad('部署目录里没有 ' + MOD_NAME + '.dll：' + DEPLOY_DLL);
  else {
    const buf = fs.readFileSync(DEPLOY_DLL);
    const st = fs.statSync(DEPLOY_DLL);
    const ageSec = (Date.now() - st.mtimeMs) / 1000;
    if (!SKIP_BUILD && ageSec > 900) bad(`部署 DLL 的 mtime 已经 ${Math.round(ageSec)} 秒前，不像本次构建的产物`);
    else ok(`mtime ${st.mtime.toISOString()}（${Math.round(ageSec)} 秒前） size ${buf.length} B`);

    const types = ['ZoneSnapperSystem', 'AreaToolPatches', 'ZoneSnapperSetting', 'WorldSampler',
      'ManualStack', 'ProjectKit', 'TraceKit', 'SnapKit', 'PolicyKit', 'SimplifyKit', 'IsoKit',
      'InputKit', 'StoreKit', 'CurveKit', 'GeoKit', 'FollowKit', 'SnapperState', 'ZoneSnapperMod',
      'ZoneSnapperFollowSystem', 'AnchorCode', 'AnchorRebind', 'FallbackTally',
      // v0.3.0（第八轮反馈 5）：路口归并。这个类型名在不在 #Strings 里，决定实机那句
      // 「拐点还是偏心节点」到底是没跑归并，还是跑了但中心点算错了。
      'JunctionKit', 'Junction',
      'PreviewKit', 'PreviewFeed', 'ZoneSnapperPreviewSystem', 'TraceContextUtil',
      // v0.2.0（第五轮）新增的两个类型：跟随高亮的信箱与多语言词条表。
      // 它们在不在 #Strings 堆里，决定实机日志能不能判「闪烁与 12 语言这两条新链路装上了没有」。
      'FollowFlashFeed', 'LocaleKit', 'TraceMode', 'ModeKnobs'];
    const missing = types.filter(t => findLatin(buf, t) < 0);
    if (missing.length) bad('#Strings 堆里缺类型名：' + missing.join(', '));
    else ok(`#Strings 堆：${types.length} 个类型名全部命中`);

    if (!findUtf16AnyParity(buf, VERSION)) bad(`#US 堆里找不到本次版本字面量 "${VERSION}"（部署的可能是旧版）`);
    else ok(`#US 堆：版本字面量 "${VERSION}" 在（双 parity 扫）`);

    // 哨兵日志行必须在，否则实机没法判「补丁到底打上没有」
    // 后面四条是 v0.1.0 实机复盘后加的：玩家回来只有一段日志，这几行决定我们能不能一次定位。
    for (const sentinel of [
      '补丁已安装',        // Harmony 装了几个（不是 2 就是签名变了）
      '选项页已注册',      // 玩家说「选项页里看不到模组」⇒ 这一行就是那条症状的直接判据
      '系统就绪',          // 主系统 OnCreate 走完了
      '不介入',            // 降级闸生效：整机不碰区域工具
      '接管游戏已有的',    // 编辑/重建路径开局接管了几个控制点
      '自愈',              // 漏收提交后把尾巴收进手动栈（否则这一局永久停在「什么都不写」）
      '闭合边已补写',      // 提交后系统真的写进了 Area.Node（需求 5 的那条边）
      '跟随系统就绪',      // 自动跟随/拖拽重描的第三个系统挂上了没有
      '跟随重描',          // 它真的改写了一块已提交区域（需求 5 与第 6 条的最终证据）
      '锚点重绑',          // 第三轮实机根因（快照重抓后锚点下标失效）修没修上，看这行有没有在动
      '预览已上屏',        // 第四轮反馈 2 的淡色实时预览：渲染端真的往 overlay 缓冲写过一次
      // —— 第五轮（v0.2.0）新增的四条：这三件都是「玩家说没效果、我们只能靠日志判」的功能
      '预览渲染就绪',      // 预览的三道消费侧闸（activeTool / state / 帧新鲜度）到底有没有拿到依赖
      '预览已按',          // 预览被作废过一次 ⇒ 反馈 1「退出工具后预览还留在地上」是修上了还是在漏
      '跟随系统就绪：ToolUpdate',  // 反馈 8 的根因修复：跟随必须跑在每帧都存在的相位，ApplyTool 只在提交帧存在
      '跟随高亮已上屏',    // 反馈 8 的「高亮被改过的区域 + 闪烁被挪动的节点」真的画过
      '已退回上一次自动跟随',      // 新快捷键真的能退（改存档的功能没有退回键就不能上）
      '贴合模式 ⇒',        // 反馈 5 的下拉框与循环键：改档确实落到了引擎
      '的开关是关的',      // 反馈 2「区域类型开关没生效」：这一行说明是我们不介入，而不是游戏原版吸附
      // —— 第六轮（v0.2.1）新增的三条：这一轮的三条反馈都是「玩家说没生效，日志里必须有对应的行」
      '拖拽落档重描',      // 反馈 6：预览有、落档没有 ⇒ 这一行是落档真写了的唯一证据
      '不会自动改',        // 反馈 5：路网变了只提示不写盘 ⇒ 这一行在，才说明自动改写那条路真被掐了
      // —— 实机测试前追加的那颗「重置所有设置项」按钮：它做的是往回退的动作，按下去没动静的话我们只能看这行
      '设置已重置',      // ResetEverythingToDefaults 真跑过一次（含四颗快捷键被清空）
      // —— 第八轮（v0.3.0）：「关于」那三颗跳转按钮（点了没开浏览器时，这行是唯一能区分「没接上」和「浏览器被拦」的）
      '关于页已打开链接',
      // —— 第八轮（v0.3.0）的三条新账。这一轮的修法全是「玩家说没反应」那一类，
      //    没有这三间账就只能靠猜：attach/cont 的消长决定接入点模型有没有跑，
      //    despike 决定「手动节点不许吸附」与「走线自己接进去」有没有在同一个路口打架，
      //    under 决定隧道/下沉路到底是被抓进来后排除的，还是根本没抓到（后者是采样器的锅）；
      //    junc 决定「路口只拐在中心那一个点」在真实路网上吃没吃到数据（反馈 5 唯一机器凭据）。
      'attach=',
      'despike=',
      'under=',
      'junc=',
      '个在地下（第八轮反馈 3）',   // 建筑轮廓白名单那行里的地下计数
    ]) {
      if (!findUtf16AnyParity(buf, sentinel)) bad(`缺少实机哨兵字面量：${sentinel}`);
    }
    ok('实机哨兵字面量齐备');

    // 反向哨兵：**不该再存在**的字面量。第六轮反馈 3 让我们去写游戏的 prefab 吸附距离，
    // 第八轮反馈 3/10 玩家明确否掉了这条（手动节点归游戏对齐管），整套借与还已删除。
    // 只删代码不设闸的话，下一轮谁为了「滑杆不管用」再把那段粘回来，我们不会知道。
    for (const forbidden of [
      '吸附距离已写入 prefab',   // ApplyPrefabSnap 的日志：出现 = 又在写 AreaGeometryData.m_SnapDistance
      '吸附距离已还原 prefab',   // Restore* 的日志：借的 machinery 整个都该没了
    ]) {
      if (findUtf16AnyParity(buf, forbidden)) bad(`不该再出现的字面量又回来了：${forbidden}（第八轮反馈 3/10：模组不许改游戏内置对齐的距离）`);
    }
    ok('反向哨兵：prefab 吸附距离的借与还确实已从产物里消失');
  }
}

// —— T2c 设置页的源码级判据
//
// 为什么在门禁脚本里读源码而不是在回归壳里反射：ZoneSnapperSetting.cs 引用游戏 DLL，
// T3 那层「零游戏引用」的壳编译不到它（这是本工程的一条硬分层）。所以「属性形状」这类
// 只有实机才炸、但**能在源码上静态判定**的问题，放在这里钉。
//
// 钉的是按钮属性的**读/写形状**，因为游戏两条分支的门槛正好相反（逐字读出来的，第八轮）：
//  · BoolButton（[SettingsUIButton] 不带确认）：`if (property.canRead || !property.canWrite) return null;`
//    ⇒ **带 getter 的普通按钮整行不显示**（FACT：research/decompiled/Game.UI.Menu/AutomaticSettings.cs:1221-1224）。
//  · BoolButtonWithConfirmation：**没有**这道判空，带 getter 也照建（FACT：同文件 :1169-1191）。
//    ⇒ 带确认弹窗的按钮我们一律给 getter：设置基类的 GetHashCode 会对每个 public 属性直接
//      GetValue(this)，**没有** CanRead 保护（FACT：research/decompiled/Game.Settings/Setting.cs:62-71，
//      对照同文件 :54 的 Equals 反倒有保护）—— 带 getter 就躲开这一层，而显示不受影响。
//  · 不带确认的按钮只能 set-only：编码器读不到值 ⇒ 那个键永远不会写进盘 ⇒ 解码侧也永远碰不到它。
//    （第三方模组 Traffic Tool Essentials 的事故是「老文件里残留了这个键」：他们有 getter 的年代写进去过。）
// 所以这里的规则是**分叉**的，不是一刀切：有确认 ⇒ 必须有 getter；没确认 ⇒ 必须没有 getter。
if (!T3_ONLY) {
  console.log('\n[T2c] 设置页按钮属性形状（源码级）');
  const settingSrc = fs.readFileSync(path.join(ROOT, 'ZoneSnapperSetting.cs'), 'utf8');
  const decls = [...settingSrc.matchAll(/\[SettingsUIButton\][\s\S]{0,600}?\b(public|internal)\s+bool\s+(\w+)\s*\{/g)];
  if (!decls.length) bad('一个 [SettingsUIButton] 属性都没找到 —— 属性写法变了，这条判据要跟着改');
  for (const d of decls) {
    const name = d[2];
    // 从属性声明处的 '{' 起，按括号配平取整块，再在里面找 getter
    const open = settingSrc.indexOf('{', d.index + d[0].length - 1);
    let depth = 0, end = open;
    for (let i = open; i < settingSrc.length; i++) {
      if (settingSrc[i] === '{') depth++;
      else if (settingSrc[i] === '}') { depth--; if (!depth) { end = i; break; } }
    }
    const body = settingSrc.slice(open, end + 1);
    const decl = d[0];
    const hasGetter = /(^|[^A-Za-z_])get\s*(\{|=>|;)/.test(body);
    const hasConfirm = /\[SettingsUIConfirmation/.test(decl);
    if (hasConfirm) {
      if (hasGetter) ok(`按钮 ${name}：带确认弹窗 + 有 getter ⇒ 显示（:1169 不查 canRead）且 GetHashCode 不踩空`);
      else bad(`按钮 ${name}：带确认弹窗却没有 getter —— 显示没问题，但 Setting.GetHashCode(Setting.cs:62-71) 会对它 GetValue`);
    } else {
      if (hasGetter) bad(`按钮 ${name}：不带确认弹窗却带 getter ⇒ AddBoolButtonProperty(AutomaticSettings.cs:1221-1224) 直接 return null，整行不显示`);
      else ok(`按钮 ${name}：set-only 普通按钮 ⇒ 正是 :1221 要的写法（编码器读不到值 ⇒ 盘里不会有这个键）`);
    }
  }
  // 「关于」那三颗跳转按钮必须共用一个按钮组名，否则三行而不是「一行三颗」
  //（FACT：AutomaticSettings.cs:718-733 GetButtonsGroup + :1239；范本 ModdingSettings.cs:36/55/74）。
  const groups = [...settingSrc.matchAll(/\[SettingsUIButtonGroup\((\w+)\)\]/g)].map(m => m[1]);
  const uniqGroups = [...new Set(groups)];
  if (!groups.length) note('没用到 [SettingsUIButtonGroup]（三颗按钮各一行也能显示，只是不并排）');
  for (const g of uniqGroups) {
    const n = groups.filter(x => x === g).length;
    const resolved = settingSrc.match(new RegExp(`${g}\\s*=\\s*"([^"]+)"`));
    if (!resolved) bad(`按钮组常量 ${g} 找不到字面量值`);
    else if (!resolved[1].includes('ZoneSnapper')) bad(`按钮组名 "${resolved[1]}" 没带模组前缀：那张表是进程内 static 全局的，撞名就跟别人的按钮并成一行`);
    else ok(`按钮组 ${g} = "${resolved[1]}"（${n} 颗按钮共用一行，名字带前缀）`);
  }
  // 只读文本行（string + 只有 getter）绝不能返回 null：:1414 是 LocalizedString.Value((string) GetValue(...))
  const strGetters = [...settingSrc.matchAll(/public\s+string\s+(\w+)\s*\{\s*get\s*\{\s*return\s+([^;}]+);/g)];
  for (const s of strGetters) ok(`只读文本行 ${s[1]} 返回 ${s[2].trim()}（非 null ⇒ StringField 不会炸）`);
}

// —— T2b 共享库身份（换 dll 失效时构建照样成功 ⇒ 必须单独钉）
if (!T3_ONLY) {
  console.log('\n[T2b] 0Harmony.dll 身份');
  const dep = path.join(DEPLOY_DIR, '0Harmony.dll');
  const nu = path.join(os.homedir(), '.nuget/packages/lib.harmony/2.3.3/lib/net48/0Harmony.dll');
  if (!fs.existsSync(dep)) bad('部署目录里没有 0Harmony.dll');
  else if (!fs.existsSync(nu)) bad('本机 NuGet 缓存里没有 2.3.3 那份：' + nu + '（联网跑 dotnet restore）');
  else {
    const a = crypto.createHash('sha256').update(fs.readFileSync(dep)).digest('hex');
    const b = crypto.createHash('sha256').update(fs.readFileSync(nu)).digest('hex');
    if (a !== b) bad(`部署的 0Harmony 不是 2.3.3 那份（换 dll 的 Target 位置错了？）\n      部署 ${a.slice(0, 16)}…\n      缓存 ${b.slice(0, 16)}…`);
    else ok('部署的 0Harmony.dll == NuGet 2.3.3 那份');
  }
}

// —— T3 无游戏回归壳
console.log('\n[T3] 离线回归壳（零游戏 DLL 引用 / 零联网 / 零额度）');
const T3PROJ = path.join(ROOT, 'tests', 't3', 't3.csproj');
const T3PROJ2 = path.join(ROOT, 'tests', 't3', 'T3.csproj');
const t3proj = fs.existsSync(T3PROJ) ? T3PROJ : (fs.existsSync(T3PROJ2) ? T3PROJ2 : null);
if (!t3proj) bad('还没建回归壳：' + path.join(ROOT, 'tests', 't3'));
else {
  try {
    const out = execSync(`dotnet run -c Release --project "${t3proj}" --property:NuGetInteractive=false`,
      { encoding: 'utf8', maxBuffer: 60 << 20, cwd: path.dirname(t3proj) });
    const m = out.match(/PASS\s+(\d+)\s*\/\s*FAIL\s+(\d+)/i);
    if (!m) bad('回归壳没打印 PASS/FAIL 汇总行');
    else if (m[2] !== '0') bad(`回归失败 ${m[2]} 条（通过 ${m[1]}）`);
    else ok(`回归全绿：${m[1]} 条断言`);
    const bugLines = out.split('\n').filter(l => /FAIL\b/.test(l)).slice(0, 20);
    bugLines.forEach(l => note(l.trim()));
  } catch (e) {
    bad('回归壳跑不起来');
    const out = String(e.stdout || '') + String(e.stderr || '');
    out.split('\n').filter(l => /error|FAIL/.test(l)).slice(0, 15).forEach(l => note(l.trim()));
  }
}

console.log('\n' + (fails ? `\x1b[31m门禁未过：${fails} 项\x1b[0m` : '\x1b[32m门禁全绿\x1b[0m'));
process.exit(fails ? 1 : 0);
