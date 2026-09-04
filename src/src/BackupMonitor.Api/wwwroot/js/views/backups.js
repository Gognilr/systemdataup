/* js/views/backups.js —— 备份集列表 / 详情 / 锁定 / 校验 / 发起恢复 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, L, optsOf, status, fmtBytes, fmtDT, relTime,
  tableHtml, pagerHtml, skeleton, emptyState, hasFilter, batchBarHtml,
  toast, errToast, confirmModal, formModal
} from '../ui.js';
import { shell, loading } from '../app.js';

export async function vBackups() {
  // grouped 默认开：多业务单元任务下平铺列表是不能用的（U8 一个任务一天 18 行），
  // 而「一个任务一行」才是这张表被打开时真正要回答的粒度。
  // openTasks 记住展开过哪几组——锁定/删除这些动作做完都会重刷整页，
  // 不记住的话每点一次按钮，刚展开的那一组就自己收回去了。
  App.state.backups = App.state.backups
    || { page: 1, pageSize: 20, status: '', keyword: '', totalCount: 0,
         sortKey: 'uploadedAt', sortDesc: true, grouped: true, openTasks: [],
         selected: [], rows: [] };
  const st = App.state.backups;
  syncRecycleLayout(st);
  $('#app').innerHTML = shell('backups', '备份集', `
    <div class="toolbar">
      <select id="f_status"><option value="">全部（不含已删除）</option>${optsOf(L.backup_set_status).map(o => `<option value="${o.v}" ${st.status === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select>
      <input id="f_kw" placeholder="关键字" value="${esc(st.keyword)}">
      <button class="primary" data-ui-action="loader" data-loader="backups">查询</button>
      <div class="spacer"></div>
      <button id="f_group">${st.grouped ? '平铺全部备份集' : '按任务归拢'}</button>
      <button id="f_recycle">${st.status === 'recycle_bin' ? '← 回到全部备份集' : '回收站'}</button>
    </div>
    <div id="backup-summary-chips" class="filter-chips" aria-label="备份集状态分布"></div>
    <div id="bb-backups">${batchBarHtml('backups')}</div>
    <div id="vwrap">${loading()}</div>`);
  // 换筛选条件之后，选中的那几行多半已经不在视野里了。留着它们，
  // 批量条会顶着「已选 12 项」而屏幕上一个勾都看不见，下一次点批量按钮
  // 动的是人早就看不到的东西。
  $('#f_status').onchange = () => { st.status = $('#f_status').value; st.page = 1; st.selected = []; LOADERS.backups(); };
  // 回收站不是一个独立的页面，它就是「状态 = 回收站」这一档；给一个直达按钮，
  // 因为「删掉的东西去哪了」是删除之后第一个会问的问题。
  $('#f_recycle').onclick = () => {
    st.status = st.status === 'recycle_bin' ? '' : 'recycle_bin';
    st.page = 1;
    st.selected = [];
    vBackups();
  };
  $('#f_group').onclick = () => {
    st.grouped = !st.grouped;
    st.page = 1;
    vBackups();
  };
  $('#f_kw').onkeydown = ev => { if (ev.key === 'Enter') { st.keyword = ev.target.value.trim(); st.page = 1; st.selected = []; LOADERS.backups(); } };
  api('/api/v1/admin/reports/backup-summary').then(summary => {
    const wrap = $('#backup-summary-chips');
    if (!wrap) return;
    wrap.innerHTML = (summary.byStatus || []).map(item => `<button class="filter-chip${st.status === item.value ? ' active' : ''}" data-ui-action="filter" data-filter-key="backups" data-filter-field="status" data-filter-value="${esc(item.value)}">${esc(L.backup_set_status[item.value] || item.value)} <strong>${esc(item.count)}</strong></button>`).join('');
  }).catch(() => {});
  await LOADERS.backups();
}

/* 进回收站时自动切成平铺。
   归拢存在的唯一理由是回答「这个任务今天备齐了没有」——回收站里没有这个问题，
   这里要回答的是「这一条我还要不要」，那是逐条的。归拢反而把每一条都藏进折叠里，
   而删除、还原、彻底删除全是逐条的动作。

   只在「状态这一档变了」的那一次动手，不是每次渲染都强制：
   人在回收站里手动点「按任务归拢」之后，下一次刷新不该又被扳回平铺，
   那样那个按钮看上去就是坏的。离开回收站时把进来之前的版式还回去。
   返回 true 表示版式改了，调用方要整页重画（工具条上那个按钮的文案也得跟着变）。 */
