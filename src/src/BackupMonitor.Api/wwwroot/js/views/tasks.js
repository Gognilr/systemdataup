/* js/views/tasks.js —— 备份任务列表 / 表单 / 抽屉详情 */
import { api } from '../api.js';
import { App, ACTIONS, LOADERS } from '../state.js';
import {
  $, esc, L, optsOf, status, fmtBytes, fmtDT, relTime, prettyJson,
  tableHtml, pagerHtml, skeleton, emptyState, hasFilter, batchBarHtml,
  toast, errToast, confirmModal, formModal, openDrawer, openModal, closeModal, clientLabel, clientName,
  describeCron, searchPickerHtml, initSearchPicker
} from '../ui.js';
import { shell, loading, syncInFlightButtons } from '../app.js';
import { runDirectoryWizard } from './recognizer-wizard.js';
// C11：业务单元清单抽成共享模块，任务详情与执行详情同样要用它。
import {
  passedCandidatesOf, unitQueueHtml, progressOf
} from './unit-roster.js';

export async function vTasks() {
  App.state.tasks = App.state.tasks || { page: 1, pageSize: 20, mode: '', totalCount: 0, selected: [] , sortKey: 'createdAt', sortDesc: true, forceFullHash: false };
  const st = App.state.tasks;
  $('#app').innerHTML = shell('tasks', '备份任务', `
    <div class="toolbar">
      <select id="f_mode"><option value="">全部模式</option>${optsOf(L.task_mode).map(o => `<option value="${o.v}" ${st.mode === o.v ? 'selected' : ''}>${o.t}</option>`).join('')}</select>
      <button class="primary" data-ui-action="loader" data-loader="tasks">查询</button>
      <label class="sub" style="display:inline-flex;align-items:center;gap:4px" title="不比对快速指纹，把每个文件整读一遍重算 SHA-256。备份没变化时会慢很多，平时不需要勾。">
        <input type="checkbox" id="f_force_full_hash" ${st.forceFullHash ? 'checked' : ''}>强制完整校验
      </label>
      <div class="spacer"></div>
      <button class="primary" data-ui-action="act" data-view="tasks" data-action="create">＋ 新建任务</button>
    </div>
    <div id="bb-tasks">${batchBarHtml('tasks')}</div>
    <div id="vwrap">${loading()}</div>`);
  $('#f_mode').onchange = () => { st.mode = $('#f_mode').value; st.page = 1; LOADERS.tasks(); };
  // 「强制完整校验」是这一页的一个开关，不是每行一个勾选框——它极少被用到，
  // 给每个任务都摆一个只会把操作列挤满。勾上之后这一页的「立即备份」都走完整校验。
  $('#f_force_full_hash').onchange = e => { st.forceFullHash = e.target.checked; };
  await LOADERS.tasks();
}

LOADERS.tasks = async function () {
  const st = App.state.tasks;
  const wrap = $('#vwrap'); if (!wrap) return;
  try {
    const q = new URLSearchParams({ page: st.page, pageSize: st.pageSize });
    if (st.sortKey) { q.set('sortBy', st.sortKey); q.set('sortDescending', String(st.sortDesc !== false)); }
    if (st.mode) q.set('taskMode', st.mode);
    const data = await api('/api/v1/admin/backup-tasks?' + q);
    st.totalCount = data.totalCount;
    const empty = !hasFilter(st, ['mode'])
      ? emptyState('first', { glyph: '⧉', title: '还没有备份任务', sub: '创建任务后，Agent 将按计划扫描并采集备份产物', actHtml: '<button class="primary" data-ui-action="act" data-view="tasks" data-action="create">＋ 新建任务</button>' })
      : emptyState('filter', { key: 'tasks', title: `没有匹配「${L.task_mode[st.mode] || ''}」的任务` });
    wrap.innerHTML = tableHtml([
      { l: '任务名', k: 'name', sort: true, render: r => `<b>${esc(r.name)}</b><span class="sub">${esc(r.applicationName)}</span>` },
      { l: '客户端', k: 'clientHostname', render: r => `${esc(clientName({ displayName: r.clientDisplayName, hostname: r.clientHostname }))}<span class="sub mono">${esc(r.clientHostname)}</span>` },
      { l: '源路径', render: r => `<span class="mono" style="font-size:12px">${esc(r.sourcePath)}</span>` },
      { l: '识别器', render: r => esc(L.recognizer[r.recognizerType] || r.recognizerType) },
      { l: '模式', k: 'taskMode', sort: true, render: r => status('task_mode', r.taskMode) },
      { l: '启用', k: 'enabled', sort: true, render: r => r.enabled ? '是' : '否' },
      { l: '重要级', render: r => esc(L.importance[r.importanceLevel] || r.importanceLevel) },
      { l: '上次检查结果', render: r => r.lastPrecheckStatus ? status('precheck', r.lastPrecheckStatus) : '—' },
      { l: '最近成功', k: 'lastSuccessAt', sort: true, render: r => relTime(r.lastSuccessAt) },
      { l: '操作', render: r => {
        const b = [`<button class="small" data-ui-action="act" data-view="tasks" data-action="detail" data-id="${esc(r.id)}">详情</button>`,
          `<button class="small" data-ui-action="act" data-view="tasks" data-action="edit" data-id="${esc(r.id)}">编辑</button>`];
        b.push(r.taskMode === 'paused'
          ? `<button class="small primary" data-ui-action="act" data-view="tasks" data-action="resume" data-id="${esc(r.id)}">恢复</button>`
          : `<button class="small" data-ui-action="act" data-view="tasks" data-action="pause" data-id="${esc(r.id)}">暂停</button>`);
        b.push(`<button class="small" data-ui-action="act" data-view="tasks" data-action="test-recognition" data-id="${esc(r.id)}">看看备份什么</button>`);
        b.push(`<button class="small primary" data-ui-action="act" data-view="tasks" data-action="backup-now" data-id="${esc(r.id)}">立即备份</button>`);
        b.push(`<button class="small danger" data-ui-action="act" data-view="tasks" data-action="del" data-id="${esc(r.id)}">删除</button>`);
        return b.join(' ');
      } }
    ], data.items, { empty, stateKey: 'tasks' }) + pagerHtml('tasks', st);
    const bb = $('#bb-tasks');
    if (bb) bb.outerHTML = `<div id="bb-tasks">${batchBarHtml('tasks')}</div>`;
    // 表格是刚重画出来的，正在跑的「立即备份」得重新锁上（见 syncInFlightButtons）。
    syncInFlightButtons(wrap);
  } catch (e) { wrap.innerHTML = `<div class="empty">加载失败：${esc(e.message)}</div>`; }
};

App.batchActs = App.batchActs || {};
App.batchActs.tasks = [
  // bulk：一次请求带上所有选中的任务，服务端建一次执行按并发度放行。
  // 逐个下发的老写法等于十个任务同时开传，服务端暂存盘扛不住——那正是这一条要消灭的行为。
  { t: '立即备份', bulk: true, fn: async ids => {
    const r = await api('/api/v1/admin/backup-tasks/batch-precheck', {
      method: 'POST',
      // 不带 forceFullHash：批量走执行队列，队列项上没地方存指令参数。
      // 「强制完整校验」是单个任务的逃生门，要用就一个一个点。
      body: { taskIds: ids }
    });
    const skipped = r.skippedTasks ? `，${r.skippedTasks} 个跳过（停用/暂停/客户端不可用）` : '';
    toast(`已排队 ${r.queuedTasks} 个任务，同时最多跑 ${r.maxConcurrent} 个${skipped}。进度在「传输中」页面。`, 'ok');
    return true;
  } },
  { t: '暂停', fn: id => api(`/api/v1/admin/backup-tasks/${id}/pause`, { method: 'POST' }) },
  { t: '恢复', fn: id => api(`/api/v1/admin/backup-tasks/${id}/resume`, { method: 'POST' }) }
];

/* ── §6.x 建任务模板选择 ──
   建任务真正难的不是字段多，而是「识别器类型」和那段 JSON 要求填表人先理解
   系统内部是怎么找备份的。可这件事他本来就知道——他知道自己的备份长什么样。
   所以第一步不问识别器，问结构；识别器和识别规则由模板填好，多数人不必再看见它们。 */
const TASK_TEMPLATES = [
  {
    id: 'multi_file_set',
    title: '一个目录里有几个文件，凑齐才算一次备份',
    detail: '例：SQL Server 的 .bak 加 .trn；缺任何一个都判为不完整',
    recognizerType: 'multi_file_set',
    recognizerConfig: { includePatterns: ['*.bak', '*.trn'], requiredFiles: ['*.bak'], recursive: false }
  },
  {
    id: 'latest_directory',
    title: '每次备份新建一个目录',
    detail: '例：源目录下是 20260824、20260823 这样的日期目录，只关心最新那个',
    recognizerType: 'latest_directory',
    recognizerConfig: {}
  },
  {
    id: 'latest_single_file',
    title: '目录里堆着历次备份文件，只看最新的那个',
    detail: '例：每天往同一个目录里丢一个 .bak，文件名带日期',
    recognizerType: 'latest_single_file',
    recognizerConfig: {}
  },
  {
    id: 'grouped_latest_set',
    title: '目录里堆着历次备份，每次备份是一组同名文件',
    detail: '例：致远 OA 的 2026-08-27@02_00.zip 加同名 .properties；只取日期最新的那一组',
    recognizerType: 'multi_file_set',
    recognizerConfig: { requiredFiles: ['*.zip', '*.properties'], groupBy: 'basename', recursive: false }
  },
  {
    id: 'subdirectory_units',
    title: '每个子目录是一个独立的库 / 账套',
    detail: '例：源目录下是 账套001、账套002，每个子目录自成一份备份',
    recognizerType: 'subdirectory_units',
    recognizerConfig: { businessUnitDepth: 1 }
  },
  {
    id: 'u8_units_daily',
    title: '每个账套下，每天再建一个备份目录',
    detail: '用友 U8 的常见结构：账套001\\20260824\\…；账套是业务单元，每天的目录是一个版本',
    recognizerType: 'subdirectory_units',
    recognizerConfig: { businessUnitDepth: 1, unitLayout: 'latest_directory' }
  },
  {
    id: 'custom',
    title: '以上都不是 / 我自己配',
    detail: '直接进入完整表单，识别器和各项规则逐个填',
    recognizerType: 'multi_file_set',
    recognizerConfig: {}
  }
];

