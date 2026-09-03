/* js/views/unit-roster.js —— 一条预检指令里的业务单元清单

   「这个任务有几个账套，每一个此刻走到哪一步」原先只在「立即备份」的弹窗里出现过一次，
   窗口一关就再也找不回来。而对 U8 这类一台机器 18 个账套的任务来说，
   这份名单恰恰是排障时第一个要看的东西：18 个里有 4 个没扫出来，
   在别的任何一个页面上都表现为「这个任务备份成功了」。

   所以抽成共享模块，接到三个地方：立即备份弹窗、任务详情、执行详情。 */
import { esc } from '../ui.js';

/* 预检指令的结构化结果里带着这次扫出来的**每一个**业务单元的候选。
   老格式（只有一个顶层 candidateBackupSetId）仍然认——升级期间 Agent 与服务端
   不是同一刻换的，读不出清单就退回那一个，总比一个都不传强。 */
export function passedCandidatesOf(resultPayload) {
  if (!resultPayload) return [];
  let parsed;
  try { parsed = JSON.parse(resultPayload); } catch (e) { return []; }

  if (Array.isArray(parsed.candidates)) {
    return parsed.candidates
      .filter(c => c && c.status === 'passed' && typeof c.candidateBackupSetId === 'string')
      .map(c => ({ candidateBackupSetId: c.candidateBackupSetId, uploadState: c.uploadState || null }));
  }

  return typeof parsed.candidateBackupSetId === 'string'
    ? [{ candidateBackupSetId: parsed.candidateBackupSetId, uploadState: null }]
    : [];
}

/* Agent 一开始列一次目录就把全部账套报上来了，服务端放在 resultPayload.units。
   老版本 Agent 不报这一项，返回空数组，调用方退回到只显示一行进度。 */
export function unitRosterOf(cmd) {
  if (!cmd || !cmd.resultPayload) return [];
  try {
    const units = JSON.parse(cmd.resultPayload).units;
    return Array.isArray(units) ? units.filter(u => typeof u === 'string' && u) : [];
  } catch (e) { return []; }
}

/* 已经扫完的那些单元，按显示名索引。 */
export function scannedUnitsOf(cmd) {
  const done = new Map();
  if (!cmd || !cmd.resultPayload) return done;
  let parsed;
  try { parsed = JSON.parse(cmd.resultPayload); } catch (e) { return done; }
  if (!Array.isArray(parsed.candidates)) return done;
  for (const c of parsed.candidates)
    if (c && typeof c.businessUnit === 'string' && c.businessUnit) done.set(c.businessUnit, c);
  return done;
}

/* 一个单元此刻处在哪一步。这几个词是给运维看的，不是状态机的名字：
   他要判断的是「这个账套到底备着了没有」，而不是它内部走到了哪个枚举值。 */
export function unitStateText(entry) {
  if (!entry) return null;
  if (entry.status !== 'passed') return entry.status === 'no_new_backup' ? '无新备份' : '未通过';
  switch (entry.uploadState) {
    case 'queued': return '排队上传';
    case 'dispatched': return '已下发上传';
    case 'already_running': return '上传进行中';
    case 'already_done': return '已上传';
    default: return '待上传';
  }
}

/* Agent 在扫描途中报上来的进度，服务端原样放在 resultPayload 里。
   没有进度（老版本 Agent、或指令还没被领走）时返回 null，调用方退回到只报状态。 */
export function progressOf(cmd) {
  if (!cmd || !cmd.resultPayload) return null;
  try {
    const p = JSON.parse(cmd.resultPayload).progress;
    return p && p.message ? p : null;
  } catch (e) { return null; }
}

/* 「共 18 个账套，3 个扫完、1 个在扫、14 个等着」，底下把每一个列出来。

   这一屏回答的是「还剩多少」。账套是 Agent 边扫边发现的，在这份名单出现之前，
   界面上只能看着它们一个一个冒出来——既没有总数，也没有次序，
   人只能盯着「传输中」猜自己还要等多久。

   opts.live=false（任务详情、执行详情这类回看历史的地方）：
   这条指令早就跑完了，「扫描中 / 等待」那两个词会把一份历史快照说成正在进行。
   没有结论的单元在那里应该显示成「没有结果」——那才是这份名单真正想说的话。 */
export function unitQueueHtml(cmd, opts = {}) {
  const live = opts.live !== false;
  const roster = unitRosterOf(cmd);
  if (roster.length < 2) return '';   // 只有一个单元时这份名单没有信息量

  const scanned = scannedUnitsOf(cmd);
  const current = live ? ((progressOf(cmd) || {}).unit || '') : '';

  let waiting = 0;
  const rows = roster.map(name => {
    const state = unitStateText(scanned.get(name));
    if (!state && name !== current) waiting++;
    const fallback = live
      ? (name === current ? '扫描中' : '等待')
      : '没有结果';
    const text = state || fallback;
    return `<div class="row"><div class="k">${esc(name)}</div><div class="v">${esc(text)}</div></div>`;
  }).join('');

  const running = current ? 1 : 0;
  const tail = live
    ? `${running} 个扫描中 · ${waiting} 个等待`
    // 历史快照里「没有结果」是要被看见的：18 个账套扫出来 14 个，
    // 在别的任何一个页面上都表现为「这个任务备份成功了」。
    : `${waiting} 个没有结果`;

  return `<div class="hint" style="margin-top:10px">共 ${roster.length} 个业务单元 ·
      ${scanned.size} 个已扫完 · ${tail}</div>
    <div class="kv" style="margin-top:6px">${rows}</div>`;
}
