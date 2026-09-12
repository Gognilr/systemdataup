/* js/views/endpoints.js —— 业务系统探测

   被监控的 Windows 服务回答「进程在不在」，这一页回答「业务用不用得了」。
   两者不能互相替代：这类系统最常见的故障形态恰恰是**进程好好的、业务已经用不了**——
   Tomcat 活着但 webapp 已经 OOM、U8 的加密狗掉了、数据库连接池耗尽。
   服务状态对这些一律显示正常。 */
import { api } from '../api.js';
import { App, LOADERS } from '../state.js';
import {
  $, esc, absTime, relText, tableHtml, emptyState, skeleton,
  toast, errToast, confirmModal, openModal, closeModal
} from '../ui.js';
import { shell, loading, schedulePoll } from '../app.js';

/* 20 秒：探测最短间隔是 10 秒、常用 60 秒，这个节奏足够让列表跟上，
   又不会让一页开着的浏览器每秒去敲一次服务端。 */
const POLL_MS = 20000;

export async function vEndpoints() {
  $('#app').innerHTML = shell('endpoints', '业务系统探测', loading());
  await LOADERS.endpoints();
}

LOADERS.endpoints = render;

async function render() {
  const view = $('#view');
  try {
    const [rows, clients] = await Promise.all([
      api('/api/v1/admin/monitored-endpoints'),
      api('/api/v1/admin/clients?pageSize=200').catch(() => null)
    ]);
    App.state.endpointClients = (clients?.items || []).map(c => ({
      id: c.id, name: c.displayName || c.hostname
    }));

    view.innerHTML = `<div class="card">
      <p class="text-muted">直接连业务端口、或打开业务页面，用和真实用户相同的路径去验。
        被监控的服务只能告诉你进程在不在——<b>进程好好的、业务已经用不了</b>才是这类系统最常见的故障。</p>
      <button class="primary" id="ep_new">新建探测</button>
    </div>
    <div class="card" id="epList">${rows.length ? '' : ''}</div>`;

    $('#epList').innerHTML = rows.length
      ? tableHtml([
          { l: '名称', render: r => `<b>${esc(r.name)}</b>${r.enabled ? '' : ' <span class="status status--mut pill">已停用</span>'}`
              + (r.clientName ? `<span class="sub">${esc(r.clientName)}</span>` : '') },
          { l: '目标', render: r => `<span class="mono">${esc(targetOf(r))}</span>` },
          { l: '状态', render: r => statusCell(r) },
          { l: '耗时', render: r => (r.lastLatencyMs == null ? '—' : `${esc(r.lastLatencyMs)} ms`) },
          { l: '最近探测', render: r => probedCell(r) },
          { l: '间隔', render: r => `${esc(r.intervalSeconds)} 秒` },
          { l: '', render: r => `<button class="small" data-edit="${esc(r.id)}">编辑</button>
              <button class="small" data-del="${esc(r.id)}">删除</button>` }
        ], rows)
      : emptyState('first', {
          glyph: '◎', title: '还没有配置探测',
          sub: '例如：U8 的加密服务 4630、登录代理 11520，致远 OA 的登录页——这些挂了，业务就停了'
        });

    $('#ep_new').addEventListener('click', () => openForm(null));
    view.querySelectorAll('[data-edit]').forEach(b =>
      b.addEventListener('click', () => openForm(rows.find(r => r.id === b.dataset.edit))));
    view.querySelectorAll('[data-del]').forEach(b =>
      b.addEventListener('click', () => remove(rows.find(r => r.id === b.dataset.del))));

    // 这一页的内容每分钟都在变，人盯着它就是为了看状态翻红——
    // 要手动刷新才更新的话，一屏不动的绿色和真的都正常长得一模一样。
    schedulePoll('endpoints', LOADERS.endpoints, POLL_MS);
  } catch (e) {
    view.innerHTML = `<div class="card"><div class="empty">加载失败：${esc(e.message)}</div></div>`;
  }
}

const targetOf = r => (r.probeType === 'http' ? r.target : `${r.target}:${r.port}`);

/* 时间列：绝对时间为主，相对时间灰着跟在后面。

   只写「1 分钟前」的话，要跟日志、告警对时间就得先做一次减法；
   只写绝对时间又看不出新鲜度——这一列恰恰是用来判断「这条探测还在不在跑」的。
   两个都给，各管各的。 */
