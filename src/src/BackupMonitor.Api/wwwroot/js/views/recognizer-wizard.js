/* js/views/recognizer-wizard.js —— 建任务向导：指着目录，而不是描述目录。

   现有流程要求填表人先把自己的目录结构翻译成我们的词汇（识别器类型、业务单元、
   层级深度、一段 JSON）。可他本来就知道自己的备份长什么样——他每天在资源管理器里看它。
   不知道的是我们的抽象。

   所以这里把责任调过来：
     第一步  他指一个目录（不需要任何词汇）
     第二步  系统看结构、出方案、用一句人话说回去
     第三步  他只判断「对 / 不对」

   两个实现上的关键决定：
   1) 目录树一次抓三层，之后在浏览器里展开。Agent 的指令轮询是 10 秒一次，
      「点一层发一条指令」意味着每次点击等 5–10 秒，那比手写 JSON 还难用。
   2) 规则预演跑在服务端已有的快照上（毫秒级），不下发指令。
      于是「改一个勾选 → 立刻看到判定变化」成立，配置从「填完再说」变成「看着调」。 */

import { api } from '../api.js';
import { esc, fmtBytes, fmtDT, status, openModal, closeModal, errToast, skeleton,
  bytesFieldHtml, initBytesFields } from '../ui.js';

const MAX_WAIT_MS = 90000;
const POLL_INTERVAL_MS = 1500;

/* 预演用的稳定观察窗口，跟建出来的任务默认值（600 秒）保持一致。
   预演的全部价值是"它现在告诉你的，就是将来会发生的"；这里传 0、任务用 600，
   等于向导说"全部完整"而第二天真扫报"仍在变化"，那份承诺就作废了。 */
const STABILITY_INTERVAL_SECONDS = 600;

/* 推断用的抓取深度：源目录(1) → 年/账套(2) → 月/日期(3) → 文件(4)。
   不设更大，是因为层数一深条目数是乘出来的，很容易顶到 3000 条上限，
   反而让本来看得懂的结构被标成"没抓全"。真正需要更深时走 DEEP_SCAN_DEPTH 重来一次。 */
const INFER_DEPTH = 4;

/* 服务端说"我是被深度卡住的"时才用的重试深度（服务端硬上限就是 6）。 */
const DEEP_SCAN_DEPTH = 6;

/* 对外入口。返回：
     { sourcePath, recognizerType, recognizerConfig, applicationName }  —— 用户确认了方案
     'manual'                                                          —— 用户要自己描述结构
     null                                                              —— 关掉了 */