/* 返回选中的模板；用户关闭对话框则返回 null。 */
function chooseTaskTemplate() {
  return new Promise(resolve => {
    const body = `
      <p class="hint" style="margin-top:0">先说明你的备份在客户端上是怎么摆放的，识别规则会据此填好。</p>
      <div class="tpl-list">
        ${TASK_TEMPLATES.map((t, i) => `
          <label class="tpl-item">
            <input type="radio" name="tpl" value="${esc(t.id)}" ${i === 0 ? 'checked' : ''}>
            <span class="tpl-copy"><strong>${esc(t.title)}</strong><small>${esc(t.detail)}</small></span>
          </label>`).join('')}
      </div>`;
    const ov = openModal('新建备份任务 · 你的备份长什么样？', body, { okText: '下一步' });
    let settled = false;
    const finish = value => { if (!settled) { settled = true; resolve(value); } };
    ov.addEventListener('bm:dismiss', () => finish(null));
    ov.addEventListener('click', ev => {
      if (ev.target === ov || ev.target.closest('[data-close]')) setTimeout(() => finish(null), 0);
    });
    ov.querySelector('[data-ok]').addEventListener('click', () => {
      const picked = ov.querySelector('input[name="tpl"]:checked');
      finish(TASK_TEMPLATES.find(t => t.id === (picked && picked.value)) || null);
      closeModal();
    });
  });
}

/* ── 识别规则：从 JSON 文本框改成结构化字段 ──
   那段 JSON 要求填表人先理解系统内部是怎么找备份的，而且拼错一个键名不会报错
   ——服务端只校验「是不是合法 JSON」，规则会静默失效。
   这里把每个键拆成一个普通输入框，JSON 退成只读预览：会看的人能核对，不会看的人不用碰。 */

/* 详情页把识别规则说成人话。JSON 收进折叠区留给要核对的人。
   「必需文件：UFDATA.BAK、UfErpAct.Lst」这一行，是运维判断这个任务配得对不对
   最常需要看的一件事，不该躲在一段 JSON 里。 */
function describeRecognizerConfig(json) {
  const cfg = parseRecognizerConfig(json);
  const rows = [];
  rows.push(['必需文件', cfg.required.length ? cfg.required.join('、') : '不检查（有文件就算一份备份）']);
  if (cfg.include.length) rows.push(['只看这些文件', cfg.include.join('、')]);
  if (cfg.exclude.length) rows.push(['忽略这些文件', cfg.exclude.join('、')]);
  if (cfg.excludeDirs.length) rows.push(['忽略这些子目录', cfg.excludeDirs.join('、')]);
  if (cfg.unitLayout) rows.push(['单元内结构', UNIT_LAYOUT_TEXT[cfg.unitLayout]]);
  // date_leaf 是按结构自动认的，固定层数对它不起作用（BusinessUnitResolver 里它排在
  // businessUnitDepth 前面），照报会让人以为层数还在管事。
  if (cfg.unitLayout !== 'date_leaf' && cfg.depth > 1) rows.push(['业务单元层级', `第 ${cfg.depth} 层`]);
  if (!cfg.recursive) rows.push(['子目录', '不深入子目录']);
  if (cfg.batchRegex) rows.push(['目录名筛选', cfg.batchRegex]);
  if (cfg.groupBy === 'basename') rows.push(['目录里的历次备份', '每次备份是一组同名文件，只取最新的那一组']);
  else if (cfg.groupBy) rows.push(['分组键（正则）', cfg.groupBy]);

  return `<div class="kv">${rows.map(([k, v]) =>
    `<div class="row"><div class="k">${esc(k)}</div><div class="v">${esc(v)}</div></div>`).join('')}</div>`;
}

/* 一行一个（也容忍逗号分隔，中英文逗号都认）。 */
const cfgLines = v => String(v || '').split(/[\n,，]/).map(x => x.trim()).filter(Boolean);

/* 「单元内结构」的两种取值，与 BusinessUnitResolver 的找法一一对应：
   latest_directory —— 按固定层数找到单元，单元下面每次备份再建一个目录；
   date_leaf        —— 单元不都在同一层，按「下面是日期目录」这个结构自己认。

   下拉里必须两个都在。只认 latest_directory 的话，推断给出的 date_leaf 在表单里
   解析成「没配」，保存一次就被抹掉，整个账套目录退回「一份备份」——用友 U8 那种
   层级不齐的结构上，16 个账套会被并成一份，目录里堆着的几天文件每次全量重传。 */
const UNIT_LAYOUT_TEXT = {
  latest_directory: '每个单元下面每次备份再建一个目录，只取最新的那个',
  date_leaf: '单元不都在同一层，按「下面是日期目录」自动认'
};
const normalizeUnitLayout = value => {
  const key = String(value || '').trim().toLowerCase();
  return Object.prototype.hasOwnProperty.call(UNIT_LAYOUT_TEXT, key) ? key : '';
};

/* 识别规则 JSON → 结构化字段值。别名键（includes / required / excludeDirs …）一并认，
   写回时统一成规范键名。 */
function parseRecognizerConfig(json) {
  let cfg = {};
  try { cfg = JSON.parse(json || '{}') || {}; } catch (e) { cfg = {}; }
  const list = (...keys) => {
    for (const key of keys) {
      const value = cfg[key];
      if (typeof value === 'string' && value.trim()) return [value.trim()];
      if (Array.isArray(value)) return value.filter(x => typeof x === 'string' && x.trim()).map(x => x.trim());
    }
    return [];
  };
  return {
    required: list('requiredFiles', 'requiredPatterns', 'required'),
    include: list('includePatterns', 'includes'),
    exclude: list('excludePatterns', 'excludes'),
    excludeDirs: list('excludeDirectories', 'excludeDirs'),
    recursive: cfg.recursive !== false,
    depth: Number.isInteger(cfg.businessUnitDepth) && cfg.businessUnitDepth > 0 ? cfg.businessUnitDepth : 1,
    unitLayout: normalizeUnitLayout(cfg.unitLayout),
    batchRegex: typeof cfg.batchRegex === 'string' ? cfg.batchRegex : '',
    groupBy: typeof cfg.groupBy === 'string' ? cfg.groupBy.trim() : ''
  };
}

/* 结构化字段值 → 识别规则 JSON。
   在 baseConfig 上**增量覆盖**，不是从零重拼一份。

   从零重拼要求「表单认得的键」与「系统认得的键」永远一致，而这两份清单一分叉
   就是一次静默的配置丢失：表单不认识的键在保存的那一刻被删掉，界面上没有任何痕迹。
   已经这么丢过的有 unitLayout: date_leaf（层级不齐的账套被并成一份备份）、
   unitMaxDepth（向导预演与 Agent 真扫的下探层数不再是同一个）、
   businessUnitPaths（人手指的单元被无视）、volumeContinuity（分卷连续性检查静默
   关掉，缺第 2 卷仍判「齐了」）。而且丢在**新建**任务那一刻——向导的推断结果是
   当作 prefill 走同一张表单的，不是直接落库。

   所以这里只碰表单真正管得到的那几个键，其余原样留着：以后推断再加新键也不会被抹掉。 */
function composeRecognizerConfig(vals, baseConfig) {
  let cfg = {};
  try { cfg = JSON.parse(baseConfig || '{}') || {}; } catch (e) { cfg = {}; }

  /* 有值就写规范键名，没值就删；别名键一律清掉——规范键与别名同时在场时两份都会生效，
     含义不清（与 applyRequiredFiles 里那条规矩一致）。 */
  const put = (value, key, ...aliases) => {
    for (const alias of aliases) delete cfg[alias];
    if (value === undefined) delete cfg[key];
    else cfg[key] = value;
  };
  const lines = value => { const list = cfgLines(value); return list.length ? list : undefined; };

  put(lines(vals.cfgRequired), 'requiredFiles', 'requiredPatterns', 'required');
  put(lines(vals.cfgInclude), 'includePatterns', 'includes');
  put(lines(vals.cfgExclude), 'excludePatterns', 'excludes');
  put(lines(vals.cfgExcludeDirs), 'excludeDirectories', 'excludeDirs');
  put(vals.cfgRecursive === false ? false : undefined, 'recursive');
  put(vals.cfgBatchRegex || undefined, 'batchRegex');
  put(vals.cfgGroupBy || undefined, 'groupBy');

  if (vals.recognizerType === 'subdirectory_units') {
    const layout = normalizeUnitLayout(vals.cfgUnitLayout);
    const depth = parseInt(vals.cfgDepth, 10);
    put(layout || undefined, 'unitLayout');
    // date_leaf 自己按结构找单元，固定层数对它不起作用；一并写出来只会让人误读。
    put(layout === 'date_leaf' ? undefined : (depth > 0 ? depth : 1), 'businessUnitDepth');
  } else {
    // 识别器换掉之后这两个键不再有意义；留着的话，下次换回子目录单元会冒出一个没人设过的值。
    put(undefined, 'unitLayout');
    put(undefined, 'businessUnitDepth');
  }

  return JSON.stringify(cfg, null, 2);
}

