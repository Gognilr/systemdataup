import {
  $, esc, emptyState, fmtBytes, fmtDT, relTime, status, tableHtml
} from '../ui.js';

const sessionLabels = {
  active: '活动', connected: '已连接', disconnected: '已断开', idle: '空闲',
  listen: '监听', reset: '重置', down: '不可用', init: '初始化', unknown: '未知'
};

function value(value, digits = 1, suffix = '') {
  if (value == null || !Number.isFinite(Number(value))) return '—';
  return `${Number(value).toFixed(digits)}${suffix}`;
}

function diskSummary(disks) {
  const source = (disks || []).filter(d => d.isSourceVolume && Number(d.totalBytes) > 0 && d.freeBytes != null);
  if (!source.length) return { label: '暂无源盘采样', tone: '' };
  const disk = source.reduce((min, current) => {
    const minFree = Number(min.freeBytes) / Number(min.totalBytes);
    const currentFree = Number(current.freeBytes) / Number(current.totalBytes);
    return currentFree < minFree ? current : min;
  });
  const freePercent = Number(disk.freeBytes) / Number(disk.totalBytes) * 100;
  return { label: `${esc(disk.driveName)} ${value(freePercent, 1, '%')} 可用`, tone: freePercent <= 10 ? 'warn' : '' };
}