export function runDirectoryWizard(clientId) {
  return new Promise(resolve => {
    const st = {
      clientId,
      commandId: null,
      snapshot: null,
      selected: null,
      expanded: new Set(),
      proposal: null,
      required: new Set(),
      // 尺寸基线的当前取值（可能被人改过）。null = 不检查。
      baseline: { minTotalBytes: null, minFileCount: null },
      history: []
    };

    let settled = false;
    const finish = value => { if (!settled) { settled = true; resolve(value); } };

    const ov = openModal('新建备份任务 · 备份目录在哪', skeleton(5), { wide: true, persistent: true });
    ov.addEventListener('bm:dismiss', () => finish(null));

    const alive = () => document.body.contains(ov);
    const body = html => { if (alive()) ov.querySelector('.mbody').innerHTML = html; };
    const footer = html => {
      if (!alive()) return;
      ov.querySelector('.mfoot').innerHTML = `<button data-close>关闭</button>${html || ''}`;
    };

    // 事件一律走委托：每次状态变化都重绘整块内容，逐个挂监听器会随重绘一起失效。
    ov.addEventListener('click', async ev => {
      const target = ev.target.closest('[data-act]');
      if (!target) return;
      const act = target.dataset.act;
      const path = target.dataset.path;

      if (act === 'toggle') {
        if (st.expanded.has(path)) st.expanded.delete(path); else st.expanded.add(path);
        renderBrowser();
      } else if (act === 'select') {
        st.selected = path;
        renderBrowser();
      } else if (act === 'open') {
        await load(path);
      } else if (act === 'up') {
        await load(parentOf(st.snapshot.root));
      } else if (act === 'drives') {
        await load(null);
      } else if (act === 'confirm-dir') {
        if (needsOwnSnapshot()) await load(st.selected, true);
        else await infer();
      } else if (act === 'manual') {
        finish('manual');
        closeModal();
      } else if (act === 'back-browse') {
        renderBrowser();
      } else if (act === 'accept') {
        finish(buildResult(st));
        closeModal();
      } else if (act === 'retry') {
        await load(st.snapshot ? st.snapshot.root : null);
      } else if (act === 'probe') {
        await probe();
      } else if (act === 'use-probed') {
        st.selected = path;
        await load(path, true);
      }
    });

    // 尺寸基线是个会产生告警的阈值，人必须看得见并能改。改完不需要重新预演——
    // 预演跑的是识别规则，而它是任务字段，两者不在一条链路上。
    // 监听 input 而不只是 change：总大小走的是「数值 + 单位」组合控件，
    // 只改数值不失焦就直接点「建任务」时，change 还没发生，填的数会丢。
    const readBaseline = target => {
      const wrap = target.closest('[data-baseline]');
      if (!wrap) return false;
      // 组合控件把换算好的字节数放在内部的隐藏 input 里；文件数下限就是这个元素本身。
      const input = wrap.matches('input') ? wrap : wrap.querySelector('input[type="hidden"]');
      const value = String(input ? input.value : '').trim();
      st.baseline[wrap.dataset.baseline] = value === '' ? null : Number(value);
      return true;
    };
    ov.addEventListener('input', ev => { readBaseline(ev.target); });

    ov.addEventListener('change', ev => {
      if (readBaseline(ev.target)) return;

      const box = ev.target.closest('[data-required]');
      if (!box) return;
      const pattern = box.dataset.required;
      if (box.checked) st.required.add(pattern); else st.required.delete(pattern);
      refreshPreview();
    });

    /* ── 第一步：浏览 ── */

    async function load(path, andInfer = false, depthOverride = 0) {
      body(`<div class="hint">正在读取 ${path ? esc(path) : '磁盘列表'}…</div>${skeleton(4)}`);
      footer('');
      try {
        const dispatched = await api(`/api/v1/admin/clients/${clientId}/browse`, {
          method: 'POST',
          // 确认目录时抓 INFER_DEPTH 层：这一次是冲着"看懂这个目录"去的，
          // 盘根那条省钱的规则（只抓一层）在这里反而会让推断看到一个空目录。
          // depthOverride 只在服务端回报"我是被深度卡住的"时才传，见 infer()。
          body: {
            path: path || null,
            maxDepth: depthOverride || (andInfer ? INFER_DEPTH : browseDepth(path)),
            maxEntries: 3000
          }
        });
        st.commandId = dispatched.commandId;
        const snapshot = await waitForSnapshot(st.commandId, alive, stage => {
          body(`<div class="hint">已下发指令，等待客户端执行…（${esc(stage)}）</div>${skeleton(4)}`);
        });
        if (!alive()) return;
        st.snapshot = snapshot;
        st.expanded = new Set();
        st.selected = snapshot.isDriveList ? null : normPath(snapshot.root);
        if (andInfer && !snapshot.isDriveList) {
          await infer();
          return;
        }
        renderBrowser();
      } catch (e) {
        if (!alive()) return;
        body(`<div class="empty">${esc(e.message)}</div>`);
        footer('<button data-act="retry">重试</button><button data-act="manual">改为自己描述结构</button>');
      }
    }

    function renderBrowser() {
      const snap = st.snapshot;
      if (!snap) return;

      if (snap.isDriveList) {
        body(`<p class="hint" style="margin-top:0">备份文件在哪个盘上？</p>
          <div class="dirtree">${snap.entries.map(driveRow).join('') || '<div class="empty">没有可浏览的磁盘</div>'}</div>`);
        // 不知道备份在哪时，「一层层点开找」是这一屏唯一的出路——探测就是给这种情况的。
        footer('<button data-act="probe">帮我找找备份目录</button><button data-act="manual">改为自己描述结构</button>');
        return;
      }

      const notes = [];
      if (snap.truncated)
        notes.push('这个目录内容太多，只取回了一部分——如果没看到要找的目录，请直接打开它的上级再往下点。');
      if (snap.deniedPaths && snap.deniedPaths.length)
        notes.push(`有 ${snap.deniedPaths.length} 个目录没有访问权限，未能读取：${snap.deniedPaths.slice(0, 3).map(esc).join('、')}`);

      body(`
        <div class="dirbar">
          <button class="small" data-act="drives">‹ 磁盘</button>
          ${parentOf(snap.root) ? '<button class="small" data-act="up">‹ 上一级</button>' : ''}
          <code class="dirpath">${esc(displayPath(snap.root))}</code>
        </div>
        ${notes.map(n => `<div class="hint">${esc(n)}</div>`).join('')}
        <div class="dirtree">
          ${rootRow(snap, st)}
          ${snap.entries.map(e => nodeHtml(e, 1, st)).join('') || '<div class="empty">这个目录是空的</div>'}
        </div>`);

      footer(`<span class="dirsel">${st.selected ? '已选择：' + esc(displayPath(st.selected)) : '请选择一个目录'}</span>
        <button class="primary" data-act="confirm-dir" ${st.selected ? '' : 'disabled'}>就用这个目录</button>`);
    }

    /* 选中的目录，快照里真的有它的内容吗。

       只有快照根拿到了完整的 3 层视图。树里的子目录，往下只剩 1–2 层——
       盘根更是只抓了一层，子目录在快照里干脆是空的。拿这样的节点去推断，
       系统看到的是"这个目录里什么都没有"，于是回一句"没能看懂这个目录的结构"，
       而人明明在资源管理器里看得见东西。

       所以确认目录时，如果选的不是当前快照的根，就先照着它自己再抓一次（10 秒左右，
       只发生在"就用这个目录"这一次点击上），再推断。 */
    function needsOwnSnapshot() {
      if (!st.snapshot || st.snapshot.isDriveList || !st.selected) return false;
      // 与推断请求的 maxDepth 保持一致：手上这份快照浅于 INFER_DEPTH 层就重抓一次，
      // 否则年/月分层的文件落在第 4 层，推断会把月目录看成空的。
      return st.selected !== normPath(st.snapshot.root) || (st.snapshot.maxDepth || 0) < INFER_DEPTH;
    }

    /* ── 主动探测：「这台机器上还有没有别的备份没被监控起来」 ──

       浏览回答不了这个问题——它是定向的，得先知道路径。探测是发现式的：
       给一台机器，返回像备份目录的地方，并标出哪些已经被现有任务盯着。
       真正的产出是**未监控的那几行**。 */
    async function probe() {
      body(`<div class="hint">正在让客户端扫描各个磁盘，找像备份目录的地方…（最多半分钟）</div>${skeleton(4)}`);
      footer('');
      try {
        const dispatched = await api(`/api/v1/admin/clients/${clientId}/probe-backup-dirs`, {
          method: 'POST',
          body: { maxDepth: 4, maxCandidates: 20 }
        });
        const result = await waitForProbe(dispatched.commandId, alive, stage => {
          body(`<div class="hint">已下发探测指令，等待客户端执行…（${esc(stage)}）</div>${skeleton(4)}`);
        });
        if (!alive()) return;
        renderProbe(result);
      } catch (e) {
        if (!alive()) return;
        body(`<div class="empty">${esc(e.message)}</div>`);
        footer('<button data-act="drives">返回磁盘列表</button><button data-act="manual">改为自己描述结构</button>');
      }
    }

    function renderProbe(response) {
      const found = (response.result && response.result.candidates) || [];
      const coverage = new Map((response.coverage || []).map(c => [c.path, c]));
      const notes = [];
      if (response.result && response.result.truncated)
        notes.push('磁盘太大，没有扫完（有时间上限）。下面是已经找到的那些。');
      const denied = (response.result && response.result.deniedPaths) || [];
      if (denied.length)
        notes.push(`有 ${denied.length} 个目录没有读取权限，没能看进去——那里也可能有备份。`);

      if (!found.length) {
        body(`<h3 style="margin-top:0">没有找到像备份目录的地方</h3>
          ${notes.map(n => `<div class="hint">${esc(n)}</div>`).join('')}
          <p class="hint">这不代表这台机器上没有备份，只代表自动判断没认出来。直接从磁盘列表点进去找更可靠。</p>`);
        footer('<button data-act="drives">自己找</button><button data-act="manual">改为自己描述结构</button>');
        return;
      }

      const unmonitored = found.filter(c => !(coverage.get(c.path) || {}).monitored).length;
      body(`
        <h3 style="margin-top:0">找到 ${found.length} 个像备份目录的地方，其中 ${unmonitored} 个还没被监控</h3>
        ${notes.map(n => `<div class="hint">${esc(n)}</div>`).join('')}
        ${found.map(c => probeRow(c, coverage.get(c.path))).join('')}`);
      footer('<button data-act="drives">自己找</button><button data-act="manual">改为自己描述结构</button>');
    }

    /* ── 第二步：推断 + 预演 ── */

    async function infer(deepened = false) {
      body(`<div class="hint">正在分析 ${esc(displayPath(st.selected))} 的结构…</div>${skeleton(4)}`);
      footer('');
      try {
        st.proposal = await api('/api/v1/admin/recognizer/infer', {
          method: 'POST',
          body: { commandId: st.commandId, path: st.selected }
        });

        /* 只差一层就能看懂时，再抓深一点重来一次。

           默认抓 4 层是有理由的：层数一深，条目数是乘出来的，很容易顶到 3000 条上限，
           反而让本来看得懂的结构被标成"没抓全"、置信度扣 20 分。所以不能一律调大，
           而是在服务端确实被深度卡住时才多花这一次往返（约 10 秒）。
           deepened 这个标志挡住第二次——抓到 6 层还看不懂，就是真的看不懂了。 */
        const depth = st.snapshot?.maxDepth || 0;
        if (!deepened && st.proposal.needsDeeperScan && depth < DEEP_SCAN_DEPTH) {
          body(`<div class="hint">目录比预想的深，正在往下再看两层…</div>${skeleton(4)}`);
          await load(st.selected, false, DEEP_SCAN_DEPTH);
          if (!alive()) return;
          await infer(true);
          return;
        }

        st.required = new Set(
          (st.proposal.requiredFileCandidates || []).filter(c => c.recommended).map(c => c.pattern));
        st.baseline = {
          minTotalBytes: st.proposal.suggestedMinTotalBytes ?? null,
          minFileCount: st.proposal.suggestedMinFileCount ?? null
        };
        renderProposal();
      } catch (e) {
        if (!alive()) return;
        body(`<div class="empty">${esc(e.message)}</div>`);
        footer('<button data-act="back-browse">返回目录</button><button data-act="manual">改为自己描述结构</button>');
      }
    }

    function renderProposal() {
      const p = st.proposal;
      if (!p) return;

      // 看不懂就说看不懂。把观察到的现象原样交出去，让人自己选模板，
      // 比给一个错的方案再让他去纠正要诚实得多，也快得多。
      if (p.confidence < 50) {
        body(`
          <h3 style="margin-top:0">没能看懂这个目录的结构</h3>
          <p class="hint">${esc(displayPath(st.selected))}</p>
          <ul class="evidence">${(p.evidence || []).map(x => `<li>${esc(x)}</li>`).join('')}</ul>
          <p class="hint">换一个更靠近备份文件的目录再试一次，或者直接自己描述结构。</p>`);
        footer('<button data-act="back-browse">换个目录</button><button class="primary" data-act="manual">我自己描述结构</button>');
        return;
      }

      body(`
        <div class="wiz-verdict">
          <div class="wiz-title">我看懂了这个目录</div>
          <p class="wiz-summary">${esc(p.summary)}</p>
          <ul class="evidence">${(p.evidence || []).map(x => `<li>${esc(x)}</li>`).join('')}</ul>
        </div>
        ${periodHtml(p)}
        ${sizeBaselineHtml(p, st)}
        ${requiredPickerHtml(p)}
        <h3>按这条规则，现在扫一遍的结果会是</h3>
        <div data-preview>${previewHtml(p.preview)}</div>`);
      initBytesFields(ov);

      footer(`<button data-act="back-browse">换个目录</button>
        <button data-act="manual">不太对，我自己调</button>
        <button class="primary" data-act="accept">对，按这个建任务</button>`);
    }

    /* 勾选变化后重新预演。跑在服务端已有的快照上，往返只有几十毫秒，
       所以可以做到「点一下立刻看到判定变了」，不需要惊动客户端。 */
    let previewSeq = 0;
    async function refreshPreview() {
      const slot = ov.querySelector('[data-preview]');
      if (!slot || !st.proposal) return;
      const seq = ++previewSeq;
      slot.classList.add('is-stale');
      try {
        const preview = await api('/api/v1/admin/recognizer/preview', {
          method: 'POST',
          body: {
            commandId: st.commandId,
            // 与 buildResult 同一个来源：预演的目录必须和最终建出来的任务一致。
            path: proposedSourcePath(st),
            recognizerType: st.proposal.recognizerType,
            recognizerConfig: buildConfig(st),
            stabilityIntervalSeconds: STABILITY_INTERVAL_SECONDS
          }
        });
        if (seq !== previewSeq || !alive()) return;
        st.proposal.preview = preview;
        slot.innerHTML = previewHtml(preview);
      } catch (e) {
        if (seq !== previewSeq) return;
        errToast(e);
      } finally {
        if (seq === previewSeq && slot.isConnected) slot.classList.remove('is-stale');
      }
    }

    /* 启动：打开就直接列这台机器的磁盘，而不是等一个永远不会发生的点击。
       漏了这一句，向导会停在骨架屏上，除了「关闭」没有任何可点的东西。 */
    load(null);
  });
}