/* ── 保存之后把服务端真正存下来的识别规则读回来核对一遍 ──

   要防的不是某一个具体的写错，而是这类错误的**性质**：识别规则没存对不会报错、
   不会告警、界面上一切正常，一直到某天真要恢复数据才发现。此前 unitLayout 在保存时
   被丢掉，从配好那天起没有任何一处显示不对，是靠人去翻仓库目录、觉得「看不明白」
   才偶然发现的——中间隔了整整一个月，而这一个月里那个任务每天都判「成功」。

   所以这里不假设写入路径都是对的，而是每次保存都当场核对：提交了什么、存下来什么。
   这条防线不依赖「下次谁记得把新键也写进表单」，新增写入路径时照样管用。 */

/* 比解析后的键值，不比 JSON 文本。服务端是原样落库、理论上逐字相同，但拿文本比
   会让将来任何一次格式化都变成假警报——而假警报会训练人忽略这条提示，
   那时它就等于不存在。 */
function diffRecognizerConfig(sent, stored) {
  const parse = json => { try { return JSON.parse(json || '{}') || {}; } catch (e) { return null; } };
  const before = parse(sent);
  const after = parse(stored);
  if (after === null) return ['整段配置没能存成合法 JSON'];
  if (before === null) return [];  // 提交的就不是合法 JSON：那是别处的毛病，这里不越权报

  const problems = [];
  for (const key of Object.keys(before)) {
    if (!(key in after)) { problems.push(`${key}：提交了，但没有存下来`); continue; }
    const from = JSON.stringify(before[key]);
    const to = JSON.stringify(after[key]);
    if (from !== to) problems.push(`${key}：提交的是 ${from}，存下来的是 ${to}`);
  }
  for (const key of Object.keys(after)) {
    if (!(key in before)) problems.push(`${key}：没提交过，却凭空多出来 ${JSON.stringify(after[key])}`);
  }
  return problems;
}

/* 用弹窗而不是 toast：toast 几秒后自己消失，而这条消息的含义是「你以为配好的东西
   没配上」，错过一次就又回到静默失效。persistent 是同一个理由——点到空白处
   不该等于把它读过了。关闭仍有「知道了」和 Esc 两条明路。 */
function acknowledgeModal(title, bodyHtml) {
  return new Promise(resolve => {
    const ov = openModal(title, bodyHtml, { okText: '知道了', persistent: true });
    let settled = false;
    const settle = () => {
      if (settled) return;
      settled = true;
      ov.removeEventListener('bm:dismiss', settle);
      resolve();
    };
    ov.addEventListener('bm:dismiss', settle);
    ov.querySelector('[data-ok]').addEventListener('click', () => { settle(); closeModal(ov); });
  });
}

/* stored 为 undefined 表示这次的响应里压根没带识别规则（核对不了），不是「存丢了」，
   静默跳过；空字符串和 '{}' 都是真值，照常参与比对。 */
async function verifyStoredRecognizerConfig(sent, stored) {
  if (stored === undefined || stored === null) return;
  const problems = diffRecognizerConfig(sent, stored);
  if (problems.length === 0) return;

  await acknowledgeModal('识别规则没有完整保存', `
    <p style="line-height:1.7">任务本身已经保存，但服务端存下来的识别规则与刚提交的<b>不一致</b>。
    在查清楚之前，这个任务的识别规则不能当作配好了——它可能会认错「一份备份」是什么。</p>
    <ul style="line-height:1.8">${problems.map(p => `<li>${esc(p)}</li>`).join('')}</ul>
    <div class="hint">这通常意味着界面上有一项配置在保存的过程中被丢掉了。
    请把上面这几行原样交给维护者，并在修好之前先用「详情 → 原始配置（JSON）」核对实际生效的规则。</div>`);
}