function syncRecycleLayout(st) {
  if (st.lastStatus === st.status) return false;
  const before = st.grouped;
  if (st.status === 'recycle_bin') {
    st.groupedBeforeRecycle = st.grouped;
    st.grouped = false;
  } else if (st.lastStatus === 'recycle_bin' && st.groupedBeforeRecycle != null) {
    st.grouped = st.groupedBeforeRecycle;
    st.groupedBeforeRecycle = null;
  }
  st.lastStatus = st.status;
  return st.grouped !== before;
}

/* 批量条要按「这一行轮不轮得上这个动作」把按钮变灰，判据来自行本身，
   所以每张表加载完都把行并进 st.rows。归拢视图下一页有十几组，
   每组一张表——覆盖式赋值的话，永远只剩最后展开的那一组判得准。
   按 id 覆盖同一条的旧值，状态变了之后不会拿着过期的行去判。 */
const MAX_TRACKED_ROWS = 1000;
function mergeRows(st, items) {
  const ids = new Set((items || []).map(r => r.id));
  st.rows = (st.rows || []).filter(r => !ids.has(r.id)).concat(items || []);
  if (st.rows.length > MAX_TRACKED_ROWS) st.rows = st.rows.slice(-MAX_TRACKED_ROWS);
}

/* 备份集明细表的列。归拢视图展开之后用的是同一套列——
   两套列定义一旦分家，同一份备份在展开前后会显示出不一样的信息，
   而人没有任何办法知道哪一边才是真的。 */
const BACKUP_COLUMNS = [
      { l: '备份集编号', k: 'backupSetCode', sort: true, render: r => `<a href="#/backups/${esc(r.id)}"><b class="mono">${esc(r.backupSetCode)}</b></a>` },
      // 账套（业务单元）必须写在这一列里：U8 一个任务下 18 个账套，18 行的客户端和任务名
      // 长得一模一样，不显示单元就分不清哪一行是哪个账套——而保留份数正是按账套各算各的。
      { l: '客户端 / 任务', render: r => `${esc(r.clientHostname)}<span class="sub">${esc(r.taskName)}${r.businessUnitName ? ' · ' + esc(r.businessUnitName) : ''}</span>` },
      { l: '应用', k: 'applicationName' },
      { l: '状态', k: 'status', sort: true, render: r => status('backup_set_status', r.status) },
      // 「3 天前」回答不了「这份是不是周二那次」——精确时间当主行，相对时间降为副行。
      // 此前精确值只藏在鼠标悬停的 title 里，等于没有。
      { l: '业务时间', k: 'backupBusinessTime', sort: true,
        render: r => `${esc(fmtDT(r.backupBusinessTime))}<span class="sub">${relTime(r.backupBusinessTime)}</span>` },
      { l: '大小', k: 'totalBytes', sort: true, num: true, render: r => `${esc(r.totalFiles)} 个文件 / ${fmtBytes(r.totalBytes)}` },
      { l: '锁定', render: r => r.locked ? '<span class="status status--wait pill">已锁定</span>' : '—' },
      { l: '保留至', k: 'retentionUntil', sort: true, render: r => relTime(r.retentionUntil) },
      { l: '操作', render: r => {
        const b = [];
        if (r.status === 'available') {
          b.push(`<button class="small primary" data-ui-action="act" data-view="backups" data-action="restore" data-id="${esc(r.id)}">恢复</button>`);
          b.push(`<button class="small" data-ui-action="act" data-view="backups" data-action="verify" data-id="${esc(r.id)}">重新校验</button>`);
        }
        if (r.locked) b.push(`<button class="small" data-ui-action="act" data-view="backups" data-action="unlock" data-id="${esc(r.id)}">解锁</button>`);
        else if (r.status === 'available') b.push(`<button class="small" data-ui-action="act" data-view="backups" data-action="lock" data-id="${esc(r.id)}">锁定</button>`);
        // D1：隔离——人工怀疑该版本有问题时的动作，不参与保留计算、不能用于恢复，但也不删除
        if (r.status === 'available' || r.status === 'verification_failed')
          b.push(`<button class="small danger" data-ui-action="act" data-view="backups" data-action="quarantine" data-id="${esc(r.id)}">隔离</button>`);
        else if (r.status === 'quarantined')
          b.push(`<button class="small primary" data-ui-action="act" data-view="backups" data-action="unquarantine" data-id="${esc(r.id)}">解除隔离</button>`);
        // 删除走回收站，不是物理删除；回收站里的行给「还原」和「立即彻底删除」两条路
        if (r.status === 'recycle_bin') {
          b.push(`<button class="small primary" data-ui-action="act" data-view="backups" data-action="restore-recycled" data-id="${esc(r.id)}">还原</button>`);
          b.push(`<button class="small danger" data-ui-action="act" data-view="backups" data-action="purge" data-id="${esc(r.id)}">立即彻底删除</button>`);
        } else if (r.status !== 'deleted' && !r.locked) {
          b.push(`<button class="small danger" data-ui-action="act" data-view="backups" data-action="recycle" data-id="${esc(r.id)}">删除</button>`);
        }
        return b.join(' ') || '—';
      } }
];