/* ── 渲染片段 ── */

function driveRow(entry) {
  return `<div class="dnode" style="--lvl:0">
    <span class="dtoggle"></span>
    <button class="dname" data-act="open" data-path="${esc(entry.relativePath)}">🖴 ${esc(entry.name)}</button>
    <span class="dmeta">${entry.sizeBytes != null ? '可用 ' + fmtBytes(entry.sizeBytes) : ''}</span>
  </div>`;
}

function rootRow(snap, st) {
  const picked = st.selected === normPath(snap.root);
  return `<div class="dnode is-root${picked ? ' is-picked' : ''}" style="--lvl:0">
    <span class="dtoggle"></span>
    <button class="dname" data-act="select" data-path="${esc(normPath(snap.root))}">📂 ${esc(leafOf(snap.root))}<small>（当前目录）</small></button>
    <span class="dmeta">${snap.totalEntries} 项</span>
  </div>`;
}

function nodeHtml(entry, level, st) {
  if (!entry.isDirectory) {
    return `<div class="dnode is-file" style="--lvl:${level}">
      <span class="dtoggle"></span>
      <span class="dname is-file">📄 ${esc(entry.name)}</span>
      <span class="dmeta">${fmtBytes(entry.sizeBytes)} · ${esc(fmtDT(entry.lastModifiedAt))}</span>
    </div>`;
  }

  const path = fullPathOf(st.snapshot.root, entry.relativePath);
  const open = st.expanded.has(path);
  const hasChildren = (entry.children || []).length > 0;
  const picked = st.selected === path;

  // 链接目录（junction / 符号链接）不下探，但要照常列出并说明——
  // 静默跳过会让「这里有东西」表现成「这里是空的」。
  const meta = entry.accessDenied
    ? '<span class="dwarn">无访问权限</span>'
    : entry.isReparsePoint
      ? '<span class="dwarn">链接目录（未展开）</span>'
      : `${entry.childCount != null ? entry.childCount + ' 项' : ''}${entry.truncated ? ' · 未列全' : ''}${
          entry.sampled ? ` · 只列了最新的 ${(entry.children || []).filter(c => !c.isDirectory).length} 个文件（共 ${entry.fileCount}）` : ''}`;

  let html = `<div class="dnode${picked ? ' is-picked' : ''}" style="--lvl:${level}">
    ${hasChildren
      ? `<button class="dtoggle" data-act="toggle" data-path="${esc(path)}" aria-label="${open ? '折叠' : '展开'}">${open ? '▾' : '▸'}</button>`
      : '<span class="dtoggle"></span>'}
    <button class="dname" data-act="select" data-path="${esc(path)}">📁 ${esc(entry.name)}</button>
    <span class="dmeta">${meta}</span>
    ${entry.depthLimited && !entry.accessDenied && !entry.isReparsePoint
      ? `<button class="small" data-act="open" data-path="${esc(path)}">打开</button>` : ''}
  </div>`;

  if (open && hasChildren)
    html += entry.children.map(child => nodeHtml(child, level + 1, st)).join('');

  return html;
}