async function taskFormFields(initial, template, prefill, client) {
  const policies = await api('/api/v1/admin/retention-policies');
  // 留空（NULL）现在的含义是「跟随默认策略」，不再是「永不清理」——清理器会自己回落到
  // 默认策略。选它的好处是：以后在保留策略页换一份默认策略，这个任务跟着一起变；
  // 挑具体某一份则是把规则钉死在这个任务上。当前默认是哪份要写在选项里，
  // 不然「跟随默认」等于让人凭空猜自己的备份会留多久。
  const defaultPolicy = (policies || []).find(p => p.isDefault);
  const policyOpts = [{
    v: '',
    t: defaultPolicy ? `跟随默认（当前：${defaultPolicy.name}）` : '跟随默认（尚未配置默认策略 —— 永不清理）'
  }].concat((policies || []).map(p => ({ v: p.id, t: p.isDefault ? `${p.name}（默认）` : p.name })));

  // 取值优先级：编辑已有任务 → 库里的值；新建 → 向导推断结果 → 模板默认值。
  const v = initial || prefill || (template
    ? { recognizerType: template.recognizerType, recognizerConfig: JSON.stringify(template.recognizerConfig, null, 2) }
    : {});
  const cfg = parseRecognizerConfig(v.recognizerConfig);
  const recognizerType = v.recognizerType || 'latest_directory';

  // 暂停中的任务：后端不接受把模式改成 paused（要走暂停/恢复接口），下拉里也没有这一项。
  // 直接回填 'paused' 会让浏览器落到列表第一项上，保存后任务被静默恢复且模式还被改掉。
  // 这里显式回填「暂停前的模式」，并把保存即恢复这件事写在提示里。
  const isPaused = v.taskMode === 'paused';
  const taskModeValue = isPaused ? (v.previousTaskMode || 'automatic') : (v.taskMode || 'automatic');
  const pausedHint = isPaused
    ? '任务当前处于暂停状态。保存表单会按这里选中的模式让它恢复运行；想继续暂停就直接关掉表单。'
    : '';

  // 保留手写进 JSON 的自定义正则：下拉里没有它的话，一次保存就把它抹掉了。
  const groupByOpts = [
    { v: '', t: '不分组（这个目录里就是一份备份）' },
    { v: 'basename', t: '只取最新的一组（同名文件算一组）' }
  ];
  if (cfg.groupBy && cfg.groupBy !== 'basename')
    groupByOpts.push({ v: cfg.groupBy, t: `自定义正则：${cfg.groupBy}` });

  const unitLayoutOpts = [
    { v: '', t: '单元目录本身就是一份备份' },
    { v: 'latest_directory', t: UNIT_LAYOUT_TEXT.latest_directory },
    { v: 'date_leaf', t: UNIT_LAYOUT_TEXT.date_leaf }
  ];

  const fields = [];
  // 建任务只有四件事必须由人决定：哪台机器、叫什么、备份的是什么应用、备份文件落在哪个目录。
  // 其余全部有可用默认值，收进「高级选项」——铺开十几个输入框会把「必须填什么」淹掉。
  if (client) {
    fields.push({
      name: 'clientStatic', type: 'static', label: '客户端',
      html: `<div class="dirpath">${esc(clientLabel(client))}</div>`,
      hint: '任务在这台机器上执行'
    });
  }
  fields.push(
    { name: 'name', label: '任务名称', type: 'text', value: v.name, required: true, placeholder: '如 财务库每日全备' },
    { name: 'applicationName', label: '应用名称', type: 'text', value: v.applicationName, required: true, placeholder: '如 SQLServer / Oracle / FileSet' },
    { name: 'sourcePath', label: '源路径', type: 'text', value: v.sourcePath, required: true,
      placeholder: 'D:\\backup\\finance', hint: '客户端机器上的路径，不是服务端上的路径。支持 * 通配，多个匹配时取最新的一个，例如 D:\\backup\\*\\* —— 按年/月分层的备份可以这样跨月自动跟随。' },
    { name: 'recognizerType', label: '识别器类型', type: 'select', value: recognizerType, required: true,
      options: optsOf(L.recognizer), hint: '源路径下如何认出「一份备份」' },
    // 必需文件不进高级选项：它决定「这份备份算不算完整」，是识别规则里唯一
    // 每个使用者都该看见并有能力回答的问题。
    { name: 'cfgRequired', label: '必需文件', type: 'textarea', rows: 3, value: cfg.required.join('\n'),
      placeholder: 'UFDATA.BAK\nUfErpAct.Lst',
      hint: '一行一个。每次备份必须包含这些文件，缺任何一个就判为不完整。留空表示不检查。支持 * 通配，大小写不敏感' },
    // 分组不进高级选项，理由和必需文件一样：一个目录里堆着一个月的备份时，
    // 配不配它决定的是「每天传最新的 6 GB」还是「每天把 64 GB 全量重传一遍」，
    // 而且不配的那一份候选压根不代表某一天的备份。这个问题使用者自己答得上来。
    { name: 'cfgGroupBy', label: '目录里的历次备份', type: 'select', value: cfg.groupBy, options: groupByOpts,
      hint: '同一个目录里堆着多次备份时用。「只取最新的一组」按去掉扩展名的文件名归组（NAME.zip 与 NAME.properties 算同一组），取时间最新的那一组' },
    // 任务模式收进高级选项：默认「自动」已经是绝大多数人要的行为——扫到就传，不需要人管。
    // 它此前摆在基本项里，默认值又是「需审批」，等于每建一个任务都要求使用者先理解一套
    // 他多半不需要的审批流程；而审批本身在管理端没有入口，选错了任务就静默不上传。
    { name: 'taskMode', label: '任务模式', type: 'select', value: taskModeValue,
      options: optsOf(L.task_mode).filter(o => o.v !== 'paused'), advanced: true,
      hint: pausedHint || '自动：扫到就传走存好（默认，不需要人管）；仅监控：只检查、发现问题告警，但不上传；需审批 / 手动：每一份都要人在这里点一次「立即备份」才会传' },
    { name: 'importanceLevel', label: '重要级', type: 'select', value: v.importanceLevel || 'normal', options: optsOf(L.importance), advanced: true },
    { name: 'enabled', label: '状态', labelText: '启用该任务', type: 'checkbox', value: v.enabled !== false, advanced: true },
    { name: 'priority', label: '优先级', type: 'number', value: v.priority ?? 100, hint: '数值小的优先', advanced: true },
    // 任务进了备份计划之后，它自己的扫描计划就不再下发给客户端（服务端会把它置空下发）。
    // 两者都留着的话，同一个任务一天会跑两次：一次 Agent 按 cron 触发，一次计划驱动。
    // 因此这里不再给一个可以填、填了却不生效的输入框，而是直说由谁驱动。
    v.planName
      ? { name: 'scanSchedule', type: 'static', label: '扫描计划',
          html: `<div class="dirpath">由备份计划「${esc(v.planName)}」驱动</div>`,
          hint: '要改执行时间，去「备份计划」页改那个计划；把任务移出计划后，这里的扫描计划会重新生效' }
      : { name: 'scanSchedule', label: '扫描计划', type: 'cron', value: v.scanSchedule || '', advanced: true },
    // 时刻用选择器而不是让人敲 HH:mm：敲错的代价是保存被拒（还算好的），
    // 或者敲成一个合法但不是他要的时刻（22:0 到底是 22:00 还是 22:10）。
    { name: 'uploadWindowStart', label: '上传窗口开始', type: 'time', value: v.uploadWindowStart || '', advanced: true,
      hint: '留空表示不限制上传时段。跨天窗口（如 22:00 到次日 06:00）直接这么填就行' },
    { name: 'uploadWindowEnd', label: '上传窗口结束', type: 'time', value: v.uploadWindowEnd || '', advanced: true },
    { name: 'retentionPolicyId', label: '保留策略', type: 'select',
      value: v.retentionPolicyId || '',
      options: policyOpts, advanced: true,
      hint: '决定这个任务的旧备份什么时候被清掉。保持「跟随默认」的话，以后在保留策略页换默认策略，这个任务会一起跟着变'
        + (defaultPolicy ? '' : '。当前系统还没有默认策略，此时「跟随默认」等于不清理，请先去保留策略页设一份') },
    // A3：大小异常判定的三个阈值，此前只在详情页展示、表单里从没有过输入项——
    // 界面上写着「大小限制 1 GB ~ 50 GB」，但没有任何地方能把它填进去。
    { name: 'minTotalBytes', label: '总大小下限', type: 'bytes', value: v.minTotalBytes ?? '', advanced: true,
      hint: '低于此值判为大小异常，留空表示不检查' },
    { name: 'maxTotalBytes', label: '总大小上限', type: 'bytes', value: v.maxTotalBytes ?? '', advanced: true,
      hint: '高于此值判为大小异常。备份通常会随业务增长，填得太紧会周期性误报' },
    { name: 'minFileCount', label: '文件数下限', type: 'number', value: v.minFileCount ?? '', advanced: true,
      hint: '备份文件数少于此值判为大小异常，留空表示不检查' },
    // B3：随机延迟此前是个死字段——实体有、表单没有输入项——用来错开多台机器同一时刻扫描/上传。
    { name: 'randomDelayMinutes', label: '随机延迟（分钟）', type: 'number', value: v.randomDelayMinutes ?? 0, advanced: true,
      hint: '0~1440 分钟，用来错开多台机器同时扫描，避免 cron 都写 0 2 * * * 时集中打满带宽' },
    { name: 'cfgInclude', label: '只看哪些文件', type: 'textarea', rows: 2, value: cfg.include.join('\n'), advanced: true,
      placeholder: '*.bak', hint: '一行一个。留空表示目录里的文件都算' },
    { name: 'cfgExclude', label: '忽略哪些文件', type: 'textarea', rows: 2, value: cfg.exclude.join('\n'), advanced: true,
      placeholder: '*.log', hint: '一行一个。排除优先于上面两项' },
    { name: 'cfgExcludeDirs', label: '忽略哪些子目录', type: 'textarea', rows: 2, value: cfg.excludeDirs.join('\n'), advanced: true,
      placeholder: 'temp', hint: '一行一个，按目录名匹配（不是路径）' },
    { name: 'cfgRecursive', label: '子目录', labelText: '深入子目录查找文件', type: 'checkbox', value: cfg.recursive, advanced: true },
    { name: 'cfgDepth', label: '业务单元在第几层', type: 'number', value: cfg.depth, advanced: true,
      hint: 'F:\\autobak\\ZT001 是第 1 层，F:\\autobak\\华东\\ZT001 是第 2 层。仅识别器为「子目录单元」时生效' },
    { name: 'cfgUnitLayout', label: '单元内结构', type: 'select', value: cfg.unitLayout, options: unitLayoutOpts, advanced: true,
      hint: '例如 ZT001\\20260825\\…。账套不都在同一层时选「自动认」——'
            + '源目录下既有 ZT001，又有 ZT201-ZT216\\ZT201 这种套了一层的，就是这一种。'
            + '仅识别器为「子目录单元」时生效' },
    { name: 'cfgBatchRegex', label: '目录名筛选（正则）', type: 'text', value: cfg.batchRegex, advanced: true,
      placeholder: '^\\d{8}$', hint: '只处理名字匹配该正则的目录。留空表示不筛选' },
    { name: 'cfgPreview', type: 'static', label: '识别规则（自动生成，只读）',
      html: '<pre class="cfgpreview" data-cfg-preview>{}</pre>', advanced: true,
      hint: '上面的填写会实时生成这段配置，不需要手写' }
  );
  return fields;
}

/* 跨字段约束：上传窗口只填一头时，服务端 IsWithinUploadWindow 直接当作"不限制"，
   于是界面上明明配了窗口，实际全天都在传，而且没有任何提示。挡在保存之前。 */
function validateTaskForm(vals) {
  const hasStart = !!vals.uploadWindowStart;
  const hasEnd = !!vals.uploadWindowEnd;
  if (hasStart !== hasEnd)
    return '上传窗口要么两头都填，要么都留空——只填一头会被当成不限制时段，看着像配了其实没生效';
  if (vals.minTotalBytes !== '' && vals.maxTotalBytes !== ''
    && Number(vals.minTotalBytes) > Number(vals.maxTotalBytes))
    return '总大小下限不能大于上限';
  return '';
}

/* 预览必须拿到与保存时同一份底稿，否则界面上显示的 JSON 和真正落库的不是同一份——
   而「看到的」与「存下去的」分叉，正是这里要修的那类毛病本身。 */
const taskFormOpts = baseConfig => ({
  onMount: (ov, readValues) => mountConfigPreview(ov, readValues, baseConfig),
  persistent: true,
  validate: validateTaskForm
});

/* 表单联动：任何一个字段变了就重算一次 JSON 预览。 */
function mountConfigPreview(ov, readValues, baseConfig) {
  const preview = ov.querySelector('[data-cfg-preview]');
  if (!preview) return;
  const update = () => { preview.textContent = composeRecognizerConfig(readValues(), baseConfig); };
  ov.querySelectorAll('input, select, textarea').forEach(el => {
    el.addEventListener('input', update);
    el.addEventListener('change', update);
  });
  update();
}

/* base 是编辑时的任务详情。
   此前编辑任务只回传表单上的十来个字段，而 PUT 的请求模型对未提供的字段一律取默认值——
   于是改一次任务名，稳定观察窗口被重置成 600、重试次数被重置成 3、告警配置被清空，
   而且没有任何提示。凡是表单不管的字段，一律用详情里的原值回填。 */
function taskFormValues(vals, base, baseConfig) {
  const carried = base ? {
    scheduleTimezone: base.scheduleTimezone,
    stabilityIntervalSeconds: base.stabilityIntervalSeconds,
    maxStabilityWaitSeconds: base.maxStabilityWaitSeconds,
    bandwidthLimitKbps: base.bandwidthLimitKbps,
    chunkSizeBytes: base.chunkSizeBytes,
    retryCount: base.retryCount,
    retryIntervalSeconds: base.retryIntervalSeconds,
    alertConfig: base.alertConfig
  } : {};

  return Object.assign(carried, {
    name: vals.name, applicationName: vals.applicationName, sourcePath: vals.sourcePath,
    recognizerType: vals.recognizerType, taskMode: vals.taskMode, enabled: vals.enabled,
    priority: Number(vals.priority) || 100, importanceLevel: vals.importanceLevel,
    // 任务在备份计划里时，表单上没有扫描计划这个输入框（由计划驱动）。
    // 这里必须回填原值：读不到就当成 null 的话，一次保存就把它存的 cron 抹掉了，
    // 而人把任务移出计划之后正指望它回来。
    scanSchedule: (vals.scanSchedule ?? base?.scanSchedule) || null,
    uploadWindowStart: vals.uploadWindowStart || null, uploadWindowEnd: vals.uploadWindowEnd || null,
    retentionPolicyId: vals.retentionPolicyId || null,
    minTotalBytes: numOrNull(vals.minTotalBytes), maxTotalBytes: numOrNull(vals.maxTotalBytes),
    minFileCount: numOrNull(vals.minFileCount),
    randomDelayMinutes: vals.randomDelayMinutes === '' || vals.randomDelayMinutes == null ? 0 : Number(vals.randomDelayMinutes),
    recognizerConfig: composeRecognizerConfig(vals, baseConfig !== undefined ? baseConfig : base && base.recognizerConfig)
  });
}