function chart(points, key, title, color, formatter) {
  const values = (points || []).map(p => Number(p[key])).filter(Number.isFinite);
  if (values.length < 2) {
    return `<section class="runtime-chart"><div class="runtime-chart-title"><b>${esc(title)}</b><span>最近 24 小时</span></div><div class="runtime-chart-empty">等待至少两个心跳采样点</div></section>`;
  }

  const width = 640;
  const height = 150;
  const padX = 12;
  const padY = 18;
  const min = Math.min(...values);
  const max = Math.max(...values);
  const range = Math.max(1, max - min);
  const coords = (points || []).map((point, index, all) => {
    const number = Number(point[key]);
    if (!Number.isFinite(number)) return null;
    const x = padX + (index / Math.max(1, all.length - 1)) * (width - padX * 2);
    const y = height - padY - ((number - min) / range) * (height - padY * 2);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).filter(Boolean).join(' ');

  return `<section class="runtime-chart"><div class="runtime-chart-title"><b>${esc(title)}</b><span>当前值 ${esc(formatter(values[values.length - 1]))}</span></div>
    <svg viewBox="0 0 ${width} ${height}" role="img" aria-label="${esc(title)}最近 24 小时趋势">
      <line class="runtime-grid-line" x1="${padX}" y1="${padY}" x2="${width - padX}" y2="${padY}" />
      <line class="runtime-grid-line" x1="${padX}" y1="${height / 2}" x2="${width - padX}" y2="${height / 2}" />
      <line class="runtime-grid-line" x1="${padX}" y1="${height - padY}" x2="${width - padX}" y2="${height - padY}" />
      <polyline class="runtime-line ${color}" points="${coords}" />
    </svg><div class="runtime-chart-axis"><span>${esc(formatter(max))}</span><span>${esc(formatter(min))}</span></div></section>`;
}

function sessionState(value) {
  return sessionLabels[value] || value || '—';
}

function renderTables(detail) {
  const sessions = detail.userSessions || [];
  const services = detail.monitoredServices || [];
  return `<div class="runtime-detail-grid">
    <section class="card runtime-detail-card"><div class="runtime-card-head"><h3>Windows 登录会话</h3><span>${sessions.length} 个</span></div>${tableHtml([
      { l: '用户', render: r => `<b>${esc([r.domain, r.username].filter(Boolean).join('\\') || '—')}</b><span class="sub">会话 ${esc(r.sessionId)}</span>` },
      { l: '状态', render: r => `<span class="runtime-session ${r.state === 'active' ? 'active' : ''}">${esc(sessionState(r.state))}</span>` },
      { l: '来源', render: r => `${esc(r.clientName || '本机')}<span class="sub">${esc(r.clientAddress || (r.isRemote ? '远程' : '本地'))}</span>` },
      { l: '登录时间', render: r => fmtDT(r.logonAt) }
    ], sessions, { empty: emptyState('ok', { title: '当前没有可见的 Windows 登录会话', sub: 'Agent 会在下一次心跳刷新会话列表' }) })}</section>
    <section class="card runtime-detail-card"><div class="runtime-card-head"><h3>关键 Windows 服务</h3><span>${services.length} 项</span></div>${tableHtml([
      { l: '服务', render: r => `${esc(r.displayName)}<span class="sub mono">${esc(r.serviceName)}</span>` },
      { l: '期望', render: r => esc(r.expectedState || '—') },
      { l: '实际', render: r => r.actualState === 'running' ? status('result', 'success') : r.actualState === 'stopped' ? status('result', 'failure') : esc(r.actualState || '—') },
      { l: '采样', render: r => relTime(r.lastSampledAt) }
    ], services, { empty: emptyState('first', { title: '未配置关键服务', sub: '在客户端配置中添加需要监控的 Windows 服务' }) })}</section>
  </div>`;
}

export function renderClientRuntimeDetail(detail, history) {
  const metrics = detail.lastMetrics || {};
  const points = history?.points || [];
  const disk = diskSummary(detail.disks);
  const memoryText = metrics.memoryTotalBytes
    ? `${fmtBytes(metrics.memoryAvailableBytes)} 可用 / ${fmtBytes(metrics.memoryTotalBytes)}`
    : fmtBytes(metrics.memoryAvailableBytes);
  const hasAlert = Number(detail.activeAlertCount) > 0;

  return `<div class="runtime-shell">
    <div class="runtime-head">
      <div><a class="runtime-back" href="#/clients">← 返回客户端列表</a><h1>${esc(detail.hostname)}</h1><div class="runtime-subtitle">${esc(detail.displayName || '')} · ${esc(detail.osName || '未知系统')} ${esc(detail.architecture || '')}</div></div>
      <div class="runtime-actions"><button data-ui-action="act" data-view="clients" data-action="metrics" data-id="${esc(detail.id)}">立即采样</button><button class="primary" data-ui-action="act" data-view="clients" data-action="refreshDetail" data-id="${esc(detail.id)}">刷新页面</button></div>
    </div>
    <div class="runtime-statusbar ${hasAlert ? 'has-alert' : ''}"><span class="runtime-status-dot"></span><strong>${status('client_status', detail.status)}</strong><span>最近心跳 ${relTime(detail.lastHeartbeatAt)}</span><span>指标采样 ${relTime(metrics.receivedAt)}</span><span>${hasAlert ? `${esc(detail.activeAlertCount)} 个活动告警` : '暂无活动告警'}</span></div>
    <div class="runtime-kpis">
      <section class="runtime-kpi ${Number(metrics.cpuPercent) >= 85 ? 'warn' : ''}"><span>系统 CPU</span><strong>${value(metrics.cpuPercent, 1, '%')}</strong><small>Agent CPU ${value(metrics.agentCpuPercent, 1, '%')}</small></section>
      <section class="runtime-kpi ${Number(metrics.memoryPercent) >= 90 ? 'warn' : ''}"><span>系统内存</span><strong>${value(metrics.memoryPercent, 1, '%')}</strong><small>${esc(memoryText)}</small></section>
      <section class="runtime-kpi ${disk.tone}"><span>源磁盘最小可用</span><strong>${disk.label}</strong><small>${esc((detail.disks || []).filter(d => d.isSourceVolume).length)} 个备份源盘</small></section>
      <section class="runtime-kpi"><span>Agent / 系统运行</span><strong>${metrics.agentUptimeSeconds ? `${Math.floor(metrics.agentUptimeSeconds / 3600)}h` : '—'}</strong><small>最后配置版本 ${esc(detail.lastConfigVersion ?? '—')}</small></section>
    </div>
    <div class="runtime-content-grid">
      <section class="card runtime-trends"><div class="runtime-card-head"><h2>资源趋势</h2><span>最近 24 小时 · ${points.length} 个心跳点</span></div><div class="runtime-chart-grid">
        ${chart(points, 'cpuPercent', '系统 CPU', 'cpu', v => value(v, 1, '%'))}
        ${chart(points, 'memoryPercent', '系统内存', 'memory', v => value(v, 1, '%'))}
        ${chart(points, 'agentMemoryBytes', 'Agent 内存', 'agent-memory', v => fmtBytes(v))}
        ${chart(points, 'networkReceiveBps', '网络接收', 'network', v => `${fmtBytes(v)}/s`)}
      </div></section>
      <section class="card runtime-meta"><div class="runtime-card-head"><h2>运行概况</h2><span>设备与安全信息</span></div><div class="kv">
        <div class="row"><div class="k">IP 地址</div><div class="v">${esc(detail.ipAddresses || '—')}</div></div>
        <div class="row"><div class="k">Agent 版本</div><div class="v">${esc(detail.agentVersion || '—')}</div></div>
        <div class="row"><div class="k">机器 ID</div><div class="v mono">${esc(detail.machineId || '—')}</div></div>
        <div class="row"><div class="k">系统运行时长</div><div class="v">${metrics.systemUptimeSeconds ? `${Math.floor(metrics.systemUptimeSeconds / 86400)} 天 ${Math.floor(metrics.systemUptimeSeconds / 3600) % 24} 小时` : '—'}</div></div>
        <div class="row"><div class="k">证书有效期</div><div class="v">${fmtDT(detail.certificateExpiresAt)}</div></div>
        <div class="row"><div class="k">时钟偏移</div><div class="v">${detail.timeOffsetSeconds != null ? `${esc(detail.timeOffsetSeconds)} 秒` : '—'}</div></div>
      </div></section>
    </div>
    <section class="card runtime-detail-card"><div class="runtime-card-head"><h2>磁盘明细</h2><span>${(detail.disks || []).length} 个盘</span></div>${tableHtml([
      { l: '盘符', render: r => `<b>${esc(r.driveName)}</b>${r.isSourceVolume ? '<span class="runtime-source-tag">备份源</span>' : ''}` },
      { l: '卷标 / 文件系统', render: r => `${esc(r.volumeLabel || '—')}<span class="sub">${esc(r.filesystem || '—')}</span>` },
      { l: '总容量', num: true, render: r => fmtBytes(r.totalBytes) },
      { l: '可用', num: true, render: r => fmtBytes(r.freeBytes) },
      { l: '可用率', num: true, render: r => Number(r.totalBytes) > 0 ? value(Number(r.freeBytes) / Number(r.totalBytes) * 100, 1, '%') : '—' },
      { l: '采样', render: r => relTime(r.sampledAt) }
    ], detail.disks, { empty: emptyState('first', { title: '暂无磁盘信息', sub: 'Agent 上报指标后自动填充' }) })}</section>
    ${renderTables(detail)}
    <details class="card runtime-technical"><summary>技术字段与诊断</summary><div class="kv">
      <div class="row"><div class="k">配置版本</div><div class="v">${esc(detail.lastConfigVersion ?? '—')}</div></div>
      <div class="row"><div class="k">审批时间</div><div class="v">${fmtDT(detail.approvedAt)}</div></div>
      <div class="row"><div class="k">证书指纹</div><div class="v mono">${esc(detail.certificateThumbprint || '—')}</div></div>
      <div class="row"><div class="k">更新时间</div><div class="v">${fmtDT(detail.updatedAt)}</div></div>
    </div></details>
  </div>`;
}