/* 必需文件的勾选区。

   分成"默认勾上"和"不作判据"两块，而不是一个平铺列表 —— 后者会让人以为
   没勾的那些是被漏掉了。真正要传达的是：它们照常备份，只是不参与完整性判定，
   而且要说清楚为什么（"各账套启用年度不同，附件库个数本来就不一样"）。
   只把勾去掉不给理由，人只会觉得系统看漏了，然后手动勾上，制造一堆假警报。

   "有候选但一条都不推荐"是真实形态，必须单独排一版：用友 U8 常常只扫出
   UFFile_*.dat 这类模式候选，而 StructureInference.InferRequiredFiles 的模式档
   要 present && stable 才给 Recommended，各账套启用年度不同、个数本来就不一样，
   于是 picked 为空、extras 非空。这时若照常渲染推荐区，人看到的是一个空的
   .filepick 框，配一句"勾上的文件如果缺了…"——一条都没勾，这句话是错的。
   所以空推荐时不渲染那个空框，改用"暂不检查"的说法，下面照常列出 extras
   （勾选框保持未勾），让人自己决定要不要拿其中某条当判据。 */
function requiredPickerHtml(proposal) {
  const candidates = proposal.requiredFileCandidates || [];
  if (!candidates.length) {
    return `<h3>每次备份必须有哪些文件</h3>
      <p class="hint">这次没有看出固定出现的文件，暂不检查。建任务后可以在任务详情里补上。</p>`;
  }

  const picked = candidates.filter(c => c.recommended);
  const extras = candidates.filter(c => !c.recommended);

  if (!picked.length) {
    return `<h3>每次备份必须有哪些文件</h3>
      <p class="hint">这次没有看出每次都固定出现的文件，暂不检查。下面这些照常备份上传，只是不作为完整性判据；建任务后也可以在任务详情里补上。</p>
      <div class="filepick">${extras.map(candidateHtml).join('')}</div>`;
  }

  return `<h3>每次备份必须有哪些文件</h3>
    <p class="hint">勾上的文件如果缺了，这次备份就判为不完整，不会入库。按模式认的那几条，判据是「至少有一个」。</p>
    <div class="filepick">
      ${picked.map(candidateHtml).join('')}
    </div>
    ${extras.length ? `
      <p class="hint" style="margin-top:.75rem">下面这些也会照常备份上传，只是不适合拿来判断"这次备份完不完整"：</p>
      <div class="filepick">${extras.map(candidateHtml).join('')}</div>` : ''}`;
}