/* 表单数字输入统一转换：空字符串/undefined → null（表示"不检查"），其余转 Number */
function numOrNull(v) {
  return v === '' || v == null ? null : Number(v);
}

/* §6.3 任务详情抽屉（替换详情模态）；viaNav=true 表示从 #/tasks/{id} 直达 */
export async function openTaskDrawer(id, viaNav = false) {
  try {
    const d = await api(`/api/v1/admin/backup-tasks/${id}`);
    // C11：最近一次预检的业务单元清单。这份名单原先只在「立即备份」的弹窗里出现，
    // 窗口一关就找不回来——而「18 个账套扫出来 14 个」在别的任何页面上
    // 都表现为「这个任务备份成功了」。取不到不该让详情页打不开。
    const lastPrecheck = await api(`/api/v1/admin/backup-tasks/${id}/precheck/latest`).catch(() => null);
    const unitsHtml = unitQueueHtml(lastPrecheck, { live: false });
    openDrawer({
      title: `任务详情：${d.name}`,
      wide: true,
      onClose: () => { if (location.hash === '#/tasks/' + id) history.replaceState(null, '', '#/tasks'); },
      bodyHtml: `
    <div style="margin-bottom:var(--s-3)">
      <button class="small primary" data-ui-action="act" data-view="tasks" data-action="backup-now" data-id="${esc(id)}">立即备份</button>
      <button class="small" data-ui-action="act" data-view="tasks" data-action="test-recognition" data-id="${esc(id)}">看看会备份哪些文件</button>
      <button class="small" data-ui-action="act" data-view="tasks" data-action="last-recognition" data-id="${esc(id)}">上次识别测试</button>
    </div>
    <div class="kv">
      <div class="row"><div class="k">客户端</div><div class="v">${esc(clientName({ displayName: d.clientDisplayName, hostname: d.clientHostname }))}<span class="sub mono">${esc(d.clientHostname)}</span></div></div>
      <div class="row"><div class="k">应用</div><div class="v">${esc(d.applicationName)}</div></div>
      <div class="row"><div class="k">源路径</div><div class="v mono">${esc(d.sourcePath)}</div></div>
      <div class="row"><div class="k">识别器</div><div class="v">${esc(L.recognizer[d.recognizerType] || d.recognizerType)}</div></div>
      <div class="row"><div class="k">模式</div><div class="v">${status('task_mode', d.taskMode)}</div></div>
      <div class="row"><div class="k">启用</div><div class="v">${d.enabled ? '是' : '否'}</div></div>
      <div class="row"><div class="k">重要级 / 优先级</div><div class="v">${esc(L.importance[d.importanceLevel] || d.importanceLevel)} / ${esc(d.priority)}</div></div>
      <div class="row"><div class="k">扫描计划</div><div class="v">${d.planName
        ? `由备份计划「${esc(d.planName)}」驱动`
        : esc(describeCron(d.scanSchedule))}</div></div>
      <div class="row"><div class="k">上传窗口</div><div class="v">${esc(d.uploadWindowStart || '—')} ~ ${esc(d.uploadWindowEnd || '—')}（${esc(d.scheduleTimezone)}）</div></div>
      <div class="row"><div class="k">稳定判定</div><div class="v">${esc(d.stabilityIntervalSeconds)}s / 最长 ${esc(d.maxStabilityWaitSeconds)}s</div></div>
      <div class="row"><div class="k">大小限制</div><div class="v">${fmtBytes(d.minTotalBytes)} ~ ${fmtBytes(d.maxTotalBytes)}，文件数 ≥ ${esc(d.minFileCount ?? '—')}</div></div>
      <div class="row"><div class="k">限速 / 分块</div><div class="v">${d.bandwidthLimitKbps ? esc(d.bandwidthLimitKbps) + ' KB/s' : '不限'} / ${fmtBytes(d.chunkSizeBytes)}</div></div>
      <div class="row"><div class="k">重试</div><div class="v">${esc(d.retryCount)} 次，间隔 ${esc(d.retryIntervalSeconds)}s</div></div>
      <div class="row"><div class="k">保留策略</div><div class="v">${d.retentionPolicyName
        ? esc(d.retentionPolicyName)
        : '跟随默认<span class="sub">按保留策略页当前设为默认的那份清理，换默认策略这个任务会一起变</span>'}</div></div>
      <div class="row"><div class="k">最近扫描 / 成功</div><div class="v">${fmtDT(d.lastScanAt)} / ${fmtDT(d.lastSuccessAt)}</div></div>
      <div class="row"><div class="k">配置版本</div><div class="v">${esc(d.configVersion)}<span class="sub">每改一次任务配置加一，客户端据此判断要不要重新拉取</span></div></div>
    </div>
    ${unitsHtml ? `<h3>业务单元（最近一次检查）</h3>${unitsHtml}` : ''}
    <h3>识别规则</h3>${describeRecognizerConfig(d.recognizerConfig)}
    <details class="fadv"><summary>原始配置（JSON）</summary><pre class="json">${esc(prettyJson(d.recognizerConfig))}</pre></details>
    ${d.alertConfig ? `<h3>告警配置</h3><pre class="json">${esc(prettyJson(d.alertConfig))}</pre>` : ''}`
    });
    if (!viaNav) history.replaceState(null, '', '#/tasks/' + id);
  } catch (e) { errToast(e); }
}

/* 「强制完整校验」勾选：跳过 Agent 的快速指纹基线，把每个文件整读一遍重算 SHA-256。
   平时不勾——快速指纹只采样头尾各 64KB，认不出「中段被改而 size 与 mtime 都没变」的文件，
   这个勾选就是那条已知边界的逃生门，而不是默认每次都付的代价。 */
const forceFullHashQuery = () => (App.state.tasks && App.state.tasks.forceFullHash) ? '?forceFullHash=true' : '';

/* ── 立即备份 ──
   一个按钮，一件事：现在就把这个任务备份一次。

   它原先叫「下发预检」——那说的是系统内部的第一步（让客户端扫一遍看有没有新备份），
   而不是使用者要的结果。更糟的是这个名字在不同任务模式下含义还不一样：自动模式下
   预检通过服务端会自己接着下发上传，所以「下发预检」实际等于完整备份；
   手动/需审批模式下它只产生一个候选，而下发上传在界面上根本没有入口——
   人点完按钮、看到「已下发」的提示，然后什么都没发生。

   这里把两步合成一步：扫描 → （非自动模式再补一刀）下发上传，全程把进度显示出来。
   使用者不需要知道「预检」「候选备份集」这些词。 */
