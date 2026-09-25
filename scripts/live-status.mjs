// 查 Paradox Mods 上 Zone Snapper 的线上真实状态（发版前后各跑一次）。
//
//   node scripts/live-status.mjs            // 默认读本模组 xml 里的 ModId
//   node scripts/live-status.mjs 160526
//
// 为什么需要它：本地 Properties\PublishConfiguration.xml 只代表"上次从本机推上去的内容"。
// 网页端手改的东西（名字 / 描述 / 封面 / 截图 / 公开状态 / 外链）本地文件里从来没有过 ——
// 而 Update / NewVersion 每次都会重推全部元数据，没回填的就会被覆盖掉（Playbook 步骤 6）。
//
// os 参数：用 windows。os=any 会返回 "Mod is corrupted or chosen operating system is not supported"，
// 那不是模组坏了（凭据见 BridgeTheLanguageGap/scripts/live-status.mjs 的 2026-09-05 实测记录）。
// 网页端同理：https://mods.paradoxplaza.com/mods/<ModId>/windows
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const ROOT = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..');

function modIdFromXml() {
  try {
    const t = fs.readFileSync(path.join(ROOT, 'Properties', 'PublishConfiguration.xml'), 'utf8');
    return (t.match(/<ModId\s+Value="(\d+)"/) || [])[1] || null;
  } catch (e) { return null; }
}

const modId = process.argv[2] || modIdFromXml();
if (!modId) { console.error('没有 ModId：命令行传一个，或先回填进 PublishConfiguration.xml'); process.exit(1); }

async function get(url, headers) {
  const r = await fetch(url, headers ? { headers } : undefined);
  return { status: r.status, json: await r.json().catch(() => null) };
}

// 私有（acl=Private）模组对匿名请求返回 "User does not have access to this mod" —— 这不是没发上去，
// 是权限。要看真状态得带作者的会话凭据：
//   %USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\.pdxsdk\<SteamId>\database.json
//   里的 SessionToken（36 位 GUID，24 小时过期，开着游戏才会刷新）。
// Authorization 头的值是**那个 JSON 字符串本体**，不是 Bearer（Playbook 步骤 8）。
function sessionHeaders() {
  // ⚠ 是 AppData\**LocalLow**，不是 LOCALAPPDATA（那个指向 AppData\Local）—— 第一次就拼错过一次。
  const base = path.join(process.env.USERPROFILE || os.homedir(),
    'AppData', 'LocalLow', 'Colossal Order', 'Cities Skylines II', '.pdxsdk');
  if (!fs.existsSync(base)) return { err: '找不到 .pdxsdk 目录（本机没登录过 Paradox？）：' + base };
  const dirs = fs.readdirSync(base, { withFileTypes: true }).filter(d => d.isDirectory()).map(d => d.name);
  let best = null;
  for (const name of dirs) {
    const p = path.join(base, name, 'database.json');
    if (!fs.existsSync(p)) continue;
    if (!best || fs.statSync(p).mtimeMs > fs.statSync(best).mtimeMs) best = p;
  }
  if (!best) return { err: '.pdxsdk 下没有 database.json' };
  let tok = null;
  try { tok = JSON.parse(fs.readFileSync(best, 'utf8')).SessionToken || null; } catch (e) { }
  if (!tok) return { err: 'database.json 里没有 SessionToken（开着游戏重进一次 Paradox 登录）' };
  const ageH = (Date.now() - fs.statSync(best).mtimeMs) / 3600000;
  return { headers: { Authorization: JSON.stringify({ session: tok, type: 'Session' }) },
    source: path.basename(path.dirname(best)), ageH };
}