function candidateHtml(c) {
  // 个数不固定的模式（U8 按年度分的附件库）现在也是判据了，只是判的是「至少有一个」。
  // 区间照常显示，但必须跟一句判据说明——只给一个「2–5 个」，人会以为要求每次都有 5 个，
  // 然后把这条唯一能发现「附件库整批丢失」的检查自己取消掉。
  const varyingCount = c.recommended && c.kind === 'pattern' && c.countPerUnitMin !== c.countPerUnitMax;
  const count = c.countPerUnitMax > 1 || c.countPerUnitMin !== c.countPerUnitMax
    ? `每个 ${c.countPerUnitMin === c.countPerUnitMax
        ? c.countPerUnitMin
        : c.countPerUnitMin + '–' + c.countPerUnitMax} 个 · `
    : '';
  const scope = c.totalUnits > 1
    ? `${c.presentIn}/${c.totalUnits} 个备份目录里有`
    : '在这个备份目录里';

  return `<label class="filepick__item">
    <input type="checkbox" data-required="${esc(c.pattern)}" ${c.recommended ? 'checked' : ''}>
    <span class="filepick__name">${esc(c.pattern)}${
      c.kind === 'pattern' ? '<span class="tagkind" title="文件名每次不同，按模式认">按模式</span>' : ''}</span>
    <span class="filepick__meta">${scope} · ${count}约 ${fmtBytes(c.typicalSizeBytes)}</span>
    ${c.notRecommendedReason ? `<span class="filepick__why">${esc(c.notRecommendedReason)}</span>` : ''}
    ${varyingCount ? `<span class="filepick__why">个数每个单元不一样，这是正常的——判据是「至少有一个」，不是个数：整批丢失才判为不完整</span>` : ''}
  </label>`;
}