async function runBackupNow(taskId) {
  let detail;
  try {
    detail = await api(`/api/v1/admin/backup-tasks/${taskId}`);
  } catch (e) { errToast(e); return; }

  let dispatched;
  try {
    dispatched = await api(`/api/v1/admin/backup-tasks/${taskId}/precheck${forceFullHashQuery()}`, { method: 'POST' });
  } catch (e) { errToast(e); return; }

  // already_running：服务端幂等键命中了一条还在跑的预检，返回的是那一条而不是新的。
  // 这里必须如实说，否则人会以为自己刚点的没生效，转头去点第三次。
  // 开场就说清「扫描阶段传输中是空的」。不说的话，人扫到一半去那一页看不到东西，
  // 只能得出「没发起成功」这一个结论——而实际上客户端正在读几十 GB。
  const opening = dispatched.status === 'already_running'
    ? '<div class="hint">这个任务已经有一次备份在进行，正在等它的结果…</div>'
    : '<div class="hint">已通知客户端，等待它开始扫描…</div>'
      + '<div class="hint">扫描要把备份文件整读一遍算校验和，大备份会花不少时间；'
      + '这个阶段「传输中」页面还是空的，等扫完才会出现上传。</div>';
  // persistent：扫描要跑几分钟，点空白处误关一次的代价是「以为没发起，再点一次」。
  // 关闭仍有「关闭」按钮与 Esc 两条明路。
  const ov = openModal(`立即备份：${detail.name}`, opening, { persistent: true });
  // 认准**这一个**遮罩，而不是 '#overlay'：这次等待会一直跑到备份有结论为止，
  // 中途人完全可能打开别的弹窗——按 id 取的话，这里的进度会写进人家的窗口里去。
  const body = () => (ov.isConnected ? ov.querySelector('.mbody') : null);
  const say = html => { const el = body(); if (el) el.innerHTML = html; };

  // 等待窗口 2 小时，而不是原先的 5 分钟：18 个账套的 U8 备份盘一轮要把几十 GB
  // 读一遍算 SHA-256，5 分钟到点解锁之后按钮重新可点，而那时扫描才刚开头——
  // 再点一次就是整份重扫。超时仍然不算失败，任务照常在后台走完。
  // background：窗口关掉也接着等，按钮跟着一直锁到这次备份真的有结论为止。
  const already = dispatched.status === 'already_running' ? '（这一次是先前那条备份）' : '';
  const cmd = await pollCommand(dispatched.commandId, 7200000, body,
    c => say(`<div class="hint">${backupProgressText(c, already)}</div>${unitQueueHtml(c)}`),
    { background: true });
  if (cmd === null) return;                       // 连续取不到指令状态，放弃等待
  if (cmd === 'timeout') {
    say('<div class="hint">扫描仍在进行。大备份读一遍要花些时间，可以关掉这里，稍后回来看这个任务的「上次检查结果」。</div>');
    return;
  }
  if (cmd.status !== 'succeeded') {
    const outcome = describeBackupOutcome(cmd);
    say(`<div class="hint">${esc(outcome)}</div>`);
    // 窗口早就被关掉时，这句话没有任何地方可以显示——补一条 toast，
    // 否则人只知道按钮解锁了，不知道这次备份到底成没成。
    if (!body()) toast(`${detail.name}：${outcome}`, 'err');
    LOADERS.tasks();
    return;
  }

  // 一个任务可以有很多个业务单元（U8 一台机器 18 个账套是常态），
  // 每一个都是独立的一份备份、独立的一个候选。只认第一个的那一版里，
  // 其余账套扫过就扔了——人看到「已开始上传」，实际只传了 1/18。
  const passed = passedCandidatesOf(cmd.resultPayload);
  if (!passed.length) {
    say('<div class="hint">扫描完成，但这次没有发现需要备份的新文件。</div>');
    if (!body()) toast(`${detail.name}：没有发现需要备份的新文件`, 'ok');
    LOADERS.tasks();
    return;
  }
  const many = passed.length > 1 ? `${passed.length} 个备份集：` : '';

  // 自动模式：服务端在收到每一个单元的预检结果时就把它的上传指令发出去了，这里不重复发，
  // 但要如实说清到底发出去几个。服务端把每个候选的下发结果写在指令结果里
  // （dispatched / queued / already_running / ...）——原先这里不看它，
  // 无论如何都说一句「已开始上传」，于是「一条都没发出去」也长这个样子。
  if (detail.taskMode === 'automatic') {
    const started = passed.filter(c => c.uploadState === 'dispatched' || c.uploadState === 'queued');
    const queued = passed.filter(c => c.uploadState === 'queued').length;
    if (!started.length) {
      const why = passed[0].uploadState === 'already_running'
        ? '上一次的上传还没结束，这次不重复发；进度在「传输中」页面。'
        : passed[0].uploadState === 'already_done'
          ? '这份备份先前已经传完入库了，没有需要重传的内容。'
          : '服务端没有下发上传，请看这个任务的告警。';
      say(`<div class="hint">扫描通过（${many}${passed.length} 个），但这次没有新的上传：${esc(why)}</div>`);
      if (!body()) toast(`${detail.name}：扫描通过，但这次没有新的上传`, 'err');
      LOADERS.tasks();
      return;
    }
    const queuedNote = queued ? `（其中 ${queued} 个在排队等名额）` : '';
    say(`<div class="hint">扫描通过，已开始上传 ${started.length} 个备份集${queuedNote}。进度在「传输中」页面。</div>`);
    toast('已开始备份', 'ok');
    LOADERS.tasks();
    return;
  }

  try {
    await dispatchUploads(taskId, passed, false);
    say(`<div class="hint">已开始上传 ${many}${passed.length} 个。进度在「传输中」页面。</div>`);
    toast('已开始备份', 'ok');
  } catch (e) {
    // 上传窗口和「客户端正忙」是配置意图，不是故障：问一句再强制，而不是默默绕过，
    // 也不是甩一句错误码让人自己去猜该怎么办。
    const askable = e.code === 'OUT_OF_UPLOAD_WINDOW' || e.code === 'CLIENT_BUSY';
    if (!askable) {
      say(`<div class="hint">扫描通过，但下发上传失败：${esc(e.message)}</div>`);
      if (!body()) toast(`${detail.name}：扫描通过，但下发上传失败——${e.message}`, 'err');
      return;
    }

    // 窗口早被关掉了：这时候突然弹一个确认框，对使用者是凭空冒出来的东西——
    // 他多半已经在看别的页面，也想不起来这是哪个任务的哪一步。改成如实报一句，
    // 扫描结果留着，他回到这个任务再点一次就能接着传。
    if (!body()) {
      toast(`${detail.name}：${e.code === 'CLIENT_BUSY' ? '客户端还有别的上传在进行' : '当前不在上传窗口内'}，这次没有传；扫描结果留着，需要的话再点一次「立即备份」`, 'err');
      return;
    }

    const question = e.code === 'CLIENT_BUSY'
      ? '这台客户端还有别的上传在进行。仍然现在就传吗？'
      : '当前不在这个任务配置的上传窗口内。仍然现在就传吗？';
    if (!await confirmModal(question)) { say('<div class="hint">已取消，扫描结果保留着，之后可以再传。</div>'); return; }
    try {
      await dispatchUploads(taskId, passed, true);
      say(`<div class="hint">已开始上传 ${many}${passed.length} 个。进度在「传输中」页面。</div>`);
      toast('已开始备份', 'ok');
    } catch (e2) {
      say(`<div class="hint">下发上传失败：${esc(e2.message)}</div>`);
      if (!body()) toast(`${detail.name}：下发上传失败——${e2.message}`, 'err');
    }
  }
  LOADERS.tasks();
}

/* 把预检失败码说成人话。
   「no_new_backup」并不是失败——它是最常见的正常结果（今天还没产生新备份），
   照着 result_code 原样甩出来只会让人以为出了故障。 */
function describeBackupOutcome(cmd) {
  const known = {
    no_new_backup: '没有发现新的备份文件——源目录里的还是上一次那一份。',
    still_changing: '备份文件还在写入中，这次先跳过；等它稳定下来会自动再试一次。',
    required_file_missing: '扫到的文件不完整，缺少「必需文件」里点名的文件。',
    size_abnormal: '备份大小超出了任务设定的上下限，判为异常，没有上传。',
    path_not_found: '客户端上找不到这个源路径。',
    access_denied: '客户端没有权限读取这个源路径。'
  };
  return known[cmd.resultCode]
    || `这次备份没有成功：${cmd.resultMessage || cmd.resultCode || cmd.status}`;
}

const dispatchUpload = (taskId, candidateBackupSetId, force) =>
  api(`/api/v1/admin/backup-tasks/${taskId}/upload`, { method: 'POST', body: { candidateBackupSetId, force } });

/* 逐个下发，串行而不是 Promise.all：并发发过去只会撞上「客户端已有活动上传」，
   把一次本来能全部排上队的备份变成一串 409。第一个失败就抛出去，
   由调用方走「问一句再强制」那条路——它问的是同一个问题。 */
async function dispatchUploads(taskId, candidates, force) {
  for (const c of candidates)
    await dispatchUpload(taskId, c.candidateBackupSetId, force);
}






/* 轮询一条指令直到有结论。
   返回指令对象 / 'timeout' / null（放弃等待）。
   识别测试与立即备份共用：两者等的是同一件事——Agent 什么时候把这条指令跑完。

   opts.background：对话框被关掉之后**继续等**，只是把节奏放慢。
   立即备份要的就是这个——「关掉窗口」表达的是「我不想盯着看」，不是「这次备份不算数」。
   而调用方仍然被 act 分发层的在途集合锁着，指令没终结之前按钮不会重新变成可点，
   这正是原先「关掉两秒按钮就亮了、再点一次触发整份重扫」的成因。
   识别测试不传这个开关：那条路径关掉窗口就是不要结果了，等下去没有意义。

   一次接口失败不再直接放弃：慢扫描要等几十分钟，中间一个网络抖动就把等待丢掉的话，
   按钮会提前解锁，等于这条锁形同虚设。连续失败到第 3 次才认输。 */
async function pollCommand(commandId, timeoutMs, body, onProgress, opts = {}) {
  const deadline = Date.now() + timeoutMs;
  let failures = 0;
  while (Date.now() < deadline) {
    const visible = !!body();
    if (!visible && !opts.background) return null;
    await new Promise(r => setTimeout(r, visible ? 2000 : 10000));
    if (!body() && !opts.background) return null;

    let cmd;
    try {
      cmd = await api(`/api/v1/admin/backup-tasks/commands/${commandId}`);
      failures = 0;
    } catch (e) {
      if (++failures >= 3) { errToast(e); return null; }
      continue;
    }

    if (cmd.status === 'succeeded' || cmd.status === 'failed') return cmd;
    if (cmd.status === 'cancelled' || cmd.status === 'expired') return cmd;
    onProgress(cmd);
  }
  return 'timeout';
}


/* 一句话说清「现在在干什么」。
   状态词（running）对使用者没有信息量——他要判断的是「这东西还在动吗、还要多久」，
   而这个判断只有进度里的「第几个账套 · 正在读哪个文件」回答得了。 */
function backupProgressText(cmd, already) {
  const p = progressOf(cmd);
  if (!p) return `客户端正在扫描备份文件…${already}（当前状态：${esc(cmd.status)}）`;
  const percent = typeof p.percent === 'number' ? `${Math.round(p.percent)}% · ` : '';
  return `${percent}${esc(p.message)}${already}`;
}

/* ── 识别测试 ──
   下发一条 testOnly 指令让 Agent 按当前配置扫一遍，把"会识别出什么"原样报回来。
   只看不写：不产生候选备份集，也不改任务的扫描时间。

   这是整个建任务流程里唯一的即时反馈。没有它，源路径写错、识别规则不匹配这类问题
   要等到某天真正需要恢复数据时才会暴露——那时已经太晚了。 */
