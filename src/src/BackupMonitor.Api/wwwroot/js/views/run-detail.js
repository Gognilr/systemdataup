/* js/views/run-detail.js —— 一次执行的详情弹窗。

   计划页和运行记录页都要打开它，而这两处显示的必须是同一份东西：
   同一次执行在两个页面上长得不一样时，人没有任何办法判断哪一边是真的。 */
import { api } from '../api.js';
import { esc, fmtDT, tableHtml, openModal, errToast, confirmModal, toast } from '../ui.js';
// C11：业务单元清单与「立即备份」弹窗共用同一份实现。
import { unitQueueHtml, progressOf } from './unit-roster.js';

export const RUN_STATUS = {
  pending: ['排队中', 'wait'], running: ['执行中', 'busy'], completed: ['全部完成', 'ok'],
  partial: ['部分失败', 'wait'], failed: ['失败', 'err'], cancelled: ['已取消', 'mut']
};

export const ITEM_STATUS = {
  pending: ['排队中', 'wait'], running: ['执行中', 'busy'], succeeded: ['完成', 'ok'],
  failed: ['失败', 'err'], timeout: ['等超时', 'err'], skipped: ['跳过', 'mut'], cancelled: ['已取消', 'mut']
};

/* 一次执行是怎么来的。计划到点、人点了「立即执行」、批量上传、
   预检发现有新备份但名额满了自己排的队——都在同一张表里，
   不写出来的话运行记录里会出现一堆看不出出处的「排队等待上传」。 */
export const RUN_KIND = {
  backup_plan: '备份计划', upload_batch: '批量上传', manual: '手动执行'
};

export const TRIGGER_TEXT = {
  schedule: '到点自动', manual: '人工发起', auto_upload_queued: '预检后自动排队'
};

export function statusPill(map, key) {
  const [text, tone] = map[key] || [key || '—', 'mut'];
  return `<span class="status status--${tone} pill">${esc(text)}</span>`;
}

export function runKindText(run) {
  return RUN_KIND[run.kind] || run.kind || '—';
}

export function runTitle(run) {
  return run.name || run.planName || runKindText(run);
}

/* 打开一次执行的详情。onChange 在取消成功后回调，让调用页刷新自己的列表。 */
export async function openRunModal(runId, { onChange } = {}) {
  let run;
  try { run = await api(`/api/v1/admin/execution-runs/${runId}`); }
  catch (e) { errToast(e); return; }

  const ov = openModal(`执行详情：${runTitle(run)}`, await runBodyHtml(run), { wide: true });
  attachCancelButton(ov, run, runId, onChange);

  // 这个弹窗原先是一张静态快照：打开的那一刻是什么样，之后就一直是什么样。
  // 而它恰恰是人盯着看「这次计划跑到第几项了」的地方——pending 项的「说明」列
  // 永远是空的，看起来就像卡住了。跑完就停下来，不做无谓的轮询。
  const timer = setInterval(async () => {
    if (!ov.isConnected) { clearInterval(timer); return; }
    let fresh;
    try { fresh = await api(`/api/v1/admin/execution-runs/${runId}`); }
    catch (e) { return; }   // 一次抖动不该把已经显示出来的内容抹掉

    const mbody = ov.querySelector('.mbody');
    if (mbody) mbody.innerHTML = await runBodyHtml(fresh);
    if (fresh.status !== 'pending' && fresh.status !== 'running') {
      clearInterval(timer);
      ov.querySelector('.mfoot [data-run-cancel]')?.remove();
    }
  }, 5000);
  // 关掉弹窗（关闭按钮 / 点遮罩 / Esc）都走 closeModal，它派发 bm:dismiss。
  // 不停这个定时器的话，人关掉窗口之后它还在每 5 秒打一次接口，直到刷新页面为止。
  ov.addEventListener('bm:dismiss', () => clearInterval(timer));
}

/* 执行详情的正文。每一项的业务单元清单跟着一起显示——
   一个计划项对应一个任务，而一个任务可以有 18 个账套，
   只看「这一项成功了」是看不出其中 4 个账套一个字节都没传的。 */