function previewHtml(preview) {
  if (!preview) return '<div class="hint">没有预演结果</div>';
  if (!preview.units || !preview.units.length)
    return '<div class="empty">按这条规则，这个目录下识别不出任何备份。</div>';

  const caveats = [];
  if (preview.snapshotTruncated) caveats.push('目录内容没有取全，下面的结果只覆盖看得见的部分。');
  if (preview.deniedPaths && preview.deniedPaths.length) caveats.push('有目录因为权限读不到，可能影响判定。');
  // "仍在变化"不是配置错的信号，恰恰是规则在正常工作：备份刚写完就该等它稳定。
  // 不说清楚，人会以为自己选错了目录，回头去改一条本来就对的规则。
  const changing = preview.units.filter(u => u.status === 'still_changing').length;
  if (changing)
    caveats.push(`其中 ${changing} 个的最新文件还在 ${STABILITY_INTERVAL_SECONDS / 60} 分钟的稳定观察窗口内`
      + '——这不是配置错误，真正扫描时会等它写完再入库。');

  return `<p class="hint">共 ${preview.units.length} 个，${preview.passedCount} 个完整`
    + (preview.failedCount ? `，<b>${preview.failedCount} 个有问题</b>` : '')
    + ` · 依据 ${esc(fmtDT(preview.capturedAt))} 读到的目录内容</p>`
    + caveats.map(c => `<div class="hint">${esc(c)}</div>`).join('')
    + preview.units.map(unitHtml).join('');
}

function unitHtml(unit) {
  const files = unit.files || [];
  return `<div class="pvunit${unit.status === 'passed' ? '' : ' is-bad'}">
    <div class="pvunit__head">
      <strong>${esc(unit.businessUnit || unit.sourceRoot)}</strong>
      ${status('precheck', unit.status)}
      ${unit.newestFileAt ? `<span class="dmeta">最新 ${esc(fmtDT(unit.newestFileAt))}</span>` : ''}
    </div>
    ${unit.status === 'passed'
      ? `<div class="hint">${unit.totalFiles} 个文件 · ${fmtBytes(unit.totalBytes)}</div>`
      : `<div class="hint">${esc(unit.failureMessage || '未通过')}${
          unit.missingRequired && unit.missingRequired.length
            ? '：缺 ' + unit.missingRequired.map(esc).join('、') : ''}</div>`}
    ${files.length
      ? `<ul class="pvfiles">${files.slice(0, 12).map(f =>
          `<li${f.isRequired ? ' class="is-required"' : ''}>${esc(f.relativePath)}
            <span class="dmeta">${fmtBytes(f.sizeBytes)}</span>
            ${f.isRequired ? '<span class="tagreq">必需</span>' : ''}</li>`).join('')}</ul>
         ${unit.totalFiles > 12 ? `<div class="hint">仅显示前 12 个，共 ${unit.totalFiles} 个</div>` : ''}`
      : ''}
    ${unit.incomplete ? '<div class="hint">这个目录没有抓全，判定可能不准</div>' : ''}
  </div>`;
}