async function runRecognitionTest(taskId) {
  let dispatched;
  try {
    dispatched = await api(`/api/v1/admin/backup-tasks/${taskId}/test-recognition`, { method: 'POST' });
  } catch (e) { errToast(e); return; }

  // 服务端不会为同一个任务堆第二条测试指令：上一条还在跑就把它还回来，这里如实说明，
  // 否则人会以为自己刚点的那一次没生效。
  const queued = dispatched.status === 'already_running';
  const waiting = queued
    ? '这个任务已经有一次识别测试在执行，正在等它的结果…'
    : '已下发指令，等待客户端执行…';
  openRecognitionModal(taskId, `<div class="hint">${waiting}</div>`);
  const body = () => document.querySelector('#overlay .mbody');

  const cmd = await pollCommand(dispatched.operationId, 60000, body, c => {
    if (body()) body().innerHTML = `<div class="hint">${waiting}（当前状态：${esc(c.status)}）</div>`;
  });
  if (cmd === null) return;                    // 用户关掉了对话框
  if (!body()) return;

  if (cmd === 'timeout')
    body().innerHTML = pendingResultHtml('客户端还没把结果报回来。');
  else if (cmd.status === 'cancelled' || cmd.status === 'expired')
    body().innerHTML = `<div class="hint">指令已${cmd.status === 'expired' ? '过期' : '取消'}，客户端可能不在线。</div>`;
  else
    body().innerHTML = renderRecognitionResult(cmd);
}

/* 等不到结果时说的话。
   要点是「这次测试没有作废」：指令有 24 小时有效期，客户端忙完手上的活照样会把它跑完，
   结果落在指令行上。原先这里写的是"稍后可以重新测试"——那等于让人把刚才那次白等的
   十几秒再等一遍，而上一次的结果其实一直在服务端躺着。 */
function pendingResultHtml(lead) {
  return `<div class="hint">${esc(lead)}测试没有取消——客户端可能正忙着传别的备份，或者这次扫描的目录比较大。
    它跑完之后，这里和任务详情里的「上次识别测试」都能看到结果。</div>
    <div style="margin-top:12px"><button class="primary" data-act="refresh-last">刷新看看</button></div>`;
}

/* 识别测试对话框的壳子：下发新测试与回看上次结果共用。
   两处都要能勾必需文件、都要能刷新，处理器写一份。 */
function openRecognitionModal(taskId, bodyHtml) {
  const ov = openModal('识别测试', bodyHtml);

  // 试扫结果里是客户端上真实存在的文件名。让人对着它们打勾，比让他回忆
  // 「那个文件到底叫 UfErpAct.Lst 还是 UFERPACT.LST」可靠得多——
  // 大小写、扩展名、要不要带路径，这些他都不需要再想。
  ov.addEventListener('click', async ev => {
    if (ev.target.closest('[data-act="refresh-last"]')) {
      await showLastRecognitionTest(taskId);
      return;
    }

    const btn = ev.target.closest('[data-act="apply-required"]');
    if (!btn) return;
    const picked = [...ov.querySelectorAll('[data-required]:checked')].map(x => x.dataset.required);
    if (!picked.length) { toast('先勾选至少一个文件', 'err'); return; }
    btn.disabled = true;
    try {
      await applyRequiredFiles(taskId, picked);
      toast(`已把 ${picked.length} 个文件设为必需文件`, 'ok');
      btn.textContent = '已保存';
      LOADERS.tasks();
    } catch (e) { errToast(e); btn.disabled = false; }
  });
  return ov;
}

/* 回看最近一次识别测试的结果。
   等待窗口只有一分钟，而扫描慢、客户端在忙、人自己把窗口关了都很常见——
   没有这个入口，那次已经跑完并躺在服务端的结果就只能靠再测一遍才能看到。 */
async function showLastRecognitionTest(taskId) {
  let cmd;
  try {
    cmd = await api(`/api/v1/admin/backup-tasks/${taskId}/test-recognition/latest`);
  } catch (e) { errToast(e); return; }

  if (!cmd) {
    openRecognitionModal(taskId, '<div class="hint">这个任务还没有做过识别测试。点「看看会备份哪些文件」测一次。</div>');
    return;
  }

  if (cmd.status === 'succeeded' || cmd.status === 'failed') {
    const when = fmtDT(cmd.completedAt || cmd.createdAt);
    openRecognitionModal(taskId,
      `<p class="hint">这是 ${esc(when)} 那次测试的结果。</p>${renderRecognitionResult(cmd)}`);
    return;
  }

  if (cmd.status === 'cancelled' || cmd.status === 'expired') {
    openRecognitionModal(taskId,
      `<div class="hint">上一次测试已${cmd.status === 'expired' ? '过期' : '取消'}，客户端始终没有领走它。确认客户端在线后再测一次。</div>`);
    return;
  }

  // pending / claimed / running：还在跑，把它当成一次新的等待接着等下去。
  openRecognitionModal(taskId, `<div class="hint">上一次测试还在执行（当前状态：${esc(cmd.status)}），继续等它的结果…</div>`);
  const body = () => document.querySelector('#overlay .mbody');
  const done = await pollCommand(cmd.commandId, 60000, body, c => {
    if (body()) body().innerHTML = `<div class="hint">上一次测试还在执行（当前状态：${esc(c.status)}），继续等它的结果…</div>`;
  });
  if (done === null || !body()) return;
  body().innerHTML = done === 'timeout'
    ? pendingResultHtml('还是没等到结果。')
    : renderRecognitionResult(done);
}

function renderRecognitionResult(cmd) {
  if (!cmd.resultPayload)
    return `<div class="hint">${esc(cmd.resultMessage || '客户端没有返回结果')}</div>`;

  let payload;
  try { payload = JSON.parse(cmd.resultPayload); }
  catch (e) { return `<pre class="json">${esc(cmd.resultPayload)}</pre>`; }

  const results = payload.results || [];
  if (!results.length)
    return `<div class="hint">源路径 <code>${esc(payload.sourcePath)}</code> 下没有识别到任何备份单元。请检查路径是否正确、以及识别规则是否过严。</div>`;

  return `<p class="hint">源路径 <code>${esc(payload.sourcePath)}</code> · 识别器 ${esc(L.recognizer[payload.recognizerType] || payload.recognizerType)}</p>`
    + results.map(r => {
      const ok = r.status === 'passed';
      const files = r.files || [];
      return `<div class="card" style="margin-bottom:var(--s-3)">
        <div><strong>${esc(r.businessUnit || r.sourceRoot)}</strong> ${status('precheck', r.status)}</div>
        ${ok
          ? `<div class="hint">${esc(r.totalFiles)} 个文件 · ${fmtBytes(r.totalBytes)}</div>`
          : `<div class="hint">${esc(r.failureMessage || r.failureCode || '未通过')}</div>`}
        ${files.length
          ? `<ul class="pvfiles">${files.map(f =>
              `<li${f.isRequired ? ' class="is-required"' : ''}>${esc(f.relativePath)}
                <span class="dmeta">${fmtBytes(f.sizeBytes)}</span>
                ${f.isRequired ? '<span class="tagreq">必需</span>' : ''}</li>`).join('')}</ul>`
            + (r.totalFiles > files.length ? `<div class="hint">仅显示前 ${files.length} 个，共 ${esc(r.totalFiles)} 个</div>` : '')
          : ''}
      </div>`;
    }).join('')
    + requiredPickerFromResults(results);
}

/* 从试扫结果里挑必需文件。
   这些是客户端上真实存在的文件名——对着它们打勾，不需要知道大小写怎么写、
   要不要带路径、通配符怎么用。这是整个识别规则里最该傻瓜化的一项。 */
function requiredPickerFromResults(results) {
  const seen = new Map();
  for (const unit of results) {
    for (const file of unit.files || []) {
      const name = leafName(file.relativePath);
      if (!name) continue;
      const prev = seen.get(name.toLowerCase());
      if (prev) { prev.count++; prev.size = Math.max(prev.size, file.sizeBytes || 0); }
      else seen.set(name.toLowerCase(), { name, count: 1, size: file.sizeBytes || 0, required: !!file.isRequired });
    }
  }
  const items = [...seen.values()].sort((a, b) => b.count - a.count || b.size - a.size).slice(0, 20);
  if (!items.length) return '';

  return `<h3>把哪些文件设为「必需」</h3>
    <p class="hint">勾上的文件如果缺了，这次备份就判为不完整，不会入库。保存后立即生效。</p>
    <div class="filepick">
      ${items.map(item => `
        <label class="filepick__item">
          <input type="checkbox" data-required="${esc(item.name)}" ${item.required ? 'checked' : ''}>
          <span class="filepick__name">${esc(item.name)}</span>
          <span class="filepick__meta">${item.count} 个单元里有 · 约 ${fmtBytes(item.size)}</span>
        </label>`).join('')}
    </div>
    <div style="margin-top:12px"><button class="primary" data-act="apply-required">把勾选的设为必需文件</button></div>`;
}

/* 只改识别规则里的 requiredFiles，其余字段一律用详情里的原值回填——
   PUT 对未提供的字段取默认值，漏一个就是一次静默的配置重置。 */