export async function runBodyHtml(run) {
  const items = run.items || [];

  // 每一项的预检指令结果各拉一次。并行发，任何一条失败都只是这一项没有清单，
  // 不该让整个弹窗打不开。
  const rosters = await Promise.all(items.map(async i => {
    if (!i.commandId) return { item: i, html: '', cmd: null };
    try {
      const cmd = await api(`/api/v1/admin/backup-tasks/commands/${i.commandId}`);
      return { item: i, html: unitQueueHtml(cmd, { live: i.status === 'running' }), cmd };
    } catch (e) { return { item: i, html: '', cmd: null }; }
  }));
  const cmdOf = new Map(rosters.map(r => [r.item.id, r.cmd]));

  const unitsHtml = rosters.filter(r => r.html).map(r => `
    <details class="fadv">
      <summary>${esc(r.item.taskName)} · 业务单元</summary>
      ${r.html}
    </details>`).join('');

  return `
    <div class="kv">
      <div class="row"><div class="k">状态</div><div class="v">${statusPill(RUN_STATUS, run.status)}</div></div>
      <div class="row"><div class="k">来源</div><div class="v">${esc(runKindText(run))}${run.planName ? ` · ${esc(run.planName)}` : ''}</div></div>
      <div class="row"><div class="k">并发度</div><div class="v">${run.maxConcurrent === 1 ? '严格顺序（1）' : esc(run.maxConcurrent)}</div></div>
      <div class="row"><div class="k">单项超时</div><div class="v">${esc(run.itemTimeoutMinutes)} 分钟</div></div>
      <div class="row"><div class="k">触发方式</div><div class="v">${esc(TRIGGER_TEXT[run.triggerSource] || run.triggerSource || '—')}</div></div>
      <div class="row"><div class="k">开始 / 结束</div><div class="v">${fmtDT(run.startedAt)} → ${run.finishedAt ? fmtDT(run.finishedAt) : '进行中'}</div></div>
      <div class="row"><div class="k">进度</div><div class="v">成功 ${esc(run.succeededItems)} · 失败 ${esc(run.failedItems)} · 共 ${esc(run.totalItems)}</div></div>
    </div>
    ${tableHtml([
      { l: '#', num: true, render: i => i.sortOrder + 1 },
      { l: '任务', render: i => `<b>${esc(i.taskName)}</b>` },
      { l: '客户端', render: i => esc(i.clientName) },
      { l: '状态', render: i => statusPill(ITEM_STATUS, i.status) },
      { l: '开始', render: i => i.startedAt ? fmtDT(i.startedAt) : '—' },
      { l: '结束', render: i => i.finishedAt ? fmtDT(i.finishedAt) : '—' },
      { l: '说明', render: i => itemMessageHtml(i, cmdOf.get(i.id)) }
    ], items)}
    ${unitsHtml ? `<h3>各项的业务单元</h3>${unitsHtml}` : ''}`;
}

/* 「说明」列。终结的项有 message，正在跑的那一项此前永远是空的——
   而它恰恰是人盯着看的那一格：备份计划显示「执行中」、「传输中」页面又是空的
   （上传会话要等客户端把备份文件整读一遍算完校验和才会建出来），
   两边合起来看就像什么都没在发生，而它可以这样待满 60 分钟的单项超时。

   要区分开的是三件完全不同的事，它们在界面上此前长得一模一样：
     指令还躺在队列里没被领走 —— 客户端多半根本没在动（离线、Agent 没起来）；
     客户端正在扫描       —— 它在干活，只是大文件算校验和要时间；
     扫完了在等上传开始   —— 球已经不在客户端那边了。
   前两者的处置完全相反，混成一句「执行中」等于把排障的第一步删掉。

   进度本身客户端一直在报（指令的 result_payload.progress），只是此前只喂给了
   业务单元清单，而那份清单在单元数 < 2 时直接返回空——单库任务永远不到那个门槛。 */
function itemMessageHtml(item, cmd) {
  if (item.message) return esc(item.message);
  if (item.status !== 'running') return '';

  const p = progressOf(cmd);
  if (p) {
    const percent = typeof p.percent === 'number' ? `${Math.round(p.percent)}% · ` : '';
    return `<span class="hint">${esc(percent + p.message)}</span>`;
  }

  switch (cmd && cmd.status) {
    case 'pending':
      return '<span class="hint">指令已下发，客户端还没来领——它每隔几秒问一次，一直停在这里就是客户端没在运行</span>';
    case 'claimed':
    case 'running':
      return '<span class="hint">客户端正在扫描备份文件、计算校验和…</span>';
    case 'succeeded':
      return '<span class="hint">已扫完，等上传开始（开始后到「传输中」看进度）</span>';
    default:
      return '<span class="hint">等待客户端…</span>';
  }
}

function attachCancelButton(ov, run, runId, onChange) {
  if (run.status !== 'pending' && run.status !== 'running') return;

  const cancel = document.createElement('button');
  cancel.className = 'small danger';
  cancel.dataset.runCancel = '1';
  cancel.textContent = '取消这次执行';
  cancel.addEventListener('click', async () => {
    if (!await confirmModal('取消这次执行？还没开跑的项会被取消，正在跑的那几项也会被停掉：它们的指令会被取消，已经开始的上传会中断，这一轮扫出来的备份不会再自动上传。')) return;
    try {
      await api(`/api/v1/admin/execution-runs/${runId}/cancel`, { method: 'POST' });
      toast('已取消', 'ok');
      onChange?.();
    } catch (e) { errToast(e); }
  });
  ov.querySelector('.mfoot')?.prepend(cancel);
}