/* ── 工具 ── */

/* 一行候选。信号必须显示出来——人要看得懂它为什么被列出来，
   否则这张表就是一串没有依据的路径，跟随便猜没有区别。 */
function probeRow(candidate, coverage) {
  const monitored = coverage && coverage.monitored;
  return `<div class="probe-row${monitored ? ' is-monitored' : ''}">
    <div class="probe-main">
      <div class="probe-path">${esc(displayPath(candidate.path))}</div>
      <ul class="probe-signals">${(candidate.signals || []).map(x => `<li>${esc(x)}</li>`).join('')}</ul>
      <div class="sub">${esc(candidate.fileCount)} 个文件 · ${fmtBytes(candidate.totalBytes)}${
        candidate.newestFileAt ? ' · 最新 ' + esc(fmtDT(candidate.newestFileAt)) : ''}</div>
    </div>
    ${monitored
      ? `<span class="sub">已被任务「${esc(coverage.taskName || '')}」监控</span>`
      : `<button class="small primary" data-act="use-probed" data-path="${esc(candidate.path)}">用这个目录建任务</button>`}
  </div>`;
}

async function waitForProbe(commandId, alive, onStage) {
  const deadline = Date.now() + MAX_WAIT_MS;
  while (Date.now() < deadline) {
    if (!alive()) throw new Error('已取消');
    const r = await api(`/api/v1/admin/clients/probe-backup-dirs/${commandId}`);
    if (r.status === 'succeeded') return r;
    if (r.status === 'failed' || r.status === 'expired' || r.status === 'cancelled')
      throw new Error(r.resultMessage || `探测失败（${r.status}）`);
    onStage(r.status);
    await sleep(POLL_INTERVAL_MS);
  }
  throw new Error('客户端一直没有回应。它可能离线了，稍后再试。');
}

async function waitForSnapshot(commandId, alive, onStage) {
  const deadline = Date.now() + MAX_WAIT_MS;
  while (Date.now() < deadline) {
    if (!alive()) throw new Error('已取消');
    await sleep(POLL_INTERVAL_MS);
    if (!alive()) throw new Error('已取消');

    const result = await api(`/api/v1/admin/clients/browse/${commandId}`);
    if (result.status === 'succeeded') {
      if (!result.snapshot) throw new Error('客户端返回的目录结果无法解析');
      return result.snapshot;
    }
    if (['failed', 'cancelled', 'expired', 'rejected'].includes(result.status))
      throw new Error(result.resultMessage || `客户端未能完成目录浏览（${result.status}）`);
    onStage && onStage(result.status);
  }
  throw new Error('等待客户端响应超时。客户端可能离线，或这个目录太大。');
}

const sleep = ms => new Promise(r => setTimeout(r, ms));

function buildConfig(st) {
  let config = {};
  try { config = JSON.parse(st.proposal.recognizerConfig || '{}'); } catch (e) { config = {}; }
  const required = [...st.required];
  if (required.length) config.requiredFiles = required;
  else delete config.requiredFiles;
  return JSON.stringify(config, null, 2);
}

/* 方案给出的源路径可能与用户点中的目录不同：年/月分层会产出带通配的
   D:\Backup\*\*，让任务跨月、跨年自动跟随。这里必须用方案的那个，
   用 st.selected 会把通配悄悄丢掉，任务又变回写死到某个月。 */
function proposedSourcePath(st) {
  const proposed = st.proposal && st.proposal.sourcePath;
  return proposed ? displayPath(proposed) : displayPath(st.selected);
}

function buildResult(st) {
  return {
    sourcePath: proposedSourcePath(st),
    recognizerType: st.proposal.recognizerType,
    recognizerConfig: buildConfig(st),
    applicationName: st.proposal.suggestedApplicationName || '',
    // 带进建任务表单的高级项。空值等于「不检查」——与表单里留空的含义一致，
    // 不需要在这里再翻译一次。
    minTotalBytes: st.baseline.minTotalBytes,
    minFileCount: st.baseline.minFileCount
  };
}

/* 备份周期与历史缺口。

   这里只**陈述事实**：不因为有缺口就降低置信度（缺口是被识别对象的性质，
   不是识别质量的问题），也不在这一步发告警（那归 MissedBackupWorker）。
   把两者混在一起，会让「这个目录有历史缺备」表现成「向导看不懂这个目录」。 */