/* 列表与归拢共用的查询串。两边的筛选口径必须一模一样：
   归拢行说「3 份」、展开只列出 2 份，从界面上分辨不出是筛选差异还是真的少了一份备份。 */
function filterQuery(st, extra = {}) {
  const q = new URLSearchParams({
    page: extra.page || st.page,
    pageSize: extra.pageSize || st.pageSize
  });
  if (st.sortKey) { q.set('sortBy', st.sortKey); q.set('sortDescending', String(st.sortDesc !== false)); }
  if (st.status) q.set('status', st.status);
  if (st.keyword) q.set('keyword', st.keyword);
  if (extra.taskId) q.set('taskId', extra.taskId);
  return q.toString();
}

LOADERS.backups = async function () {
  const st = App.state.backups;
  // 状态换到/换出回收站会连带换版式，工具条按钮的文案也要跟着变——这时只重画表格不够。
  if (syncRecycleLayout(st)) { vBackups(); return; }
  const wrap = $('#vwrap'); if (!wrap) return;
  try {
    const empty = !hasFilter(st, ['status', 'keyword'])
      ? emptyState('first', { glyph: '❏', title: '暂无备份集', sub: '备份任务成功入库后，备份集会出现在这里' })
      : emptyState('filter', { key: 'backups', title: `没有匹配「${st.keyword || L.backup_set_status[st.status] || ''}」的备份集` });

    if (st.grouped) { await renderGroups(wrap, st, empty); return; }

    const data = await api('/api/v1/admin/backups?' + filterQuery(st));
    st.totalCount = data.totalCount;
    mergeRows(st, data.items);
    wrap.innerHTML = tableHtml(BACKUP_COLUMNS, data.items, { empty, stateKey: 'backups' }) + pagerHtml('backups', st);
    refreshBatchBar();
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};

function refreshBatchBar() {
  const bb = $('#bb-backups');
  if (bb) bb.outerHTML = `<div id="bb-backups">${batchBarHtml('backups')}</div>`;
}

/* ── 按任务归拢 ──
   一个任务一行，收起来只留结论。折叠本身不是目的——
   把 18 行藏起来而不给出「18 个账套都在、都可用」这句话，
   等于把「今天备齐了没有」这个问题也一起藏了。 */
async function renderGroups(wrap, st, empty) {
  const data = await api('/api/v1/admin/backups/groups?' + filterQuery(st));
  st.totalCount = data.totalCount;
  // 筛了状态之后组头那个「N 份不可用」是句废话：筛「回收站」时可用份数恒为 0，
  // 于是每一组都顶着红字报警，而那些恰恰是人自己刚删进去的，不是故障。
  const showAlarm = !st.status;
  wrap.innerHTML = (data.items.length ? data.items.map(g => groupHtml(g, showAlarm)).join('') : empty)
    + pagerHtml('backups', st);
  refreshBatchBar();

  wrap.querySelectorAll('details.bsgroup').forEach(el => {
    el.addEventListener('toggle', () => onGroupToggle(el, st));
    // 刷新前展开着的那几组照旧展开。open 置位会自己触发 toggle，明细在那里加载。
    if (st.openTasks.includes(el.dataset.taskId)) el.open = true;
  });
}

function groupHtml(g, showAlarm = true) {
  // 「几个业务单元」摆在最前面：U8 任务里它就是账套数，
  // 而「18 个账套只备出来 14 个」在别的任何一列上都表现为「这个任务有备份」。
  const alarm = showAlarm && g.notAvailableCount
    ? `<span class="bsg-alarm">${esc(g.notAvailableCount)} 份不可用</span>`
    : '';
  const latest = g.latestBusinessTime || g.latestUploadedAt;
  return `<details class="bsgroup${showAlarm && g.notAvailableCount ? ' has-work' : ''}" data-task-id="${esc(g.taskId)}">
    <summary>
      <span class="bsg-title">${esc(g.taskName)}</span>
      <span class="bsg-sub">${esc(g.clientHostname)} · ${esc(g.applicationName)}</span>
      <span class="bsg-meta">
        ${alarm}
        <span>${esc(g.businessUnitCount)} 个业务单元</span>
        <span>${esc(g.backupSetCount)} 份备份</span>
        <span>${fmtBytes(g.totalBytes)}</span>
        <span>最新 ${latest ? esc(fmtDT(latest)) : '—'}</span>
      </span>
    </summary>
    <div class="bsg-body" data-group-body></div>
  </details>`;
}

async function onGroupToggle(el, st) {
  const taskId = el.dataset.taskId;
  const open = new Set(st.openTasks);
  if (!el.open) { open.delete(taskId); st.openTasks = [...open]; return; }
  open.add(taskId);
  st.openTasks = [...open];

  const body = el.querySelector('[data-group-body]');
  if (!body || body.dataset.loaded === '1') return;
  body.innerHTML = skeleton(3);
  try {
    // 一个任务的账套数是几十的量级，一次取完。给组里再套一层分页，
    // 会把「这一组齐了没有」重新变成一个要翻页才能回答的问题。
    const data = await api('/api/v1/admin/backups?' + filterQuery(st, { taskId, page: 1, pageSize: 200 }));
    mergeRows(st, data.items);
    body.innerHTML = tableHtml(BACKUP_COLUMNS, data.items,
      { empty: '<div class="empty">这一组里没有匹配的备份集</div>', stateKey: 'backups' })
      + (data.totalCount > data.items.length
        ? `<div class="hint">这一组共 ${esc(data.totalCount)} 份，这里只列出最近 ${esc(data.items.length)} 份。</div>`
        : '');
    body.dataset.loaded = '1';
  } catch (e) {
    body.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`;
  }
}

ACTIONS['backups:lock'] = async id => {
  formModal('锁定备份集', [
    { name: 'reason', label: '锁定原因', type: 'text', required: true },
    { name: 'expiresAt', label: '锁到期时间（留空为永久）', type: 'datetime-local' }
  ], async v => {
    await api(`/api/v1/admin/backups/${id}/lock`, { method: 'POST',
      body: { reason: v.reason, expiresAt: v.expiresAt ? new Date(v.expiresAt).toISOString() : null } });
    toast('备份集已锁定', 'ok'); LOADERS.backups();
  }, '锁定');
};
ACTIONS['backups:unlock'] = async id => {
  if (!await confirmModal('解除该备份集的锁定？')) return;
  await api(`/api/v1/admin/backups/${id}/unlock`, { method: 'POST' });
  toast('已解锁', 'ok'); LOADERS.backups();
};
ACTIONS['backups:verify'] = async id => {
  if (!await confirmModal('对该备份集发起重新校验（异步 SHA-256 复核）？')) return;
  const d = await api(`/api/v1/admin/backups/${id}/verify`, { method: 'POST' });
  toast(`校验已开始（操作 ${d.operationId ? String(d.operationId).slice(0, 8) : ''}）`, 'ok'); LOADERS.backups();
};
ACTIONS['backups:quarantine'] = async id => {
  formModal('隔离备份集', [
    { name: 'reason', label: '隔离原因', type: 'text', required: true,
      placeholder: '如：校验通过但怀疑数据被截断，待人工核实' }
  ], async v => {
    await api(`/api/v1/admin/backups/${id}/quarantine`, { method: 'POST', body: { reason: v.reason } });
    toast('备份集已隔离', 'ok'); LOADERS.backups();
  }, '隔离');
};
ACTIONS['backups:unquarantine'] = async id => {
  if (!await confirmModal('解除该备份集的隔离，恢复为可用状态？')) return;
  await api(`/api/v1/admin/backups/${id}/unquarantine`, { method: 'POST' });
  toast('已解除隔离', 'ok'); LOADERS.backups();
};
ACTIONS['backups:recycle'] = async id => {
  if (!await confirmModal('把这份备份集移进回收站？到期前都可以还原，到期后才真正从磁盘删除。')) return;
  try {
    await api(`/api/v1/admin/backups/${id}/recycle`, { method: 'POST' });
    toast('已移入回收站', 'ok'); LOADERS.backups();
  } catch (e) { errToast(e); }
};
ACTIONS['backups:restore-recycled'] = async id => {
  if (!await confirmModal('把这份备份集从回收站还原为可用？')) return;
  try {
    await api(`/api/v1/admin/backups/${id}/restore-from-recycle-bin`, { method: 'POST' });
    toast('已还原', 'ok'); LOADERS.backups();
  } catch (e) { errToast(e); }
};
ACTIONS['backups:purge'] = async id => {
  // 这一步不可撤销，所以问得直白些：它删的是磁盘上的那份文件
  if (!await confirmModal('立即彻底删除？磁盘上的备份文件会被真正删掉，删完无法找回。')) return;
  try {
    await api(`/api/v1/admin/backups/${id}/purge`, { method: 'POST' });
    toast('已彻底删除', 'ok'); LOADERS.backups();
  } catch (e) { errToast(e); }
};
/* ── 批量：删除 / 还原 / 彻底删除 ──
   逐条点 36 次不是一个可用的做法。选中行之后工具条变形出现这三个按钮，
   服务端逐条执行、逐条报账——一份被锁定不该让另外 35 份也做不成。 */

/* 调批量端点，把「哪几条没做成、为什么」说清楚。
   36 份里 5 份因为同一个原因失败，报 5 行同样的话没有意义，所以按原因归并计数。 */
async function runBackupBatch(url, ids, label) {
  let d;
  try {
    d = await api(url, { method: 'POST', body: { backupSetIds: ids } });
  } catch (e) {
    // 整个请求就没成（超出单批上限、没权限、断网）。这时外层只会说一句
    // 「失败 N」，原因得由这里说出来，否则人拿不到任何可以据以行动的信息。
    errToast(e);
    return { ok: 0, fail: ids.length };
  }
  const failed = d.failed || [];
  if (failed.length) {
    const byMsg = new Map();
    for (const f of failed) byMsg.set(f.message, (byMsg.get(f.message) || 0) + 1);
    toast(`${label}：${failed.length} 份没做成 —— `
      + [...byMsg].map(([m, n]) => `${m}（${n} 份）`).join('；'), 'err');
  }
  return { ok: d.successCount || 0, fail: failed.length };
}

/* 要求手输份数的确认框。这是这套系统里唯一一个真会让数据消失的按钮，
   一批几十份的误点代价是单条的几十倍，多这一道门槛值这个钱；
   其余批量动作（删进回收站、还原）都可撤销，保持普通确认，不堆流程。 */
function confirmTypedCount(title, bodyHtml, expected, okText) {
  return new Promise(resolve => {
    let settled = false;
    const settle = v => { if (settled) return; settled = true; resolve(v); };
    const ov = formModal(title, [
      { name: 'notice', type: 'static', html: bodyHtml },
      { name: 'typed', label: `请输入份数 ${expected} 以确认`, type: 'text', required: true, placeholder: String(expected) }
    ], async v => {
      if (String(v.typed).trim() !== String(expected))
        throw new Error(`输入的份数与选中的对不上（应为 ${expected}），没有删除任何东西`);
      settle(true);
    }, okText);
    // 关掉窗口（点空白、关闭按钮、Esc）都算取消。不落定的话调用方会永久挂起。
    ov.addEventListener('bm:dismiss', () => settle(false));
  });
}

async function confirmBatchPurge(ids) {
  const st = App.state.backups;
  const picked = (st.rows || []).filter(r => ids.includes(r.id));
  const bytes = picked.reduce((a, r) => a + (r.totalBytes || 0), 0);
  // 容量只在这一批的行都还在手上时才报——报一个算漏了的数比不报更糟。
  const size = picked.length === ids.length ? `，合计 <b>${esc(fmtBytes(bytes))}</b>` : '';
  return confirmTypedCount('彻底删除备份集',
    `<p style="line-height:1.7">选中 <b>${esc(ids.length)}</b> 份备份集${size}。<br>
     磁盘上的备份文件会被真正删掉，<b>删完无法找回</b>，也不再占用仓库空间。</p>`,
    ids.length, '彻底删除');
}

App.batchActs = App.batchActs || {};
/* allow 与单行按钮同一个判据：回收站里的行给「还原 / 彻底删除」，
   其余给「删除」，被锁定的一律不给——两处判据一旦分家，批量条上亮着的按钮
   点下去只会换来一句 409。 */
App.batchActs.backups = [
  { t: '删除（移入回收站）', danger: true, bulk: true,
    allow: r => r.status !== 'recycle_bin' && r.status !== 'deleted' && !r.locked,
    why: '所选备份集都不能删除（已在回收站/已删除，或已被锁定）',
    confirm: ids => confirmModal(`把这 ${ids.length} 份备份集移进回收站？到期前都可以还原，到期后才真正从磁盘删除。`),
    fn: ids => runBackupBatch('/api/v1/admin/backups/recycle-batch', ids, '移入回收站') },
  { t: '还原', primary: true, bulk: true,
    allow: r => r.status === 'recycle_bin',
    why: '只有回收站里的备份集才能还原',
    confirm: ids => confirmModal(`把这 ${ids.length} 份备份集从回收站还原为可用？`),
    fn: ids => runBackupBatch('/api/v1/admin/backups/restore-from-recycle-bin-batch', ids, '还原') },
  { t: '彻底删除', danger: true, bulk: true,
    allow: r => r.status === 'recycle_bin',
    why: '只有回收站里的备份集才能彻底删除（先删进回收站，再决定要不要真删）',
    confirm: ids => confirmBatchPurge(ids),
    fn: ids => runBackupBatch('/api/v1/admin/backups/purge-batch', ids, '彻底删除') }
];

ACTIONS['backups:copy-path'] = async () => {
  const path = App.state.backupDetailPath || '';
  if (!path) return;
  try {
    await navigator.clipboard.writeText(path);
    toast('路径已复制，粘到资源管理器地址栏就能打开', 'ok');
  } catch {
    // 非安全上下文（http 访问）下 clipboard API 不可用，退回选中让人自己复制
    toast('浏览器不允许自动复制，请手动选中路径复制', 'err');
  }
};
ACTIONS['backups:restore'] = async id => createRestore(id);
function createRestore(backupSetId) {
  formModal('创建恢复请求', [
    { name: 'purpose', label: '恢复用途', type: 'textarea', required: true, placeholder: '说明恢复原因，将记入审计日志' }
  ], async v => {
    const d = await api('/api/v1/admin/restore-requests', { method: 'POST',
      headers: { 'Idempotency-Key': crypto.randomUUID() },
      body: { backupSetId, purpose: v.purpose, selection: { type: 'full' } } });
    toast(`恢复请求已创建（${L.restore_status[d.status] || d.status}），校验通过后可签发下载令牌`, 'ok');
    location.hash = '#/restores';
  }, '创建');
}

export async function vBackupDetail(id) {
  $('#app').innerHTML = shell('backups', '备份集详情', loading());
  try {
    const d = await api(`/api/v1/admin/backups/${id}`);
    App.state.backupDetailPath = d.repositoryPath || '';
    $('#view').innerHTML = `
    <div class="toolbar"><a href="#/backups">← 返回备份集列表</a><div class="spacer"></div>
      ${d.status === 'available' ? `<button class="primary" data-ui-action="act" data-view="backups" data-action="restore" data-id="${esc(d.id)}">发起恢复</button>
      <button data-ui-action="act" data-view="backups" data-action="verify" data-id="${esc(d.id)}">重新校验</button>` : ''}
      ${d.locked ? `<button data-ui-action="act" data-view="backups" data-action="unlock" data-id="${esc(d.id)}">解锁</button>`
        : (d.status === 'available' ? `<button data-ui-action="act" data-view="backups" data-action="lock" data-id="${esc(d.id)}">锁定</button>` : '')}
      ${d.status === 'available' || d.status === 'verification_failed'
        ? `<button class="danger" data-ui-action="act" data-view="backups" data-action="quarantine" data-id="${esc(d.id)}">隔离</button>`
        : (d.status === 'quarantined' ? `<button class="primary" data-ui-action="act" data-view="backups" data-action="unquarantine" data-id="${esc(d.id)}">解除隔离</button>` : '')}
      ${d.status === 'recycle_bin'
        ? `<button class="primary" data-ui-action="act" data-view="backups" data-action="restore-recycled" data-id="${esc(d.id)}">还原</button>
           <button class="danger" data-ui-action="act" data-view="backups" data-action="purge" data-id="${esc(d.id)}">立即彻底删除</button>`
        : (d.status !== 'deleted' && !d.locked
          ? `<button class="danger" data-ui-action="act" data-view="backups" data-action="recycle" data-id="${esc(d.id)}">删除</button>`
          : '')}
    </div>
    <div class="card"><b class="mono">${esc(d.backupSetCode)}</b> ${status('backup_set_status', d.status)}
      ${d.locked ? '<span class="status status--wait pill">已锁定</span>' : ''}
      <div class="kv" style="margin-top:12px">
        <div class="row"><div class="k">客户端 / 任务</div><div class="v">${esc(d.clientHostname)} / ${esc(d.taskName)}</div></div>
        <div class="row"><div class="k">应用</div><div class="v">${esc(d.applicationName)}</div></div>
        <div class="row"><div class="k">业务时间</div><div class="v">${fmtDT(d.backupBusinessTime)}</div></div>
        <div class="row"><div class="k">上传时间</div><div class="v">${fmtDT(d.uploadedAt)}</div></div>
        <div class="row"><div class="k">规模</div><div class="v">${esc(d.totalFiles)} 个文件 · ${fmtBytes(d.totalBytes)}</div></div>
        <div class="row"><div class="k">校验时间</div><div class="v">${fmtDT(d.verifiedAt)}</div></div>
        <!-- 浏览器打不开服务器上的资源管理器，也点不开 file:// 链接（Chrome/Edge 一律拦截）。
             能做的是把路径原样交到手里：复制出来粘到资源管理器地址栏就行；
             把仓库根做成只读 SMB 共享之后，这个路径直接就是 \\服务器\repo\… 的形式。 -->
        <div class="row"><div class="k">仓库路径</div><div class="v mono">${esc(d.repositoryPath || '—')}
          ${d.repositoryPath ? ' <button class="small" data-ui-action="act" data-view="backups" data-action="copy-path">复制路径</button>' : ''}</div></div>
        <div class="row"><div class="k">清单 SHA-256</div><div class="v mono">${esc(d.manifestSha256 || '—')}</div></div>
        <div class="row"><div class="k">保留至</div><div class="v">${fmtDT(d.retentionUntil)}</div></div>
      </div></div>
    ${d.retentionLocks.length ? `<div class="card"><b>保留锁</b>${tableHtml([
      { l: '原因', k: 'reason' }, { l: '锁定人', k: 'lockedByName' }, { l: '锁定时间', render: r => fmtDT(r.lockedAt) },
      { l: '到期', render: r => fmtDT(r.expiresAt) }, { l: '有效', render: r => r.active ? '是' : '否' }
    ], d.retentionLocks)}</div>` : ''}
    <div class="card"><b>文件明细（${esc(d.files.length)}）</b>${tableHtml([
      { l: '相对路径', render: r => `<span class="mono" style="font-size:12px">${esc(r.relativePath)}</span>` },
      { l: '大小', num: true, render: r => fmtBytes(r.sizeBytes) }, { l: '修改时间', render: r => fmtDT(r.lastModifiedAt) },
      { l: 'SHA-256', render: r => `<span class="mono" style="font-size:11px">${esc((r.sha256 || '').slice(0, 16))}…</span>` },
      { l: '校验', render: r => status('result', r.verificationStatus) }
    ], d.files, { empty: emptyState('first', { title: '无文件记录' }) })}</div>`;
  } catch (e) { $('#view').innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
}