async function applyRequiredFiles(taskId, patterns) {
  const detail = await api(`/api/v1/admin/backup-tasks/${taskId}`);
  let cfg = {};
  try { cfg = JSON.parse(detail.recognizerConfig || '{}') || {}; } catch (e) { cfg = {}; }
  // 别名键一并清掉：requiredFiles 和 required 同时存在时，两个都会生效，含义不清。
  delete cfg.requiredPatterns;
  delete cfg.required;
  cfg.requiredFiles = patterns;

  const sent = JSON.stringify(cfg, null, 2);
  const saved = await api(`/api/v1/admin/backup-tasks/${taskId}`, {
    method: 'PUT',
    body: {
      name: detail.name, applicationName: detail.applicationName, sourcePath: detail.sourcePath,
      recognizerType: detail.recognizerType, taskMode: detail.taskMode, enabled: detail.enabled,
      priority: detail.priority, importanceLevel: detail.importanceLevel,
      scanSchedule: detail.scanSchedule,
      uploadWindowStart: detail.uploadWindowStart, uploadWindowEnd: detail.uploadWindowEnd,
      scheduleTimezone: detail.scheduleTimezone, randomDelayMinutes: detail.randomDelayMinutes,
      stabilityIntervalSeconds: detail.stabilityIntervalSeconds,
      maxStabilityWaitSeconds: detail.maxStabilityWaitSeconds,
      minTotalBytes: detail.minTotalBytes, maxTotalBytes: detail.maxTotalBytes,
      minFileCount: detail.minFileCount, bandwidthLimitKbps: detail.bandwidthLimitKbps,
      chunkSizeBytes: detail.chunkSizeBytes, retryCount: detail.retryCount,
      retryIntervalSeconds: detail.retryIntervalSeconds, retentionPolicyId: detail.retentionPolicyId,
      alertConfig: detail.alertConfig,
      recognizerConfig: sent,
      rowVersion: detail.rowVersion
    }
  });
  await verifyStoredRecognizerConfig(sent, saved && saved.recognizerConfig);
}

/* 第 0 步：在哪台机器上。
   浏览目录需要先知道是哪台客户端，所以这一问提到了最前面——它本来也是建任务
   第一个要决定的事。 */
/* 第 0 步：在哪台机器上。
   B5：此前一次拉 200 台塞进下拉框，超过 200 台的部署会静默丢掉后面的机器，
   而使用者看不出自己在一个被截断的列表里挑。改成按关键字问服务端要匹配项。 */
function chooseClient() {
  return new Promise(resolve => {
    let settled = false;
    const finish = value => { if (!settled) { settled = true; resolve(value); } };
    const ov = openModal('新建备份任务 · 在哪台机器上',
      `${searchPickerHtml('task-client', { placeholder: '输入主机名或显示名搜索' })}
       <div class="hint" style="margin-top:8px">备份文件在这台机器上</div>`,
      { okText: '下一步', persistent: true });

    const picker = initSearchPicker(ov, 'task-client', { search: searchClients });
    ov.addEventListener('bm:dismiss', () => finish(null));
    ov.querySelector('[data-ok]').addEventListener('click', () => {
      const client = picker.selected();
      if (!client) { toast('请先选择一台客户端', 'err'); return; }
      finish(client);
      closeModal(ov);
    });
  });
}

/* 客户端搜索：只取前 20 条，总数由服务端给，剩下的靠继续输入关键字收敛。 */
async function searchClients(keyword) {
  const qs = `page=1&pageSize=20${keyword ? `&keyword=${encodeURIComponent(keyword)}` : ''}`;
  const d = await api(`/api/v1/admin/clients?${qs}`);
  return {
    total: d.totalCount,
    items: (d.items || []).map(c => ({
      v: c.id, raw: c,
      t: clientLabel(c),
      sub: L.client_status[c.status] || c.status
    }))
  };
}

/* 第 1 步：怎么告诉系统备份在哪。
   浏览目录需要 Agent 支持 browse_path（1.1.0 起）。版本不够就不显示这个入口——
   给一个点了要等 90 秒才超时的按钮，比不给更糟。 */
function chooseSetupMode(client) {
  const canBrowse = supportsBrowse(client.agentVersion);
  return new Promise(resolve => {
    let settled = false;
    const finish = value => { if (!settled) { settled = true; resolve(value); } };
    const body = `
      <div class="tpl-list">
        ${canBrowse ? `
        <label class="tpl-item">
          <input type="radio" name="mode" value="browse" checked>
          <span class="tpl-copy"><strong>打开这台机器的目录，我指给你看</strong>
            <small>像资源管理器一样点进去选中备份目录，系统自己看懂结构、填好规则</small></span>
        </label>` : ''}
        <label class="tpl-item">
          <input type="radio" name="mode" value="describe" ${canBrowse ? '' : 'checked'}>
          <span class="tpl-copy"><strong>我自己描述备份的摆放方式</strong>
            <small>从几种常见结构里挑一种，然后手工填写源路径</small></span>
        </label>
      </div>
      ${canBrowse ? '' : `<div class="hint" style="margin-top:12px">这台客户端的 Agent 版本是 ${esc(client.agentVersion || '未知')}，
        还不支持在界面上浏览目录。升级到 1.1.0 之后就能用了。</div>`}`;

    const ov = openModal('新建备份任务 · 怎么找到备份目录', body, { okText: '下一步' });
    ov.addEventListener('bm:dismiss', () => finish(null));
    ov.querySelector('[data-ok]').addEventListener('click', () => {
      const picked = ov.querySelector('input[name="mode"]:checked');
      finish(picked ? picked.value : null);
      closeModal();
    });
  });
}

/* 语义化版本比较，只比前三段。解析不出来一律当作不支持——
   宁可让人走手填这条稳的路，也不要让他对着一个转 90 秒的对话框猜发生了什么。 */
function supportsBrowse(version) {
  const parts = String(version || '').trim().split('.').map(x => parseInt(x, 10));
  if (parts.length < 2 || parts.some(x => !Number.isFinite(x))) return false;
  const [major, minor] = parts;
  return major > 1 || (major === 1 && minor >= 1);
}

ACTIONS['tasks:create'] = async () => {
  const client = await chooseClient();
  if (!client) return;

  const mode = await chooseSetupMode(client);
  if (!mode) return;

  let prefill = null;
  if (mode === 'browse') {
    const outcome = await runDirectoryWizard(client.id);
    if (outcome === null) return;
    if (outcome !== 'manual') {
      prefill = outcome;
      // 名字和应用名都给一个能直接用的建议，但仍然让人能改——
      // 这两项没有正确答案，只有他自己的叫法。
      prefill.name = `${leafName(prefill.sourcePath)} 备份`;
    }
  }

  let template = null;
  if (!prefill) {
    template = await chooseTaskTemplate();
    if (!template) return;
  }

  const fields = await taskFormFields(null, template, prefill, client);
  formModal('新建备份任务', fields, async vals => {
    const body = taskFormValues(vals, null, prefill && prefill.recognizerConfig);
    body.clientId = client.id;
    const d = await api('/api/v1/admin/backup-tasks', { method: 'POST', body });
    toast(`任务「${d.name}」已创建`, 'ok');
    LOADERS.tasks();
    // 必须 await：下面那句 confirmModal 会走 openModal，而 openModal 开头就是 closeModal()，
    // 不等的话这条提示会被下一个弹窗顶掉，等于没提示过。
    await verifyStoredRecognizerConfig(body.recognizerConfig, d.recognizerConfig);
    // 建完立刻验证：这是唯一一个能在"等到需要恢复时才发现规则写错"之前发现问题的时机。
    // 走过向导的任务已经在快照上预演过一遍，就不必再问一次。
    if (!prefill && await confirmModal('任务已创建。现在让客户端试扫一次，看看能识别到哪些文件吗？'))
      await runRecognitionTest(d.id);
    // persistent：这张表单里装着刚刚一路点出来的结果和手填的名字，
    // 点到弹窗外面不该等于把它们全丢掉。
  }, '创建', taskFormOpts(prefill && prefill.recognizerConfig));
};

ACTIONS['tasks:edit'] = async id => {
  const detail = await api(`/api/v1/admin/backup-tasks/${id}`);
  const fields = await taskFormFields(detail);
  formModal(`编辑任务：${detail.name}`, fields, async vals => {
    const body = taskFormValues(vals, detail);
    body.rowVersion = detail.rowVersion;
    const d = await api(`/api/v1/admin/backup-tasks/${id}`, { method: 'PUT', body });
    toast(`任务「${d.name}」已保存`, 'ok'); LOADERS.tasks();
    await verifyStoredRecognizerConfig(body.recognizerConfig, d.recognizerConfig);
  }, '保存', taskFormOpts(detail.recognizerConfig));
};

function leafName(path) {
  const normalized = String(path || '').replace(/\\/g, '/').replace(/\/+$/, '');
  const index = normalized.lastIndexOf('/');
  return (index < 0 ? normalized : normalized.slice(index + 1)) || normalized || '备份';
}
ACTIONS['tasks:detail'] = async id => openTaskDrawer(id);
ACTIONS['tasks:pause'] = async id => { await api(`/api/v1/admin/backup-tasks/${id}/pause`, { method: 'POST' }); toast('任务已暂停', 'ok'); LOADERS.tasks(); };
ACTIONS['tasks:resume'] = async id => { await api(`/api/v1/admin/backup-tasks/${id}/resume`, { method: 'POST' }); toast('任务已恢复', 'ok'); LOADERS.tasks(); };
ACTIONS['tasks:test-recognition'] = async id => { await runRecognitionTest(id); };
ACTIONS['tasks:last-recognition'] = async id => { await showLastRecognitionTest(id); };
ACTIONS['tasks:backup-now'] = async id => { await runBackupNow(id); };
ACTIONS['tasks:del'] = async id => {
  if (!await confirmModal('删除该备份任务？此操作不可恢复。')) return;
  await api(`/api/v1/admin/backup-tasks/${id}`, { method: 'DELETE' });
  toast('任务已删除', 'ok'); LOADERS.tasks();
};