function periodHtml(p) {
  if (!p.detectedPeriod) return '';
  const missing = p.missingBackupDates || [];
  return `<div class="wiz-note">
    <div><strong>备份周期</strong>：${esc(p.detectedPeriod)}一备</div>
    ${missing.length
      ? `<div class="hint">按这个周期推算，这 ${missing.length} 天没有对应的备份：${missing.slice(0, 10).map(esc).join('、')}${missing.length > 10 ? ' …' : ''}。<br>这是这个目录本身的情况，不影响上面的识别结论——列出来只是让你知道。</div>`
      : '<div class="hint">历史记录连续，没有发现缺口。</div>'}
  </div>`;
}

/* 尺寸基线。

   判据（备份大小低于下限就判「大小异常」并告警）本来就存在且链路完整，缺的只有建议值——
   要人凭空想一个字节数填进去，于是现场基本不会生效，而「备份文件突然变成 0 字节」
   是备份故障里最经典的一种。

   只给下限，不给上限：备份变大通常是业务正常增长，给上限会制造周期性假警报。 */
function sizeBaselineHtml(p, st) {
  const note = p.sizeBaselineNote;
  if (!note && p.suggestedMinTotalBytes == null && p.suggestedMinFileCount == null) return '';
  return `<div class="wiz-note">
    <div><strong>备份大小下限</strong></div>
    ${note ? `<div class="hint">${esc(note)}</div>` : ''}
    <div class="wiz-baseline">
      <label>总大小下限
        <div data-baseline="minTotalBytes">${bytesFieldHtml('baselineMinTotalBytes', st.baseline.minTotalBytes ?? '')}</div>
      </label>
      <label>文件数下限
        <input type="number" min="0" data-baseline="minFileCount"
               value="${st.baseline.minFileCount ?? ''}" placeholder="留空表示不检查">
      </label>
    </div>
  </div>`;
}

/* 一次抓几层。
   盘根下面是 Windows / Program Files 这种几十万文件的子树：按 3 层抓，3000 条限额会在
   头几个系统目录里耗光，人真正要找的目录（尤其中文名，排序在 ASCII 之后）根本不会出现
   在列表里——而截断提示让他"打开上级再往下点"，盘根之上没有上级，他就卡死在这一屏。
   所以盘根只抓一层：先让他挑一个目录，进去之后再按 3 层抓，展开依然是零延迟的。 */
function browseDepth(path) {
  return isDriveRoot(path) ? 1 : 3;
}

function isDriveRoot(path) {
  return /^[A-Za-z]:\/?$/.test(String(path || '').replace(/\\/g, '/'));
}

/* 快照里的路径一律用 '/' 归一化后再比较——Agent 回来的 root 带反斜杠，
   而子节点是我们自己拼的。两种分隔符混在一起，选中判定和服务端定位都会时灵时不灵。 */
function normPath(path) {
  return String(path || '').replace(/\\/g, '/').replace(/\/+$/, '') || String(path || '');
}

/* 反过来：凡是给人看的、以及要存进任务的源路径，都换回 Windows 习惯的反斜杠。
   界面上出现 F:/autobak 会让人怀疑自己是不是选错了地方。 */
function displayPath(path) {
  const normalized = String(path || '');
  if (!/^[A-Za-z]:/.test(normalized)) return normalized;
  const windows = normalized.replace(/\//g, '\\');
  // "F:" 在 Windows 下的语义是「F 盘的当前目录」，不是 F 盘根。整盘作备份盘是常见用法，
  // 而这个路径会原样存进任务的源路径——少一个反斜杠就可能扫到另一个地方去。
  return /^[A-Za-z]:$/.test(windows) ? windows + '\\' : windows;
}

function fullPathOf(root, relative) {
  const normalizedRoot = String(root || '').replace(/\\/g, '/').replace(/\/+$/, '');
  const normalizedRelative = String(relative || '').replace(/\\/g, '/');
  return normalizedRoot ? `${normalizedRoot}/${normalizedRelative}` : normalizedRelative;
}

function parentOf(path) {
  const normalized = String(path || '').replace(/\\/g, '/').replace(/\/+$/, '');
  const index = normalized.lastIndexOf('/');
  if (index <= 0) return null;
  const parent = normalized.slice(0, index);
  // "F:" 这种只剩盘符的情况回到磁盘根，而不是一个不存在的空路径。
  return /^[A-Za-z]:$/.test(parent) ? parent + '/' : parent;
}

function leafOf(path) {
  const normalized = String(path || '').replace(/\\/g, '/').replace(/\/+$/, '');
  const index = normalized.lastIndexOf('/');
  return index < 0 ? normalized : (normalized.slice(index + 1) || normalized);
}