(async () => {
  let d = null, osUsed = null, bad = [], authed = false;

  for (const os of ['Windows', 'Any']) {
    const res = await get(`https://api.paradox-interactive.com/mods?modId=${modId}&os=${os.toLowerCase()}`);
    if (res.json?.result === 'OK') { d = res.json.modDetail; osUsed = os; break; }
    bad.push(`匿名 ${os}: ${res.json?.errorMessage || res.json?.result || 'HTTP ' + res.status}`);
  }

  // 匿名拿不到（私有列表就是这样）⇒ 换成作者凭据再试一次。
  if (!d) {
    const s = sessionHeaders();
    if (s.err) { console.error(bad.join('\n  ') + '\n  带凭据也不行：' + s.err); process.exit(1); }
    for (const os of ['Windows', 'Any']) {
      const res = await get(`https://api.paradox-interactive.com/mods?modId=${modId}&os=${os.toLowerCase()}`, s.headers);
      if (res.json?.result === 'OK') { d = res.json.modDetail; osUsed = os; authed = true; break; }
      bad.push(`凭据 ${os}: ${res.json?.errorMessage || res.json?.result || 'HTTP ' + res.status}`);
    }
    if (!d) {
      // 2026-09-25 首发实测：acl=Private 的模组对 mods?modId=… 返回 403 "User does not have access to this mod"，
      // **带作者自己的会话凭据也一样**（这条详情端点按 ACL 过滤，私有列表不在范围内）。
      // 但 mods/versions?modId=… 不按 ACL 过滤 ⇒ 它才是"到底传上去了没有"的凭据（Playbook 步骤 8 的判据 ①）。
      const v = await get(`https://api.paradox-interactive.com/mods/versions?modId=${modId}`, s.headers);
      const list = v.json?.modVersions;
      if (Array.isArray(list) && list.length) {
        console.log(`modId            ${modId}`);
        console.log('模组详情端点 403（私有列表按 ACL 过滤，带凭据也一样）⇒ 改看版本表，这是"传没传上去"的真凭据：');
        for (const x of list)
          console.log(`  userVersion=${x.userVersion}  ${x.size} B  ${x.created}  (平台版本 id=${x.id})`);
        console.log('平台同步状态（ModState.Publishing 时这里看不到 state，不是失败）：');
        console.log('  网页端 https://mods.paradoxplaza.com/mods/' + modId + '/windows 的 ALL VERSIONS 面板才是全貌');
        process.exit(0);
      }
      console.error('匿名与凭据都失败，且版本表也是空的：\n  ' + bad.join('\n  ')
        + '\n  （SessionToken 24 小时过期，开着游戏重进一次再跑。）');
      process.exit(1);
    }
  }

  const versions = await get(`https://api.paradox-interactive.com/mods/versions?modId=${modId}`,
    authed ? sessionHeaders().headers : undefined);

  console.log(`modId            ${d.modId}   (os=${osUsed}${authed ? '，带作者凭据 ⇒ 私有列表只有这样才看得见' : ''})`);
  console.log(`网站名            ${d.displayName}`);
  console.log(`作者              ${d.author}`);
  console.log(`可见性            acl=${d.acl}  state=${d.state}  enabled=${d.enabled}`);
  console.log(`版本              平台版本=${d.modVersion}  自报版本=${d.userModVersion}  latestVersion=${d.latestVersion}`);
  console.log(`游戏版本要求      ${d.requiredVersion}`);
  console.log(`创建 / 最后更新    ${d.creationDate}  →  ${d.latestUpdate}`);
  console.log(`订阅 / 评分        ${d.subscriptions} 订阅,  rating=${d.rating} (${d.ratingsTotal} 人)`);
  console.log(`Tag              ${JSON.stringify(d.tags)}`);
  console.log(`外链              ${d.externalLinks.map(l => l.type + '=' + l.url).join('  ') || '(无)'}`);
  console.log(`论坛帖            ${d.forumLinks?.map(f => f.url || f).join('  ') || d.forumLink || '(无)'}`);
  console.log(`封面 / 截图        ${d.displayImagePath}  + ${(d.screenshots || []).length} 张`);
  console.log(`描述长度          short=${(d.shortDescription || '').length}  long=${(d.longDescription || '').length} 字符`);
  console.log(`分区被墙          ${d.isRegionBlocked ? '是' : '否'}`);
  console.log(`商店页            https://mods.paradoxplaza.com/mods/${modId}/windows`);

  if (versions.json?.modVersions) {
    console.log(`\n平台上的全部版本：`);
    for (const v of versions.json.modVersions)
      console.log(`  v${v.id}  userVersion=${v.userVersion}  ${v.size} B  ${v.created}`);
  }
  if (bad.length) console.log(`\n(其他 os 参数：${bad.join(' | ')})`);
})();