function probedCell(r) {
  if (!r.lastProbedAt) return '<span class="sub">还没探过</span>';
  return `${absTime(r.lastProbedAt)}<span class="sub">${esc(relText(r.lastProbedAt))}</span>`;
}

/* 状态列。失败时把原因直接写出来——「探测失败」四个字帮不上任何忙，
   而「状态码 200 正常，但响应里找不到 login_username」一眼就知道是应用层死了。 */
function statusCell(r) {
  if (!r.lastStatus) return '<span class="sub">—</span>';
  if (r.lastStatus === 'up') return '<span class="status status--ok pill">正常</span>';
  return `<span class="status status--err pill">失败</span>`
    + (r.lastError ? `<span class="sub cell-warn">${esc(r.lastError)}</span>` : '')
    + (r.consecutiveFailures > 1 ? `<span class="sub">已连续失败 ${esc(r.consecutiveFailures)} 次</span>` : '');
}

async function remove(row) {
  if (!row) return;
  if (!await confirmModal(`删除探测「${row.name}」？删除后它的告警也会一并消掉。`)) return;
  try {
    await api(`/api/v1/admin/monitored-endpoints/${row.id}`, { method: 'DELETE' });
    toast('探测已删除', 'ok');
    await render();
  } catch (e) { errToast(e); }
}

function openForm(row) {
  const v = row || {};
  const isHttp = (v.probeType || 'tcp') === 'http';
  const clients = App.state.endpointClients || [];

  const ov = openModal(row ? '编辑探测' : '新建探测', `
    <div class="frow"><label for="ep_name">名称</label>
      <input id="ep_name" value="${esc(v.name || '')}" placeholder="例如：瑞来U8 加密服务">
      <div class="hint">告警标题会用它，取个一看就知道是哪台机器哪个服务的名字。</div></div>

    <div class="form-grid">
      <div class="frow"><label for="ep_type">类型</label>
        <select id="ep_type">
          <option value="tcp" ${isHttp ? '' : 'selected'}>TCP 端口</option>
          <option value="http" ${isHttp ? 'selected' : ''}>HTTP 页面</option>
        </select></div>
      <div class="frow"><label for="ep_client">关联客户端（可不选）</label>
        <select id="ep_client"><option value="">不关联</option>
          ${clients.map(c => `<option value="${esc(c.id)}" ${v.clientId === c.id ? 'selected' : ''}>${esc(c.name)}</option>`).join('')}
        </select>
        <div class="hint">选了之后告警会挂到这台机器名下。探测目标不一定装了客户端，所以可以不选。</div></div>
    </div>

    <div class="frow"><label for="ep_target">地址</label>
      <input id="ep_target" class="mono" value="${esc(v.target || '')}"
        placeholder="TCP 填 IP，如 172.16.11.141；HTTP 填完整 URL，如 http://172.16.11.175:8088/seeyon/index.jsp"></div>

    <div class="frow" id="ep_portRow"><label for="ep_port">端口</label>
      <input id="ep_port" type="number" min="1" max="65535" value="${esc(v.port ?? '')}"></div>

    <div id="ep_httpRows">
      <div class="form-grid">
        <div class="frow"><label for="ep_status">期望状态码</label>
          <input id="ep_status" value="${esc(v.expectedStatus || '')}" placeholder="200">
          <div class="hint">留空按 200 算。登录页常会 302，可以填 <span class="mono">200,302</span>。</div></div>
        <div class="frow"><label for="ep_slow">响应慢于（毫秒，可留空）</label>
          <input id="ep_slow" type="number" min="1" value="${esc(v.slowMilliseconds ?? '')}" placeholder="不判"></div>
      </div>
      <div class="frow"><label for="ep_content">期望包含文本</label>
        <input id="ep_content" value="${esc(v.expectedContent || '')}" placeholder="例如登录页上某个只有正常时才出现的字段名">
        <div class="hint"><b>这一项才是 HTTP 探测真正管用的地方。</b>只看状态码基本没用——
          Tomcat 的错误页、应用的「系统维护中」页，返回的都是 200。
          留空就是不校验正文，那这条探测只能告诉你「HTTP 还通」。
          <b>用下面的「立即试一次」看真实响应，照着挑词</b>，别凭猜——
          填的词如果错误页里也有，这条探测就永远是绿的，而你以为它在把关。</div></div>
    </div>

    <div class="form-grid">
      <div class="frow"><label for="ep_interval">探测间隔（秒）</label>
        <input id="ep_interval" type="number" min="10" max="86400" value="${esc(v.intervalSeconds ?? 60)}">
        <div class="hint">打登录页这类会建 session、写日志的地址，建议放到 300 秒以上。</div></div>
      <div class="frow"><label for="ep_timeout">超时（秒）</label>
        <input id="ep_timeout" type="number" min="1" max="120" value="${esc(v.timeoutSeconds ?? 10)}"></div>
      <div class="frow"><label for="ep_threshold">连续失败几次才告警</label>
        <input id="ep_threshold" type="number" min="1" max="100" value="${esc(v.failureThreshold ?? 3)}">
        <div class="hint">一次抖动就报警，练几次人就麻木了，而麻木之后真出事的那条也会被一起划走。</div></div>
    </div>

    <div class="frow inline"><label><input type="checkbox" id="ep_enabled" ${v.enabled === false ? '' : 'checked'}> 启用这条探测</label></div>
    <div class="frow inline"><label><input type="checkbox" id="ep_alert" ${v.alertOnFailure === false ? '' : 'checked'}> 失败时产生告警</label></div>

    <div class="frow inline"><button class="small" id="ep_try">立即试一次</button>
      <span class="tip">不保存，当场探一下看结果</span></div>
    <div id="ep_tryResult"></div>`, { okText: row ? '保存' : '创建', wide: true, persistent: true });

  const syncType = () => {
    const http = $('#ep_type').value === 'http';
    $('#ep_portRow').hidden = http;
    $('#ep_httpRows').hidden = !http;
  };
  $('#ep_type').addEventListener('change', syncType);
  syncType();

  $('#ep_try').addEventListener('click', tryProbe);
  ov.querySelector('[data-ok]').addEventListener('click', () => save(row, ov));
}

function collect() {
  const http = $('#ep_type').value === 'http';
  return {
    name: $('#ep_name').value.trim(),
    clientId: $('#ep_client').value || null,
    probeType: http ? 'http' : 'tcp',
    target: $('#ep_target').value.trim(),
    port: http ? null : (Number($('#ep_port').value) || null),
    expectedStatus: http ? ($('#ep_status').value.trim() || null) : null,
    expectedContent: http ? ($('#ep_content').value.trim() || null) : null,
    slowMilliseconds: http ? (Number($('#ep_slow').value) || null) : null,
    intervalSeconds: Number($('#ep_interval').value) || 60,
    timeoutSeconds: Number($('#ep_timeout').value) || 10,
    failureThreshold: Number($('#ep_threshold').value) || 3,
    enabled: $('#ep_enabled').checked,
    alertOnFailure: $('#ep_alert').checked
  };
}

async function tryProbe() {
  const btn = $('#ep_try');
  const box = $('#ep_tryResult');
  btn.disabled = true;
  box.innerHTML = skeleton(2);
  try {
    const r = await api('/api/v1/admin/monitored-endpoints/try', { method: 'POST', body: collect() });
    const head = r.success
      ? `<span class="status status--ok pill">通了</span>`
      : `<span class="status status--err pill">没通</span> <span class="cell-warn">${esc(r.error || '')}</span>`;
    box.innerHTML = `<div class="card" style="margin-top:8px">
      <div>${head} <span class="sub">耗时 ${esc(r.latencyMs)} ms${r.statusCode ? `，状态码 ${esc(r.statusCode)}` : ''}</span></div>
      ${r.bodyPreview ? `<div class="hint" style="margin-top:8px">响应开头（照着它挑「期望包含文本」）：</div>
        <pre class="mono" style="white-space:pre-wrap;word-break:break-all;max-height:220px;overflow:auto">${esc(r.bodyPreview)}</pre>` : ''}
    </div>`;
  } catch (e) {
    box.innerHTML = `<div class="empty" style="margin-top:8px">试探失败：${esc(e.message)}</div>`;
  } finally {
    btn.disabled = false;
  }
}

async function save(row, ov) {
  const btn = ov.querySelector('[data-ok]');
  btn.disabled = true;
  try {
    const body = collect();
    if (row) await api(`/api/v1/admin/monitored-endpoints/${row.id}`, { method: 'PUT', body });
    else await api('/api/v1/admin/monitored-endpoints', { method: 'POST', body });
    closeModal(ov);
    toast(row ? '探测已保存' : '探测已创建', 'ok');
    await render();
  } catch (e) {
    errToast(e);
    btn.disabled = false;
  }
}
